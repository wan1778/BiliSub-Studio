namespace BiliSubStudio.Core.Timeline;

// G27 render mapping for speed envelopes. The compiler freezes each clip's
// envelope into explicit spans (sequence window tile + source offset tile +
// rate) so preview and export share one mapping (DEC-013). Spans are
// clip-local: sequence offsets run from the clip's TimelineStartUs, source
// offsets from SourceInUs. Legacy constant-rate clips carry no spans and keep
// the historical arithmetic byte-identical.
public sealed record TimelineResolvedSpeedSpan(
    long SequenceStartUs,
    long SequenceEndUs,
    long SourceOffsetUs,
    long SourceEndOffsetUs,
    double Rate);

public static class TimelineSpeedMapping
{
    public const int MaxSpans = 64;

    // Freezes an envelope into spans. Envelope validation already pinned
    // exact tiling of both dimensions, so the cursors must land exactly.
    public static IReadOnlyList<TimelineResolvedSpeedSpan> BuildSpans(TimelineSpeedEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.Segments.Count > MaxSpans)
            throw new InvalidDataException("Timeline speed envelope vượt 64 span render.");
        var spans = new List<TimelineResolvedSpeedSpan>(envelope.Segments.Count);
        var sequenceCursor = 0L;
        var sourceCursor = 0L;
        foreach (var segment in envelope.Segments)
        {
            var sourceLength = segment.SourceDurationUs;
            spans.Add(new TimelineResolvedSpeedSpan(
                sequenceCursor,
                checked(sequenceCursor + segment.SequenceDurationUs),
                sourceCursor,
                checked(sourceCursor + sourceLength),
                segment.Rate));
            sequenceCursor = checked(sequenceCursor + segment.SequenceDurationUs);
            sourceCursor = checked(sourceCursor + sourceLength);
        }
        return spans;
    }

    // Clip-local sequence offset → clip-local source offset. Null/empty spans
    // keep the constant-rate arithmetic; otherwise piecewise span lookup with
    // boundary equality resolving to the span end.
    public static long SequenceToSourceUs(
        IReadOnlyList<TimelineResolvedSpeedSpan>? spans,
        double playbackRate,
        long sequenceOffsetUs)
    {
        if (sequenceOffsetUs < 0)
            throw new InvalidDataException("Timeline speed mapping sequence offset không được âm.");
        if (spans is not { Count: > 0 })
        {
            if (!double.IsFinite(playbackRate) || playbackRate <= 0)
                throw new InvalidDataException("PlaybackRate phải là số dương hữu hạn.");
            return (long)Math.Round(sequenceOffsetUs * playbackRate, MidpointRounding.AwayFromZero);
        }
        foreach (var span in spans)
        {
            if (sequenceOffsetUs <= span.SequenceEndUs)
            {
                if (sequenceOffsetUs < span.SequenceStartUs)
                    break;
                var offset = Math.Round(
                    (sequenceOffsetUs - span.SequenceStartUs) * span.Rate,
                    MidpointRounding.AwayFromZero);
                return checked(span.SourceOffsetUs + (long)offset);
            }
        }
        throw new InvalidDataException("Timeline speed mapping sequence offset vượt clip window.");
    }

    // Clip-local source offset → clip-local sequence offset (inverse mapping).
    public static long SourceToSequenceUs(
        IReadOnlyList<TimelineResolvedSpeedSpan>? spans,
        double playbackRate,
        long sourceOffsetUs)
    {
        if (sourceOffsetUs < 0)
            throw new InvalidDataException("Timeline speed mapping source offset không được âm.");
        if (spans is not { Count: > 0 })
        {
            if (!double.IsFinite(playbackRate) || playbackRate <= 0)
                throw new InvalidDataException("PlaybackRate phải là số dương hữu hạn.");
            return (long)Math.Round(sourceOffsetUs / playbackRate, MidpointRounding.AwayFromZero);
        }
        foreach (var span in spans)
        {
            if (sourceOffsetUs <= span.SourceEndOffsetUs)
            {
                if (sourceOffsetUs < span.SourceOffsetUs)
                    break;
                var offset = Math.Round(
                    (sourceOffsetUs - span.SourceOffsetUs) / span.Rate,
                    MidpointRounding.AwayFromZero);
                return checked(span.SequenceStartUs + (long)offset);
            }
        }
        throw new InvalidDataException("Timeline speed mapping source offset vượt clip source range.");
    }

    // Piecewise setpts expression re-timing the trimmed source window. The
    // expression is single-quoted so FFmpeg's filtergraph parser keeps the
    // commas inside the if() branches (argv reaches FFmpeg unquoted by any
    // shell). Output PTS starts at zero (STARTPTS base). Null spans return
    // null so callers keep the constant-rate setpts.
    public static string? BuildSetptsExpression(IReadOnlyList<TimelineResolvedSpeedSpan>? spans)
    {
        if (spans is not { Count: > 1 })
            return null;
        var expression = BuildPiecewiseBody(spans);
        return $"'{expression}'";
    }

    private static string BuildPiecewiseBody(IReadOnlyList<TimelineResolvedSpeedSpan> spans)
    {
        // Nested if(lt(PTS-STARTPTS, boundary), branch, fallback): each branch
        // emits the accumulated output offset plus the re-timed remainder.
        // (PTS-STARTPTS) after trim is the source-local offset in seconds.
        string Nested(int index)
        {
            var span = spans[index];
            var rate = span.Rate.ToString("0.#######", System.Globalization.CultureInfo.InvariantCulture);
            var branch = span.SourceOffsetUs == 0
                ? $"(PTS-STARTPTS)/{rate}"
                : $"{Seconds(span.SequenceStartUs)}+(PTS-STARTPTS-{Seconds(span.SourceOffsetUs)})/{rate}";
            if (index == spans.Count - 1)
                return branch;
            var boundary = Seconds(span.SourceEndOffsetUs);
            return $"if(lt(PTS-STARTPTS,{boundary}),{branch},{Nested(index + 1)})";
        }
        return Nested(0);
    }

    private static string Seconds(long microseconds) =>
        (microseconds / (double)TimelineTime.MicrosecondsPerSecond)
            .ToString("0.#######", System.Globalization.CultureInfo.InvariantCulture);

    // Audio re-timing chain (up to and excluding the caller's aresample and
    // volume stages). Absent or single-span envelopes return the constant-rate
    // chain byte-identically; multi-span envelopes split the trimmed source
    // audio into span atrim windows, run each span's atempo chain and concat
    // the results in order.
    public static string BuildAudioChain(
        IReadOnlyList<TimelineResolvedSpeedSpan>? spans,
        string audioTrim,
        int audioInput,
        double playbackRate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioTrim);
        var input = $"[{audioInput}:a]";
        if (spans is not { Count: > 1 })
        {
            if (!double.IsFinite(playbackRate) || playbackRate <= 0)
                throw new InvalidDataException("PlaybackRate phải là số dương hữu hạn.");
            return $"{input}{audioTrim},{TimelineSpeedEnvelope.BuildAtempoChain(playbackRate)}";
        }
        var parts = new List<string>
        {
            $"{input}{audioTrim}asplit={spans.Count}" + string.Concat(
                Enumerable.Range(0, spans.Count).Select(index => $"[sp{audioInput}_{index}]")),
        };
        for (var index = 0; index < spans.Count; index++)
        {
            var span = spans[index];
            var startSeconds = Seconds(span.SourceOffsetUs);
            var durationSeconds = Seconds(span.SourceEndOffsetUs - span.SourceOffsetUs);
            var trim = $"atrim=start={startSeconds}:duration={durationSeconds},asetpts=PTS-STARTPTS";
            var label = index == spans.Count - 1 ? "[spjoin]" : $"[spc{audioInput}_{index}]";
            parts.Add($"[sp{audioInput}_{index}]{trim},{TimelineSpeedEnvelope.BuildAtempoChain(span.Rate)}{label}");
        }
        parts.Add($"[spjoin]concat=n={spans.Count}:v=0:a=1[spcat]");
        return string.Join(";", parts);
    }
}
