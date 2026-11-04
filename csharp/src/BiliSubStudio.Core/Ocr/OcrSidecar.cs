using System.Text.Json;
using System.Text.Json.Serialization;

namespace BiliSubStudio.Core.Ocr;

// Sidecar beside the video (Adobe .pek style): data DERIVED FROM THE FILE (not tied to config),
// computed once, used by many scans. Deleting this file still lets the scan run (recomputed as before).
// Iron rules:
// - Only contains things derived from file contents + version section. Checkpoint (tied to ROI/mode)
//   stays put in Data, never enters here.
// - Any mismatch (file stamp, version, corrupt JSON) => silently ignored, recompute via the old path.
// - Path is only for display: matching uses Size+ModUnixNano so renaming/moving drives still hits.
public static class OcrSidecar
{
    public const int ProbeVersion = 1;
    public const int VadVersion = 1;
    public const int PixelVersion = 1;

    public static string FileNameFor(string videoFileName) => videoFileName + ".ocr-sidecar.json";

    public sealed record FileIdentity(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("mtime_ns")] long ModUnixNano)
    {
        public static FileIdentity? FromFile(string videoPath)
        {
            try
            {
                var full = System.IO.Path.GetFullPath(videoPath.Trim());
                var info = new System.IO.FileInfo(full);
                if (!info.Exists || info.Length <= 0) return null;
                return new FileIdentity(full, info.Length, (info.LastWriteTimeUtc - DateTime.UnixEpoch).Ticks * 100);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return null;
            }
        }

        public bool Matches(FileIdentity other) => Size == other.Size && ModUnixNano == other.ModUnixNano;
    }

    public sealed record ProbeSection(
        [property: JsonPropertyName("v")] int V,
        [property: JsonPropertyName("w")] int Width,
        [property: JsonPropertyName("h")] int Height,
        [property: JsonPropertyName("tb_s")] double TimeBaseSeconds,
        [property: JsonPropertyName("frame_dur")] double FrameDurationSeconds,
        [property: JsonPropertyName("fps")] double AvgFps,
        [property: JsonPropertyName("start")] double StartTimeSeconds,
        [property: JsonPropertyName("tb_num")] int TimeBaseNum,
        [property: JsonPropertyName("tb_den")] int TimeBaseDen,
        [property: JsonPropertyName("stream_dur")] double StreamDurationSeconds);

    public sealed record VadSection(
        [property: JsonPropertyName("v")] int V,
        [property: JsonPropertyName("frame_s")] double FrameSeconds,
        [property: JsonPropertyName("count")] int Count,
        [property: JsonPropertyName("flags_b64")] string FlagsBase64);

    public sealed record PixelEntry(
        [property: JsonPropertyName("a")] long First,
        [property: JsonPropertyName("b")] long Second,
        [property: JsonPropertyName("l")] int Left,
        [property: JsonPropertyName("t")] int Top,
        [property: JsonPropertyName("r")] int Right,
        [property: JsonPropertyName("bot")] int Bottom,
        [property: JsonPropertyName("v")] int Verdict);

    public sealed record PixelSection(
        [property: JsonPropertyName("v")] int V,
        [property: JsonPropertyName("entries")] List<PixelEntry> Entries);

    public sealed record SidecarDocument(
        [property: JsonPropertyName("identity")] FileIdentity Identity,
        [property: JsonPropertyName("probe")] ProbeSection? Probe,
        [property: JsonPropertyName("vad")] VadSection? Vad,
        [property: JsonPropertyName("pxv")] PixelSection? Pixel);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static ProbeSection FromProbe(VideoProbe probe) => new(
        ProbeVersion, probe.Width, probe.Height, probe.TimeBaseSeconds, probe.FrameDurationSeconds,
        probe.AvgFps, probe.StartTimeSeconds, probe.TimeBaseNum, probe.TimeBaseDen, probe.StreamDurationSeconds);

    public static VideoProbe? ToProbe(ProbeSection? section)
    {
        if (section is null || section.V != ProbeVersion) return null;
        if (section.Width <= 0 || section.Height <= 0 || section.TimeBaseNum <= 0 || section.TimeBaseDen <= 0) return null;
        if (!double.IsFinite(section.AvgFps) || !double.IsFinite(section.StartTimeSeconds) || !double.IsFinite(section.StreamDurationSeconds)) return null;
        return new VideoProbe(section.Width, section.Height, section.TimeBaseSeconds, section.FrameDurationSeconds,
            section.AvgFps, section.StartTimeSeconds, section.TimeBaseNum, section.TimeBaseDen, section.StreamDurationSeconds);
    }

    // VadFlag 3 values (0/1/2) -> 2 bits per flag, 4 flags per byte. Trailing bits must be 0 (the encoder writes 0):
    // garbage bits mean a corrupt file, throw so the caller skips the whole section.
    public static byte[] PackVadFlags(IReadOnlyList<VadFlag> flags)
    {
        var bytes = new byte[(flags.Count + 3) / 4];
        for (var i = 0; i < flags.Count; i++)
        {
            var value = (int)flags[i];
            if (value < 0 || value > 2) throw new InvalidDataException($"VadFlag ngoài biên: {value}.");
            bytes[i / 4] |= (byte)(value << ((i % 4) * 2));
        }
        return bytes;
    }

    public static VadFlag[] UnpackVadFlags(byte[] bytes, int count)
    {
        if (count < 0 || bytes.Length != (count + 3) / 4)
            throw new InvalidDataException($"VadSection sai kích thước: {bytes.Length}B cho {count} cờ.");
        var flags = new VadFlag[count];
        for (var i = 0; i < count; i++)
        {
            var value = (bytes[i / 4] >> ((i % 4) * 2)) & 3;
            if (value > 2) throw new InvalidDataException($"VadFlag hỏng ở khung {i}: {value}.");
            flags[i] = (VadFlag)value;
        }
        return flags;
    }

    public static VadSection FromTimeline(VadTimeline timeline)
    {
        var packed = PackVadFlags(timeline.Flags);
        return new VadSection(VadVersion, timeline.FrameSeconds, timeline.Flags.Length, Convert.ToBase64String(packed));
    }

    public static VadTimeline? ToTimeline(VadSection? section)
    {
        if (section is null || section.V != VadVersion) return null;
        if (section.FrameSeconds <= 0 || !double.IsFinite(section.FrameSeconds) || section.Count < 0) return null;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(section.FlagsBase64); }
        catch (FormatException) { return null; }
        try { return new VadTimeline(section.FrameSeconds, UnpackVadFlags(bytes, section.Count)); }
        catch (InvalidDataException) { return null; }
    }

    // Only CLEAR verdicts (Same/Different) are worth persisting: Unavailable is "no conclusion",
    // recompute next time (it may be a transient FFmpeg error) rather than pinning "separate" + review.
    public static PixelEntry? ToEntry(long first, long second, System.Drawing.Rectangle crop, PixelCheckResult verdict)
    {
        if (verdict != PixelCheckResult.Same && verdict != PixelCheckResult.Different) return null;
        if (crop.Right <= crop.Left || crop.Bottom <= crop.Top) return null;
        return new PixelEntry(first, second, crop.Left, crop.Top, crop.Right, crop.Bottom,
            verdict == PixelCheckResult.Same ? 0 : 1);
    }

    public static (System.Drawing.Rectangle Crop, PixelCheckResult Verdict)? FromEntry(PixelEntry entry)
    {
        if (entry.Verdict is < 0 or > 1) return null;
        if (entry.Right <= entry.Left || entry.Bottom <= entry.Top) return null;
        if (entry.Left < 0 || entry.Top < 0) return null;
        return (System.Drawing.Rectangle.FromLTRB(entry.Left, entry.Top, entry.Right, entry.Bottom),
            entry.Verdict == 0 ? PixelCheckResult.Same : PixelCheckResult.Different);
    }

    public static string Serialize(SidecarDocument document)
        => JsonSerializer.Serialize(document, JsonOptions);

    // Corrupt anywhere => null (caller skips it, recomputes via the old path). Never throws.
    public static SidecarDocument? Parse(string json)
    {
        try
        {
            var document = JsonSerializer.Deserialize<SidecarDocument>(json, JsonOptions);
            if (document?.Identity is null || document.Identity.Size <= 0 || document.Identity.ModUnixNano <= 0) return null;
            if (string.IsNullOrWhiteSpace(document.Identity.Path)) return null;
            return document;
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or ArgumentException or InvalidDataException or FormatException or OverflowException)
        {
            return null;
        }
    }
}
