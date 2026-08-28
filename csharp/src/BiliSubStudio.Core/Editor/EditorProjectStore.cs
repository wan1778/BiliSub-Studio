using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BiliSubStudio.Core.Configuration;

namespace BiliSubStudio.Core.Editor;

public sealed record EditorSourceFingerprint(
    string Path,
    long Size,
    long LastWriteUtcTicks,
    int Width,
    int Height,
    double Duration);

public sealed record EditorSubtitlePlacement(double X, double Y, double Width, double Height)
{
    public static EditorSubtitlePlacement Default { get; } = new(.08, .70, .84, .22);
}

public sealed record EditorAudioSettings(string SourceMode, double SourceGain)
{
    public static EditorAudioSettings Default { get; } = new("keep", 1);
}

public sealed record EditorAsrProject(
    string Status,
    string ModelName,
    string ModelRevision,
    string Device,
    string ComputeType,
    string OutputPath,
    int CueCount,
    double ProbeRealtimeFactor);

public sealed record EditorSpeechProject(
    string Status,
    string ModelName,
    string ModelRevision,
    string Device,
    string ComputeType,
    string AnalysisPath,
    string AnalysisSha256,
    int SegmentCount,
    int WordCount,
    double ProbeRealtimeFactor);

public sealed record EditorTtsProject(
    string Status,
    string Engine,
    string EngineVersion,
    string MaleVoice,
    string FemaleVoice,
    string ManifestPath,
    string ManifestSha256,
    EditorVoiceTrack VoiceTrack,
    int CueCount,
    int ReviewCount);

public sealed record EditorSubtitleProject(
    string SourcePath,
    long SourceSize,
    long SourceLastWriteUtcTicks,
    string SourceSha256,
    IReadOnlyList<EditorSubtitleCue> Cues,
    EditorSubtitlePlacement Placement,
    string SkillName,
    string SkillSha256,
    string OutputPath,
    bool Karaoke = true,
    string? TranslationPolicyKey = null);

public sealed record EditorProject(
    int Schema,
    string Id,
    string Name,
    EditorSourceFingerprint Source,
    string FileName,
    IReadOnlyList<EditRegion> Regions,
    EditorSubtitleProject? Subtitle,
    DateTimeOffset UpdatedUtc,
    EditorAudioSettings? Audio = null,
    EditorAsrProject? Asr = null,
    EditorSpeechProject? Speech = null,
    EditorTtsProject? Tts = null,
    IReadOnlyDictionary<string, string>? VoiceOverrides = null);

public sealed class EditorProjectStore
{
    public const int CurrentSchema = 5;
    private const long MaxProjectBytes = 64L * 1024 * 1024;
    private const string CurrentTtsEngine = "nghi-tts";
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public EditorProjectStore(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _directory = Path.Combine(paths.Data, "Projects");
    }

    public static bool SourceFingerprintMatchesCurrent(
        EditorSourceFingerprint? previous,
        string inputPath,
        int width,
        int height,
        double duration)
    {
        if (previous is null) return false;
        try
        {
            return !SourceFingerprintChanged(previous, Fingerprint(inputPath, width, height, duration));
        }
        catch
        {
            return false;
        }
    }

    public async Task<EditorProject> LoadOrCreateAsync(
        string inputPath,
        int width,
        int height,
        double duration,
        CancellationToken cancellationToken)
    {
        var source = Fingerprint(inputPath, width, height, duration);
        var id = ProjectId(source.Path);
        var projectPath = ProjectPath(id);
        if (File.Exists(projectPath))
        {
            try
            {
                var info = new FileInfo(projectPath);
                if (info.Length <= 0 || info.Length > MaxProjectBytes)
                    throw new InvalidDataException("Project Editor có kích thước không hợp lệ.");
                EditorProject loaded;
                await using (var stream = new FileStream(projectPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    loaded = await JsonSerializer.DeserializeAsync<EditorProject>(stream, _json, cancellationToken)
                        ?? throw new InvalidDataException("Project Editor rỗng.");
                }
                if (loaded.Schema is not (1 or 2 or 3 or 4 or CurrentSchema)
                    || !string.Equals(loaded.Id, id, StringComparison.Ordinal)
                    || loaded.Source is null)
                    throw new InvalidDataException("Project Editor không đúng phiên bản hoặc nguồn.");
                if (SourceFingerprintChanged(loaded.Source, source))
                {
                    // PROJECT-05: the image sidecar shares the path-derived project ID,
                    // so archive it before the project file or stale logos could attach
                    // to a replacement video at the same path.
                    ArchiveSourceChanged(ImageSidecarPath(id));
                    ArchiveSourceChanged(projectPath);
                    return CreateFreshProject(source, id, loaded.Name, loaded.FileName);
                }
                var regions = NormalizeRegions(loaded.Regions, source.Duration, normalizeStored: true);
                var subtitle = NormalizeSubtitle(loaded.Subtitle);
                return loaded with
                {
                    Schema = CurrentSchema,
                    Source = source,
                    Regions = regions,
                    Subtitle = subtitle,
                    Audio = NormalizeAudio(loaded.Audio),
                    Asr = NormalizeAsr(loaded.Asr),
                    Speech = NormalizeSpeech(loaded.Speech),
                    Tts = subtitle is null ? null : NormalizeTts(loaded.Tts),
                    VoiceOverrides = subtitle is null ? null : NormalizeVoiceOverrides(loaded.VoiceOverrides),
                };
            }
            catch (OperationCanceledException) { throw; }
            catch (SourceChangeArchiveException) { throw; }
            catch (ProjectCorruptArchiveException) { throw; }
            catch (Exception error) when (IsProjectCorruption(error))
            {
                // PROJECT-07: a corrupt primary project invalidates the sidecar that
                // would otherwise be reattached to the fresh project with the same ID.
                // Archive the sidecar first so a failed quarantine cannot produce a
                // half-old/half-new project state.
                ArchiveCorruptState(ImageSidecarPath(id));
                ArchiveCorruptState(projectPath);
                return CreateFreshProject(source, id);
            }
        }

        return CreateFreshProject(source, id);
    }

    private static EditorProject CreateFreshProject(
        EditorSourceFingerprint source,
        string id,
        string? name = null,
        string? fileName = null)
    {
        var defaultName = Path.GetFileNameWithoutExtension(source.Path);
        return new EditorProject(
            CurrentSchema,
            id,
            string.IsNullOrWhiteSpace(name) ? defaultName : name.Trim(),
            source,
            string.IsNullOrWhiteSpace(fileName) ? defaultName + "_edited.mp4" : fileName.Trim(),
            [],
            null,
            DateTimeOffset.UtcNow,
            EditorAudioSettings.Default);
    }

    private static bool SourceFingerprintChanged(EditorSourceFingerprint? previous, EditorSourceFingerprint current)
    {
        if (previous is null || previous.Size <= 0 || previous.LastWriteUtcTicks <= 0
            || previous.Width <= 0 || previous.Height <= 0 || !double.IsFinite(previous.Duration) || previous.Duration < 0)
            return true;
        string previousPath;
        try { previousPath = Path.GetFullPath(previous.Path.Trim()); }
        catch { return true; }
        return !string.Equals(previousPath, current.Path, StringComparison.OrdinalIgnoreCase)
            || previous.Size != current.Size
            || previous.LastWriteUtcTicks != current.LastWriteUtcTicks
            || previous.Width != current.Width
            || previous.Height != current.Height
            || Math.Abs(previous.Duration - current.Duration) > .05;
    }

    private static void ArchiveSourceChanged(string path)
    {
        if (!File.Exists(path)) return;
        var archive = path + ".source-changed-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "-" + Guid.NewGuid().ToString("N");
        Exception? last = null;
        try
        {
            File.Move(path, archive, overwrite: false);
            return;
        }
        catch (Exception error)
        {
            last = error;
        }
        try
        {
            File.Copy(path, archive, overwrite: false);
            File.Delete(path);
            if (!File.Exists(path)) return;
        }
        catch (Exception error)
        {
            last = error;
        }
        throw new SourceChangeArchiveException("Không thể lưu trữ state Editor cũ sau khi video nguồn thay đổi.", last ?? new IOException("Không rõ lỗi lưu trữ source cũ."));
    }

    public async Task SaveAsync(EditorProject project, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.Schema != CurrentSchema) throw new InvalidDataException("Không thể lưu project Editor khác phiên bản.");
        var expectedId = ProjectId(Path.GetFullPath(project.Source.Path));
        if (!string.Equals(project.Id, expectedId, StringComparison.Ordinal)) throw new InvalidDataException("Project Editor không khớp video nguồn.");
        var normalizedSource = Fingerprint(project.Source.Path, project.Source.Width, project.Source.Height, project.Source.Duration);
        if (SourceFingerprintChanged(project.Source, normalizedSource))
            throw new InvalidDataException("Video nguồn đã thay đổi ngoài ứng dụng; hãy mở lại video trước khi tiếp tục lưu project.");
        var subtitle = NormalizeSubtitle(project.Subtitle);
        var normalized = project with
        {
            Source = normalizedSource,
            FileName = string.IsNullOrWhiteSpace(project.FileName) ? project.Name + "_edited.mp4" : project.FileName.Trim(),
            Regions = NormalizeRegions(project.Regions, normalizedSource.Duration, normalizeStored: false),
            Subtitle = subtitle,
            Audio = NormalizeAudio(project.Audio),
            Asr = NormalizeAsr(project.Asr),
            Speech = NormalizeSpeech(project.Speech),
            Tts = subtitle is null ? null : NormalizeTts(project.Tts),
            VoiceOverrides = subtitle is null ? null : NormalizeVoiceOverrides(project.VoiceOverrides),
            UpdatedUtc = DateTimeOffset.UtcNow,
        };

        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_directory);
            var path = ProjectPath(project.Id);
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream, normalized, _json, cancellationToken);
                    await stream.WriteAsync("\n"u8.ToArray(), cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                try { File.Delete(temporary); } catch { }
            }
        }
        finally { _gate.Release(); }
    }

    public string GetProjectPath(string inputPath) => ProjectPath(ProjectId(Path.GetFullPath(inputPath)));

    private static EditorSourceFingerprint Fingerprint(string inputPath, int width, int height, double duration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        var path = Path.GetFullPath(inputPath.Trim());
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= 0) throw new FileNotFoundException("Video nguồn của project Editor không tồn tại.", path);
        if (width <= 0 || height <= 0) throw new InvalidDataException("Kích thước video Editor không hợp lệ.");
        return new EditorSourceFingerprint(path, info.Length, info.LastWriteTimeUtc.Ticks, width, height, Math.Max(0, duration));
    }

    private static IReadOnlyList<EditRegion> NormalizeRegions(IReadOnlyList<EditRegion>? source, double duration, bool normalizeStored)
    {
        if (source is null) return [];
        if (source.Count > 32) throw new InvalidDataException("Project Editor vượt quá 32 vùng.");
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new List<EditRegion>(source.Count);
        foreach (var region in source)
        {
            if (!double.IsFinite(region.X) || !double.IsFinite(region.Y)
                || !double.IsFinite(region.Width) || !double.IsFinite(region.Height)
                || !double.IsFinite(region.Start) || !double.IsFinite(region.End)
                || region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0
                || region.X >= 1 || region.Y >= 1 || region.X + region.Width > 1.0001 || region.Y + region.Height > 1.0001)
                throw new InvalidDataException("Project Editor chứa vùng không hợp lệ.");
            var effect = region.Effect?.Trim().ToLowerInvariant();
            if (effect is not ("blur" or "mosaic" or "cover"))
                throw new InvalidDataException("Project Editor chứa hiệu ứng không hỗ trợ.");
            var identity = string.IsNullOrWhiteSpace(region.Id) ? string.Empty : region.Id.Trim();
            if (identity.Length == 0 || identity.Length > 64 || !identities.Add(identity))
            {
                do { identity = Guid.NewGuid().ToString("N"); }
                while (!identities.Add(identity));
            }
            var normalizedRegion = region with
            {
                Id = identity,
                Effect = effect,
                Strength = effect switch
                {
                    "blur" => EditorBlurStrength.NormalizeStored(region.Strength),
                    "mosaic" => EditorMosaicStrength.NormalizeStored(region.Strength),
                    _ => EditorCoverEffect.NormalizeStored(region.Strength),
                },
            };
            normalized.Add(normalizeStored
                ? EditorRegionTimeScope.NormalizeStored(normalizedRegion, duration)
                : EditorRegionTimeScope.Normalize(normalizedRegion, duration));
        }
        return normalized;
    }

    private static EditorSubtitleProject? NormalizeSubtitle(EditorSubtitleProject? subtitle)
    {
        if (subtitle is null) return null;
        var path = Path.GetFullPath(subtitle.SourcePath.Trim());
        if (subtitle.SourceSize <= 0 || subtitle.SourceLastWriteUtcTicks <= 0 ||
            subtitle.SourceSha256.Length != 64 || subtitle.SourceSha256.Any(x => !Uri.IsHexDigit(x)))
            throw new InvalidDataException("Project Editor chứa nguồn SRT không hợp lệ.");
        if (!EditorSubtitleDocument.SourceFingerprintMatchesCurrent(
                path, subtitle.SourceSize, subtitle.SourceLastWriteUtcTicks, subtitle.SourceSha256)) return null;
        if (subtitle.Cues is null || subtitle.Cues.Count is 0 or > EditorSubtitleDocument.MaxCues)
            throw new InvalidDataException("Project Editor chứa số cue SRT không hợp lệ.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cue in subtitle.Cues)
        {
            if (cue.Id.Length is < 8 or > 64 || !ids.Add(cue.Id) || string.IsNullOrWhiteSpace(cue.Number) ||
                string.IsNullOrWhiteSpace(cue.Timing) || string.IsNullOrWhiteSpace(cue.SourceText) || cue.End <= cue.Start)
                throw new InvalidDataException("Project Editor chứa cue SRT không hợp lệ.");
        }
        var placement = subtitle.Placement ?? EditorSubtitlePlacement.Default;
        if (!double.IsFinite(placement.X) || !double.IsFinite(placement.Y) || !double.IsFinite(placement.Width) || !double.IsFinite(placement.Height) ||
            placement.X < 0 || placement.Y < 0 || placement.Width < .05 || placement.Height < .04 ||
            placement.X + placement.Width > 1.0001 || placement.Y + placement.Height > 1.0001)
            throw new InvalidDataException("Project Editor chứa vị trí phụ đề không hợp lệ.");
        return subtitle with
        {
            SourcePath = path,
            Placement = placement,
            SkillName = subtitle.SkillName?.Trim() ?? string.Empty,
            SkillSha256 = subtitle.SkillSha256?.Trim().ToLowerInvariant() ?? string.Empty,
            OutputPath = subtitle.OutputPath?.Trim() ?? string.Empty,
        };
    }

    public static EditorAudioSettings NormalizeAudio(EditorAudioSettings? audio)
    {
        if (audio is null) return EditorAudioSettings.Default;
        var mode = audio.SourceMode?.Trim().ToLowerInvariant();
        if (mode is not ("keep" or "duck" or "mute"))
            throw new InvalidDataException("Project Editor chứa chế độ âm thanh không hợp lệ.");
        if (!double.IsFinite(audio.SourceGain))
            throw new InvalidDataException("Project Editor chứa mức âm thanh không hợp lệ.");
        return mode switch
        {
            "keep" => new EditorAudioSettings("keep", 1),
            "mute" => new EditorAudioSettings("mute", 0),
            _ => new EditorAudioSettings("duck", Math.Clamp(audio.SourceGain, .05, .95)),
        };
    }

    private static EditorAsrProject? NormalizeAsr(EditorAsrProject? asr)
    {
        if (asr is null) return null;
        var status = asr.Status?.Trim().ToLowerInvariant();
        var device = asr.Device?.Trim().ToLowerInvariant();
        var compute = asr.ComputeType?.Trim().ToLowerInvariant();
        if (status is not ("complete" or "partial") || device is not ("cpu" or "cuda") || string.IsNullOrWhiteSpace(compute)
            || string.IsNullOrWhiteSpace(asr.ModelName) || asr.ModelRevision?.Length != 40 || asr.ModelRevision.Any(x => !Uri.IsHexDigit(x))
            || asr.CueCount is < 0 or > EditorSubtitleDocument.MaxCues || !double.IsFinite(asr.ProbeRealtimeFactor) || asr.ProbeRealtimeFactor <= 0)
            throw new InvalidDataException("Project Editor chứa trạng thái ASR không hợp lệ.");
        var output = string.IsNullOrWhiteSpace(asr.OutputPath) ? string.Empty : Path.GetFullPath(asr.OutputPath.Trim());
        if (status == "complete" && (output.Length == 0 || !File.Exists(output))) return null;
        return asr with
        {
            Status = status,
            ModelName = asr.ModelName.Trim(),
            ModelRevision = asr.ModelRevision.ToLowerInvariant(),
            Device = device,
            ComputeType = compute,
            OutputPath = output,
        };
    }

    private static EditorSpeechProject? NormalizeSpeech(EditorSpeechProject? speech)
    {
        if (speech is null) return null;
        var status = speech.Status?.Trim().ToLowerInvariant();
        var device = speech.Device?.Trim().ToLowerInvariant();
        var compute = speech.ComputeType?.Trim().ToLowerInvariant();
        if (status is not ("complete" or "partial") || device is not ("cpu" or "cuda") || string.IsNullOrWhiteSpace(compute)
            || string.IsNullOrWhiteSpace(speech.ModelName) || speech.ModelRevision?.Length != 40 || speech.ModelRevision.Any(x => !Uri.IsHexDigit(x))
            || speech.AnalysisSha256?.Length != 64 || speech.AnalysisSha256.Any(x => !Uri.IsHexDigit(x))
            || speech.SegmentCount is < 0 or > EditorSubtitleDocument.MaxCues || speech.WordCount < 0
            || !double.IsFinite(speech.ProbeRealtimeFactor) || speech.ProbeRealtimeFactor <= 0)
            throw new InvalidDataException("Project Editor chứa trạng thái Whisper timing không hợp lệ.");
        var analysisPath = string.IsNullOrWhiteSpace(speech.AnalysisPath) ? string.Empty : Path.GetFullPath(speech.AnalysisPath.Trim());
        if (status == "complete" && (analysisPath.Length == 0 || !File.Exists(analysisPath) || !FileShaMatches(analysisPath, speech.AnalysisSha256))) return null;
        return speech with
        {
            Status = status,
            ModelName = speech.ModelName.Trim(),
            ModelRevision = speech.ModelRevision.ToLowerInvariant(),
            Device = device,
            ComputeType = compute,
            AnalysisPath = analysisPath,
            AnalysisSha256 = speech.AnalysisSha256.ToLowerInvariant(),
        };
    }

    private static EditorTtsProject? NormalizeTts(EditorTtsProject? tts)
    {
        if (tts is null) return null;
        var status = tts.Status?.Trim().ToLowerInvariant();
        if (status is not ("complete" or "partial") || string.IsNullOrWhiteSpace(tts.Engine) || string.IsNullOrWhiteSpace(tts.EngineVersion)
            || string.IsNullOrWhiteSpace(tts.MaleVoice) || string.IsNullOrWhiteSpace(tts.FemaleVoice)
            || tts.ManifestSha256?.Length != 64 || tts.ManifestSha256.Any(x => !Uri.IsHexDigit(x))
            || tts.CueCount is < 0 or > EditorSubtitleDocument.MaxCues || tts.ReviewCount is < 0 || tts.ReviewCount > tts.CueCount)
            throw new InvalidDataException("Project Editor chứa trạng thái voice Việt không hợp lệ.");
        var engine = tts.Engine.Trim();
        var engineVersion = tts.EngineVersion.Trim();
        var maleVoice = tts.MaleVoice.Trim();
        var femaleVoice = tts.FemaleVoice.Trim();
        if (!string.Equals(engine, CurrentTtsEngine, StringComparison.Ordinal)
            || !string.Equals(engineVersion, LocalTtsInstaller.EngineVersion, StringComparison.Ordinal)
            || !string.Equals(maleVoice, LocalTtsInstaller.Voice, StringComparison.Ordinal)
            || !string.Equals(femaleVoice, LocalTtsInstaller.Voice, StringComparison.Ordinal))
            return null;
        var manifest = string.IsNullOrWhiteSpace(tts.ManifestPath) ? string.Empty : Path.GetFullPath(tts.ManifestPath.Trim());
        var track = tts.VoiceTrack;
        if (track is null || string.IsNullOrWhiteSpace(track.Path) || !double.IsFinite(track.Start) || track.Start < 0
            || !double.IsFinite(track.Duration) || track.Duration <= 0 || !double.IsFinite(track.Gain) || track.Gain is < 0 or > 4)
            return null;
        var trackPath = Path.GetFullPath(track.Path.Trim());
        if (status == "complete" && (manifest.Length == 0 || !File.Exists(manifest) || !FileShaMatches(manifest, tts.ManifestSha256)
            || !File.Exists(trackPath) || new FileInfo(trackPath).Length <= 64)) return null;
        return tts with
        {
            Status = status,
            Engine = engine,
            EngineVersion = engineVersion,
            MaleVoice = maleVoice,
            FemaleVoice = femaleVoice,
            ManifestPath = manifest,
            ManifestSha256 = tts.ManifestSha256.ToLowerInvariant(),
            VoiceTrack = track with { Path = trackPath, Gain = Math.Clamp(track.Gain, 0, 4) },
        };
    }

    private static IReadOnlyDictionary<string, string> NormalizeVoiceOverrides(IReadOnlyDictionary<string, string>? source)
    {
        if (source is null || source.Count == 0) return new Dictionary<string, string>(StringComparer.Ordinal);
        if (source.Count > EditorSubtitleDocument.MaxCues) throw new InvalidDataException("Project Editor có quá nhiều override voice.");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in source)
        {
            var id = pair.Key?.Trim() ?? string.Empty;
            var voice = pair.Value?.Trim().ToLowerInvariant() ?? string.Empty;
            if (id.Length is < 8 or > 64 || voice is not ("male" or "female")) continue;
            result[id] = voice;
        }
        return result;
    }

    private static bool FileShaMatches(string path, string expected)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return string.Equals(Convert.ToHexStringLower(SHA256.HashData(stream)), expected, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string ProjectId(string inputPath)
    {
        var normalized = Path.GetFullPath(inputPath).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant()[..24];
    }

    private string ProjectPath(string id) => Path.Combine(_directory, id + ".json");
    private string ImageSidecarPath(string id) => Path.Combine(_directory, id + ".images.json");

    private sealed class SourceChangeArchiveException : IOException
    {
        public SourceChangeArchiveException(string message, Exception innerException) : base(message, innerException) { }
    }

    private sealed class ProjectCorruptArchiveException : IOException
    {
        public ProjectCorruptArchiveException(string message, Exception innerException) : base(message, innerException) { }
    }

    private static bool IsProjectCorruption(Exception error) =>
        error is JsonException
            or InvalidDataException
            or ArgumentException
            or FormatException
            or OverflowException;

    private static void ArchiveCorruptState(string path)
    {
        if (!File.Exists(path)) return;
        var archive = path + ".corrupt-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "-" + Guid.NewGuid().ToString("N");
        Exception? last = null;
        try
        {
            File.Move(path, archive, overwrite: false);
            return;
        }
        catch (Exception error)
        {
            last = error;
        }
        try
        {
            File.Copy(path, archive, overwrite: false);
            File.Delete(path);
            if (!File.Exists(path)) return;
        }
        catch (Exception error)
        {
            last = error;
        }
        throw new ProjectCorruptArchiveException(
            "Không thể cách ly project Editor bị hỏng; dữ liệu hiện tại được giữ nguyên để tránh ghi đè ngoài ý muốn.",
            last ?? new IOException("Không rõ lỗi cách ly project Editor bị hỏng."));
    }
}

public sealed class EditorRegionDocument
{
    private sealed record Snapshot(IReadOnlyList<EditRegion> Regions, int SelectedIndex);
    private readonly Stack<Snapshot> _undo = new();
    private readonly Stack<Snapshot> _redo = new();
    private readonly List<EditRegion> _regions = [];

    public IReadOnlyList<EditRegion> Regions => _regions;
    public int SelectedIndex { get; private set; } = -1;
    public EditRegion? Selected => SelectedIndex >= 0 && SelectedIndex < _regions.Count ? _regions[SelectedIndex] : null;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Reset(IEnumerable<EditRegion> regions)
    {
        _regions.Clear();
        _regions.AddRange(regions.Select(EnsureIdentity));
        SelectedIndex = _regions.Count == 0 ? -1 : 0;
        _undo.Clear();
        _redo.Clear();
    }

    public void Select(int index) => SelectedIndex = index >= 0 && index < _regions.Count ? index : -1;

    public void BeginChange()
    {
        _undo.Push(new Snapshot(_regions.ToArray(), SelectedIndex));
        while (_undo.Count > 50)
        {
            var ordered = _undo.Reverse().Skip(1).ToArray();
            _undo.Clear();
            foreach (var item in ordered) _undo.Push(item);
        }
        _redo.Clear();
    }

    public void Add(EditRegion region)
    {
        BeginChange();
        _regions.Add(EnsureIdentity(region));
        SelectedIndex = _regions.Count - 1;
    }

    public bool ReplaceSelected(EditRegion region, bool capture = true)
    {
        if (SelectedIndex < 0 || SelectedIndex >= _regions.Count) return false;
        var replacement = EnsureIdentity(region with { Id = _regions[SelectedIndex].Id });
        if (replacement == _regions[SelectedIndex]) return false;
        if (capture) BeginChange();
        _regions[SelectedIndex] = replacement;
        return true;
    }

    public bool RemoveSelected()
    {
        if (SelectedIndex < 0 || SelectedIndex >= _regions.Count) return false;
        BeginChange();
        _regions.RemoveAt(SelectedIndex);
        if (_regions.Count == 0) SelectedIndex = -1;
        else if (SelectedIndex >= _regions.Count) SelectedIndex = _regions.Count - 1;
        return true;
    }

    public bool Undo() => Restore(_undo, _redo);
    public bool Redo() => Restore(_redo, _undo);

    public bool CancelChange()
    {
        if (_undo.Count == 0) return false;
        var snapshot = _undo.Pop();
        _regions.Clear();
        _regions.AddRange(snapshot.Regions);
        SelectedIndex = snapshot.SelectedIndex >= 0 && snapshot.SelectedIndex < _regions.Count
            ? snapshot.SelectedIndex
            : _regions.Count - 1;
        return true;
    }

    private bool Restore(Stack<Snapshot> source, Stack<Snapshot> destination)
    {
        if (source.Count == 0) return false;
        destination.Push(new Snapshot(_regions.ToArray(), SelectedIndex));
        var snapshot = source.Pop();
        _regions.Clear();
        _regions.AddRange(snapshot.Regions);
        SelectedIndex = snapshot.SelectedIndex >= 0 && snapshot.SelectedIndex < _regions.Count
            ? snapshot.SelectedIndex
            : _regions.Count - 1;
        return true;
    }

    private static EditRegion EnsureIdentity(EditRegion region) => region with
    {
        Id = string.IsNullOrWhiteSpace(region.Id) ? Guid.NewGuid().ToString("N") : region.Id.Trim(),
    };
}
