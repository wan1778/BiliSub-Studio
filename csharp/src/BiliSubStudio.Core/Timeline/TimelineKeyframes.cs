namespace BiliSubStudio.Core.Timeline;

// G25 keyframes (user-approved Concat parity, 2026-09-08). Keyframes ride the
// same channels as G21 transforms but interpolate over the clip's sequence
// window: values are output-frame fractions so they stay resolution
// independent, times are clip-local sequence microseconds bounded to the clip
// window. A channel with one keyframe is constant; empty channels are absent
// by construction (omitted in JSON).
public enum TimelineKeyframeChannel
{
    ScaleX = 0,
    ScaleY = 1,
    PositionX = 2,
    PositionY = 3,
    RotationDegrees = 4,
    Opacity = 5,
}

public enum TimelineKeyframeEasing
{
    Linear = 0,
    EaseIn = 1,
    EaseOut = 2,
    EaseInOut = 3,
    Hold = 4,
}

public sealed record TimelineKeyframe(long SequenceOffsetUs, double Value, TimelineKeyframeEasing Easing)
{
    public void Validate(long clipSequenceDurationUs)
    {
        if (SequenceOffsetUs < 0 || SequenceOffsetUs > clipSequenceDurationUs)
            throw new InvalidDataException("Timeline keyframe offset phải nằm trong clip window.");
        if (!double.IsFinite(Value))
            throw new InvalidDataException("Timeline keyframe value phải hữu hạn.");
        if (!Enum.IsDefined(typeof(TimelineKeyframeEasing), Easing))
            throw new InvalidDataException("Timeline keyframe easing không hợp lệ.");
    }
}

public sealed record TimelineKeyframeTrack(
    TimelineKeyframeChannel Channel,
    IReadOnlyList<TimelineKeyframe> Keyframes)
{
    public const int MaxKeyframesPerTrack = 256;

    public void Validate(long clipSequenceDurationUs)
    {
        if (!Enum.IsDefined(typeof(TimelineKeyframeChannel), Channel))
            throw new InvalidDataException("Timeline keyframe channel không hợp lệ.");
        ArgumentNullException.ThrowIfNull(Keyframes);
        if (Keyframes.Count == 0)
            throw new InvalidDataException("Timeline keyframe track cần ít nhất một keyframe.");
        if (Keyframes.Count > MaxKeyframesPerTrack)
            throw new InvalidDataException("Timeline keyframe track vượt 256 keyframe.");
        foreach (var keyframe in Keyframes)
            keyframe.Validate(clipSequenceDurationUs);
        for (var index = 1; index < Keyframes.Count; index++)
        {
            if (Keyframes[index].SequenceOffsetUs < Keyframes[index - 1].SequenceOffsetUs)
                throw new InvalidDataException("Timeline keyframe track phải sắp theo offset tăng dần.");
        }
        // Duplicate offsets would make interpolation ambiguous; the first
        // segment would silently win. Values may still repeat (hold-style).
        for (var index = 1; index < Keyframes.Count; index++)
        {
            if (Keyframes[index].SequenceOffsetUs == Keyframes[index - 1].SequenceOffsetUs)
                throw new InvalidDataException("Timeline keyframe track chứa offset trùng.");
        }
    }

    public static IComparer<TimelineKeyframe> OffsetComparer { get; } =
        Comparer<TimelineKeyframe>.Create((left, right) => left.SequenceOffsetUs.CompareTo(right.SequenceOffsetUs));
}

// Per-clip keyframe set. Validation is clip-scoped because offsets are
// clip-local: the compiler passes the owning clip's sequence duration.
public sealed record TimelineKeyframes(
    string ClipId,
    IReadOnlyList<TimelineKeyframeTrack> Tracks)
{
    public const int MaxTracks = 8;

    public static TimelineKeyframes Create(
        string clipId,
        IReadOnlyList<TimelineKeyframeTrack> tracks,
        long clipSequenceDurationUs)
    {
        var set = new TimelineKeyframes(clipId, tracks);
        set.Validate(clipSequenceDurationUs);
        return set;
    }

    public TimelineKeyframeTrack? Find(TimelineKeyframeChannel channel) =>
        Tracks.FirstOrDefault(track => track.Channel == channel);

    public double Evaluate(TimelineKeyframeChannel channel, long sequenceOffsetUs)
    {
        var track = Find(channel)
            ?? throw new InvalidDataException("Timeline keyframes không có channel này.");
        if (sequenceOffsetUs < 0)
            throw new InvalidDataException("Timeline keyframes offset không được âm.");
        var keyframes = track.Keyframes;
        if (keyframes.Count == 1 || sequenceOffsetUs <= keyframes[0].SequenceOffsetUs)
            return keyframes[0].Value;
        var last = keyframes[^1];
        if (sequenceOffsetUs >= last.SequenceOffsetUs)
            return last.Value;
        var index = 1;
        while (index < keyframes.Count && keyframes[index].SequenceOffsetUs < sequenceOffsetUs)
            index++;
        var from = keyframes[index - 1];
        var to = keyframes[index];
        var span = to.SequenceOffsetUs - from.SequenceOffsetUs;
        var progress = (sequenceOffsetUs - from.SequenceOffsetUs) / (double)span;
        var eased = from.Easing switch
        {
            TimelineKeyframeEasing.Linear => progress,
            TimelineKeyframeEasing.Hold => 0,
            TimelineKeyframeEasing.EaseIn => progress * progress,
            TimelineKeyframeEasing.EaseOut => 1 - (1 - progress) * (1 - progress),
            _ => progress * progress * (3 - 2 * progress),
        };
        return checked(from.Value + (to.Value - from.Value) * eased);
    }

    public void Validate(long clipSequenceDurationUs)
    {
        if (ClipId is not { Length: 32 } || !ClipId.All(character => char.IsAsciiHexDigit(character)))
            throw new InvalidDataException("Timeline keyframes ClipId không hợp lệ.");
        ArgumentNullException.ThrowIfNull(Tracks);
        if (Tracks.Count > MaxTracks)
            throw new InvalidDataException("Timeline keyframes vượt 8 channel.");
        if (Tracks.Select(track => track.Channel).Distinct().Count() != Tracks.Count)
            throw new InvalidDataException("Timeline keyframes chứa channel trùng.");
        foreach (var track in Tracks)
            track.Validate(clipSequenceDurationUs);
    }
}
