namespace BiliSubStudio.Core.Timeline;

public enum TimelineDragKind
{
    None,
    Reorder,
    TrimIn,
    TrimOut,
}

public readonly record struct TimelineTrimProposal(long SourceInUs, long SourceOutUs);

// G17 pointer math. It returns proposals only; the page commits one proposal
// through TimelineEditingController when the captured pointer is released.
public static class TimelineDragMath
{
    public static int ReorderPosition(
        IReadOnlyList<TimelineClip> clips,
        string clipId,
        long pointerSequenceUs)
    {
        ArgumentNullException.ThrowIfNull(clips);
        if (pointerSequenceUs < 0)
            throw new InvalidDataException("Vị trí kéo Timeline không được âm.");
        var ordered = clips.OrderBy(clip => clip.TimelineStartUs).ToArray();
        TimelineSequence.ValidateSequential(ordered);
        var current = Array.FindIndex(ordered, clip =>
            string.Equals(clip.Id, clipId, StringComparison.Ordinal));
        if (current < 0)
            throw new InvalidDataException("Timeline clip không tồn tại để kéo.");

        var insertion = ordered.Count(clip =>
            pointerSequenceUs >= checked(clip.TimelineStartUs + clip.DurationUs / 2));
        if (insertion > current)
            insertion--;
        return Math.Clamp(insertion, 0, ordered.Length - 1);
    }

    public static TimelineTrimProposal TrimProposal(
        TimelineClip clip,
        TimelineMediaAsset asset,
        double sequenceFrameRate,
        TimelineDragKind kind,
        long pointerSequenceUs)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(asset);
        clip.ValidateAgainst(asset);
        if (kind is not TimelineDragKind.TrimIn and not TimelineDragKind.TrimOut)
            throw new InvalidDataException("Kiểu kéo trim Timeline không hợp lệ.");

        var oneFrameUs = TimelineTime.SnapToFrameGrid(
            TimelineTime.FromSeconds(1 / sequenceFrameRate), sequenceFrameRate);
        if (oneFrameUs <= 0 || clip.DurationUs <= oneFrameUs)
            return new TimelineTrimProposal(clip.SourceInUs, clip.SourceOutUs);

        if (pointerSequenceUs < 0)
            throw new InvalidDataException("Vị trí kéo Timeline không được âm.");
        if ((kind == TimelineDragKind.TrimIn && pointerSequenceUs <= clip.TimelineStartUs)
            || (kind == TimelineDragKind.TrimOut && pointerSequenceUs >= clip.TimelineEndUs))
            return new TimelineTrimProposal(clip.SourceInUs, clip.SourceOutUs);
        var snapped = TimelineTime.SnapToFrameGrid(pointerSequenceUs, sequenceFrameRate);
        var minimum = kind == TimelineDragKind.TrimIn
            ? clip.TimelineStartUs
            : checked(clip.TimelineStartUs + oneFrameUs);
        var maximum = kind == TimelineDragKind.TrimIn
            ? checked(clip.TimelineEndUs - oneFrameUs)
            : clip.TimelineEndUs;
        var sequenceTimeUs = Math.Clamp(snapped, minimum, maximum);
        var source = TimelineTime.SequenceToSourceUs(
            sequenceTimeUs,
            clip.TimelineStartUs,
            clip.SourceInUs,
            clip.PlaybackRate);
        source = Math.Clamp(source, clip.SourceInUs, clip.SourceOutUs);
        var minimumSourceDuration = Math.Ceiling(oneFrameUs * clip.PlaybackRate);
        if (!double.IsFinite(minimumSourceDuration) || minimumSourceDuration > long.MaxValue)
            throw new InvalidDataException("Trim Timeline vượt phạm vi int64 microseconds.");
        var minimumSourceDurationUs = Math.Max(1, (long)minimumSourceDuration);
        source = kind == TimelineDragKind.TrimIn
            ? Math.Min(source, checked(clip.SourceOutUs - minimumSourceDurationUs))
            : Math.Max(source, checked(clip.SourceInUs + minimumSourceDurationUs));

        return kind == TimelineDragKind.TrimIn
            ? new TimelineTrimProposal(source, clip.SourceOutUs)
            : new TimelineTrimProposal(clip.SourceInUs, source);
    }
}
