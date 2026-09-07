using System.Globalization;

namespace BiliSubStudio.Core.Timeline;

// G22 transition filtergraph (user-approved Concat parity, 2026-09-08).
// Transitions between primary-track clips map to FFmpeg xfade. The concat
// demuxer renders per-segment files, so cross-segment transitions cannot use
// two live inputs: instead each segment pair renders independently and the
// transition applies via a dedicated xfade pass over the two segment outputs.
// This builder computes exact xfade offsets in sequence seconds so preview and
// export share one arithmetic (DEC-013 single-plan contract).
public static class TimelineTransitionGraph
{
    // Resolves one transition into (xfadeFilter, offsetSeconds) anchored to the
    // two resolved primary clips it bridges. Rejects clips that are not
    // sequential neighbours or transitions longer than either clip.
    public static (string XfadeName, long OffsetUs) Resolve(
        TimelineResolvedTransition transition,
        TimelineRenderPlan plan)
    {
        ArgumentNullException.ThrowIfNull(transition);
        ArgumentNullException.ThrowIfNull(plan);
        var from = plan.Clips.FirstOrDefault(clip =>
            string.Equals(clip.ClipId, transition.FromClipId, StringComparison.Ordinal))
            ?? throw new InvalidDataException("Transition from-clip không có trong render plan.");
        var to = plan.Clips.FirstOrDefault(clip =>
            string.Equals(clip.ClipId, transition.ToClipId, StringComparison.Ordinal))
            ?? throw new InvalidDataException("Transition to-clip không có trong render plan.");
        if (to.SequenceStartUs != from.SequenceEndUs)
            throw new InvalidDataException(
                "Transition chỉ hợp lệ giữa hai clip nối tiếp liên tục trên cùng track.");
        if (transition.DurationUs <= 0)
            throw new InvalidDataException("Transition duration phải dương.");
        if (transition.DurationUs > from.SequenceDurationUs
            || transition.DurationUs > to.SequenceDurationUs)
            throw new InvalidDataException(
                "Transition duration không được vượt quá độ dài một trong hai clip.");
        // xfade offset counts from the start of the *output* of the first input
        // in seconds; with sequential clips the cross point is the from-clip end.
        var offsetUs = from.SequenceEndUs - transition.DurationUs;
        if (offsetUs < 0)
            throw new InvalidDataException("Transition offset âm — duration vượt from-clip.");
        return (transition.XfadeName, offsetUs);
    }

    // Builds the complete xfade chain arguments for every transition in the
    // plan, given the per-segment output files in primary order. Returns the
    // filter_complex string plus the expected extra arguments (inputs already
    // opened by the caller in the same order as plan.Clips).
    public static string BuildXfadeChain(
        IReadOnlyList<TimelineResolvedTransition> transitions,
        IReadOnlyList<TimelineRenderSegment> segments,
        int outputWidth,
        int outputHeight,
        double outputFrameRate)
    {
        ArgumentNullException.ThrowIfNull(transitions);
        ArgumentNullException.ThrowIfNull(segments);
        if (transitions.Count == 0)
            throw new InvalidDataException("Không có transition để dựng xfade chain.");
        if (segments.Count < transitions.Count + 1)
            throw new InvalidDataException("Số segment không đủ cho xfade chain.");
        var segmentByClipId = segments.ToDictionary(segment => segment.Clip.ClipId, StringComparer.Ordinal);
        var chain = string.Empty;
        var previousLabel = "[0:v]";
        var inputIndex = 1;
        foreach (var transition in transitions)
        {
            var resolution = Resolve(transition, segments[0].Plan);
            _ = segmentByClipId[transition.ToClipId];
            var fromSeconds = ToSeconds(resolution.OffsetUs);
            chain += $"{previousLabel}[{inputIndex}:v]" +
                     $"xfade=transition={transition.XfadeName}" +
                     $":duration={ToSeconds(transition.DurationUs)}" +
                     $":offset={fromSeconds}" +
                     $":rate={FormatRate(outputFrameRate)}" +
                     $":width={outputWidth}:height={outputHeight}" +
                     $"[vx{inputIndex}];";
            previousLabel = $"[vx{inputIndex}]";
            inputIndex++;
        }
        return chain.TrimEnd(';');
    }

    private static string ToSeconds(long microseconds) =>
        (microseconds / 1_000_000.0).ToString("0.#######", CultureInfo.InvariantCulture);

    private static string FormatRate(double rate) =>
        rate.ToString("0.###", CultureInfo.InvariantCulture);
}
