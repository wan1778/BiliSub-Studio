using System.Globalization;

namespace BiliSubStudio.Core.Timeline;

// G28 keyframe render mapping. Channels that map to per-frame FFmpeg
// expressions render on the primary segment: RotationDegrees becomes a
// rotate expression and Opacity becomes a geq alpha expression, both
// evaluated against the clip-local sequence time T in seconds (identical to
// the re-timed PTS under a speed envelope, so speed and keyframes compose).
// Position/Scale channels need the overlay compositing stage (G31) and stay
// explicitly rejected by the mux capability gate. Easing maps to closed-form
// polynomial expressions so preview and export share one arithmetic.
public static class TimelineKeyframeGraph
{
    public static string BuildExpression(TimelineKeyframeTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        var keyframes = track.Keyframes;
        if (keyframes.Count == 0)
            throw new InvalidDataException("Timeline keyframe track cần ít nhất một keyframe.");
        if (keyframes.Count == 1)
            return Fmt(keyframes[0].Value);
        string Branch(int index)
        {
            var from = keyframes[index];
            var to = keyframes[index + 1];
            var spanSeconds = Seconds(to.SequenceOffsetUs - from.SequenceOffsetUs);
            var progress = $"((T-{Seconds(from.SequenceOffsetUs)})/{spanSeconds})";
            var eased = from.Easing switch
            {
                TimelineKeyframeEasing.Linear => progress,
                TimelineKeyframeEasing.Hold => "0",
                TimelineKeyframeEasing.EaseIn => $"pow({progress},2)",
                TimelineKeyframeEasing.EaseOut => $"1-pow(1-{progress},2)",
                _ => $"pow({progress},2)*(3-2*{progress})",
            };
            return $"{Fmt(from.Value)}+({Fmt(to.Value)}-{Fmt(from.Value)})*{eased}";
        }
        // Piecewise if chain: clamps before the first and after the last
        // keyframe, easing inside each segment. Boundary equality resolves to
        // the next segment's start value, which equals the previous end value.
        string Nested(int index)
        {
            var branch = Branch(index);
            if (index == keyframes.Count - 2)
                return branch;
            var boundary = Seconds(keyframes[index + 1].SequenceOffsetUs);
            return $"if(lt(T,{boundary}),{branch},{Nested(index + 1)})";
        }
        var first = Seconds(keyframes[0].SequenceOffsetUs);
        return keyframes.Count == 2
            ? $"if(lt(T,{Seconds(keyframes[1].SequenceOffsetUs)}),{Branch(0)},{Fmt(keyframes[^1].Value)})"
            : $"if(lt(T,{first}),{Fmt(keyframes[0].Value)},{Nested(0)})";
    }

    // Deterministic filter chain for the wired channels of one keyframe set.
    // Null when the set carries no rotation and no opacity track so segments
    // without keyframes keep the fast path byte-identical.
    public static string? BuildVideoFilters(TimelineKeyframes? set)
    {
        if (set is null)
            return null;
        var parts = new List<string>();
        var rotation = set.Find(TimelineKeyframeChannel.RotationDegrees);
        if (rotation is not null)
            parts.Add($"rotate=angle='{BuildExpression(rotation)}':c=black:ow=rotw(iw):oh=roth(ih)");
        var opacity = set.Find(TimelineKeyframeChannel.Opacity);
        if (opacity is not null)
            parts.Add(
                $"format=yuva420p,geq=lum='lum(X,Y)':cb='cb(X,Y)':cr='cr(X,Y)':a='p(X,Y)*({BuildExpression(opacity)})'," +
                "format=yuv420p");
        return parts.Count == 0 ? null : string.Join(",", parts);
    }

    private static string Seconds(long microseconds) =>
        (microseconds / (double)TimelineTime.MicrosecondsPerSecond)
            .ToString("0.#######", CultureInfo.InvariantCulture);

    private static string Fmt(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);
}
