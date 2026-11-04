using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using BiliSubStudio.Core.Configuration;

namespace BiliSubStudio.Core.Ocr;

internal sealed record OcrScanMode(
    double Fps,
    double Guard,
    double ActiveGuard,
    double DiffTrigger,
    double LowConfidence,
    bool AdaptiveTiming = false,
    bool ExhaustiveRecognition = false);
internal sealed record OcrScanSegment(int Index, double CoreStart, double CoreEnd, double ScanStart, double ScanEnd);
internal sealed record OcrLaneCheckpoint(OcrScanSegment Segment, double MediaSeconds, List<OcrCue> Cues, OcrCue? Active, int Frames, int OcrImages, bool Completed);
internal sealed record OcrParallelCheckpoint(int Schema, string Key, int SelectedParallelism, List<OcrLaneCheckpoint> Lanes, int BoundaryMerges = 0)
{
    public int Workers { get; init; } = 1;
    public string WorkerMode { get; init; } = "gpu";
}

internal sealed class OcrCheckpointStore
{
    // Older lanes could report Completed after FFmpeg emitted no JPEGs or only
    // the overlap. Their stored CoreEnd is not evidence of scanned coverage.
    // Keep old files on disk, but do not resume those falsely-completed lanes.
    // Schema 7 can also contain weak one-glyph misreads and fragmented captions
    // from the old recognition policy. Schema 8 OCR'd every decoded frame. The
    // adaptive scanner keeps native PTS coverage while OCR'ing only periodic
    // samples and transition windows. Schema 9 can retain tiny non-Chinese
    // companion boxes and CJK whitespace fragments. Schema 10 still trusted
    // high-confidence tiny companions. Schema 11 could immediately prefer a
    // one-frame longer hallucination, splitting stable captions around spaces
    // or duplicated glyphs. Schema 12 can also retain an A/B/A one-glyph
    // substitution as three final cues. Schema 13 sampled Balanced/Fast modes
    // on a fixed FPS grid, which quantized cue boundaries and dropped captions
    // shorter than the tracker's two-hit window. Schema 14 still used the Small
    // detector/recognizer and allowed Accurate mode's old visual-change probe
    // to skip a complete short caption. Schema 15 bound the Medium model identity
    // and exhaustive Accurate policy into the key. The current selector probes
    // every native frame, confirms both sides of every visual boundary and keeps
    // exact source PTS, so Accurate no longer needs full Paddle inference. The
    // ExhaustiveRecognition identity bit changes its key and prevents old partial
    // evidence from being presented as a completed scan. Schema 15 can retain
    // changing lower-right branding as part of a subtitle and split one visual
    // caption into several cues. Schema 16 still let the same branding become
    // standalone cues whenever no dialogue shared the frame.
    // Schema 18 separates off-baseline scene glyphs before inline stitching and
    // admits inflated subtitle boxes to pixel verification. Rescan old evidence.
    // Schema 19 persists inference topology independently of decoder lanes.
    private const int Schema = 19;
    private readonly AppPaths _paths;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public OcrCheckpointStore(AppPaths paths) => _paths = paths;

    public async Task<OcrParallelCheckpoint?> LoadAsync(OcrScanRequest request, CancellationToken cancellationToken)
    {
        var key = await KeyAsync(request, Schema, cancellationToken);
        var path = Path.Combine(DirectoryPath, key + ".json");
        // Crash safety: the main file may be missing or torn if the app was
        // killed mid-save. Fall back to the previous rotated backup so a
        // sudden close/crash still resumes instead of forcing a fresh scan.
        return await TryLoadAsync(path, key, request.Duration, request.ScanStartOffset, cancellationToken)
            ?? await TryLoadAsync(path + ".bak", key, request.Duration, request.ScanStartOffset, cancellationToken);
    }

    private static async Task<OcrParallelCheckpoint?> TryLoadAsync(string path, string key, double duration, double offset, CancellationToken cancellationToken)
    {
        var checkpoint = await ParseFileAsync(path, cancellationToken);
        if (checkpoint is null || checkpoint.Schema != Schema || checkpoint.Key != key || !IsValidCheckpoint(checkpoint, duration, offset))
            return null;
        return checkpoint;
    }

    internal static async Task<OcrParallelCheckpoint?> ParseFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<OcrParallelCheckpoint>(await File.ReadAllTextAsync(path, cancellationToken), JsonOptions);
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    // Read a resume candidate WITHOUT end-match validation (06/10, the container padding
    // case): the caller (scanner, has tools to probe stream truth) decides
    // whether it accepts a span shorter than duration. Wrong schema/key is dropped immediately.
    internal async Task<OcrParallelCheckpoint?> ParseCandidateAsync(OcrScanRequest request, CancellationToken cancellationToken)
    {
        string key;
        try
        {
            key = await KeyAsync(request, Schema, cancellationToken);
        }
        catch
        {
            return null;
        }
        foreach (var suffix in new[] { ".json", ".json.bak" })
        {
            var checkpoint = await ParseFileAsync(Path.Combine(DirectoryPath, key + suffix), cancellationToken);
            if (checkpoint is null || checkpoint.Schema != Schema || checkpoint.Key != key)
                continue;
            return checkpoint;
        }
        return null;
    }

    // Resume-miss diagnosis 06/10 (pausing then pressing Continue reported a lost
    // checkpoint): break the chain apart (does the file exist? is it readable?
    // which of schema/key/validity dropped?) so the log states it explicitly instead of guessing.
    // Only used when resume fails; never touches the hot path.
    internal sealed record CheckpointResumeDiagnosis(
        string Key,
        bool MainExists,
        bool BackupExists,
        bool Parsed,
        int Schema,
        bool SchemaMatch,
        bool KeyMatch,
        bool Valid,
        int Lanes,
        int Workers,
        string WorkerMode,
        int SelectedParallelism);

    internal async Task<CheckpointResumeDiagnosis> DiagnoseResumeAsync(
        OcrScanRequest request, CancellationToken cancellationToken)
    {
        var key = await KeyAsync(request, Schema, cancellationToken);
        var path = Path.Combine(DirectoryPath, key + ".json");
        var bakPath = path + ".bak";
        var mainExists = File.Exists(path);
        var bakExists = File.Exists(bakPath);
        var parsedPath = mainExists ? path : bakExists ? bakPath : null;
        OcrParallelCheckpoint? checkpoint = null;
        if (parsedPath is not null)
        {
            try
            {
                checkpoint = JsonSerializer.Deserialize<OcrParallelCheckpoint>(
                    await File.ReadAllTextAsync(parsedPath, cancellationToken), JsonOptions);
            }
            catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
            {
                checkpoint = null;
            }
        }
        var schemaMatch = checkpoint is not null && checkpoint.Schema == Schema;
        var keyMatch = checkpoint is not null && checkpoint.Key == key;
        var valid = checkpoint is not null
            && IsValidCheckpoint(checkpoint, request.Duration, request.ScanStartOffset);
        return new CheckpointResumeDiagnosis(
            key, mainExists, bakExists, checkpoint is not null,
            checkpoint?.Schema ?? -1, schemaMatch, keyMatch, valid,
            checkpoint?.Lanes?.Count ?? -1, checkpoint?.Workers ?? -1,
            checkpoint?.WorkerMode ?? "?", checkpoint?.SelectedParallelism ?? -1);
    }

    public async Task SaveAsync(OcrScanRequest request, OcrParallelCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        var key = await KeyAsync(request, Schema, cancellationToken);
        if (checkpoint.Schema != Schema || checkpoint.Key != key) throw new InvalidDataException($"Checkpoint OCR schema {Schema} không hợp lệ.");
        Directory.CreateDirectory(DirectoryPath);
        var path = Path.Combine(DirectoryPath, key + ".json");
        var temporary = path + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(checkpoint, JsonOptions);
        try
        {
            await using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await file.WriteAsync(bytes, cancellationToken);
                await file.FlushAsync(cancellationToken);
                file.Flush(flushToDisk: true);
            }
            // Rotate the previous durable checkpoint aside before installing the
            // new one, so a kill/crash between saves can never lose more than
            // one 30-second window. Backup is best-effort: the main save must
            // proceed even if rotation fails (disk full, ACL).
            try
            {
                if (File.Exists(path))
                    File.Copy(path, path + ".bak", overwrite: true);
            }
            catch { }
            File.Move(temporary, path, overwrite: true);
            WriteInfo(request, key);
        }
        finally { TryDelete(temporary); }
    }

    // Per-video checkpoint recognition profile (06/10, the pause-then-resume
    // case reporting a lost checkpoint because default ROI/mode after startup differed from
    // scan time): <key>.info.json records the video + exactly the settings that produced the key. A pick-video miss
    // then looks up info for the same video, restores ROI/mode, and inspects
    // again using the canonical key — resume works again, no guesswork. Best-effort:
    // never fails save/load/remove.
    // (Type is public in OcrModels so App can use it; the store only reads/writes.)

    private void WriteInfo(OcrScanRequest request, string key)
    {
        try
        {
            var fullPath = Path.GetFullPath(request.Path.Trim());
            var file = new FileInfo(fullPath);
            var info = new OcrCheckpointVideoInfo(
                key,
                fullPath,
                file.Exists ? file.Length : 0,
                file.Exists ? (file.LastWriteTimeUtc - DateTime.UnixEpoch).Ticks * 100 : 0,
                request.Region,
                request.Mode,
                request.Device,
                request.Parallelism,
                request.Sensitivity,
                request.Duration,
                request.ScanStartOffset,
                DateTimeOffset.UtcNow);
            // Separate sub-directory: the "exactly 1 checkpoint file" contract scans
            // top-level *.json — info sits outside so it does not break the clean
            // invariant of the checkpoint directory.
            var indexDir = Path.Combine(DirectoryPath, "index");
            Directory.CreateDirectory(indexDir);
            File.WriteAllBytes(
                Path.Combine(indexDir, key + ".info.json"),
                JsonSerializer.SerializeToUtf8Bytes(info, JsonOptions));
        }
        catch { }
    }

    internal IReadOnlyList<OcrCheckpointVideoInfo> FindByVideo(string videoPath)
    {
        var found = new List<OcrCheckpointVideoInfo>();
        string fullPath;
        long size;
        long modUnixNano;
        try
        {
            fullPath = Path.GetFullPath(videoPath.Trim());
            var file = new FileInfo(fullPath);
            if (!file.Exists || file.Length <= 0) return found;
            size = file.Length;
            modUnixNano = (file.LastWriteTimeUtc - DateTime.UnixEpoch).Ticks * 100;
        }
        catch { return found; }
        string[] candidates;
        try
        {
            var indexDir = Path.Combine(DirectoryPath, "index");
            if (!Directory.Exists(indexDir)) return found;
            candidates = Directory.GetFiles(indexDir, "*.info.json");
        }
        catch { return found; }
        foreach (var candidate in candidates)
        {
            OcrCheckpointVideoInfo? info;
            try
            {
                info = JsonSerializer.Deserialize<OcrCheckpointVideoInfo>(
                    File.ReadAllBytes(candidate), JsonOptions);
            }
            catch { continue; }
            if (info is null || info.Key.Length != 64) continue;
            if (!string.Equals(info.VideoPath, fullPath, StringComparison.OrdinalIgnoreCase)
                || info.Size != size || info.ModUnixNano != modUnixNano)
                continue;
            if (!File.Exists(Path.Combine(DirectoryPath, info.Key + ".json"))
                && !File.Exists(Path.Combine(DirectoryPath, info.Key + ".json.bak")))
                continue;
            found.Add(info);
        }
        found.Sort((left, right) => right.SavedUtc.CompareTo(left.SavedUtc));
        return found;
    }

    public async Task<OcrCheckpointInfo> InspectAsync(OcrScanRequest request, CancellationToken cancellationToken)
    {
        var checkpoint = await LoadAsync(request, cancellationToken);
        if (checkpoint is null) return new OcrCheckpointInfo(false, 0, 0, 0, 0, 0, 0, 0, [], []);
        return await AssembleInfoAsync(checkpoint, request, cancellationToken);
    }

    internal async Task<OcrCheckpointInfo> AssembleInfoAsync(
        OcrParallelCheckpoint checkpoint, OcrScanRequest request, CancellationToken cancellationToken)
    {
        var completed = checkpoint.Lanes.Count(x => x.Completed);
        var media = ContiguousFrontier(checkpoint.Lanes);
        // Recovery export must contain only tracker-committed, lane-owned cues.
        // Active cues are useful for the live preview but are still provisional.
        // Async production call-site: always pass videoPath + ProductionPixelComparer.
        var (reconciled, _) = await OcrScanner.ReconcileAsync(checkpoint.Lanes, request.Path, new ProductionPixelComparer(), cancellationToken);
        var cues = reconciled
            .Where(x => x.Start <= media + 0.001)
            .OrderBy(x => x.Start)
            .ToArray();
        return new OcrCheckpointInfo(
            true, Schema, media, cues.Length, checkpoint.SelectedParallelism, completed, checkpoint.Lanes.Count,
            request.Duration > 0 ? Math.Clamp(media / request.Duration * 100, 0, 100) : 0,
            cues.TakeLast(120).ToArray(), cues);
    }

    // Exclusion list at SRT export (see OcrExportExclusions): the file
    // <checkpointkey>.exclude.json beside the checkpoint. Not part of the checkpoint so it does not
    // affect key/resume/schema; missing/corrupt = no exclusion, NEVER
    // fails export. Deleting the file is the undo. RemoveAsync does not delete it (the user's decision
    // persists through Fresh).
    internal async Task<IReadOnlyList<OcrCueExclusion>> LoadExclusionsAsync(OcrScanRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var key = await KeyAsync(request, Schema, cancellationToken);
            var path = Path.Combine(DirectoryPath, key + ".exclude.json");
            if (!File.Exists(path)) return [];
            var entries = JsonSerializer.Deserialize<List<OcrCueExclusion>>(
                await File.ReadAllTextAsync(path, cancellationToken), JsonOptions);
            if (entries is null) return [];
            return entries
                .Where(entry => entry is not null && entry.Text is not null
                    && double.IsFinite(entry.Start) && entry.Start >= 0)
                .ToArray();
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // Add cues to the exclusion list (the reviewer verdicts it in the app).
    // Merges with the existing file, dropping duplicates (Start ±1ms + Text). Atomic tmp+move write.
    // Returns the number of newly added cues. Never touches the checkpoint.
    internal async Task<int> SaveExclusionsAsync(
        OcrScanRequest request,
        IReadOnlyList<OcrCueExclusion> additions,
        CancellationToken cancellationToken)
    {
        var key = await KeyAsync(request, Schema, cancellationToken);
        var path = Path.Combine(DirectoryPath, key + ".exclude.json");
        Directory.CreateDirectory(DirectoryPath);
        var merged = new List<OcrCueExclusion>(await LoadExclusionsAsync(request, cancellationToken));
        var added = 0;
        foreach (var addition in additions)
        {
            if (addition is null || addition.Text is null
                || !double.IsFinite(addition.Start) || addition.Start < 0)
                continue;
            var duplicate = false;
            foreach (var existing in merged)
            {
                if (Math.Abs(existing.Start - addition.Start) <= 0.001
                    && string.Equals(existing.Text, addition.Text, StringComparison.Ordinal))
                {
                    duplicate = true;
                    break;
                }
            }
            if (duplicate) continue;
            merged.Add(addition);
            added++;
        }
        var temporary = path + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(merged, JsonOptions);
        try
        {
            await using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await file.WriteAsync(bytes, cancellationToken);
                await file.FlushAsync(cancellationToken);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { TryDelete(temporary); }
        return added;
    }

    // Remove exclusion entries (undo a manual deletion in the app, 21/09). Same matching rule
    // as SaveExclusionsAsync (Start ±1ms + exact Text), so the undo always hits
    // exactly the entry written. Missing file/no match = 0, NO error.
    // Atomic tmp+move write like the append. Never touches the checkpoint.
    internal async Task<int> RemoveExclusionsAsync(
        OcrScanRequest request,
        IReadOnlyList<OcrCueExclusion> removals,
        CancellationToken cancellationToken)
    {
        var key = await KeyAsync(request, Schema, cancellationToken);
        var path = Path.Combine(DirectoryPath, key + ".exclude.json");
        var merged = new List<OcrCueExclusion>(await LoadExclusionsAsync(request, cancellationToken));
        var removed = 0;
        foreach (var removal in removals)
        {
            if (removal is null || removal.Text is null
                || !double.IsFinite(removal.Start) || removal.Start < 0)
                continue;
            for (var index = merged.Count - 1; index >= 0; index--)
            {
                if (Math.Abs(merged[index].Start - removal.Start) <= 0.001
                    && string.Equals(merged[index].Text, removal.Text, StringComparison.Ordinal))
                {
                    merged.RemoveAt(index);
                    removed++;
                    break;
                }
            }
        }
        if (removed == 0) return 0;
        var temporary = path + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(merged, JsonOptions);
        try
        {
            await using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await file.WriteAsync(bytes, cancellationToken);
                await file.FlushAsync(cancellationToken);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { TryDelete(temporary); }
        return removed;
    }

    // AI text-correction list (see OcrExportFixes): the file <checkpointkey>.fix.json
    // beside the checkpoint. Rule identical to exclusion: not part of the checkpoint, missing/corrupt
    // = no correction, NEVER fails export. Duplicate fixes (Start ±1ms + old text)
    // let the later one win (overwrites with the new text). Deleting the file is the undo.
    internal async Task<IReadOnlyList<OcrCueFix>> LoadFixesAsync(OcrScanRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var key = await KeyAsync(request, Schema, cancellationToken);
            var path = Path.Combine(DirectoryPath, key + ".fix.json");
            if (!File.Exists(path)) return [];
            var entries = JsonSerializer.Deserialize<List<OcrCueFix>>(
                await File.ReadAllTextAsync(path, cancellationToken), JsonOptions);
            if (entries is null) return [];
            return entries
                .Where(entry => entry is not null && entry.OldText is not null
                    && entry.NewText is not null && entry.NewText.Length > 0
                    && double.IsFinite(entry.Start) && entry.Start >= 0)
                .ToArray();
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // Add/overwrite text corrections (the fix file during scan/export). Merges with the existing file.
    // Returns the number of newly added cues (overwrites are not counted). Never touches the checkpoint.
    internal async Task<int> SaveFixesAsync(
        OcrScanRequest request,
        IReadOnlyList<OcrCueFix> additions,
        CancellationToken cancellationToken)
    {
        var key = await KeyAsync(request, Schema, cancellationToken);
        var path = Path.Combine(DirectoryPath, key + ".fix.json");
        Directory.CreateDirectory(DirectoryPath);
        var merged = new List<OcrCueFix>(await LoadFixesAsync(request, cancellationToken));
        var added = 0;
        foreach (var addition in additions)
        {
            if (addition is null || addition.OldText is null || addition.NewText is null
                || addition.NewText.Length == 0
                || !double.IsFinite(addition.Start) || addition.Start < 0)
                continue;
            var overwritten = false;
            for (var index = 0; index < merged.Count; index++)
            {
                if (Math.Abs(merged[index].Start - addition.Start) <= 0.001
                    && string.Equals(merged[index].OldText, addition.OldText, StringComparison.Ordinal))
                {
                    merged[index] = addition;
                    overwritten = true;
                    break;
                }
            }
            if (overwritten) continue;
            merged.Add(addition);
            added++;
        }
        var temporary = path + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(merged, JsonOptions);
        try
        {
            await using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await file.WriteAsync(bytes, cancellationToken);
                await file.FlushAsync(cancellationToken);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { TryDelete(temporary); }
        return added;
    }

    public async Task RemoveAsync(OcrScanRequest request, CancellationToken cancellationToken)
    {
        foreach (var schema in new[] { 19, 18, 17, 16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3 })
        {
            var key = await KeyAsync(request, schema, cancellationToken);
            var path = Path.Combine(DirectoryPath, key + ".json");
            TryDelete(path);
            TryDelete(path + ".bak");
            TryDelete(path + ".tmp");
            TryDelete(Path.Combine(DirectoryPath, "index", key + ".info.json"));
            if (File.Exists(path) || File.Exists(path + ".bak") || File.Exists(path + ".tmp"))
                throw new IOException($"Không thể xóa checkpoint OCR schema {schema}: {path}");
        }
    }

    // Clean up the .bak file after a scan finishes cleanly (23/09): the crash backup is only needed
    // during a scan (power loss/abrupt shutdown); once done the main file suffices, so
    // keeping .bak only costs disk (a few dozen MB). Absolutely best-effort: never
    // throws, never touches the main file/notebook. Pause/cancel/error => NOT called.
    public async Task DeleteBackupAsync(OcrScanRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var key = await KeyAsync(request, Schema, cancellationToken);
            TryDelete(Path.Combine(DirectoryPath, key + ".json.bak"));
        }
        catch
        {
        }
    }

    public async Task<OcrParallelCheckpoint> NewAsync(OcrScanRequest request, int parallelism, CancellationToken cancellationToken)
    {
        var scanMode = ModeFor(request.Mode, request.Sensitivity);
        var overlap = Math.Max(scanMode.Guard, scanMode.ActiveGuard);
        var segments = BuildSegments(request.Duration, parallelism, overlap, request.ScanStartOffset);
        var key = await KeyAsync(request, Schema, cancellationToken);
        var workerMode = request.Device.Trim().ToLowerInvariant() is "cpu" or "hybrid" ? request.Device.Trim().ToLowerInvariant() : "gpu";
        return new OcrParallelCheckpoint(Schema, key, segments.Count,
            segments.Select(x => new OcrLaneCheckpoint(x, x.ScanStart, [], null, 0, 0, false)).ToList())
        {
            WorkerMode = workerMode,
            Workers = OcrTopologyBenchmark.WorkerTarget(workerMode, segments.Count),
        };
    }

    public static IReadOnlyList<OcrScanSegment> BuildSegments(double duration, int parallelism, double overlap, double offset = 0)
    {
        if (duration <= 0 || double.IsNaN(duration) || double.IsInfinity(duration)) throw new ArgumentException("Thời lượng video không hợp lệ.");
        if (!double.IsFinite(offset) || offset < 0) throw new ArgumentException("Offset targeted scan không hợp lệ.");
        parallelism = Math.Clamp(parallelism, 1, 16);
        var output = new List<OcrScanSegment>();
        for (var index = 0; index < parallelism; index++)
        {
            var coreStart = offset + duration * index / parallelism;
            var coreEnd = offset + duration * (index + 1) / parallelism;
            output.Add(new OcrScanSegment(index, coreStart, coreEnd, Math.Max(offset, coreStart - overlap), Math.Min(offset + duration, coreEnd + overlap)));
        }
        return output;
    }

    public static OcrScanMode ModeFor(string mode, double sensitivity)
    {
        var result = mode.Trim().ToLowerInvariant() switch
        {
            // Accurate checks every decoded source frame with the cheap visual
            // probe, samples PaddleOCR at 4 Hz, and confirms native frames around
            // every boundary or unstable reading. It preserves exact source PTS
            // without paying Medium-model inference cost for unchanged frames.
            "accurate" or "precise" or "chinh-xac" => new OcrScanMode(
                4, 3, 12, 0.10, 0.68, AdaptiveTiming: true),
            // Balanced/Fast still reduce the steady Paddle sampling rate, but
            // must retain every native-frame PTS and cheaply probe visual
            // changes. Transition frames are then OCR'd in a small batch, so a
            // short subtitle is not lost between two sparse baseline samples.
            "fast" or "nhanh" => new OcrScanMode(1.5, 8, 24, 0.22, 0.58, AdaptiveTiming: true),
            _ => new OcrScanMode(2.5, 5, 16, 0.16, 0.62, AdaptiveTiming: true),
        };
        sensitivity = sensitivity <= 0 ? 1 : Math.Clamp(sensitivity, 0.60, 1.50);
        return result with { DiffTrigger = result.DiffTrigger * sensitivity };
    }

    private async Task<string> KeyAsync(OcrScanRequest request, int schema, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Path.GetFullPath(request.Path.Trim());
        var file = new FileInfo(path);
        if (!file.Exists || file.Length <= 0) throw new FileNotFoundException("Video nguồn không tồn tại hoặc rỗng.", path);
        var region = CanonicalRegion(request.Region);
        var mode = ModeFor(request.Mode, request.Sensitivity);
        var modUnixNano = (file.LastWriteTimeUtc - DateTime.UnixEpoch).Ticks * 100;
        object identity = schema >= 9
            ? new CheckpointIdentity(
                schema, path, file.Length, modUnixNano,
                region, request.Mode.Trim().ToLowerInvariant(), mode.Fps, mode.Guard, mode.ActiveGuard, mode.DiffTrigger,
                mode.AdaptiveTiming, mode.ExhaustiveRecognition,
                OcrModelTier.DetectionModel(request.Mode), OcrModelTier.RecognitionModel(request.Mode), 1280, 320)
            : new LegacyCheckpointIdentity(
                schema, path, file.Length, modUnixNano,
                region, request.Mode.Trim().ToLowerInvariant(), mode.Fps, mode.Guard, mode.ActiveGuard, mode.DiffTrigger,
                1280, 320);
        var json = JsonSerializer.SerializeToUtf8Bytes(identity, JsonOptions);
        var key = Convert.ToHexStringLower(SHA256.HashData(json));
        // Targeted scan (offset != 0) uses its own key so it never overwrites/collides with a full-scan checkpoint.
        // Offset 0 keeps the old key byte-for-byte (backward compatible, does not touch existing checkpoints).
        if (request.ScanStartOffset != 0)
        {
            var extra = System.Text.Encoding.UTF8.GetBytes(key + "|offset:" + request.ScanStartOffset.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            key = Convert.ToHexStringLower(SHA256.HashData(extra));
        }
        return key;
    }

    public static OcrRegion NormalizeRegion(OcrRegion region)
    {
        if (region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0 || region.X >= 1 || region.Y >= 1 || region.X + region.Width > 1.000001 || region.Y + region.Height > 1.000001)
            throw new ArgumentException("Vùng OCR không hợp lệ.");
        return region;
    }

    public static OcrRegion CanonicalRegion(OcrRegion region)
    {
        region = NormalizeRegion(region);
        var left = Math.Clamp((int)Math.Floor(region.X * 100), 0, 99);
        var top = Math.Clamp((int)Math.Floor(region.Y * 100), 0, 99);
        var right = Math.Clamp(Math.Max(left + 1, (int)Math.Ceiling((region.X + region.Width) * 100)), 1, 100);
        var bottom = Math.Clamp(Math.Max(top + 1, (int)Math.Ceiling((region.Y + region.Height) * 100)), 1, 100);
        return new OcrRegion(left / 100d, top / 100d, (right - left) / 100d, (bottom - top) / 100d);
    }

    internal static double ContiguousFrontier(IReadOnlyList<OcrLaneCheckpoint> lanes)
    {
        var frontier = 0d;
        foreach (var lane in lanes.OrderBy(x => x.Segment.Index))
        {
            if (lane.Completed) frontier = lane.Segment.CoreEnd;
            else
            {
                frontier = Math.Max(frontier, Math.Clamp(lane.MediaSeconds, lane.Segment.CoreStart, lane.Segment.CoreEnd));
                break;
            }
        }
        return frontier;
    }

    internal static bool IsValidCheckpoint(OcrParallelCheckpoint checkpoint, double duration, double offset = 0)
    {
        if (checkpoint.Workers is < 1 or > 16 ||
            checkpoint.WorkerMode is not ("gpu" or "cpu" or "hybrid") ||
            checkpoint.Workers != OcrTopologyBenchmark.WorkerTarget(checkpoint.WorkerMode, checkpoint.SelectedParallelism) ||
            (checkpoint.WorkerMode == "hybrid" && checkpoint.Workers < 2) ||
            checkpoint.SelectedParallelism is < 1 or > 16 || checkpoint.Lanes is null ||
            checkpoint.Lanes.Count != checkpoint.SelectedParallelism ||
            checkpoint.Lanes.Any(lane => lane is null || lane.Segment is null || lane.Cues is null) ||
            !double.IsFinite(duration) || duration <= 0 || !double.IsFinite(offset) || offset < 0)
            return false;

        var ordered = checkpoint.Lanes.OrderBy(x => x.Segment.Index).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var lane = ordered[index];
            var segment = lane.Segment;
            if (segment is null || lane.Cues is null || segment.Index != index || !double.IsFinite(segment.CoreStart) || !double.IsFinite(segment.CoreEnd) ||
                !double.IsFinite(segment.ScanStart) || !double.IsFinite(segment.ScanEnd) ||
                segment.CoreStart < offset - 0.001 || segment.CoreEnd <= segment.CoreStart || segment.CoreEnd > offset + duration + 0.001 ||
                segment.ScanStart < offset - 0.001 || segment.ScanStart > segment.CoreStart ||
                // ScanEnd-vs-CoreEnd uses the same ±0.001 tolerance as every neighboring bound check:
                // BuildSegments computes CoreEnd = offset + duration*N/N in floating-point so it can
                // sludge ~1e-12 on ScanEnd (min with offset+duration); drift <= 1ms is still valid.
                segment.ScanEnd + 0.001 < segment.CoreEnd || segment.ScanEnd > offset + duration + 0.001 ||
                !double.IsFinite(lane.MediaSeconds) || lane.MediaSeconds < segment.ScanStart - 0.001 || lane.MediaSeconds > segment.ScanEnd + 0.001 ||
                lane.Frames < 0 || lane.OcrImages < 0 || lane.OcrImages > lane.Frames ||
                (index > 0 && Math.Abs(ordered[index - 1].Segment.CoreEnd - segment.CoreStart) > 0.001) ||
                lane.Cues.Any(cue => cue is null || !IsValidCue(cue)) || lane.Active is { } active && !IsValidCue(active))
                return false;
        }
        return Math.Abs(ordered[0].Segment.CoreStart - offset) <= 0.001 && Math.Abs(ordered[^1].Segment.CoreEnd - (offset + duration)) <= 0.001;
    }

    private static bool IsValidCue(OcrCue cue) =>
        double.IsFinite(cue.Start) && double.IsFinite(cue.End) && double.IsFinite(cue.Confidence) &&
        cue.Start >= 0 && cue.End >= cue.Start && cue.Text is not null;

    private string DirectoryPath => Path.Combine(_paths.Data, "OCRCheckpoints");
    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }

    private sealed record CheckpointIdentity(
        [property: JsonPropertyName("schema")] int Schema,
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("mtime_ns")] long ModUnixNano,
        [property: JsonPropertyName("region")] OcrRegion Region,
        [property: JsonPropertyName("mode")] string Mode,
        [property: JsonPropertyName("fps")] double Fps,
        [property: JsonPropertyName("guard")] double Guard,
        [property: JsonPropertyName("active_guard")] double ActiveGuard,
        [property: JsonPropertyName("diff")] double Diff,
        [property: JsonPropertyName("adaptive_timing")] bool AdaptiveTiming,
        [property: JsonPropertyName("exhaustive_recognition")] bool ExhaustiveRecognition,
        [property: JsonPropertyName("detection_model")] string DetectionModel,
        [property: JsonPropertyName("recognition_model")] string RecognitionModel,
        [property: JsonPropertyName("width")] int Width,
        [property: JsonPropertyName("height")] int Height);

    private sealed record LegacyCheckpointIdentity(
        [property: JsonPropertyName("schema")] int Schema,
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("mtime_ns")] long ModUnixNano,
        [property: JsonPropertyName("region")] OcrRegion Region,
        [property: JsonPropertyName("mode")] string Mode,
        [property: JsonPropertyName("fps")] double Fps,
        [property: JsonPropertyName("guard")] double Guard,
        [property: JsonPropertyName("active_guard")] double ActiveGuard,
        [property: JsonPropertyName("diff")] double Diff,
        [property: JsonPropertyName("width")] int Width,
        [property: JsonPropertyName("height")] int Height);
}
