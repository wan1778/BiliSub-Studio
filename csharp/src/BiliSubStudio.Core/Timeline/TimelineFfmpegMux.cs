using System.Globalization;
using System.Text.Json;
using BiliSubStudio.Core.Processes;

namespace BiliSubStudio.Core.Timeline;

// G12 real mux follow-up (DEC-022). Renders normalized H.264/AAC segments
// with FFmpeg and joins them with the concat demuxer, then validates the
// muxed streams, duration and A/V drift. Scope is video plus clip source
// audio only: clip items, subtitle cues, voice cues and music are rejected
// explicitly instead of being silently dropped, until their graphs are
// ported. The caller owns processes (one OwnedProcessGroup per job) and
// atomic promotion; every child goes through ProcessRunner with the group.
public sealed record TimelineMuxExpectation(
    int Width,
    int Height,
    double FrameRate,
    bool ExpectAudio,
    long ExpectedDurationUs,
    long MaxDriftUs);

public static class TimelineMuxValidation
{
    public const long TotalDurationToleranceUs = 300_000;

    // Field drift bound: looser than the plan-exact DEC-019 tolerance because
    // AAC priming and container rounding add tens of milliseconds. Plan-level
    // microsecond exactness is still proven by the G8 contracts.
    public static long FieldDriftToleranceUs(long planDriftUs, double frameRate)
    {
        if (planDriftUs < 0)
            throw new InvalidDataException("A/V drift tolerance không được âm.");
        if (!double.IsFinite(frameRate) || frameRate <= 0)
            throw new InvalidDataException("Sequence FPS không hợp lệ cho A/V sync.");
        var frameUs = TimelineTime.MicrosecondsPerSecond / frameRate;
        if (!double.IsFinite(frameUs) || frameUs > long.MaxValue)
            throw new InvalidDataException("Sequence FPS vượt phạm vi A/V sync microseconds.");
        return Math.Max(checked(4 * planDriftUs), Math.Max((long)Math.Ceiling(frameUs), 60_000));
    }

    // Concat-timeline expectation: durations sum over joined clips (clip gaps
    // collapse in the mux), and audio presence follows clip sources only.
    // Voice/music audibility belongs to the final mix expectation instead.
    public static bool HasAudibleSourceAudio(TimelineRenderPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.Audio.SourceAudio
            .Any(source => source.HasSourceStream && source.Gain > 0);
    }

    public static long ConcatDurationUs(TimelineRenderPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.Clips
            .OrderBy(clip => clip.SequenceStartUs)
            .Sum(clip => clip.SequenceDurationUs);
    }

    public static TimelineMuxExpectation BuildExpectation(TimelineRenderPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.ValidateOutputGeometry();
        return new TimelineMuxExpectation(
            plan.OutputWidth,
            plan.OutputHeight,
            plan.OutputFrameRate,
            HasAudibleSourceAudio(plan),
            ConcatDurationUs(plan),
            FieldDriftToleranceUs(plan.Audio.MaximumOutputDriftUs, plan.OutputFrameRate));
    }

    public static void ValidateProbe(string json, TimelineMuxExpectation expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var probe = ParseProbe(json);
        if (probe.Video is null)
            throw new InvalidDataException("Output mux thiếu video stream.");
        if (probe.Video.Width != expected.Width || probe.Video.Height != expected.Height)
            throw new InvalidDataException("Output mux sai output geometry.");
        if (!string.Equals(probe.Video.Codec, "h264", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Output mux phải là H.264.");
        if (Math.Abs(probe.Video.FrameRate - expected.FrameRate) > 0.51)
            throw new InvalidDataException("Output mux sai sequence FPS.");
        if (expected.ExpectAudio)
        {
            if (probe.Audio is null)
                throw new InvalidDataException("Output mux thiếu audio stream.");
            if (!string.Equals(probe.Audio.Codec, "aac", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Output mux audio phải là AAC.");
            if (probe.Audio.SampleRate != TimelineAudioTime.OutputSampleRate)
                throw new InvalidDataException("Output mux audio phải là 48 kHz.");
        }
        if (Math.Abs(probe.FormatDurationUs - expected.ExpectedDurationUs) > TotalDurationToleranceUs)
            throw new InvalidDataException("Output mux sai tổng duration.");
        if (probe.Video.DurationUs is { } videoUs && probe.Audio?.DurationUs is { } audioUs)
        {
            if (Math.Abs(audioUs - videoUs) > expected.MaxDriftUs)
                throw new InvalidDataException("Output mux vượt ngưỡng A/V drift.");
        }
    }

    private sealed record ProbeVideo(int Width, int Height, string Codec, double FrameRate, long? DurationUs);
    private sealed record ProbeAudio(string Codec, int SampleRate, long? DurationUs);
    private sealed record ProbeResult(ProbeVideo? Video, ProbeAudio? Audio, long FormatDurationUs);

    private static ProbeResult ParseProbe(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        ProbeVideo? video = null;
        ProbeAudio? audio = null;
        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var stream in streams.EnumerateArray())
            {
                var type = stream.TryGetProperty("codec_type", out var typeNode)
                    ? typeNode.GetString() : null;
                if (string.Equals(type, "video", StringComparison.Ordinal) && video is null)
                {
                    video = new ProbeVideo(
                        stream.TryGetProperty("width", out var w) ? w.GetInt32() : 0,
                        stream.TryGetProperty("height", out var h) ? h.GetInt32() : 0,
                        stream.TryGetProperty("codec_name", out var c) ? c.GetString() ?? string.Empty : string.Empty,
                        ParseFrameRate(
                            stream.TryGetProperty("avg_frame_rate", out var avg) ? avg.GetString() : null,
                            stream.TryGetProperty("r_frame_rate", out var raw) ? raw.GetString() : null),
                        ParseDurationUs(stream));
                }
                else if (string.Equals(type, "audio", StringComparison.Ordinal) && audio is null)
                {
                    audio = new ProbeAudio(
                        stream.TryGetProperty("codec_name", out var c) ? c.GetString() ?? string.Empty : string.Empty,
                        stream.TryGetProperty("sample_rate", out var rate)
                            && int.TryParse(rate.GetString(), NumberStyles.Integer,
                                CultureInfo.InvariantCulture, out var parsedRate)
                            ? parsedRate : 0,
                        ParseDurationUs(stream));
                }
            }
        }
        var formatUs = 0L;
        if (root.TryGetProperty("format", out var format)
            && format.ValueKind == JsonValueKind.Object
            && format.TryGetProperty("duration", out var durationNode)
            && double.TryParse(durationNode.GetString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var seconds))
        {
            formatUs = (long)Math.Round(seconds * TimelineTime.MicrosecondsPerSecond,
                MidpointRounding.AwayFromZero);
        }
        return new ProbeResult(video, audio, formatUs);
    }

    private static long? ParseDurationUs(JsonElement stream)
    {
        if (stream.TryGetProperty("duration", out var node)
            && double.TryParse(node.GetString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var seconds)
            && double.IsFinite(seconds) && seconds >= 0)
        {
            return (long)Math.Round(seconds * TimelineTime.MicrosecondsPerSecond,
                MidpointRounding.AwayFromZero);
        }
        return null;
    }

    private static double ParseFrameRate(string? average, string? raw)
    {
        foreach (var value in new[] { average, raw })
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            var parts = value.Split('/', 2);
            if (!double.TryParse(parts[0], NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var numerator))
                continue;
            if (parts.Length == 1 && numerator > 0) return numerator;
            if (double.TryParse(parts[1], NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var denominator)
                && denominator != 0 && numerator > 0)
                return numerator / denominator;
        }
        return 0;
    }
}

public static class TimelineFfmpegArguments
{
    public static IReadOnlyList<string> BuildSegment(
        TimelineRenderSegment segment, string renderingPath, string? assPath = null)
    {
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentException.ThrowIfNullOrWhiteSpace(renderingPath);
        TimelineRenderCapability.EnsureMuxSupported(segment.Plan);
        var plan = segment.Plan;
        plan.ValidateOutputGeometry();
        var sourceDurationUs = checked(segment.Clip.SourceOutUs - segment.Clip.SourceInUs);
        if (sourceDurationUs <= 0)
            throw new InvalidDataException("Timeline segment source range không hợp lệ.");
        var startSeconds = ToSeconds(segment.Clip.SourceInUs);
        var durationSeconds = ToSeconds(sourceDurationUs);
        var rate = segment.Clip.PlaybackRate;
        if (!double.IsFinite(rate) || rate <= 0)
            throw new InvalidDataException("Timeline segment PlaybackRate không hợp lệ.");
        // G24/G27: a speed envelope re-times the segment piecewise. The clip's
        // stored sequence window stays fixed; single-span or absent envelopes
        // degrade to the constant-rate path byte-identically. Audio re-timing
        // composes per-span atrim/atempo chains over the same source window.
        var spans = segment.Clip.SpeedSpans;
        if (spans is { Count: > 0 }
            && (spans.Count > TimelineSpeedMapping.MaxSpans
                || spans[^1].SequenceEndUs != segment.DurationUs))
            throw new InvalidDataException("Timeline segment speed spans không phủ đúng segment window.");
        // G26: the clip's grade composes into the effect head (same source-
        // coordinate stage, deterministic eq/colorbalance chain); absent or
        // neutral grades keep the G12 fast path byte-identical.
        var grade = plan.ColorGrades.FirstOrDefault(spec =>
            string.Equals(spec.ClipId, segment.Clip.ClipId, StringComparison.Ordinal))?.Grade;

        // Segment audio layout follows clip sources only, so every segment of
        // one export carries identical streams for the concat demuxer. Voice
        // cues and global music join later in the final mix pass.
        var audibleOutput = TimelineMuxValidation.HasAudibleSourceAudio(plan);
        var audibleClip = segment.SourceAudio.HasSourceStream && segment.SourceAudio.Gain > 0;
        var logos = TimelineEffectGraph.MapLogosForSegment(segment);
        var arguments = new List<string>
        {
            "-y", "-hide_banner", "-loglevel", "error", "-nostdin",
            "-i", segment.Asset.Path,
        };
        foreach (var logo in logos)
            arguments.AddRange(["-loop", "1", "-framerate", "1", "-i", logo.Path]);
        // Logo inputs sit between the source and the optional silence input,
        // so the audio input index shifts only when lavfi is actually added.
        var audioInput = 0;
        if (audibleOutput && !audibleClip)
        {
            arguments.AddRange(["-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo"]);
            audioInput = 1 + logos.Count;
        }
        // Windowing stays inside the filtergraph (trim/atrim): output -ss/-t
        // cannot bound an infinite lavfi silence input, which previously left
        // a full-window audio tail after atempo rate conversion. Clip effects
        // and subtitle burns compose ahead of trim in source coordinates, so
        // enable windows and ASS timestamps stay exact downstream. Logos layer
        // after normalize in output coordinates with source-second windows.
        var composed = TimelineEffectGraph.BuildComposedVideoFilter(segment, assPath);
        // G26 grade prepends to the effect head: it must apply before trim so
        // enable windows and ASS timestamps downstream stay in source time.
        // The composed body already opens with its own [0:v] label, so the
        // grade chain splices in right after that label.
        var gradeFilter = TimelineColorGradeGraph.BuildFilter(grade);
        // G28: wired keyframe channels (rotation, opacity) render as one
        // filter stage. Rotation uses the clip-local T (source seconds); the
        // opacity yuva420p roundtrip forces the stage after normalize, so the
        // keyframe stage runs last, right before [vout].
        var keyframeSet = plan.KeyframeSets.FirstOrDefault(set =>
            string.Equals(set.ClipId, segment.Clip.ClipId, StringComparison.Ordinal));
        var keyframeFilter = TimelineKeyframeGraph.BuildVideoFilters(keyframeSet);
        var head = composed is null
            ? (gradeFilter is null ? "[0:v]" : $"[0:v]{gradeFilter}[gx];[gx]")
            : gradeFilter is null
                ? $"{composed}[fx];[fx]"
                : $"[0:v]{gradeFilter},{composed["[0:v]".Length..]}[fx];[fx]";
        var normalized =
            $"trim=start={startSeconds}:duration={durationSeconds}," +
            $"setpts=(PTS-STARTPTS)/{FormatNumber(rate)}[sv];" +
            $"[sv]scale={plan.OutputWidth}:{plan.OutputHeight}," +
            $"setsar=1,fps={FormatNumber(plan.OutputFrameRate)},format=yuv420p";
        // G27: piecewise envelope re-timing replaces the constant setpts scale.
        var envelopeSetpts = TimelineSpeedMapping.BuildSetptsExpression(spans);
        if (envelopeSetpts is not null)
        {
            var replaced = normalized.Replace(
                $"setpts=(PTS-STARTPTS)/{FormatNumber(rate)}",
                $"setpts={envelopeSetpts}", StringComparison.Ordinal);
            if (string.Equals(replaced, normalized, StringComparison.Ordinal))
                throw new InvalidOperationException("Timeline speed envelope không nối được vào setpts.");
            normalized = replaced;
        }
        // The classic body already opens with its own [0:v] input label.
        // Without logos the chain stays byte-identical to the G12 graph.
        // The logo chain terminates at [vout] itself. G28 keyframes splice
        // between normalize and vout so the alpha roundtrip stays safe.
        var tail = keyframeFilter is null ? string.Empty : $",{keyframeFilter}";
        var videoFilter = logos.Count == 0
            ? $"{head}{normalized}{tail}[vout]"
            : $"{head}{normalized}{tail}[nv];{BuildLogoChain(logos, "nv", ToSeconds(segment.DurationUs))}";
        string audioFilter = string.Empty;
        if (audibleOutput)
        {
            var audioTrim = audibleClip
                ? $"atrim=start={startSeconds}:duration={durationSeconds},asetpts=PTS-STARTPTS"
                : $"atrim=duration={durationSeconds},asetpts=PTS-STARTPTS";
            // G27: per-span re-timing splits the trimmed source audio into
            // span atrim windows, applies each span's atempo chain, then
            // concats them back in order. Absent/single-span envelopes stay
            // on the constant-rate chain byte-identically (aresample sits
            // between the trim and the rate filters, as in the G12 graph).
            var spanAudio = TimelineSpeedMapping.BuildAudioChain(
                spans, audioTrim, audioInput, rate);
            audioFilter = spanAudio
                + $",volume={FormatNumber(segment.SourceAudio.Gain)}[aout]";
        }
        arguments.AddRange([
            "-filter_complex", audibleOutput ? videoFilter + ";" + audioFilter : videoFilter,
            "-map", "[vout]",
        ]);
        if (audibleOutput)
        {
            arguments.AddRange(["-map", "[aout]", "-c:a", "aac", "-b:a", "128k",
                "-ar", "48000", "-ac", "2"]);
        }
        else
        {
            arguments.Add("-an");
        }
        arguments.AddRange(["-c:v", "libx264", "-preset", "veryfast", "-crf", "23",
            "-movflags", "+faststart", renderingPath]);
        return arguments;
    }

    // Logo overlays mirror the classic composer (rgba scale, alpha mixer,
    // clamped pixels) with one documented extension: source-second enable
    // windows for clip-local and sequence-global items. The looped still
    // input is trimmed to the segment output duration: without a bound the
    // infinite logo stream would keep the mux running forever.
    private static string BuildLogoChain(
        IReadOnlyList<TimelineLogoOverlay> logos, string current, string outputDurationSeconds)
    {
        var parts = new List<string>();
        for (var index = 0; index < logos.Count; index++)
        {
            var logo = logos[index];
            var label = $"l{index}";
            var output = index == logos.Count - 1 ? "vout" : $"o{index}";
            parts.Add($"[{index + 1}:v]trim=duration={outputDurationSeconds}," +
                $"format=rgba,scale={logo.Width}:{logo.Height}:flags=lanczos," +
                $"colorchannelmixer=aa={logo.Opacity.ToString("0.000", CultureInfo.InvariantCulture)}[{label}]");
            var enable = logo.WholeVideo
                ? string.Empty
                : $":enable='between(t,{logo.StartSeconds.ToString("0.000", CultureInfo.InvariantCulture)}," +
                  $"{logo.EndSeconds.ToString("0.000", CultureInfo.InvariantCulture)})'";
            parts.Add($"[{current}][{label}]overlay={logo.X}:{logo.Y}:eof_action=repeat:shortest=0{enable}[{output}]");
            current = output;
        }
        return string.Join(";", parts);
    }

    public static IReadOnlyList<string> BuildConcat(string listPath, string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        return ["-y", "-hide_banner", "-loglevel", "error", "-nostdin",
            "-f", "concat", "-safe", "0", "-i", listPath,
            "-c", "copy", "-movflags", "+faststart", outputPath];
    }

    public static IReadOnlyList<string> BuildProbe(string inputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        return ["-v", "error",
            "-show_entries",
            "stream=index,codec_type,codec_name,width,height,sample_rate,avg_frame_rate,r_frame_rate,duration:format=duration",
            "-of", "json", inputPath];
    }

    private static string AtempoChain(double rate)
    {
        var parts = new List<string>();
        var remaining = rate;
        while (remaining > 100)
        {
            parts.Add("atempo=100");
            remaining /= 100;
        }
        while (remaining is > 0 and < 0.5)
        {
            parts.Add("atempo=0.5");
            remaining *= 2;
        }
        parts.Add("atempo=" + FormatNumber(remaining));
        return string.Join(",", parts);
    }

    private static string FormatNumber(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string ToSeconds(long microseconds) =>
        (microseconds / (double)TimelineTime.MicrosecondsPerSecond)
        .ToString("0.000000", CultureInfo.InvariantCulture);
}

public sealed class TimelineFfmpegSegmentRenderer : ITimelineExportSegmentRenderer
{
    private readonly string _ffmpeg;
    private readonly ProcessRunner _processes = new();

    public TimelineFfmpegSegmentRenderer(string ffmpegPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);
        _ffmpeg = ffmpegPath.Trim();
    }

    public async Task RenderSegmentAsync(
        TimelineRenderSegment segment,
        string renderingPath,
        OwnedProcessGroup processes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentException.ThrowIfNullOrWhiteSpace(renderingPath);
        ArgumentNullException.ThrowIfNull(processes);
        var arguments = TimelineFfmpegArguments.BuildSegment(segment, renderingPath, segment.AssPath);
        var result = await _processes.RunAsync(
            _ffmpeg, arguments, cancellationToken, owner: processes).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"FFmpeg segment {segment.SegmentIndex} thất bại: {result.StandardError.Trim()} " +
                $"[argv: {string.Join(" ", arguments)}]");
    }
}

public interface ITimelineExportConcatenator
{
    Task ConcatAsync(
        TimelineConcatListArtifact list,
        string renderingPath,
        TimelineMuxExpectation expectation,
        OwnedProcessGroup processes,
        CancellationToken cancellationToken);
}

public sealed class TimelineFfmpegConcatenator : ITimelineExportConcatenator
{
    private readonly string _ffmpeg;
    private readonly string _ffprobe;
    private readonly ProcessRunner _processes = new();

    public TimelineFfmpegConcatenator(string ffmpegPath, string ffprobePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(ffprobePath);
        _ffmpeg = ffmpegPath.Trim();
        _ffprobe = ffprobePath.Trim();
    }

    public async Task ConcatAsync(
        TimelineConcatListArtifact list,
        string renderingPath,
        TimelineMuxExpectation expectation,
        OwnedProcessGroup processes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentException.ThrowIfNullOrWhiteSpace(renderingPath);
        ArgumentNullException.ThrowIfNull(expectation);
        ArgumentNullException.ThrowIfNull(processes);
        var mux = await _processes.RunAsync(
            _ffmpeg,
            TimelineFfmpegArguments.BuildConcat(list.ListPath, renderingPath),
            cancellationToken,
            owner: processes).ConfigureAwait(false);
        if (mux.ExitCode != 0)
            throw new InvalidOperationException(
                $"FFmpeg concat thất bại: {mux.StandardError.Trim()} " +
                $"[argv: {string.Join(" ", TimelineFfmpegArguments.BuildConcat(list.ListPath, renderingPath))}]");
        var probe = await _processes.RunAsync(
            _ffprobe,
            TimelineFfmpegArguments.BuildProbe(renderingPath),
            cancellationToken,
            owner: processes).ConfigureAwait(false);
        if (probe.ExitCode != 0)
            throw new InvalidOperationException(
                $"FFprobe mux thất bại: {probe.StandardError.Trim()}");
        TimelineMuxValidation.ValidateProbe(probe.StandardOutput, expectation);
    }
}
