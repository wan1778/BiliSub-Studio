using System.Text.Json;
using BiliSubStudio.Core.Processes;
using BiliSubStudio.Core.Tools;

namespace BiliSubStudio.Core.Media;

public sealed record MediaPreviewInfo(
    int Width,
    int Height,
    double Duration,
    string Codec,
    string Container,
    bool DirectCompatible,
    double FrameRate = 0);

public sealed class MediaPreviewService
{
    private readonly ToolManager _tools;
    private readonly ProcessRunner _processes;

    public MediaPreviewService(ToolManager tools, ProcessRunner processes)
    {
        _tools = tools;
        _processes = processes;
    }

    public async Task<MediaPreviewInfo> ProbeAsync(string inputPath, CancellationToken cancellationToken)
    {
        var input = ValidateInput(inputPath);
        var ffprobe = await _tools.EnsureFfprobeAsync(cancellationToken);
        var result = await _processes.RunAsync(ffprobe,
        [
            "-v", "error", "-select_streams", "v:0",
            "-show_entries", "stream=codec_name,codec_type,width,height,avg_frame_rate,r_frame_rate:format=duration,format_name",
            "-of", "json", input,
        ], cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Đọc thông tin video: {result.StandardError.Trim()}");
        }
        return ParseProbe(result.StandardOutput, Path.GetExtension(input));
    }

    public async Task<byte[]> GetFrameJpegAsync(string inputPath, double seconds, CancellationToken cancellationToken)
    {
        var input = ValidateInput(inputPath);
        var ffmpeg = await _tools.EnsureFfmpegAsync(cancellationToken);
        var frame = await _processes.CaptureBytesAsync(ffmpeg,
        [
            "-hide_banner", "-loglevel", "error", "-nostdin", "-ss", Math.Max(0, seconds).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture),
            "-i", input, "-map", "0:v:0", "-an", "-sn", "-dn", "-frames:v", "1",
            "-vf", "scale=1280:-2:force_original_aspect_ratio=decrease", "-q:v", "4", "-c:v", "mjpeg", "-f", "image2pipe", "pipe:1",
        ], cancellationToken);
        return frame.Length > 0 ? frame : throw new InvalidDataException("Frame preview rỗng.");
    }

    public static MediaPreviewInfo ParseProbe(string json, string extension)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var format = root.TryGetProperty("format", out var formatNode) ? formatNode : default;
        var duration = 0d;
        if (format.ValueKind == JsonValueKind.Object && format.TryGetProperty("duration", out var durationNode))
        {
            double.TryParse(durationNode.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out duration);
        }
        var container = format.ValueKind == JsonValueKind.Object && format.TryGetProperty("format_name", out var nameNode)
            ? nameNode.GetString() ?? string.Empty : string.Empty;
        var width = 0;
        var height = 0;
        var codec = string.Empty;
        var frameRate = 0d;
        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var stream in streams.EnumerateArray())
            {
                if (stream.TryGetProperty("codec_type", out var type) && type.GetString() == "video")
                {
                    width = stream.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
                    height = stream.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
                    codec = stream.TryGetProperty("codec_name", out var c) ? (c.GetString() ?? string.Empty).ToLowerInvariant() : string.Empty;
                    frameRate = ParseFrameRate(stream.TryGetProperty("avg_frame_rate", out var avg) ? avg.GetString() : null);
                    if (frameRate <= 0)
                        frameRate = ParseFrameRate(stream.TryGetProperty("r_frame_rate", out var raw) ? raw.GetString() : null);
                    if (width > 0 && height > 0) break;
                }
            }
        }
        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException("Video không có luồng hình hợp lệ.");
        }
        var ext = extension.ToLowerInvariant();
        var direct = ext is ".mp4" or ".m4v" or ".mov"
            ? codec is "h264" or "hevc" or "av1"
            : ext == ".webm" && codec is "vp8" or "vp9" or "av1";
        return new MediaPreviewInfo(width, height, duration, codec, container, direct, frameRate);
    }

    private static double ParseFrameRate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        var parts = value.Split('/', 2);
        if (!double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var numerator)) return 0;
        if (parts.Length == 1) return numerator;
        return double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var denominator) && denominator != 0
            ? numerator / denominator
            : 0;
    }

    private static string ValidateInput(string inputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        var input = Path.GetFullPath(inputPath.Trim());
        var info = new FileInfo(input);
        if (!info.Exists || info.Length <= 0)
        {
            throw new FileNotFoundException("Không tìm thấy video nguồn.", input);
        }
        return input;
    }
}
