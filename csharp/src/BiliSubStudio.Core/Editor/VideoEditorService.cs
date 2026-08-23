using System.Globalization;
using System.Text;
using BiliSubStudio.Core.Configuration;
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
    IReadOnlyList<EditRegion> Regions,
    EditorSubtitleBurn? Subtitle = null,
    EditorAudioSettings? Audio = null);

public sealed record EditorSubtitleBurn(IReadOnlyList<EditorSubtitleCue> Cues, EditorSubtitlePlacement Placement);

public sealed record VideoEditResult(string OutputPath);

public sealed record EditorPreviewSegment(string Path, double SourceStart, double Duration);

public sealed class VideoEditorService
{
    private readonly ToolManager _tools;
    private readonly ProcessRunner _processes;
    private readonly string _previewDirectory;

    public VideoEditorService(AppPaths paths, ToolManager tools, ProcessRunner processes)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _tools = tools;
        _processes = processes;
        _previewDirectory = Path.Combine(paths.Temp, "Editor", "Preview");
    }

    public async Task<VideoEditResult> RunAsync(AppJob job, VideoEditRequest request)
    {
        var token = job.CancellationToken;
        var input = Path.GetFullPath(request.InputPath.Trim());
        if (!File.Exists(input) || new FileInfo(input).Length <= 0) throw new FileNotFoundException("Video nguồn không hợp lệ.", input);
        if (!HasEdit(request)) throw new ArgumentException("Hãy tạo vùng hiệu ứng, nạp bản Vietsub hoặc thay đổi âm thanh để xuất video.");
        var outputDirectory = string.IsNullOrWhiteSpace(request.OutputDirectory) ? Path.GetDirectoryName(input)! : Path.GetFullPath(request.OutputDirectory.Trim());
        Directory.CreateDirectory(outputDirectory);
        var defaultExtension = Path.GetExtension(input).ToLowerInvariant() is ".mkv" or ".mp4" ? Path.GetExtension(input).ToLowerInvariant() : ".mp4";
        var fileName = string.IsNullOrWhiteSpace(request.FileName)
            ? Path.GetFileNameWithoutExtension(input) + "_edited" + defaultExtension
            : FileNamePolicy.Sanitize(request.FileName, "BiliSub_edited.mp4");
        if (Path.GetExtension(fileName).ToLowerInvariant() is not (".mp4" or ".mkv")) fileName += ".mp4";
        var output = FileNamePolicy.UniquePath(Path.Combine(outputDirectory, fileName), input);
        var temporary = output + ".rendering" + Path.GetExtension(output);
        var subtitleAss = request.Subtitle is null ? null : Path.Combine(Path.GetTempPath(), "bilisub-editor-sub-" + Guid.NewGuid().ToString("N") + ".ass");
        TryDelete(temporary);
        if (subtitleAss is not null)
            await File.WriteAllTextAsync(subtitleAss, BuildAss(request.Subtitle!, request.SourceWidth, request.SourceHeight), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), token);
        var graph = BuildFilter(request, subtitleAss);
        var ffmpeg = await _tools.EnsureFfmpegAsync(token);
        var audio = EditorProjectStore.NormalizeAudio(request.Audio);
        var mp4 = Path.GetExtension(output).Equals(".mp4", StringComparison.OrdinalIgnoreCase);
        var args = new List<string>
        {
            "-y", "-hide_banner", "-loglevel", "error", "-nostdin", "-i", input,
            "-filter_complex", graph, "-map", "[vout]", "-map_metadata", "0",
            "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p",
        };
        args.AddRange(BuildAudioArguments(audio, mp4));
        if (mp4) args.AddRange(["-movflags", "+faststart"]);
        args.AddRange(["-progress", "pipe:1", "-nostats", temporary]);
        job.Set("rendering", 1, "Đang chuẩn bị xuất video...");
        job.Log($"Video Editor: {request.Regions.Count} vùng, audio={audio.SourceMode}/{audio.SourceGain:0.00}, output {output}");
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
        finally { TryDelete(temporary); if (subtitleAss is not null) TryDelete(subtitleAss); }
    }

    public async Task<EditorPreviewSegment> CreatePreviewSegmentAsync(
        VideoEditRequest request,
        double requestedStart,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var input = Path.GetFullPath(request.InputPath.Trim());
        if (!File.Exists(input) || new FileInfo(input).Length <= 0) throw new FileNotFoundException("Video nguồn không hợp lệ.", input);
        if (!double.IsFinite(request.Duration) || request.Duration <= 0) throw new InvalidDataException("Thời lượng video không hợp lệ.");

        var (sourceStart, segmentDuration) = PreviewWindow(request.Duration, requestedStart);
        var (previewWidth, previewHeight) = PreviewDimensions(request.SourceWidth, request.SourceHeight);
        var sliced = BuildPreviewSlice(request, sourceStart, segmentDuration, previewWidth, previewHeight);
        Directory.CreateDirectory(_previewDirectory);
        CleanupStalePreviewFiles();
        var identity = Guid.NewGuid().ToString("N");
        var output = Path.Combine(_previewDirectory, identity + ".mp4");
        var temporary = Path.Combine(_previewDirectory, identity + ".rendering.mp4");
        var subtitleAss = sliced.Subtitle is null ? null : Path.Combine(_previewDirectory, identity + ".ass");
        TryDelete(output);
        TryDelete(temporary);
        try
        {
            if (subtitleAss is not null)
                await File.WriteAllTextAsync(subtitleAss, BuildAss(sliced.Subtitle!, previewWidth, previewHeight), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken);
            var graph = $"[0:v]setpts=PTS-STARTPTS,scale={previewWidth}:{previewHeight}:flags=lanczos,setsar=1[previewbase];" +
                BuildFilterCore(sliced, subtitleAss, "previewbase", requireEdit: false);
            var ffmpeg = await _tools.EnsureFfmpegAsync(cancellationToken);
            var args = BuildPreviewArguments(input, temporary, graph, sliced.Audio, sourceStart, segmentDuration);
            var result = await _processes.RunAsync(ffmpeg, args, cancellationToken);
            if (result.ExitCode != 0) throw new InvalidOperationException($"FFmpeg preview Editor: {result.StandardError.Trim()}");
            if (!File.Exists(temporary) || new FileInfo(temporary).Length <= 0) throw new InvalidDataException("Video xem trước đã xử lý bị rỗng.");
            File.Move(temporary, output);
            return new EditorPreviewSegment(output, sourceStart, segmentDuration);
        }
        finally
        {
            TryDelete(temporary);
            if (subtitleAss is not null) TryDelete(subtitleAss);
        }
    }

    public async Task DeletePreviewSegmentAsync(string? path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var candidate = Path.GetFullPath(path.Trim());
        var root = Path.GetFullPath(_previewDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(candidate), ".mp4", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Không được xóa file ngoài vùng preview Editor do ứng dụng quản lý.");
        for (var attempt = 0; attempt < 6; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Delete(candidate);
                return;
            }
            catch (FileNotFoundException) { return; }
            catch (DirectoryNotFoundException) { return; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (attempt < 5) await Task.Delay(TimeSpan.FromMilliseconds(80 * (attempt + 1)), cancellationToken);
        }
    }

    internal static VideoEditRequest BuildPreviewSlice(
        VideoEditRequest request,
        double sourceStart,
        double segmentDuration,
        int previewWidth,
        int previewHeight)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!double.IsFinite(sourceStart) || sourceStart < 0 || !double.IsFinite(segmentDuration) || segmentDuration <= 0)
            throw new ArgumentException("Khoảng thời gian preview không hợp lệ.");
        if (previewWidth <= 0 || previewHeight <= 0) throw new ArgumentException("Kích thước preview không hợp lệ.");
        var sourceEnd = sourceStart + segmentDuration;
        var regions = request.Regions
            .Where(region => region.WholeVideo || region.End > sourceStart && region.Start < sourceEnd)
            .Select(region => region.WholeVideo
                ? region with { Start = 0, End = segmentDuration }
                : region with
                {
                    Start = Math.Max(0, region.Start - sourceStart),
                    End = Math.Min(segmentDuration, region.End - sourceStart),
                })
            .ToArray();
        EditorSubtitleBurn? subtitle = null;
        if (request.Subtitle is not null)
        {
            var cues = request.Subtitle.Cues
                .Where(cue => cue.End > sourceStart && cue.Start < sourceEnd)
                .Select(cue => cue with
                {
                    Start = Math.Max(0, cue.Start - sourceStart),
                    End = Math.Min(segmentDuration, cue.End - sourceStart),
                })
                .ToArray();
            if (cues.Length > 0) subtitle = request.Subtitle with { Cues = cues };
        }
        return request with
        {
            SourceWidth = previewWidth,
            SourceHeight = previewHeight,
            Duration = segmentDuration,
            Regions = regions,
            Subtitle = subtitle,
        };
    }

    internal static IReadOnlyList<string> BuildPreviewArguments(
        string input,
        string output,
        string graph,
        EditorAudioSettings? audio,
        double sourceStart,
        double segmentDuration)
    {
        var arguments = new List<string>
        {
            "-y", "-hide_banner", "-loglevel", "error", "-nostdin",
            "-ss", sourceStart.ToString("0.000", CultureInfo.InvariantCulture),
            "-i", input,
            "-t", segmentDuration.ToString("0.000", CultureInfo.InvariantCulture),
            "-filter_complex", graph,
            "-map", "[vout]", "-map_metadata", "-1", "-sn", "-dn",
            "-c:v", "libx264", "-preset", "ultrafast", "-crf", "23", "-tune", "zerolatency", "-pix_fmt", "yuv420p",
        };
        arguments.AddRange(BuildAudioArgumentsCore(audio, mp4: true, resetTimestamps: true));
        arguments.AddRange(["-movflags", "+faststart", "-nostats", output]);
        return arguments;
    }

    internal static IReadOnlyList<string> BuildAudioArguments(EditorAudioSettings? settings, bool mp4)
        => BuildAudioArgumentsCore(settings, mp4, resetTimestamps: false);

    private static IReadOnlyList<string> BuildAudioArgumentsCore(EditorAudioSettings? settings, bool mp4, bool resetTimestamps)
    {
        var audio = EditorProjectStore.NormalizeAudio(settings);
        if (audio.SourceMode == "mute") return ["-an"];
        var arguments = new List<string> { "-map", "0:a?" };
        var filters = new List<string>();
        if (resetTimestamps) filters.Add("asetpts=PTS-STARTPTS");
        if (audio.SourceMode == "duck") filters.Add("volume=" + audio.SourceGain.ToString("0.000", CultureInfo.InvariantCulture));
        if (filters.Count > 0) arguments.AddRange(["-af", string.Join(',', filters)]);
        if (mp4 || audio.SourceMode == "duck") arguments.AddRange(["-c:a", "aac", "-b:a", "192k"]);
        else arguments.AddRange(["-c:a", "copy"]);
        return arguments;
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

    public static string BuildFilter(VideoEditRequest request, string? subtitleAssPath = null)
        => BuildFilterCore(request, subtitleAssPath, "0:v", requireEdit: true);

    private static string BuildFilterCore(VideoEditRequest request, string? subtitleAssPath, string inputLabel, bool requireEdit)
    {
        if (request.SourceWidth <= 0 || request.SourceHeight <= 0) throw new ArgumentException("Không đọc được kích thước video.");
        if (string.IsNullOrWhiteSpace(inputLabel)) throw new ArgumentException("Nhãn video đầu vào không hợp lệ.");
        if (requireEdit && !HasEdit(request))
            throw new ArgumentException("Hãy tạo vùng hiệu ứng, nạp bản Vietsub hoặc thay đổi âm thanh để xuất video.");
        if (request.Regions.Count > 32) throw new ArgumentException("Tối đa 32 vùng chỉnh video.");
        var parts = new List<string>();
        var current = inputLabel;
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
        if (request.Subtitle is not null)
        {
            if (string.IsNullOrWhiteSpace(subtitleAssPath))
                parts.Add($"[{current}]null[vout]");
            else
            {
                var escaped = EscapeFilterPath(subtitleAssPath);
                parts.Add($"[{current}]ass=filename='{escaped}'[vout]");
            }
        }
        else parts.Add($"[{current}]null[vout]");
        return string.Join(";", parts);
    }

    public static string BuildAss(EditorSubtitleBurn subtitle, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(subtitle);
        if (width <= 0 || height <= 0) throw new ArgumentException("Kích thước video không hợp lệ.");
        if (subtitle.Cues.Count == 0 || subtitle.Cues.Any(x => string.IsNullOrWhiteSpace(x.VietnameseText)))
            throw new InvalidDataException("Bản Vietsub chưa hoàn tất nên chưa thể hardsub.");
        var placement = subtitle.Placement;
        if (placement.X < 0 || placement.Y < 0 || placement.Width < .05 || placement.Height < .04 ||
            placement.X + placement.Width > 1.0001 || placement.Y + placement.Height > 1.0001)
            throw new InvalidDataException("Vị trí phụ đề không hợp lệ.");
        var fontSize = Math.Clamp((int)Math.Round(height * Math.Min(.06, placement.Height * .33)), 20, Math.Max(20, height / 8));
        var marginL = Math.Max(0, (int)Math.Round(placement.X * width));
        var marginR = Math.Max(0, (int)Math.Round((1 - placement.X - placement.Width) * width));
        var marginV = Math.Max(0, (int)Math.Round((1 - placement.Y - placement.Height * .82) * height));
        var builder = new StringBuilder(subtitle.Cues.Count * 128);
        builder.AppendLine("[Script Info]")
            .AppendLine("ScriptType: v4.00+")
            .Append("PlayResX: ").AppendLine(width.ToString(CultureInfo.InvariantCulture))
            .Append("PlayResY: ").AppendLine(height.ToString(CultureInfo.InvariantCulture))
            .AppendLine("WrapStyle: 0")
            .AppendLine("ScaledBorderAndShadow: yes")
            .AppendLine()
            .AppendLine("[V4+ Styles]")
            .AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding")
            .Append("Style: Vietsub,Arial,").Append(fontSize.ToString(CultureInfo.InvariantCulture))
            .Append(",&H00FFFFFF,&H000000FF,&H00101010,&H78000000,-1,0,0,0,100,100,0,0,1,2.2,0.8,2,")
            .Append(marginL.ToString(CultureInfo.InvariantCulture)).Append(',')
            .Append(marginR.ToString(CultureInfo.InvariantCulture)).Append(',')
            .Append(marginV.ToString(CultureInfo.InvariantCulture)).AppendLine(",1")
            .AppendLine()
            .AppendLine("[Events]")
            .AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");
        foreach (var cue in subtitle.Cues)
        {
            builder.Append("Dialogue: 0,").Append(AssTime(cue.Start)).Append(',').Append(AssTime(cue.End))
                .Append(",Vietsub,,0,0,0,,").AppendLine(EscapeAssText(cue.VietnameseText));
        }
        return builder.ToString();
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

    private static string AssTime(double seconds)
    {
        var value = TimeSpan.FromMilliseconds(Math.Round(Math.Max(0, seconds) * 1000));
        return $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}.{value.Milliseconds / 10:00}";
    }

    private static string EscapeAssText(string value) => value.Trim()
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("{", "\\{", StringComparison.Ordinal)
        .Replace("}", "\\}", StringComparison.Ordinal)
        .Replace("\r\n", "\\N", StringComparison.Ordinal)
        .Replace("\r", "\\N", StringComparison.Ordinal)
        .Replace("\n", "\\N", StringComparison.Ordinal);

    private static string EscapeFilterPath(string value) => Path.GetFullPath(value).Replace('\\', '/')
        .Replace(":", "\\:", StringComparison.Ordinal)
        .Replace("'", "\\'", StringComparison.Ordinal)
        .Replace("[", "\\[", StringComparison.Ordinal)
        .Replace("]", "\\]", StringComparison.Ordinal)
        .Replace(",", "\\,", StringComparison.Ordinal)
        .Replace(";", "\\;", StringComparison.Ordinal);

    private static bool HasEdit(VideoEditRequest request) =>
        request.Regions.Count > 0 || request.Subtitle is not null || EditorProjectStore.NormalizeAudio(request.Audio).SourceMode != "keep";

    private static (double Start, double Duration) PreviewWindow(double sourceDuration, double requestedStart)
    {
        const double targetDuration = 12;
        var start = Math.Clamp(double.IsFinite(requestedStart) ? requestedStart : 0, 0, sourceDuration);
        if (sourceDuration - start < Math.Min(2, sourceDuration)) start = Math.Max(0, sourceDuration - targetDuration);
        var duration = Math.Min(targetDuration, sourceDuration - start);
        if (duration <= 0) throw new InvalidDataException("Không còn đoạn video để tạo preview.");
        return (start, duration);
    }

    private static (int Width, int Height) PreviewDimensions(int sourceWidth, int sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0) throw new ArgumentException("Không đọc được kích thước video.");
        const double maxLongSide = 1280;
        var scale = Math.Min(1, maxLongSide / Math.Max(sourceWidth, sourceHeight));
        static int Even(double value) => Math.Max(2, (int)Math.Round(value / 2, MidpointRounding.AwayFromZero) * 2);
        return (Even(sourceWidth * scale), Even(sourceHeight * scale));
    }

    private void CleanupStalePreviewFiles()
    {
        try
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromDays(1);
            foreach (var path in Directory.EnumerateFiles(_previewDirectory))
            {
                try { if (File.GetLastWriteTimeUtc(path) < cutoff) File.Delete(path); }
                catch { }
            }
        }
        catch { }
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }
}
