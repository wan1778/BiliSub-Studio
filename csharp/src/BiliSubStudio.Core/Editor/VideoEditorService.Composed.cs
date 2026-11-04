using System.Globalization;
using System.Text;
using BiliSubStudio.Core.IO;
using BiliSubStudio.Core.Jobs;

namespace BiliSubStudio.Core.Editor;

/// <summary>
/// Export video for a multi-segment composition: each segment is rendered separately (effects, subtitles,
/// voice each using exactly that segment's source file) and then concatenated into a single file.
/// The segments are normalized to the same size, fps, codec and audio sample rate
/// so the concatenation copies the streams as is — no second encode, no quality loss.
/// </summary>
public sealed partial class VideoEditorService
{
    private const int ComposedAudioSampleRate = 48000;
    private const int ComposedVideoTrackTimescale = 90000;

    private async Task<VideoEditResult> RunComposedAsync(
        AppJob job,
        VideoEditRequest request,
        IReadOnlyList<VideoEditSegment> segments)
    {
        var token = job.CancellationToken;
        var prepared = segments.Select(NormalizeComposedSegment).ToArray();
        var export = EditorExportPolicy.Normalize(request.Export);
        var primary = prepared[0];
        var dimensions = EditorExportPolicy.ResolveDimensions(export, primary.Width, primary.Height);
        var frameRate = export.FrameRate ?? await ProbeSourceFrameRateAsync(primary.InputPath, token) ?? 30d;
        var audio = EditorProjectStore.NormalizeAudio(request.Audio);
        var hasVoice = prepared.Any(segment => segment.VoiceTrack is not null);
        var outputAudio = audio.SourceMode != "mute" || hasVoice;
        var totalDuration = prepared.Sum(segment => segment.Duration);
        if (totalDuration <= 0) throw new InvalidDataException("Dải ghép không có nội dung để xuất.");

        var workDirectory = Path.Combine(_previewDirectory, "composed-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(workDirectory);
        try
        {
            var rendered = new List<string>(prepared.Length);
            for (var index = 0; index < prepared.Length; index++)
            {
                var segment = prepared[index];
                job.Set(
                    "rendering",
                    2 + index * (90d / prepared.Length),
                    $"Đang xuất đoạn {index + 1}/{prepared.Length}: {Path.GetFileName(segment.InputPath)}");
                rendered.Add(await RenderComposedSegmentAsync(
                    job, request, segment, dimensions, frameRate, outputAudio, workDirectory, index, token));
            }

            job.Set("finalizing", 95, "Đang nối các đoạn thành một video...");
            var output = ComposeOutputPath(request, primary.InputPath);
            var temporary = output + ".rendering" + Path.GetExtension(output);
            TryDelete(temporary);
            var listPath = Path.Combine(workDirectory, "concat.txt");
            await File.WriteAllTextAsync(
                listPath,
                BuildConcatList(rendered, prepared.Select(segment => segment.Duration).ToArray()),
                new UTF8Encoding(false),
                token);
            var ffmpeg = await _tools.EnsureFfmpegAsync(token);
            var concatArguments = BuildConcatArguments(listPath, temporary, Path.GetExtension(output));
            var concat = await _processes.RunAsync(ffmpeg, concatArguments, token);
            if (concat.ExitCode != 0)
                throw new InvalidOperationException("FFmpeg nối các đoạn: " + concat.StandardError.Trim());
            if (!File.Exists(temporary) || new FileInfo(temporary).Length <= 0)
                throw new InvalidDataException("Video nối từ các đoạn bị rỗng.");

            job.Set("validating", 97, "Đang kiểm tra stream, thời lượng và khả năng giải mã...");
            await ValidateRenderedOutputAsync(
                job, temporary, totalDuration, outputAudio,
                dimensions, export.Codec, export.FrameRate, null, 0, null, token);
            job.Set("finalizing", 99, "Đã xác minh file; đang hoàn tất...");
            File.Move(temporary, output);
            job.Log($"Video Editor composed: {prepared.Length} đoạn, {totalDuration:0.000}s, {dimensions.Width}x{dimensions.Height}, fps={frameRate:0.###}, output {output}");
            return new VideoEditResult(output);
        }
        finally { TryDeleteDirectory(workDirectory); }
    }

    private async Task<string> RenderComposedSegmentAsync(
        AppJob job,
        VideoEditRequest request,
        VideoEditSegment segment,
        EditorExportDimensions dimensions,
        double frameRate,
        bool outputAudio,
        string workDirectory,
        int index,
        CancellationToken cancellationToken)
    {
        var output = Path.Combine(workDirectory, $"segment-{index:00}.mp4");
        var sourceDuration = Math.Max(segment.SourceDuration, segment.End);
        var slice = BuildPreviewSlice(
            new VideoEditRequest(
                segment.InputPath,
                workDirectory,
                $"segment-{index:00}.mp4",
                segment.Width,
                segment.Height,
                sourceDuration,
                segment.Regions,
                segment.Subtitle,
                request.Audio,
                segment.VoiceTrack,
                Trim: new EditorTrimRange(segment.Start, segment.End)),
            segment.Start,
            segment.Duration,
            segment.Width,
            segment.Height);

        var subtitleAss = slice.Subtitle is null
            ? null
            : Path.Combine(workDirectory, $"segment-{index:00}.ass");
        if (subtitleAss is not null)
            await File.WriteAllTextAsync(
                subtitleAss,
                BuildAss(slice.Subtitle!, segment.Width, segment.Height),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
                cancellationToken);

        var sourceHasAudio = await ProbeSourceHasAudioAsync(segment.InputPath, cancellationToken);
        var graph = BuildComposedSegmentGraph(
            slice, subtitleAss, dimensions, frameRate, request.Audio, segment.VoiceTrack,
            sourceHasAudio, segment.Duration, segment.Start);
        var ffmpeg = await _tools.EnsureFfmpegAsync(cancellationToken);
        var export = EditorExportPolicy.Normalize(request.Export);
        var encoder = await EditorExportPolicy.ResolveEncoderAsync(ffmpeg, _processes, export, cancellationToken, job.Log);
        var arguments = BuildComposedSegmentArguments(
            segment.InputPath, output, graph, request.Audio, segment.VoiceTrack,
            segment.Start, segment.Duration, sourceHasAudio, outputAudio, export, encoder);
        var result = await _processes.RunAsync(ffmpeg, arguments, cancellationToken);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"FFmpeg editor (đoạn {index + 1}): {result.StandardError.Trim()}");
        if (!File.Exists(output) || new FileInfo(output).Length <= 0)
            throw new InvalidDataException($"Đoạn {index + 1} render rỗng.");
        return output;
    }

    /// <summary>Filter graph of one segment: effects + subtitles + voice, then frame normalization.</summary>
    private static string BuildComposedSegmentGraph(
        VideoEditRequest slice,
        string? subtitleAssPath,
        EditorExportDimensions dimensions,
        double frameRate,
        EditorAudioSettings? audioSettings,
        EditorVoiceTrack? voiceTrack,
        bool sourceHasAudio,
        double segmentDuration,
        double sourceStart)
    {
        var video = "[0:v]setpts=PTS-STARTPTS[renderbase];"
            + BuildFilterCore(slice, subtitleAssPath, "renderbase", requireEdit: false)
            + ";[vout]scale=" + dimensions.Width + ":" + dimensions.Height
            + ":force_original_aspect_ratio=decrease"
            + ",pad=" + dimensions.Width + ":" + dimensions.Height + ":(ow-iw)/2:(oh-ih)/2"
            + ",setsar=1"
            + ",fps=" + frameRate.ToString("0.###", CultureInfo.InvariantCulture)
            + "[exportv]";
        var voice = NormalizeVoiceTrack(voiceTrack, requireFile: false);
        if (voice is not null)
        {
            // A segment with no source audio leaves the voice alone (mute) — otherwise
            // the voice's [0:a] branch would reference a stream that does not exist.
            var voiceAudio = sourceHasAudio
                ? EditorProjectStore.NormalizeAudio(audioSettings)
                : EditorAudioSettings.Default with { SourceMode = "mute" };
            return video + ";" + BuildVoiceAudioFilter(voiceAudio, voice, 1, sourceStart);
        }

        var audio = EditorProjectStore.NormalizeAudio(audioSettings);
        if (audio.SourceMode == "mute") return video;
        if (!sourceHasAudio)
            return video + ";anullsrc=r=" + ComposedAudioSampleRate + ":cl=stereo:d="
                + segmentDuration.ToString("0.000", CultureInfo.InvariantCulture) + "[aout]";
        var filters = new List<string>
        {
            "asetpts=PTS-STARTPTS",
            "aresample=" + ComposedAudioSampleRate,
            "aformat=sample_fmts=fltp:sample_rates=" + ComposedAudioSampleRate + ":channel_layouts=stereo",
        };
        if (audio.SourceMode == "duck")
            filters.Add("volume=" + audio.SourceGain.ToString("0.000", CultureInfo.InvariantCulture));
        return video + ";[0:a]" + string.Join(",", filters) + "[aout]";
    }

    internal static IReadOnlyList<string> BuildComposedSegmentArguments(
        string input,
        string output,
        string graph,
        EditorAudioSettings? audioSettings,
        EditorVoiceTrack? voiceTrack,
        double sourceStart,
        double segmentDuration,
        bool sourceHasAudio,
        bool outputAudio,
        EditorExportSettings exportSettings,
        EditorResolvedVideoEncoder encoder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(output);
        ArgumentException.ThrowIfNullOrWhiteSpace(graph);
        var voice = NormalizeVoiceTrack(voiceTrack, requireFile: false);
        var audio = EditorProjectStore.NormalizeAudio(audioSettings);
        var arguments = new List<string>
        {
            "-y", "-hide_banner", "-loglevel", "error", "-nostdin",
            "-ss", sourceStart.ToString("0.000", CultureInfo.InvariantCulture), "-i", input,
        };
        if (voice is not null)
        {
            var voiceSeek = Math.Max(0, sourceStart - voice.Start);
            arguments.AddRange(["-ss", voiceSeek.ToString("0.000", CultureInfo.InvariantCulture), "-i", voice.Path]);
        }
        arguments.AddRange([
            "-t", segmentDuration.ToString("0.000", CultureInfo.InvariantCulture),
            "-filter_complex", graph,
            "-map", "[exportv]", "-map_metadata", "-1", "-sn", "-dn",
        ]);
        arguments.AddRange(EditorExportPolicy.BuildVideoEncoderArguments(exportSettings, encoder, mp4: true));
        // FFmpeg requires files concatenated by the concat demuxer to share the same time base:
        // set an explicit timescale on every segment so -c copy at concat time has no timestamp drift.
        arguments.AddRange(["-video_track_timescale", ComposedVideoTrackTimescale.ToString(CultureInfo.InvariantCulture)]);
        if (!outputAudio)
        {
            arguments.AddRange(["-an"]);
        }
        else
        {
            arguments.AddRange(["-map", "[aout]", "-c:a", "aac",
                "-b:a", exportSettings.AudioBitrateKbps.ToString(CultureInfo.InvariantCulture) + "k",
                "-ar", ComposedAudioSampleRate.ToString(CultureInfo.InvariantCulture), "-ac", "2"]);
        }
        arguments.AddRange(["-movflags", "+faststart", "-progress", "pipe:1", "-nostats", output]);
        return arguments;
    }

    internal static string BuildConcatList(IReadOnlyList<string> segments, IReadOnlyList<double>? durations = null)
    {
        // Per the FFmpeg documentation: a concat file begins with "ffconcat version 1.0"
        // and a "duration" directive for each file so the demuxer joins the timestamps correctly
        // when the duration recorded in the container is off (avoiding gaps/timestamp drift).
        var builder = new StringBuilder();
        builder.Append("ffconcat version 1.0\n");
        for (var index = 0; index < segments.Count; index++)
        {
            var path = segments[index].Replace('\\', '/').Replace("'", "'\\''");
            builder.Append("file '").Append(path).Append("'\n");
            if (durations is not null && index < durations.Count && durations[index] > 0)
            {
                builder.Append("duration ")
                    .Append(durations[index].ToString("0.000", CultureInfo.InvariantCulture))
                    .Append('\n');
            }
        }
        return builder.ToString();
    }

    internal static IReadOnlyList<string> BuildConcatArguments(string listPath, string output, string outputExtension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(output);
        var arguments = new List<string>
        {
            "-y", "-hide_banner", "-loglevel", "error", "-nostdin",
            "-f", "concat", "-safe", "0", "-i", listPath,
            "-c", "copy",
        };
        if (outputExtension.Equals(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            arguments.AddRange(["-video_track_timescale", ComposedVideoTrackTimescale.ToString(CultureInfo.InvariantCulture)]);
            arguments.AddRange(["-movflags", "+faststart"]);
        }
        arguments.Add(output);
        return arguments;
    }

    private static VideoEditSegment NormalizeComposedSegment(VideoEditSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (string.IsNullOrWhiteSpace(segment.InputPath)) throw new InvalidDataException("Đoạn video không có đường dẫn nguồn.");
        var path = Path.GetFullPath(segment.InputPath.Trim());
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= 0) throw new FileNotFoundException("Video nguồn của đoạn không hợp lệ.", path);
        if (segment.Width <= 0 || segment.Height <= 0) throw new InvalidDataException("Kích thước đoạn video không hợp lệ.");
        if (!double.IsFinite(segment.Start) || segment.Start < 0
            || !double.IsFinite(segment.End) || segment.End - segment.Start < .1)
            throw new InvalidDataException("Khoảng thời gian của đoạn video không hợp lệ.");
        var sourceDuration = Math.Max(segment.SourceDuration, segment.End);
        return segment with
        {
            InputPath = path,
            SourceDuration = sourceDuration,
            Start = Math.Min(segment.Start, sourceDuration),
            End = Math.Min(segment.End, sourceDuration),
            Regions = segment.Regions ?? [],
        };
    }

    private string ComposeOutputPath(VideoEditRequest request, string primaryInput)
    {
        var outputDirectory = string.IsNullOrWhiteSpace(request.OutputDirectory)
            ? Path.GetDirectoryName(primaryInput)!
            : Path.GetFullPath(request.OutputDirectory.Trim());
        Directory.CreateDirectory(outputDirectory);
        var defaultExtension = Path.GetExtension(primaryInput).ToLowerInvariant() is ".mkv" or ".mp4"
            ? Path.GetExtension(primaryInput).ToLowerInvariant()
            : ".mp4";
        var fileName = string.IsNullOrWhiteSpace(request.FileName)
            ? Path.GetFileNameWithoutExtension(primaryInput) + "_edited" + defaultExtension
            : FileNamePolicy.Sanitize(request.FileName, "BiliSub_edited.mp4");
        if (Path.GetExtension(fileName).ToLowerInvariant() is not (".mp4" or ".mkv")) fileName += ".mp4";
        return FileNamePolicy.UniquePath(Path.Combine(outputDirectory, fileName), primaryInput);
    }

    private async Task<double?> ProbeSourceFrameRateAsync(string path, CancellationToken cancellationToken)
    {
        var ffprobe = await _tools.EnsureFfprobeAsync(cancellationToken);
        var result = await _processes.RunAsync(ffprobe,
        [
            "-v", "error", "-select_streams", "v:0",
            "-show_entries", "stream=avg_frame_rate",
            "-of", "default=noprint_wrappers=1:nokey=1", path,
        ], cancellationToken);
        if (result.ExitCode != 0) return null;
        var parts = result.StandardOutput.Trim().Split('/');
        if (parts.Length != 2) return null;
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)) return null;
        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator)) return null;
        if (denominator <= 0 || numerator <= 0) return null;
        var frameRate = numerator / denominator;
        return frameRate is > 0 and <= 240 ? frameRate : null;
    }

    private async Task<bool> ProbeSourceHasAudioAsync(string path, CancellationToken cancellationToken)
    {
        var ffprobe = await _tools.EnsureFfprobeAsync(cancellationToken);
        var result = await _processes.RunAsync(ffprobe,
        [
            "-v", "error", "-select_streams", "a",
            "-show_entries", "stream=index",
            "-of", "csv=p=0", path,
        ], cancellationToken);
        return result.ExitCode == 0 && result.StandardOutput.Trim().Length > 0;
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
