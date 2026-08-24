using System.Globalization;
using System.Text.Json;
using BiliSubStudio.Core.IO;
using BiliSubStudio.Core.Jobs;
using BiliSubStudio.Core.Processes;
using BiliSubStudio.Core.Tools;

namespace BiliSubStudio.App.Services;

internal sealed record EditorImageOverlaySpec(
    string Path,
    double X,
    double Y,
    double Width,
    double Height,
    double Opacity);

internal sealed class EditorImageOverlayComposer(ToolManager tools, ProcessRunner processes)
{
    private const int MaxImages = 8;

    public async Task<string> RenderAsync(
        AppJob job,
        string inputPath,
        string outputDirectory,
        string fileName,
        int sourceWidth,
        int sourceHeight,
        double duration,
        IReadOnlyList<EditorImageOverlaySpec> images,
        bool copyAudio)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(images);
        if (sourceWidth <= 0 || sourceHeight <= 0 || !double.IsFinite(duration) || duration <= 0)
            throw new InvalidDataException("Thông tin video không hợp lệ để ghép ảnh/logo.");
        if (images.Count is 0 or > MaxImages)
            throw new InvalidDataException($"Cần từ 1 đến {MaxImages} ảnh/logo.");

        var input = Path.GetFullPath(inputPath.Trim());
        if (!File.Exists(input) || new FileInfo(input).Length <= 0)
            throw new FileNotFoundException("Video dùng để ghép ảnh/logo không tồn tại.", input);

        var normalized = images.Select(image => Normalize(image, sourceWidth, sourceHeight)).ToArray();
        var directory = string.IsNullOrWhiteSpace(outputDirectory)
            ? Path.GetDirectoryName(input)!
            : Path.GetFullPath(outputDirectory.Trim());
        Directory.CreateDirectory(directory);

        var sanitized = FileNamePolicy.Sanitize(fileName, "BiliSub_edited.mp4");
        if (Path.GetExtension(sanitized).ToLowerInvariant() is not (".mp4" or ".mkv")) sanitized += ".mp4";
        var output = FileNamePolicy.UniquePath(Path.Combine(directory, sanitized), input);
        var extension = Path.GetExtension(output);
        var temporary = output + ".rendering" + extension;
        TryDelete(temporary);

        var expectAudio = await HasAudioStreamAsync(input, job.CancellationToken);
        var ffmpeg = await tools.EnsureFfmpegAsync(job.CancellationToken);
        var args = new List<string>
        {
            "-y", "-hide_banner", "-loglevel", "error", "-nostdin", "-i", input,
        };
        foreach (var image in normalized)
        {
            args.AddRange(["-loop", "1", "-framerate", "1", "-i", image.Path]);
        }

        var graph = BuildGraph(normalized, sourceWidth, sourceHeight);
        args.AddRange([
            "-filter_complex", graph,
            "-map", "[vout]", "-map", "0:a?", "-map_metadata", "0", "-sn", "-dn",
            "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p",
        ]);
        if (copyAudio) args.AddRange(["-c:a", "copy"]);
        else args.AddRange(["-c:a", "aac", "-b:a", "192k"]);
        if (string.Equals(extension, ".mp4", StringComparison.OrdinalIgnoreCase))
            args.AddRange(["-movflags", "+faststart"]);
        args.AddRange([
            "-t", duration.ToString("0.000", CultureInfo.InvariantCulture),
            "-progress", "pipe:1", "-nostats", temporary,
        ]);

        job.Set("image-overlay", 2, "Đang ghép ảnh/logo vào video...");
        job.Log($"Editor image overlay: {normalized.Length} ảnh, output {output}");
        try
        {
            var result = await processes.RunAsync(ffmpeg, args, job.CancellationToken, standardOutputLine: line =>
            {
                var split = line.Split('=', 2);
                if (split.Length != 2 || split[0] is not ("out_time_us" or "out_time_ms")) return;
                if (!double.TryParse(split[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var microseconds)) return;
                var percent = Math.Clamp(2 + microseconds / 1_000_000d / duration * 94, 2, 96);
                job.Set("image-overlay", percent, $"Đang ghép ảnh/logo... {(int)percent}%");
            });
            if (result.ExitCode != 0)
                throw new InvalidOperationException("FFmpeg ghép ảnh/logo: " + result.StandardError.Trim());
            if (!File.Exists(temporary) || new FileInfo(temporary).Length <= 0)
                throw new InvalidDataException("Video ghép ảnh/logo bị rỗng.");

            job.Set("image-validate", 97, "Đang kiểm tra stream, kích thước, audio và thời lượng...");
            await ValidateAsync(temporary, sourceWidth, sourceHeight, duration, expectAudio, job.CancellationToken);
            File.Move(temporary, output);
            job.Set("image-complete", 99, "Đã ghép và xác minh ảnh/logo.");
            return output;
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private async Task<bool> HasAudioStreamAsync(string path, CancellationToken cancellationToken)
    {
        var ffprobe = await tools.EnsureFfprobeAsync(cancellationToken);
        var probe = await processes.RunAsync(ffprobe,
        [
            "-v", "error",
            "-select_streams", "a:0",
            "-show_entries", "stream=codec_type",
            "-of", "json", path,
        ], cancellationToken);
        if (probe.ExitCode != 0)
            throw new InvalidDataException("Không đọc được audio stream của video dùng để ghép ảnh/logo: " + probe.StandardError.Trim());

        using var document = JsonDocument.Parse(probe.StandardOutput);
        if (!document.RootElement.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
            return false;
        return streams.EnumerateArray().Any(stream =>
            stream.TryGetProperty("codec_type", out var type) && type.GetString() == "audio");
    }

    private async Task ValidateAsync(
        string path,
        int expectedWidth,
        int expectedHeight,
        double expectedDuration,
        bool expectAudio,
        CancellationToken cancellationToken)
    {
        var ffprobe = await tools.EnsureFfprobeAsync(cancellationToken);
        var probe = await processes.RunAsync(ffprobe,
        [
            "-v", "error",
            "-show_entries", "stream=codec_type,width,height:format=duration,size",
            "-of", "json", path,
        ], cancellationToken);
        if (probe.ExitCode != 0)
            throw new InvalidDataException("Không đọc được video sau khi ghép ảnh/logo: " + probe.StandardError.Trim());

        using var document = JsonDocument.Parse(probe.StandardOutput);
        var root = document.RootElement;
        var videoStreams = 0;
        var audioStreams = 0;
        var validVideo = false;
        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var stream in streams.EnumerateArray())
            {
                if (!stream.TryGetProperty("codec_type", out var type)) continue;
                if (type.GetString() == "video")
                {
                    videoStreams++;
                    var width = stream.TryGetProperty("width", out var widthNode) && widthNode.TryGetInt32(out var parsedWidth) ? parsedWidth : 0;
                    var height = stream.TryGetProperty("height", out var heightNode) && heightNode.TryGetInt32(out var parsedHeight) ? parsedHeight : 0;
                    if (width == expectedWidth && height == expectedHeight) validVideo = true;
                }
                else if (type.GetString() == "audio") audioStreams++;
            }
        }
        if (videoStreams == 0 || !validVideo)
            throw new InvalidDataException("Kích thước video sau khi ghép ảnh/logo bị thay đổi ngoài dự kiến.");
        if (expectAudio && audioStreams == 0)
            throw new InvalidDataException("Video sau khi ghép ảnh/logo bị mất audio nguồn/base.");
        if (!expectAudio && audioStreams != 0)
            throw new InvalidDataException("Video sau khi ghép ảnh/logo xuất hiện audio ngoài dự kiến.");
        if (!root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Video sau khi ghép ảnh/logo thiếu metadata container.");
        if (!TryDouble(format, "duration", out var duration) || !double.IsFinite(duration) || duration <= 0)
            throw new InvalidDataException("Video sau khi ghép ảnh/logo không có duration hợp lệ.");
        if (!TryDouble(format, "size", out var size) || !double.IsFinite(size) || size <= 0)
            throw new InvalidDataException("Video sau khi ghép ảnh/logo không có kích thước container hợp lệ.");
        var tolerance = Math.Clamp(expectedDuration * .0005, 1.5, 5.0);
        if (Math.Abs(duration - expectedDuration) > tolerance)
            throw new InvalidDataException($"Duration video ghép ảnh/logo lệch {Math.Abs(duration - expectedDuration):0.000}s.");
    }

    private static EditorImageOverlaySpec Normalize(EditorImageOverlaySpec image, int sourceWidth, int sourceHeight)
    {
        if (image is null) throw new InvalidDataException("Ảnh/logo rỗng.");
        var path = Path.GetFullPath(image.Path.Trim());
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg"))
            throw new InvalidDataException("Ảnh/logo chỉ hỗ trợ PNG, JPG hoặc JPEG.");
        if (!File.Exists(path) || new FileInfo(path).Length <= 0)
            throw new FileNotFoundException("Không tìm thấy ảnh/logo.", path);
        if (!double.IsFinite(image.X) || !double.IsFinite(image.Y) || !double.IsFinite(image.Width) || !double.IsFinite(image.Height)
            || image.X < 0 || image.Y < 0 || image.Width <= 0 || image.Height <= 0
            || image.X + image.Width > 1.0001 || image.Y + image.Height > 1.0001)
            throw new InvalidDataException("Vị trí/kích thước ảnh/logo không hợp lệ.");
        if (!double.IsFinite(image.Opacity) || image.Opacity is < 0.05 or > 1)
            throw new InvalidDataException("Độ mờ ảnh/logo phải từ 5% đến 100%.");
        if (Math.Round(image.Width * sourceWidth) < 2 || Math.Round(image.Height * sourceHeight) < 2)
            throw new InvalidDataException("Ảnh/logo quá nhỏ.");
        return image with { Path = path, Opacity = Math.Clamp(image.Opacity, .05, 1) };
    }

    private static string BuildGraph(IReadOnlyList<EditorImageOverlaySpec> images, int sourceWidth, int sourceHeight)
    {
        var parts = new List<string>();
        var current = "0:v";
        for (var index = 0; index < images.Count; index++)
        {
            var image = images[index];
            var width = Math.Max(2, (int)Math.Round(image.Width * sourceWidth));
            var height = Math.Max(2, (int)Math.Round(image.Height * sourceHeight));
            var x = Math.Clamp((int)Math.Round(image.X * sourceWidth), 0, Math.Max(0, sourceWidth - width));
            var y = Math.Clamp((int)Math.Round(image.Y * sourceHeight), 0, Math.Max(0, sourceHeight - height));
            var alpha = image.Opacity.ToString("0.000", CultureInfo.InvariantCulture);
            var imageLabel = $"logo{index}";
            var outputLabel = index == images.Count - 1 ? "vout" : $"logoout{index}";
            parts.Add($"[{index + 1}:v]format=rgba,scale={width}:{height}:flags=lanczos,colorchannelmixer=aa={alpha}[{imageLabel}]");
            parts.Add($"[{current}][{imageLabel}]overlay={x}:{y}:eof_action=repeat:shortest=0[{outputLabel}]");
            current = outputLabel;
        }
        return string.Join(';', parts);
    }

    private static bool TryDouble(JsonElement parent, string name, out double value)
    {
        value = 0;
        if (!parent.TryGetProperty(name, out var node)) return false;
        if (node.ValueKind == JsonValueKind.Number) return node.TryGetDouble(out value);
        return node.ValueKind == JsonValueKind.String
            && double.TryParse(node.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
