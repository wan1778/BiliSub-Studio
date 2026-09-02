using System.Globalization;
using System.Text;
using System.Text.Json;
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
    EditorAudioSettings? Audio = null,
    EditorVoiceTrack? VoiceTrack = null,
    double MosaicScaleX = 1,
    double MosaicScaleY = 1,
    EditorTrimRange? Trim = null,
    EditorExportSettings? Export = null);

public sealed record EditorSubtitleBurn(
    IReadOnlyList<EditorSubtitleCue> Cues,
    EditorSubtitlePlacement Placement,
    IReadOnlyList<EditorCueSpeechTiming>? SpeechTiming = null,
    bool Karaoke = true,
    EditorSubtitleStyle? Style = null);

public sealed record VideoEditResult(string OutputPath);

public sealed record EditorPreviewSegment(string Path, double SourceStart, double Duration);

public sealed class VideoEditorService
{
    internal const long RenderSafetyReserveBytes = 512L * 1024 * 1024;
    internal const int RenderPreflightSourceMultiplier = 2;
    internal const int RenderDiskCheckIntervalMilliseconds = 3000;
    internal const double PreviewSegmentDurationSeconds = 12;
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
        var inputInfo = new FileInfo(input);
        if (!inputInfo.Exists || inputInfo.Length <= 0) throw new FileNotFoundException("Video nguồn không hợp lệ.", input);
        var trim = EditorProjectStore.NormalizeTrim(request.Trim, request.Duration);
        if (!HasEdit(request)) throw new ArgumentException("Hãy cắt video, tạo vùng hiệu ứng, nạp bản Vietsub hoặc thay đổi âm thanh để xuất video.");
        var renderRequest = BuildPreviewSlice(
            request, trim.Start, trim.Duration, request.SourceWidth, request.SourceHeight);
        var outputDirectory = string.IsNullOrWhiteSpace(request.OutputDirectory) ? Path.GetDirectoryName(input)! : Path.GetFullPath(request.OutputDirectory.Trim());
        Directory.CreateDirectory(outputDirectory);
        DriveInfo? outputDrive = null;
        long? initialFreeSpace = null;
        try
        {
            var outputRoot = Path.GetPathRoot(outputDirectory);
            if (!string.IsNullOrWhiteSpace(outputRoot))
            {
                var candidate = new DriveInfo(outputRoot);
                if (candidate.IsReady)
                {
                    outputDrive = candidate;
                    initialFreeSpace = candidate.AvailableFreeSpace;
                }
            }
        }
        catch (ArgumentException) { outputDrive = null; }
        catch (IOException) { outputDrive = null; }
        catch (UnauthorizedAccessException) { outputDrive = null; }
        if (initialFreeSpace is long freeSpace)
        {
            var requiredFreeSpace = inputInfo.Length > (long.MaxValue - RenderSafetyReserveBytes) / RenderPreflightSourceMultiplier
                ? long.MaxValue
                : inputInfo.Length * RenderPreflightSourceMultiplier + RenderSafetyReserveBytes;
            if (freeSpace < requiredFreeSpace)
            {
                const double gib = 1024d * 1024 * 1024;
                throw new IOException($"Không đủ dung lượng để xuất video an toàn. Cần ít nhất {requiredFreeSpace / gib:0.0} GB trống, hiện còn {freeSpace / gib:0.0} GB.");
            }
        }
        var defaultExtension = Path.GetExtension(input).ToLowerInvariant() is ".mkv" or ".mp4" ? Path.GetExtension(input).ToLowerInvariant() : ".mp4";
        var fileName = string.IsNullOrWhiteSpace(request.FileName)
            ? Path.GetFileNameWithoutExtension(input) + "_edited" + defaultExtension
            : FileNamePolicy.Sanitize(request.FileName, "BiliSub_edited.mp4");
        if (Path.GetExtension(fileName).ToLowerInvariant() is not (".mp4" or ".mkv")) fileName += ".mp4";
        var output = FileNamePolicy.UniquePath(Path.Combine(outputDirectory, fileName), input);
        var temporary = output + ".rendering" + Path.GetExtension(output);
        var subtitleAss = renderRequest.Subtitle is null ? null : Path.Combine(Path.GetTempPath(), "bilisub-editor-sub-" + Guid.NewGuid().ToString("N") + ".ass");
        TryDelete(temporary);
        if (subtitleAss is not null)
            await File.WriteAllTextAsync(subtitleAss, BuildAss(renderRequest.Subtitle!, request.SourceWidth, request.SourceHeight), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), token);
        var graph = "[0:v]setpts=PTS-STARTPTS[renderbase];"
            + BuildFilterCore(renderRequest, subtitleAss, "renderbase", requireEdit: false);
        var ffmpeg = await _tools.EnsureFfmpegAsync(token);
        var audio = EditorProjectStore.NormalizeAudio(renderRequest.Audio);
        var voice = NormalizeVoiceTrack(renderRequest.VoiceTrack, requireFile: true);
        var mp4 = Path.GetExtension(output).Equals(".mp4", StringComparison.OrdinalIgnoreCase);
        var export = EditorExportPolicy.Normalize(request.Export);
        var exportGraph = EditorExportPolicy.BuildVideoGraph(graph, export, request.SourceWidth, request.SourceHeight);
        job.Set("encoder-probe", .5, "Đang kiểm tra bộ mã hóa video...");
        var encoder = await EditorExportPolicy.ResolveEncoderAsync(ffmpeg, _processes, export, token, job.Log);
        var args = BuildConfiguredRenderArguments(
            input, temporary, exportGraph.FilterGraph, audio, trim, voice, mp4,
            export, encoder, exportGraph.OutputLabel);
        job.Set("rendering", 1, "Đang chuẩn bị xuất video...");
        job.Log($"Video Editor: {request.Regions.Count} vùng, giữ {trim.Start:0.000}-{trim.End:0.000}s ({trim.Duration:0.000}s), codec={export.Codec}/{encoder.Name}, quality={export.Quality}, size={exportGraph.Dimensions.Width}x{exportGraph.Dimensions.Height}, fps={(export.FrameRate?.ToString("0.###", CultureInfo.InvariantCulture) ?? "source")}, audio={audio.SourceMode}/{audio.SourceGain:0.00}/{export.AudioBitrateKbps}k, voice={(voice is null ? "off" : "local")}, output {output}");
        var nextDiskCheck = Environment.TickCount64;
        try
        {
            var result = await _processes.RunAsync(ffmpeg, args, token, standardOutputLine: line =>
            {
                var split = line.Split('=', 2);
                if (split.Length != 2 || split[0] is not ("out_time_us" or "out_time_ms") || trim.Duration <= 0) return;
                if (!double.TryParse(split[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var microseconds)) return;
                if (outputDrive is not null && Environment.TickCount64 >= nextDiskCheck)
                {
                    long liveFreeSpace;
                    try { liveFreeSpace = outputDrive.AvailableFreeSpace; }
                    catch (IOException error) { throw new IOException("Không còn truy cập được ổ đĩa đang xuất video.", error); }
                    catch (UnauthorizedAccessException error) { throw new IOException("Không còn quyền truy cập ổ đĩa đang xuất video.", error); }
                    if (liveFreeSpace < RenderSafetyReserveBytes)
                    {
                        const double mib = 1024d * 1024;
                        throw new IOException($"Đã dừng xuất để bảo vệ ổ đĩa: dung lượng trống chỉ còn {liveFreeSpace / mib:0} MB.");
                    }
                    nextDiskCheck = Environment.TickCount64 + RenderDiskCheckIntervalMilliseconds;
                }
                var percent = Math.Clamp(2 + microseconds / 1_000_000d / trim.Duration * 94, 2, 96);
                job.Set("rendering", percent, $"Đang xuất video... {(int)percent}%");
            });
            if (result.ExitCode != 0) throw new InvalidOperationException($"FFmpeg editor: {result.StandardError.Trim()}");
            if (!File.Exists(temporary) || new FileInfo(temporary).Length <= 0) throw new InvalidDataException("Video đã render rỗng.");
            job.Set("validating", 97, "Đang kiểm tra stream, thời lượng và khả năng giải mã...");
            await ValidateRenderedOutputAsync(
                temporary, trim.Duration, voice is not null || audio.SourceMode != "mute",
                exportGraph.Dimensions, export.Codec, export.FrameRate, token);
            job.Set("finalizing", 99, "Đã xác minh file; đang hoàn tất...");
            File.Move(temporary, output);
            return new VideoEditResult(output);
        }
        finally { TryDelete(temporary); if (subtitleAss is not null) TryDelete(subtitleAss); }
    }

    private async Task ValidateRenderedOutputAsync(
        string path,
        double expectedDuration,
        bool expectAudio,
        EditorExportDimensions expectedDimensions,
        string expectedCodec,
        double? expectedFrameRate,
        CancellationToken cancellationToken)
    {
        var ffprobe = await _tools.EnsureFfprobeAsync(cancellationToken);
        var probe = await _processes.RunAsync(ffprobe,
        [
            "-v", "error",
            "-show_entries", "stream=codec_type,codec_name,width,height,avg_frame_rate:format=duration,size",
            "-of", "json", path,
        ], cancellationToken);
        if (probe.ExitCode != 0)
            throw new InvalidDataException("Không đọc được file vừa render: " + probe.StandardError.Trim());
        ValidateRenderedProbe(probe.StandardOutput, expectedDuration, expectAudio);
        ValidateExportProbe(probe.StandardOutput, expectedDimensions, expectedCodec, expectedFrameRate);

        var ffmpeg = await _tools.EnsureFfmpegAsync(cancellationToken);
        var videoHead = await _processes.RunAsync(ffmpeg,
        [
            "-hide_banner", "-loglevel", "error", "-xerror", "-nostdin",
            "-i", path, "-map", "0:v:0", "-t", "1.000", "-f", "null", "-",
        ], cancellationToken);
        if (videoHead.ExitCode != 0)
            throw new InvalidDataException("Video đầu ra không giải mã được: " + videoHead.StandardError.Trim());

        if (expectAudio)
        {
            var audioFrame = await _processes.RunAsync(ffmpeg,
            [
                "-hide_banner", "-loglevel", "error", "-xerror", "-nostdin",
                "-i", path, "-map", "0:a:0", "-frames:a", "1", "-f", "null", "-",
            ], cancellationToken);
            if (audioFrame.ExitCode != 0)
                throw new InvalidDataException("Audio đầu ra không giải mã được: " + audioFrame.StandardError.Trim());
        }

        if (expectedDuration > 2.5)
        {
            var videoTail = await _processes.RunAsync(ffmpeg,
            [
                "-hide_banner", "-loglevel", "error", "-xerror", "-nostdin",
                "-sseof", "-2.000", "-i", path, "-map", "0:v:0", "-frames:v", "1", "-f", "null", "-",
            ], cancellationToken);
            if (videoTail.ExitCode != 0)
                throw new InvalidDataException("Phần cuối video đầu ra không giải mã được: " + videoTail.StandardError.Trim());
        }
    }

    private static void ValidateRenderedProbe(string json, double expectedDuration, bool expectAudio)
    {
        if (!double.IsFinite(expectedDuration) || expectedDuration <= 0)
            throw new InvalidDataException("Thời lượng nguồn không hợp lệ để xác minh output.");

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var videoStreams = 0;
        var audioStreams = 0;
        var videoWidth = 0;
        var videoHeight = 0;
        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var stream in streams.EnumerateArray())
            {
                var type = stream.TryGetProperty("codec_type", out var typeNode) ? typeNode.GetString() : null;
                if (string.Equals(type, "video", StringComparison.Ordinal))
                {
                    videoStreams++;
                    if (stream.TryGetProperty("width", out var widthNode) && widthNode.TryGetInt32(out var width)) videoWidth = Math.Max(videoWidth, width);
                    if (stream.TryGetProperty("height", out var heightNode) && heightNode.TryGetInt32(out var height)) videoHeight = Math.Max(videoHeight, height);
                }
                else if (string.Equals(type, "audio", StringComparison.Ordinal)) audioStreams++;
            }
        }
        if (videoStreams == 0 || videoWidth <= 0 || videoHeight <= 0)
            throw new InvalidDataException("Output không có video stream hợp lệ.");
        if (expectAudio && audioStreams == 0)
            throw new InvalidDataException("Output thiếu audio theo chính sách Keep/Duck/voice Việt.");
        if (!expectAudio && audioStreams != 0)
            throw new InvalidDataException("Output vẫn có audio dù chính sách yêu cầu Mute hoàn toàn.");

        if (!root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Output thiếu metadata container.");
        if (!TryJsonDouble(format, "duration", out var duration) || !double.IsFinite(duration) || duration <= 0)
            throw new InvalidDataException("Output không có duration hợp lệ.");
        if (!TryJsonDouble(format, "size", out var size) || !double.IsFinite(size) || size <= 0)
            throw new InvalidDataException("Output không có kích thước container hợp lệ.");

        var tolerance = Math.Clamp(expectedDuration * .0005, 1.5, 5.0);
        if (Math.Abs(duration - expectedDuration) > tolerance)
            throw new InvalidDataException($"Duration output lệch {Math.Abs(duration - expectedDuration):0.000}s, vượt tolerance {tolerance:0.000}s.");
    }

    private static void ValidateExportProbe(
        string json,
        EditorExportDimensions expectedDimensions,
        string expectedCodec,
        double? expectedFrameRate)
    {
        ArgumentNullException.ThrowIfNull(expectedDimensions);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Output không có danh sách stream để xác minh thiết lập xuất.");
        JsonElement video = default;
        foreach (var stream in streams.EnumerateArray())
        {
            if (stream.TryGetProperty("codec_type", out var type) && type.GetString() == "video")
            {
                video = stream;
                break;
            }
        }
        if (video.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Output không có video stream để xác minh thiết lập xuất.");
        var width = video.TryGetProperty("width", out var widthNode) && widthNode.TryGetInt32(out var parsedWidth) ? parsedWidth : 0;
        var height = video.TryGetProperty("height", out var heightNode) && heightNode.TryGetInt32(out var parsedHeight) ? parsedHeight : 0;
        if (width != expectedDimensions.Width || height != expectedDimensions.Height)
            throw new InvalidDataException($"Kích thước output sai: cần {expectedDimensions.Width}x{expectedDimensions.Height}, thực tế {width}x{height}.");
        var codec = video.TryGetProperty("codec_name", out var codecNode) ? codecNode.GetString()?.ToLowerInvariant() : null;
        var requiredCodec = expectedCodec == "hevc" ? "hevc" : "h264";
        if (!string.Equals(codec, requiredCodec, StringComparison.Ordinal))
            throw new InvalidDataException($"Codec output sai: cần {requiredCodec}, thực tế {codec ?? "không xác định"}.");
        if (expectedFrameRate is not double expectedFps) return;
        var actualFps = video.TryGetProperty("avg_frame_rate", out var fpsNode)
            ? ParseFrameRate(fpsNode.GetString())
            : 0;
        if (!double.IsFinite(actualFps) || actualFps <= 0 || Math.Abs(actualFps - expectedFps) > .05)
            throw new InvalidDataException($"FPS output sai: cần {expectedFps:0.###}, thực tế {actualFps:0.###}.");
    }

    private static double ParseFrameRate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        var parts = value.Split('/', 2);
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)) return 0;
        if (parts.Length == 1) return numerator;
        return double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) && denominator != 0
            ? numerator / denominator
            : 0;
    }

    private static bool TryJsonDouble(JsonElement parent, string name, out double value)
    {
        value = 0;
        if (!parent.TryGetProperty(name, out var node)) return false;
        if (node.ValueKind == JsonValueKind.Number) return node.TryGetDouble(out value);
        return node.ValueKind == JsonValueKind.String
            && double.TryParse(node.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
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

        var trim = EditorProjectStore.NormalizeTrim(request.Trim, request.Duration);
        var (sourceStart, segmentDuration) = PreviewWindow(trim.Start, trim.End, requestedStart);
        var (previewWidth, previewHeight) = PreviewDimensions(request.SourceWidth, request.SourceHeight);
        var sliced = BuildPreviewSlice(request, sourceStart, segmentDuration, previewWidth, previewHeight);
        Directory.CreateDirectory(_previewDirectory);
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
            var voice = NormalizeVoiceTrack(sliced.VoiceTrack, requireFile: true);
            if (voice is not null) graph += ";" + BuildVoiceAudioFilter(EditorProjectStore.NormalizeAudio(sliced.Audio), voice, 1, sourceStart);
            var ffmpeg = await _tools.EnsureFfmpegAsync(cancellationToken);
            var args = BuildPreviewArguments(input, temporary, graph, sliced.Audio, sourceStart, segmentDuration, voice);
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
        await DeletePreviewArtifactAsync(candidate, cancellationToken);
    }

    public async Task CleanupPreviewCacheAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_previewDirectory)) return;
        foreach (var path in Directory.EnumerateFiles(_previewDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsManagedPreviewArtifact(path)) continue;
            await DeletePreviewArtifactAsync(path, cancellationToken);
        }
    }

    private static bool IsManagedPreviewArtifact(string path)
    {
        var extension = Path.GetExtension(path);
        return string.Equals(extension, ".mp4", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".ass", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task DeletePreviewArtifactAsync(string candidate, CancellationToken cancellationToken)
    {
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
        if (request.SourceWidth <= 0 || request.SourceHeight <= 0) throw new ArgumentException("Kích thước source không hợp lệ.");
        var sourceEnd = sourceStart + segmentDuration;
        var regions = request.Regions
            .Select(region => EditorRegionTimeScope.Normalize(region, request.Duration))
            .Where(region => region.WholeVideo || region.End > sourceStart && region.Start < sourceEnd)
            .Select(region => region.WholeVideo
                ? EditorRegionTimeScope.NormalizeWholeVideo(region, segmentDuration)
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
            if (cues.Length > 0)
            {
                IReadOnlyList<EditorCueSpeechTiming>? speechTiming = null;
                if (request.Subtitle.SpeechTiming is not null)
                {
                    var cueIds = cues.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
                    speechTiming = request.Subtitle.SpeechTiming
                        .Where(timing => cueIds.Contains(timing.CueId) && timing.SpeechEnd > sourceStart && timing.SpeechStart < sourceEnd)
                        .Select(timing => timing with
                        {
                            CueStart = Math.Max(0, timing.CueStart - sourceStart),
                            CueEnd = Math.Min(segmentDuration, timing.CueEnd - sourceStart),
                            SpeechStart = Math.Max(0, timing.SpeechStart - sourceStart),
                            SpeechEnd = Math.Min(segmentDuration, timing.SpeechEnd - sourceStart),
                            LeadingSilence = Math.Max(0, Math.Max(0, timing.SpeechStart - sourceStart) - Math.Max(0, timing.CueStart - sourceStart)),
                            TrailingSilence = Math.Max(0, Math.Min(segmentDuration, timing.CueEnd - sourceStart) - Math.Min(segmentDuration, timing.SpeechEnd - sourceStart)),
                            Words = timing.Words
                                .Where(word => word.End > sourceStart && word.Start < sourceEnd)
                                .Select(word => word with
                                {
                                    Start = Math.Max(0, word.Start - sourceStart),
                                    End = Math.Min(segmentDuration, word.End - sourceStart),
                                })
                                .Where(word => word.End > word.Start)
                                .ToArray(),
                            Pauses = timing.Pauses
                                .Where(pause => pause.End > sourceStart && pause.Start < sourceEnd)
                                .Select(pause => pause with
                                {
                                    Start = Math.Max(0, pause.Start - sourceStart),
                                    End = Math.Min(segmentDuration, pause.End - sourceStart),
                                })
                                .Where(pause => pause.End > pause.Start)
                                .ToArray(),
                        })
                        .ToArray();
                }
                subtitle = request.Subtitle with { Cues = cues, SpeechTiming = speechTiming };
            }
        }
        return request with
        {
            SourceWidth = previewWidth,
            SourceHeight = previewHeight,
            Duration = segmentDuration,
            Regions = regions,
            Subtitle = subtitle,
            MosaicScaleX = previewWidth / (double)request.SourceWidth,
            MosaicScaleY = previewHeight / (double)request.SourceHeight,
            Trim = new EditorTrimRange(0, segmentDuration),
        };
    }

    internal static IReadOnlyList<string> BuildPreviewArguments(
        string input,
        string output,
        string graph,
        EditorAudioSettings? audio,
        double sourceStart,
        double segmentDuration,
        EditorVoiceTrack? voiceTrack = null)
    {
        var voice = NormalizeVoiceTrack(voiceTrack, requireFile: false);
        var arguments = new List<string>
        {
            "-y", "-hide_banner", "-loglevel", "error", "-nostdin",
            "-ss", sourceStart.ToString("0.000", CultureInfo.InvariantCulture),
            "-i", input,
        };
        if (voice is not null)
        {
            var voiceSeek = Math.Max(0, sourceStart - voice.Start);
            arguments.AddRange(["-ss", voiceSeek.ToString("0.000", CultureInfo.InvariantCulture), "-i", voice.Path]);
        }
        arguments.AddRange([
            "-t", segmentDuration.ToString("0.000", CultureInfo.InvariantCulture),
            "-filter_complex", graph,
            "-map", "[vout]", "-map_metadata", "-1", "-sn", "-dn",
            "-c:v", "libx264", "-preset", "ultrafast", "-crf", "23", "-tune", "zerolatency", "-pix_fmt", "yuv420p",
        ]);
        if (voice is null) arguments.AddRange(BuildAudioArgumentsCore(audio, mp4: true, resetTimestamps: true));
        else arguments.AddRange(["-map", "[aout]", "-c:a", "aac", "-b:a", "192k"]);
        arguments.AddRange(["-movflags", "+faststart", "-nostats", output]);
        return arguments;
    }

    internal static IReadOnlyList<string> BuildRenderArguments(
        string input,
        string output,
        string graph,
        EditorAudioSettings? audioSettings,
        EditorTrimRange trim,
        EditorVoiceTrack? voiceTrack,
        bool mp4) =>
        BuildConfiguredRenderArguments(input, output, graph, audioSettings, trim, voiceTrack, mp4);

    internal static IReadOnlyList<string> BuildConfiguredRenderArguments(
        string input,
        string output,
        string graph,
        EditorAudioSettings? audioSettings,
        EditorTrimRange trim,
        EditorVoiceTrack? voiceTrack,
        bool mp4,
        EditorExportSettings? exportSettings = null,
        EditorResolvedVideoEncoder? resolvedEncoder = null,
        string videoOutputLabel = "[vout]")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(output);
        ArgumentException.ThrowIfNullOrWhiteSpace(graph);
        if (!double.IsFinite(trim.Start) || trim.Start < 0
            || !double.IsFinite(trim.End) || trim.End - trim.Start < .1)
            throw new InvalidDataException("Khoảng cắt video không hợp lệ.");
        var audio = EditorProjectStore.NormalizeAudio(audioSettings);
        var voice = NormalizeVoiceTrack(voiceTrack, requireFile: false);
        var export = EditorExportPolicy.Normalize(exportSettings);
        var encoder = resolvedEncoder ?? new EditorResolvedVideoEncoder(export.Codec == "hevc" ? "libx265" : "libx264", false);
        if (videoOutputLabel is not ("[vout]" or "[exportv]"))
            throw new InvalidDataException("Nhãn video output không hợp lệ.");
        var arguments = new List<string>
        {
            "-y", "-hide_banner", "-loglevel", "error", "-nostdin",
            "-ss", trim.Start.ToString("0.000", CultureInfo.InvariantCulture), "-i", input,
        };
        if (voice is not null)
        {
            var voiceSeek = Math.Max(0, trim.Start - voice.Start);
            arguments.AddRange(["-ss", voiceSeek.ToString("0.000", CultureInfo.InvariantCulture), "-i", voice.Path]);
        }
        var combinedGraph = voice is null
            ? graph
            : graph + ";" + BuildVoiceAudioFilter(audio, voice, 1, trim.Start);
        arguments.AddRange([
            "-t", trim.Duration.ToString("0.000", CultureInfo.InvariantCulture),
            "-filter_complex", combinedGraph, "-map", videoOutputLabel, "-map_metadata", "0",
        ]);
        arguments.AddRange(EditorExportPolicy.BuildVideoEncoderArguments(export, encoder, mp4));
        if (voice is null) arguments.AddRange(BuildFinalAudioArguments(audio, export.AudioBitrateKbps, resetTimestamps: true));
        else arguments.AddRange(["-map", "[aout]", "-c:a", "aac", "-b:a", export.AudioBitrateKbps.ToString(CultureInfo.InvariantCulture) + "k"]);
        if (mp4) arguments.AddRange(["-movflags", "+faststart"]);
        arguments.AddRange(["-progress", "pipe:1", "-nostats", output]);
        return arguments;
    }

    internal static IReadOnlyList<string> BuildAudioArguments(EditorAudioSettings? settings, bool mp4)
        => BuildAudioArgumentsCore(settings, mp4, resetTimestamps: false);

    private static IReadOnlyList<string> BuildFinalAudioArguments(EditorAudioSettings? settings, int bitrateKbps, bool resetTimestamps)
    {
        var audio = EditorProjectStore.NormalizeAudio(settings);
        if (audio.SourceMode == "mute") return ["-an"];
        var arguments = new List<string> { "-map", "0:a?" };
        var filters = new List<string>();
        if (resetTimestamps) filters.Add("asetpts=PTS-STARTPTS");
        if (audio.SourceMode == "duck") filters.Add("volume=" + audio.SourceGain.ToString("0.000", CultureInfo.InvariantCulture));
        if (filters.Count > 0) arguments.AddRange(["-af", string.Join(',', filters)]);
        arguments.AddRange(["-c:a", "aac", "-b:a", bitrateKbps.ToString(CultureInfo.InvariantCulture) + "k"]);
        return arguments;
    }

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
        EditorSubtitleBurn? subtitle,
        CancellationToken cancellationToken)
    {
        var input = Path.GetFullPath(inputPath.Trim());
        if (!File.Exists(input) || new FileInfo(input).Length <= 0) throw new FileNotFoundException("Video nguồn không hợp lệ.", input);
        var ffmpeg = await _tools.EnsureFfmpegAsync(cancellationToken);
        var active = regions.Select(region => EditorRegionTimeScope.Normalize(region, duration))
            .Where(region => IsActiveAt(region, seconds)).Select(region =>
            EditorRegionTimeScope.NormalizeWholeVideo(region with { WholeVideo = true }, Math.Max(0, duration))).ToArray();
        var subtitleAss = subtitle is null
            ? null
            : Path.Combine(Path.GetTempPath(), "bilisub-editor-frame-sub-" + Guid.NewGuid().ToString("N") + ".ass");
        try
        {
            if (subtitleAss is not null)
                await File.WriteAllTextAsync(
                    subtitleAss, BuildAss(subtitle!, sourceWidth, sourceHeight),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken);
            var args = BuildPreviewFrameArguments(
                input, seconds, sourceWidth, sourceHeight, duration, active, subtitle, subtitleAss);
            var frame = await _processes.CaptureBytesAsync(ffmpeg, args, cancellationToken);
            return frame.Length > 0 ? frame : throw new InvalidDataException("Frame preview Editor rỗng.");
        }
        finally
        {
            if (subtitleAss is not null) TryDelete(subtitleAss);
        }
    }

    internal static IReadOnlyList<string> BuildPreviewFrameArguments(
        string input,
        double seconds,
        int sourceWidth,
        int sourceHeight,
        double duration,
        IReadOnlyList<EditRegion> activeRegions,
        EditorSubtitleBurn? subtitle,
        string? subtitleAssPath)
    {
        var sourceTime = Math.Clamp(double.IsFinite(seconds) ? seconds : 0, 0, Math.Max(0, duration));
        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostdin",
            "-ss", sourceTime.ToString("0.000", CultureInfo.InvariantCulture),
            "-i", input, "-frames:v", "1",
        };
        if (activeRegions.Count > 0 || subtitle is not null)
        {
            var request = new VideoEditRequest(
                input, ".", "preview.mp4", sourceWidth, sourceHeight, duration, activeRegions, subtitle);
            var timestamp = sourceTime.ToString("0.000", CultureInfo.InvariantCulture);
            var graph = $"[0:v]setpts=PTS-STARTPTS+{timestamp}/TB[framebase];"
                + BuildFilterCore(request, subtitleAssPath, "framebase", requireEdit: false)
                + ";[vout]scale=1280:-2:force_original_aspect_ratio=decrease[preview]";
            arguments.AddRange(["-filter_complex", graph, "-map", "[preview]"]);
        }
        else
        {
            arguments.AddRange(["-vf", "scale=1280:-2:force_original_aspect_ratio=decrease", "-map", "0:v:0"]);
        }
        arguments.AddRange(["-an", "-sn", "-dn", "-q:v", "4", "-c:v", "mjpeg", "-f", "image2pipe", "pipe:1"]);
        return arguments;
    }

    public static string BuildFilter(VideoEditRequest request, string? subtitleAssPath = null)
        => BuildFilterCore(request, subtitleAssPath, "0:v", requireEdit: true);

    private static string BuildFilterCore(VideoEditRequest request, string? subtitleAssPath, string inputLabel, bool requireEdit)
    {
        if (request.SourceWidth <= 0 || request.SourceHeight <= 0) throw new ArgumentException("Không đọc được kích thước video.");
        if (string.IsNullOrWhiteSpace(inputLabel)) throw new ArgumentException("Nhãn video đầu vào không hợp lệ.");
        if (requireEdit && !HasEdit(request))
            throw new ArgumentException("Hãy cắt video, tạo vùng hiệu ứng, nạp bản Vietsub hoặc thay đổi âm thanh để xuất video.");
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
                var (smallWidth, smallHeight) = EditorMosaicStrength.DownsampleDimensions(
                    region.Strength, width, height, request.MosaicScaleX, request.MosaicScaleY);
                parts.Add($"[{current}]split=2[base{index}][fx{index}]");
                parts.Add($"[fx{index}]crop={width}:{height}:{x}:{y},scale={smallWidth}:{smallHeight}:flags=neighbor,scale={width}:{height}:flags=neighbor[rendered{index}]");
                parts.Add($"[base{index}][rendered{index}]overlay={x}:{y}{enable}[{output}]");
            }
            else if (effect is "" or "blur")
            {
                var strength = EditorBlurStrength.EffectiveRadius(region.Strength, width, height);
                parts.Add($"[{current}]split=2[base{index}][fx{index}]");
                parts.Add($"[fx{index}]crop={width}:{height}:{x}:{y},boxblur=luma_radius={strength}:luma_power=1:chroma_radius='min({strength},floor((min(cw,ch)-1)/2))':chroma_power=1[rendered{index}]");
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
        var style = EditorSubtitleStylePolicy.Normalize(subtitle.Style);
        var automaticFontSize = Math.Clamp(
            height * Math.Min(.06, placement.Height * .33),
            20d,
            Math.Max(20d, height / 8d));
        var fontSize = Math.Clamp((int)Math.Round(automaticFontSize * style.FontScale), 10, Math.Max(10, height / 4));
        var marginL = Math.Max(0, (int)Math.Round(placement.X * width));
        var marginR = Math.Max(0, (int)Math.Round((1 - placement.X - placement.Width) * width));
        var marginV = Math.Max(0, (int)Math.Round((1 - placement.Y - placement.Height * .82) * height));
        var textColor = EditorSubtitleStylePolicy.ToAssColor(style.TextColor, 1);
        var secondaryColor = EditorSubtitleStylePolicy.ToAssColor(style.TextColor, .45);
        var outlineColor = EditorSubtitleStylePolicy.ToAssColor(style.OutlineColor, 1);
        var shadowColor = EditorSubtitleStylePolicy.ToAssColor("#000000", style.Shadow > .001 ? .55 : 0);
        var backgroundColor = EditorSubtitleStylePolicy.ToAssColor(style.BackgroundColor, style.BackgroundOpacity);
        var bold = style.Bold ? -1 : 0;
        var italic = style.Italic ? -1 : 0;
        var underline = style.Underline ? -1 : 0;
        static string AssNumber(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
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
            .Append("Style: Vietsub,").Append(style.FontName).Append(',').Append(fontSize.ToString(CultureInfo.InvariantCulture))
            .Append(',').Append(textColor).Append(',').Append(secondaryColor).Append(',').Append(outlineColor).Append(',').Append(shadowColor).Append(',')
            .Append(bold.ToString(CultureInfo.InvariantCulture)).Append(',')
            .Append(italic.ToString(CultureInfo.InvariantCulture)).Append(',')
            .Append(underline.ToString(CultureInfo.InvariantCulture)).Append(",0,100,100,0,0,1,")
            .Append(AssNumber(style.OutlineWidth)).Append(',').Append(AssNumber(style.Shadow)).Append(",2,")
            .Append(marginL.ToString(CultureInfo.InvariantCulture)).Append(',')
            .Append(marginR.ToString(CultureInfo.InvariantCulture)).Append(',')
            .Append(marginV.ToString(CultureInfo.InvariantCulture)).AppendLine(",1");
        if (style.BackgroundOpacity > .001)
        {
            builder.Append("Style: VietsubBox,").Append(style.FontName).Append(',').Append(fontSize.ToString(CultureInfo.InvariantCulture))
                .Append(',').Append(EditorSubtitleStylePolicy.ToAssColor(style.TextColor, 0)).Append(',')
                .Append(EditorSubtitleStylePolicy.ToAssColor(style.TextColor, 0)).Append(',')
                .Append(backgroundColor).Append(',').Append(backgroundColor).Append(',')
                .Append(bold.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(italic.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(underline.ToString(CultureInfo.InvariantCulture)).Append(",0,100,100,0,0,3,")
                .Append(AssNumber(style.BackgroundPadding)).Append(",0,2,")
                .Append(marginL.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(marginR.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(marginV.ToString(CultureInfo.InvariantCulture)).AppendLine(",1");
        }
        builder.AppendLine()
            .AppendLine("[Events]")
            .AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");
        var timing = subtitle.SpeechTiming?.ToDictionary(x => x.CueId, StringComparer.Ordinal);
        var foregroundLayer = style.BackgroundOpacity > .001 ? 1 : 0;
        foreach (var cue in subtitle.Cues)
        {
            double start;
            double end;
            string foregroundText;
            if (subtitle.Karaoke && timing is not null && timing.TryGetValue(cue.Id, out var rhythm) && rhythm.SpeechEnd > rhythm.SpeechStart + .04)
            {
                start = rhythm.SpeechStart;
                end = rhythm.SpeechEnd;
                foregroundText = BuildKaraokeText(cue.VietnameseText, rhythm);
            }
            else
            {
                start = cue.Start;
                end = cue.End;
                foregroundText = EscapeAssText(cue.VietnameseText);
            }
            if (style.BackgroundOpacity > .001)
                builder.Append("Dialogue: 0,").Append(AssTime(start)).Append(',').Append(AssTime(end))
                    .Append(",VietsubBox,,0,0,0,,").AppendLine(EscapeAssText(cue.VietnameseText));
            builder.Append("Dialogue: ").Append(foregroundLayer).Append(',').Append(AssTime(start)).Append(',').Append(AssTime(end))
                .Append(",Vietsub,,0,0,0,,").AppendLine(foregroundText);
        }
        return builder.ToString();
    }

    internal static string BuildKaraokeText(string text, EditorCueSpeechTiming timing)
    {
        var tokens = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0) return string.Empty;
        var totalCs = Math.Max(tokens.Length, (int)Math.Round(Math.Max(.01, timing.SpeechEnd - timing.SpeechStart) * 100));
        var sourceUnits = new List<double>();
        if (timing.Words.Count > 0)
        {
            for (var index = 0; index < timing.Words.Count; index++)
            {
                var word = timing.Words[index];
                var next = index + 1 < timing.Words.Count ? timing.Words[index + 1].Start : timing.SpeechEnd;
                sourceUnits.Add(Math.Max(.02, Math.Min(timing.SpeechEnd, next) - Math.Max(timing.SpeechStart, word.Start)));
            }
        }
        if (sourceUnits.Count == 0) sourceUnits.Add(Math.Max(.01, timing.SpeechEnd - timing.SpeechStart));
        var durations = ResampleKaraokeDurations(sourceUnits, tokens.Length, totalCs);
        var output = new StringBuilder(text.Length + tokens.Length * 10);
        for (var index = 0; index < tokens.Length; index++)
        {
            output.Append(@"{\kf").Append(durations[index].ToString(CultureInfo.InvariantCulture)).Append('}')
                .Append(EscapeAssText(tokens[index]));
            if (index + 1 < tokens.Length) output.Append(' ');
        }
        return output.ToString();
    }

    private static int[] ResampleKaraokeDurations(IReadOnlyList<double> source, int targetCount, int totalCs)
    {
        var result = Enumerable.Repeat(1, targetCount).ToArray();
        var remaining = Math.Max(targetCount, totalCs) - targetCount;
        var totalWeight = source.Sum(x => Math.Max(.001, x));
        for (var index = 0; index < targetCount && remaining > 0; index++)
        {
            var position = (index + .5) / targetCount * source.Count - .5;
            var left = Math.Clamp((int)Math.Floor(position), 0, source.Count - 1);
            var right = Math.Clamp(left + 1, 0, source.Count - 1);
            var fraction = Math.Clamp(position - Math.Floor(position), 0, 1);
            var weight = source[left] * (1 - fraction) + source[right] * fraction;
            var share = index == targetCount - 1 ? remaining : Math.Clamp((int)Math.Round(remaining * weight / Math.Max(.001, totalWeight)), 0, remaining);
            result[index] += share;
            remaining -= share;
            totalWeight = Math.Max(.001, totalWeight - weight);
        }
        if (remaining > 0) result[^1] += remaining;
        return result;
    }

    private static EditorVoiceTrack? NormalizeVoiceTrack(EditorVoiceTrack? track, bool requireFile)
    {
        if (track is null) return null;
        if (string.IsNullOrWhiteSpace(track.Path) || !double.IsFinite(track.Start) || track.Start < 0
            || !double.IsFinite(track.Duration) || track.Duration <= 0 || !double.IsFinite(track.Gain) || track.Gain is < 0 or > 4)
            throw new InvalidDataException("Track voice Việt không hợp lệ.");
        var path = Path.GetFullPath(track.Path.Trim());
        if (requireFile && (!File.Exists(path) || new FileInfo(path).Length <= 64))
            throw new FileNotFoundException("Thiếu track voice Việt đã tạo.", path);
        return track with { Path = path, Gain = Math.Clamp(track.Gain, 0, 4) };
    }

    private static string BuildVoiceAudioFilter(EditorAudioSettings audio, EditorVoiceTrack voice, int voiceInputIndex, double sourceStart)
    {
        var relativeDelay = Math.Max(0, voice.Start - sourceStart);
        var voiceFilters = new List<string> { "asetpts=PTS-STARTPTS" };
        if (relativeDelay > .0005)
        {
            var milliseconds = (int)Math.Round(relativeDelay * 1000);
            voiceFilters.Add($"adelay={milliseconds}:all=1");
        }
        if (Math.Abs(voice.Gain - 1) > .0005)
            voiceFilters.Add("volume=" + voice.Gain.ToString("0.000", CultureInfo.InvariantCulture));
        var voiceChain = $"[{voiceInputIndex}:a]{string.Join(',', voiceFilters)}[voicea]";
        if (audio.SourceMode == "mute") return voiceChain + ";[voicea]anull[aout]";
        var sourceFilters = new List<string> { "asetpts=PTS-STARTPTS" };
        if (audio.SourceMode == "duck") sourceFilters.Add("volume=" + audio.SourceGain.ToString("0.000", CultureInfo.InvariantCulture));
        return $"[0:a]{string.Join(',', sourceFilters)}[sourcea];{voiceChain};[sourcea][voicea]amix=inputs=2:duration=longest:dropout_transition=0:normalize=0[aout]";
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
        var normalized = EditorRegionTimeScope.Normalize(region, duration);
        if (normalized.WholeVideo) return string.Empty;
        return $":enable='between(t,{normalized.Start.ToString("0.000", CultureInfo.InvariantCulture)},{normalized.End.ToString("0.000", CultureInfo.InvariantCulture)})'";
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
        request.Regions.Count > 0 || request.Subtitle is not null || request.VoiceTrack is not null
        || EditorProjectStore.NormalizeAudio(request.Audio).SourceMode != "keep"
        || EditorProjectStore.HasTrim(request.Trim, request.Duration);

    public static double? NextPreviewStart(double sourceStart, double segmentDuration, double sourceDuration)
    {
        if (!double.IsFinite(sourceDuration) || sourceDuration <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceDuration));
        if (!double.IsFinite(sourceStart) || sourceStart < 0 || sourceStart > sourceDuration)
            throw new ArgumentOutOfRangeException(nameof(sourceStart));
        if (!double.IsFinite(segmentDuration) || segmentDuration <= 0)
            throw new ArgumentOutOfRangeException(nameof(segmentDuration));
        var sourceEnd = Math.Min(sourceDuration, sourceStart + segmentDuration);
        return sourceEnd >= sourceDuration - .05 ? null : sourceEnd;
    }

    private static (double Start, double Duration) PreviewWindow(double sourceDuration, double requestedStart)
        => PreviewWindow(0, sourceDuration, requestedStart);

    private static (double Start, double Duration) PreviewWindow(
        double rangeStart,
        double rangeEnd,
        double requestedStart)
    {
        if (!double.IsFinite(rangeStart) || !double.IsFinite(rangeEnd) || rangeStart < 0 || rangeEnd <= rangeStart)
            throw new InvalidDataException("Khoảng preview video không hợp lệ.");
        var rangeDuration = rangeEnd - rangeStart;
        var start = Math.Clamp(double.IsFinite(requestedStart) ? requestedStart : rangeStart, rangeStart, rangeEnd);
        if (rangeEnd - start < Math.Min(2, rangeDuration))
            start = Math.Max(rangeStart, rangeEnd - PreviewSegmentDurationSeconds);
        var duration = Math.Min(PreviewSegmentDurationSeconds, rangeEnd - start);
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

    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }
}
