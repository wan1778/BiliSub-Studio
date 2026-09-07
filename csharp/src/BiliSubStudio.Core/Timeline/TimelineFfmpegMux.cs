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
public static class TimelineRenderCapability
{
    public static void EnsureVideoAudioOnly(TimelineRenderPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Items.Count != 0)
            throw new InvalidOperationException(
                "Timeline FFmpeg mux chưa hỗ trợ clip item/effect; plan phải chỉ có video và source audio.");
        if (plan.SubtitleCues.Count != 0)
            throw new InvalidOperationException(
                "Timeline FFmpeg mux chưa hỗ trợ subtitle cue; plan phải chỉ có video và source audio.");
        if (plan.Audio.VoiceCues.Count != 0)
            throw new InvalidOperationException(
                "Timeline FFmpeg mux chưa hỗ trợ voice cue; plan phải chỉ có video và source audio.");
        if (plan.Audio.Music is not null)
            throw new InvalidOperationException(
                "Timeline FFmpeg mux chưa hỗ trợ music; plan phải chỉ có video và source audio.");
    }
}

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

    public static TimelineMuxExpectation BuildExpectation(TimelineRenderPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.ValidateOutputGeometry();
        return new TimelineMuxExpectation(
            plan.OutputWidth,
            plan.OutputHeight,
            plan.OutputFrameRate,
            plan.Audio.HasAudibleAudio,
            plan.TotalDurationUs,
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
        TimelineRenderSegment segment, string renderingPath)
    {
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentException.ThrowIfNullOrWhiteSpace(renderingPath);
        TimelineRenderCapability.EnsureVideoAudioOnly(segment.Plan);
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

        var audibleOutput = plan.Audio.HasAudibleAudio;
        var audibleClip = segment.SourceAudio.HasSourceStream && segment.SourceAudio.Gain > 0;
        var arguments = new List<string>
        {
            "-y", "-hide_banner", "-loglevel", "error", "-nostdin",
            "-i", segment.Asset.Path,
        };
        var audioInput = 0;
        if (audibleOutput && !audibleClip)
        {
            arguments.AddRange(["-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo"]);
            audioInput = 1;
        }
        // Windowing stays inside the filtergraph (trim/atrim): output -ss/-t
        // cannot bound an infinite lavfi silence input, which previously left
        // a full-window audio tail after atempo rate conversion.
        var videoFilter =
            $"[0:v]trim=start={startSeconds}:duration={durationSeconds}," +
            $"setpts=(PTS-STARTPTS)/{FormatNumber(rate)}[sv];" +
            $"[sv]scale={plan.OutputWidth}:{plan.OutputHeight}," +
            $"setsar=1,fps={FormatNumber(plan.OutputFrameRate)},format=yuv420p[vout]";
        string audioFilter = string.Empty;
        if (audibleOutput)
        {
            var audioTrim = audibleClip
                ? $"atrim=start={startSeconds}:duration={durationSeconds},asetpts=PTS-STARTPTS"
                : $"atrim=duration={durationSeconds},asetpts=PTS-STARTPTS";
            audioFilter =
                $"[{audioInput}:a]{audioTrim},aresample=48000,{AtempoChain(rate)}," +
                $"volume={FormatNumber(segment.SourceAudio.Gain)}[aout]";
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
        var arguments = TimelineFfmpegArguments.BuildSegment(segment, renderingPath);
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
