using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using BiliSubStudio.Core.Configuration;

namespace BiliSubStudio.Core.Ocr;

internal sealed record OcrScanMode(double Fps, double Guard, double ActiveGuard, double DiffTrigger, double LowConfidence);
internal sealed record OcrScanSegment(int Index, double CoreStart, double CoreEnd, double ScanStart, double ScanEnd);
internal sealed record OcrLaneCheckpoint(OcrScanSegment Segment, double MediaSeconds, List<OcrCue> Cues, OcrCue? Active, int Frames, int OcrImages, bool Completed);
internal sealed record OcrParallelCheckpoint(int Schema, string Key, int SelectedParallelism, List<OcrLaneCheckpoint> Lanes, int BoundaryMerges = 0);

internal sealed class OcrCheckpointStore
{
    private const int Schema = 4;
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
        if (!File.Exists(path)) return null;
        try
        {
            var checkpoint = JsonSerializer.Deserialize<OcrParallelCheckpoint>(await File.ReadAllTextAsync(path, cancellationToken), JsonOptions);
            if (checkpoint is null || checkpoint.Schema != Schema || checkpoint.Key != key || !IsValidCheckpoint(checkpoint, request.Duration))
                return null;
            return checkpoint;
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    public async Task SaveAsync(OcrScanRequest request, OcrParallelCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        var key = await KeyAsync(request, Schema, cancellationToken);
        if (checkpoint.Schema != Schema || checkpoint.Key != key) throw new InvalidDataException("Checkpoint OCR schema 4 không hợp lệ.");
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
            File.Move(temporary, path, overwrite: true);
        }
        finally { TryDelete(temporary); }
    }

    public async Task<OcrCheckpointInfo> InspectAsync(OcrScanRequest request, CancellationToken cancellationToken)
    {
        var checkpoint = await LoadAsync(request, cancellationToken);
        if (checkpoint is null) return new OcrCheckpointInfo(false, 0, 0, 0, 0, 0, 0, 0, []);
        var completed = checkpoint.Lanes.Count(x => x.Completed);
        var media = ContiguousFrontier(checkpoint.Lanes);
        var cues = checkpoint.Lanes.SelectMany(x => x.Cues.Concat(x.Active is { } active ? new[] { active } : Array.Empty<OcrCue>())).OrderBy(x => x.Start).ToArray();
        return new OcrCheckpointInfo(
            true, Schema, media, cues.Length, checkpoint.SelectedParallelism, completed, checkpoint.Lanes.Count,
            request.Duration > 0 ? Math.Clamp(media / request.Duration * 100, 0, 100) : 0,
            cues.TakeLast(120).ToArray());
    }

    public async Task RemoveAsync(OcrScanRequest request, CancellationToken cancellationToken)
    {
        foreach (var schema in new[] { 4, 3 })
        {
            var key = await KeyAsync(request, schema, cancellationToken);
            TryDelete(Path.Combine(DirectoryPath, key + ".json"));
        }
    }

    public async Task<OcrParallelCheckpoint> NewAsync(OcrScanRequest request, int parallelism, CancellationToken cancellationToken)
    {
        var scanMode = ModeFor(request.Mode, request.Sensitivity);
        var overlap = Math.Max(scanMode.Guard, scanMode.ActiveGuard);
        var segments = BuildSegments(request.Duration, parallelism, overlap);
        var key = await KeyAsync(request, Schema, cancellationToken);
        return new OcrParallelCheckpoint(Schema, key, segments.Count,
            segments.Select(x => new OcrLaneCheckpoint(x, x.ScanStart, [], null, 0, 0, false)).ToList());
    }

    public static IReadOnlyList<OcrScanSegment> BuildSegments(double duration, int parallelism, double overlap)
    {
        if (duration <= 0 || double.IsNaN(duration) || double.IsInfinity(duration)) throw new ArgumentException("Thời lượng video không hợp lệ.");
        parallelism = Math.Clamp(parallelism, 1, Math.Min(16, Math.Max(1, (int)Math.Floor(duration / 120))));
        var output = new List<OcrScanSegment>();
        for (var index = 0; index < parallelism; index++)
        {
            var coreStart = duration * index / parallelism;
            var coreEnd = duration * (index + 1) / parallelism;
            output.Add(new OcrScanSegment(index, coreStart, coreEnd, Math.Max(0, coreStart - overlap), Math.Min(duration, coreEnd + overlap)));
        }
        return output;
    }

    public static OcrScanMode ModeFor(string mode, double sensitivity)
    {
        var result = mode.Trim().ToLowerInvariant() switch
        {
            "accurate" or "precise" or "chinh-xac" => new OcrScanMode(4, 3, 12, 0.10, 0.68),
            "fast" or "nhanh" => new OcrScanMode(1.5, 8, 24, 0.22, 0.58),
            _ => new OcrScanMode(2.5, 5, 16, 0.16, 0.62),
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
        var identity = new CheckpointIdentity(
            schema, path, file.Length, (file.LastWriteTimeUtc - DateTime.UnixEpoch).Ticks * 100,
            region, request.Mode.Trim().ToLowerInvariant(), mode.Fps, mode.Guard, mode.ActiveGuard, mode.DiffTrigger, 1280, 320);
        var json = JsonSerializer.SerializeToUtf8Bytes(identity, JsonOptions);
        return Convert.ToHexStringLower(SHA256.HashData(json));
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

    private static double ContiguousFrontier(IReadOnlyList<OcrLaneCheckpoint> lanes)
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

    private static bool IsValidCheckpoint(OcrParallelCheckpoint checkpoint, double duration)
    {
        if (checkpoint.SelectedParallelism is < 1 or > 16 || checkpoint.Lanes is null ||
            checkpoint.Lanes.Count != checkpoint.SelectedParallelism ||
            checkpoint.Lanes.Any(lane => lane is null || lane.Segment is null || lane.Cues is null) ||
            !double.IsFinite(duration) || duration <= 0)
            return false;

        var ordered = checkpoint.Lanes.OrderBy(x => x.Segment.Index).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var lane = ordered[index];
            var segment = lane.Segment;
            if (segment is null || lane.Cues is null || segment.Index != index || !double.IsFinite(segment.CoreStart) || !double.IsFinite(segment.CoreEnd) ||
                !double.IsFinite(segment.ScanStart) || !double.IsFinite(segment.ScanEnd) ||
                segment.CoreStart < 0 || segment.CoreEnd <= segment.CoreStart || segment.CoreEnd > duration + 0.001 ||
                segment.ScanStart < 0 || segment.ScanStart > segment.CoreStart ||
                segment.ScanEnd < segment.CoreEnd || segment.ScanEnd > duration + 0.001 ||
                !double.IsFinite(lane.MediaSeconds) || lane.MediaSeconds < segment.ScanStart - 0.001 || lane.MediaSeconds > segment.ScanEnd + 0.001 ||
                lane.Frames < 0 || lane.OcrImages < 0 || lane.OcrImages > lane.Frames ||
                (index > 0 && Math.Abs(ordered[index - 1].Segment.CoreEnd - segment.CoreStart) > 0.001) ||
                lane.Cues.Any(cue => cue is null || !IsValidCue(cue)) || lane.Active is { } active && !IsValidCue(active))
                return false;
        }
        return Math.Abs(ordered[0].Segment.CoreStart) <= 0.001 && Math.Abs(ordered[^1].Segment.CoreEnd - duration) <= 0.001;
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
        [property: JsonPropertyName("width")] int Width,
        [property: JsonPropertyName("height")] int Height);
}
