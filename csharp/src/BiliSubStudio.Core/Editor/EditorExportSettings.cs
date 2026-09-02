using System.Globalization;
using BiliSubStudio.Core.Processes;

namespace BiliSubStudio.Core.Editor;

public sealed record EditorExportSettings(
    string Codec = "h264",
    string Quality = "high",
    int? TargetHeight = null,
    double? FrameRate = null,
    bool PreferHardwareAcceleration = true,
    int AudioBitrateKbps = 192)
{
    public static EditorExportSettings Default { get; } = new();
}

public sealed record EditorExportDimensions(int Width, int Height);

public sealed record EditorResolvedVideoEncoder(string Name, bool HardwareAccelerated);

public sealed record EditorExportVideoGraph(string FilterGraph, string OutputLabel, EditorExportDimensions Dimensions);

public static class EditorExportPolicy
{
    private static readonly HashSet<int> AllowedTargetHeights = [480, 720, 1080, 2160];
    private static readonly double[] AllowedFrameRates = [24, 25, 30, 50, 60];
    private static readonly HashSet<int> AllowedAudioBitrates = [128, 192, 256, 320];

    public static EditorExportSettings Normalize(EditorExportSettings? settings)
    {
        var value = settings ?? EditorExportSettings.Default;
        var codec = value.Codec.Trim().ToLowerInvariant() switch
        {
            "h264" or "avc" => "h264",
            "h265" or "hevc" => "hevc",
            _ => throw new InvalidDataException("Mã hóa video phải là H.264 hoặc H.265/HEVC."),
        };
        var quality = value.Quality.Trim().ToLowerInvariant() switch
        {
            "high" => "high",
            "standard" => "standard",
            "compact" => "compact",
            _ => throw new InvalidDataException("Mức chất lượng xuất video không hợp lệ."),
        };
        if (value.TargetHeight is int targetHeight && !AllowedTargetHeights.Contains(targetHeight))
            throw new InvalidDataException("Độ phân giải xuất video không được hỗ trợ.");
        if (value.FrameRate is double frameRate
            && (!double.IsFinite(frameRate) || !AllowedFrameRates.Any(allowed => Math.Abs(allowed - frameRate) < .001)))
            throw new InvalidDataException("FPS xuất video không được hỗ trợ.");
        if (!AllowedAudioBitrates.Contains(value.AudioBitrateKbps))
            throw new InvalidDataException("Bitrate âm thanh không được hỗ trợ.");
        return value with { Codec = codec, Quality = quality };
    }

    public static EditorExportDimensions ResolveDimensions(EditorExportSettings? settings, int sourceWidth, int sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
            throw new InvalidDataException("Kích thước video nguồn không hợp lệ.");
        var normalized = Normalize(settings);
        if (normalized.TargetHeight is not int targetHeight || targetHeight == sourceHeight)
            return new EditorExportDimensions(sourceWidth, sourceHeight);

        var scaledWidth = Math.Max(2, (int)Math.Round(sourceWidth * targetHeight / (double)sourceHeight));
        if ((scaledWidth & 1) != 0) scaledWidth++;
        var evenHeight = (targetHeight & 1) == 0 ? targetHeight : targetHeight + 1;
        return new EditorExportDimensions(scaledWidth, evenHeight);
    }

    public static EditorExportVideoGraph BuildVideoGraph(
        string graph,
        EditorExportSettings? settings,
        int sourceWidth,
        int sourceHeight)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graph);
        var normalized = Normalize(settings);
        var dimensions = ResolveDimensions(normalized, sourceWidth, sourceHeight);
        var filters = new List<string>();
        if (dimensions.Width != sourceWidth || dimensions.Height != sourceHeight)
            filters.Add($"scale={dimensions.Width}:{dimensions.Height}:flags=lanczos");
        if (normalized.FrameRate is double frameRate)
            filters.Add("fps=" + frameRate.ToString("0.###", CultureInfo.InvariantCulture));
        if (filters.Count == 0) return new EditorExportVideoGraph(graph, "[vout]", dimensions);
        return new EditorExportVideoGraph(
            graph + ";[vout]" + string.Join(',', filters) + "[exportv]",
            "[exportv]",
            dimensions);
    }

    public static IReadOnlyList<string> BuildVideoEncoderArguments(
        EditorExportSettings? settings,
        EditorResolvedVideoEncoder encoder,
        bool mp4)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        var normalized = Normalize(settings);
        var quality = QualityValue(normalized);
        var arguments = new List<string> { "-c:v", encoder.Name };
        if (encoder.HardwareAccelerated)
        {
            arguments.AddRange(["-preset", "p5", "-tune", "hq", "-rc", "vbr", "-cq", quality.ToString(CultureInfo.InvariantCulture), "-b:v", "0"]);
        }
        else
        {
            arguments.AddRange(["-preset", "medium", "-crf", quality.ToString(CultureInfo.InvariantCulture)]);
        }
        arguments.AddRange(["-pix_fmt", "yuv420p"]);
        if (mp4 && normalized.Codec == "hevc") arguments.AddRange(["-tag:v", "hvc1"]);
        return arguments;
    }

    public static async Task<EditorResolvedVideoEncoder> ResolveEncoderAsync(
        string ffmpeg,
        ProcessRunner processes,
        EditorExportSettings? settings,
        CancellationToken cancellationToken,
        Action<string>? diagnostic = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpeg);
        ArgumentNullException.ThrowIfNull(processes);
        var normalized = Normalize(settings);
        if (normalized.PreferHardwareAcceleration)
        {
            var hardware = normalized.Codec == "hevc" ? "hevc_nvenc" : "h264_nvenc";
            var hardwareProbe = await ProbeEncoderAsync(ffmpeg, processes, hardware, cancellationToken);
            if (hardwareProbe.Success)
            {
                diagnostic?.Invoke($"Encoder: {hardware} (NVIDIA NVENC).");
                return new EditorResolvedVideoEncoder(hardware, true);
            }
            diagnostic?.Invoke("NVENC không khả dụng, tự động chuyển sang CPU: " + hardwareProbe.Error);
        }

        var software = normalized.Codec == "hevc" ? "libx265" : "libx264";
        var softwareProbe = await ProbeEncoderAsync(ffmpeg, processes, software, cancellationToken);
        if (!softwareProbe.Success)
            throw new NotSupportedException($"FFmpeg hiện tại không dùng được encoder {software}: {softwareProbe.Error}");
        diagnostic?.Invoke($"Encoder: {software} (CPU).");
        return new EditorResolvedVideoEncoder(software, false);
    }

    private static int QualityValue(EditorExportSettings settings) => (settings.Codec, settings.Quality) switch
    {
        ("h264", "high") => 18,
        ("h264", "standard") => 21,
        ("h264", "compact") => 24,
        ("hevc", "high") => 20,
        ("hevc", "standard") => 24,
        ("hevc", "compact") => 28,
        _ => throw new InvalidDataException("Không ánh xạ được chất lượng video."),
    };

    private static async Task<(bool Success, string Error)> ProbeEncoderAsync(
        string ffmpeg,
        ProcessRunner processes,
        string encoder,
        CancellationToken cancellationToken)
    {
        var result = await processes.RunAsync(ffmpeg,
        [
            "-hide_banner", "-loglevel", "error", "-nostdin",
            "-f", "lavfi", "-i", "color=c=black:s=256x256:d=0.04",
            "-frames:v", "1", "-an", "-c:v", encoder, "-pix_fmt", "yuv420p",
            "-f", "null", "-",
        ], cancellationToken);
        var error = result.StandardError.Trim();
        if (error.Length > 240) error = error[..240] + "…";
        return (result.ExitCode == 0, error.Length == 0 ? "encoder từ chối bài kiểm tra" : error);
    }
}
