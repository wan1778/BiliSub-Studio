using System.Globalization;
using BiliSubStudio.Core.IO;
using BiliSubStudio.Core.Jobs;
using BiliSubStudio.Core.Processes;
using BiliSubStudio.Core.Tools;

namespace BiliSubStudio.Core.Editor;

public sealed record EditRegion(double X, double Y, double Width, double Height, string Effect, int Strength, bool WholeVideo, double Start, double End, string Id = "");

public sealed record VideoEditRequest(
    string InputPath,
    string OutputDirectory,
    string FileName,
    int SourceWidth,
    int SourceHeight,
    double Duration,
    IReadOnlyList<EditRegion> Regions);

public sealed record VideoEditResult(string OutputPath);

public sealed class VideoEditorService
{
    private readonly ToolManager _tools;
    private readonly ProcessRunner _processes;

    public VideoEditorService(ToolManager tools, ProcessRunner processes)
    {
        _tools = tools;
        _processes = processes;
    }

    public async Task<VideoEditResult> RunAsync(AppJob job, VideoEditRequest request)
    {
        var token = job.CancellationToken;
        var input = Path.GetFullPath(request.InputPath.Trim());
        if (!File.Exists(input) || new FileInfo(input).Length <= 0) throw new FileNotFoundException("Video nguồn không hợp lệ.", input);
        var graph = BuildFilter(request);
        var outputDirectory = string.IsNullOrWhiteSpace(request.OutputDirectory) ? Path.GetDirectoryName(input)! : Path.GetFullPath(request.OutputDirectory.Trim());
        Directory.CreateDirectory(outputDirectory);
        var defaultExtension = Path.GetExtension(input).ToLowerInvariant() is ".mkv" or ".mp4" ? Path.GetExtension(input).ToLowerInvariant() : ".mp4";
        var fileName = string.IsNullOrWhiteSpace(request.FileName)
            ? Path.GetFileNameWithoutExtension(input) + "_edited" + defaultExtension
            : FileNamePolicy.Sanitize(request.FileName, "BiliSub_edited.mp4");
        if (Path.GetExtension(fileName).ToLowerInvariant() is not (".mp4" or ".mkv")) fileName += ".mp4";
        var output = FileNamePolicy.UniquePath(Path.Combine(outputDirectory, fileName), input);
        var temporary = output + ".rendering" + Path.GetExtension(output);
        TryDelete(temporary);
        var ffmpeg = await _tools.EnsureFfmpegAsync(token);
        var args = new List<string>
        {
            "-y", "-hide_banner", "-loglevel", "error", "-nostdin", "-i", input,
            "-filter_complex", graph, "-map", "[vout]", "-map", "0:a?", "-map_metadata", "0",
            "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p",
        };
        if (Path.GetExtension(output).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
            args.AddRange(["-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart"]);
        else args.AddRange(["-c:a", "copy"]);
        args.AddRange(["-progress", "pipe:1", "-nostats", temporary]);
        job.Set("rendering", 1, "Đang chuẩn bị xuất video...");
        job.Log($"Video Editor: {request.Regions.Count} vùng, output {output}");
        try
        {
            var result = await _processes.RunAsync(ffmpeg, args, token, standardOutputLine: line =>
            {
                var split = line.Split('=', 2);
                if (split.Length != 2 || split[0] is not ("out_time_us" or "out_time_ms") || request.Duration <= 0) return;
                if (!double.TryParse(split[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var microseconds)) return;
                var percent = Math.Clamp(2 + microseconds / 1_000_000d / request.Duration * 94, 2, 96);
                job.Set("rendering", percent, $"Đang xuất video... {(int)percent}%");
            });
            if (result.ExitCode != 0) throw new InvalidOperationException($"FFmpeg editor: {result.StandardError.Trim()}");
            if (!File.Exists(temporary) || new FileInfo(temporary).Length <= 0) throw new InvalidDataException("Video đã render rỗng.");
            job.Set("finalizing", 98, "Đang hoàn tất file...");
            File.Move(temporary, output);
            return new VideoEditResult(output);
        }
        finally { TryDelete(temporary); }
    }

    public async Task<byte[]> GetPreviewFrameJpegAsync(
        string inputPath,
        double seconds,
        int sourceWidth,
        int sourceHeight,
        double duration,
        IReadOnlyList<EditRegion> regions,
        CancellationToken cancellationToken)
    {
        var input = Path.GetFullPath(inputPath.Trim());
        if (!File.Exists(input) || new FileInfo(input).Length <= 0) throw new FileNotFoundException("Video nguồn không hợp lệ.", input);
        var ffmpeg = await _tools.EnsureFfmpegAsync(cancellationToken);
        var active = regions.Where(region => IsActiveAt(region, seconds)).Select(region => region with
        {
            WholeVideo = true,
            Start = 0,
            End = Math.Max(0, duration),
        }).ToArray();
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostdin",
            "-ss", Math.Max(0, seconds).ToString("0.000", CultureInfo.InvariantCulture),
            "-i", input, "-frames:v", "1",
        };
        if (active.Length > 0)
        {
            var graph = BuildFilter(new VideoEditRequest(input, ".", "preview.mp4", sourceWidth, sourceHeight, duration, active));
            graph += ";[vout]scale=1280:-2:force_original_aspect_ratio=decrease[preview]";
            args.AddRange(["-filter_complex", graph, "-map", "[preview]"]);
        }
        else
        {
            args.AddRange(["-vf", "scale=1280:-2:force_original_aspect_ratio=decrease", "-map", "0:v:0"]);
        }
        args.AddRange(["-an", "-sn", "-dn", "-q:v", "4", "-c:v", "mjpeg", "-f", "image2pipe", "pipe:1"]);
        var frame = await _processes.CaptureBytesAsync(ffmpeg, args, cancellationToken);
        return frame.Length > 0 ? frame : throw new InvalidDataException("Frame preview Editor rỗng.");
    }

    public static string BuildFilter(VideoEditRequest request)
    {
        if (request.SourceWidth <= 0 || request.SourceHeight <= 0) throw new ArgumentException("Không đọc được kích thước video.");
        if (request.Regions.Count == 0) throw new ArgumentException("Hãy khoanh ít nhất một vùng cần xử lý.");
        if (request.Regions.Count > 32) throw new ArgumentException("Tối đa 32 vùng chỉnh video.");
        var parts = new List<string>();
        var current = "0:v";
        for (var index = 0; index < request.Regions.Count; index++)
        {
            var region = request.Regions[index];
            var (x, y, width, height) = RegionPixels(region, request.SourceWidth, request.SourceHeight);
            var enable = RegionEnable(region, request.Duration);
            var output = $"v{index}";
            var effect = region.Effect.Trim().ToLowerInvariant();
            if (effect == "cover")
            {
                parts.Add($"[{current}]drawbox=x={x}:y={y}:w={width}:h={height}:color=black@1:t=fill{enable}[{output}]");
            }
            else if (effect == "mosaic")
            {
                var strength = Math.Clamp(region.Strength, 4, 64);
                var smallWidth = Math.Max(1, width / strength);
                var smallHeight = Math.Max(1, height / strength);
                parts.Add($"[{current}]split=2[base{index}][fx{index}]");
                parts.Add($"[fx{index}]crop={width}:{height}:{x}:{y},scale={smallWidth}:{smallHeight}:flags=neighbor,scale={width}:{height}:flags=neighbor[rendered{index}]");
                parts.Add($"[base{index}][rendered{index}]overlay={x}:{y}{enable}[{output}]");
            }
            else if (effect is "" or "blur")
            {
                var strength = Math.Clamp(region.Strength, 2, 40);
                parts.Add($"[{current}]split=2[base{index}][fx{index}]");
                parts.Add($"[fx{index}]crop={width}:{height}:{x}:{y},boxblur=luma_radius={strength}:luma_power=1[rendered{index}]");
                parts.Add($"[base{index}][rendered{index}]overlay={x}:{y}{enable}[{output}]");
            }
            else throw new ArgumentException($"Hiệu ứng {region.Effect} không hỗ trợ.");
            current = output;
        }
        parts.Add($"[{current}]null[vout]");
        return string.Join(";", parts);
    }

    public static bool IsActiveAt(EditRegion region, double seconds) =>
        region.WholeVideo || seconds >= Math.Max(0, region.Start) && seconds <= region.End;

    private static (int X, int Y, int Width, int Height) RegionPixels(EditRegion region, int width, int height)
    {
        if (region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0 || region.X >= 1 || region.Y >= 1)
            throw new ArgumentException("Tọa độ vùng không hợp lệ.");
        var x = (int)(region.X * width);
        var y = (int)(region.Y * height);
        var w = (int)(Math.Min(1, region.X + region.Width) * width) - x;
        var h = (int)(Math.Min(1, region.Y + region.Height) * height) - y;
        if (w < 2 || h < 2) throw new ArgumentException("Vùng quá nhỏ.");
        return (x, y, w, h);
    }

    private static string RegionEnable(EditRegion region, double duration)
    {
        if (region.WholeVideo) return string.Empty;
        var start = Math.Max(0, region.Start);
        var end = duration > 0 ? Math.Min(duration, region.End) : region.End;
        if (end <= start) throw new ArgumentException("Thời gian kết thúc phải lớn hơn bắt đầu.");
        return $":enable='between(t,{start.ToString("0.000", CultureInfo.InvariantCulture)},{end.ToString("0.000", CultureInfo.InvariantCulture)})'";
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }
}
