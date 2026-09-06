namespace BiliSubStudio.Core.Timeline;

// G3 sequence (MASTER_PLAN sections 4/7). MVP is one sequential primary
// video track with hard cuts. Output resolution defaults to the first clip
// (PDEC-001 recommendation); sequence FPS is always explicit and never
// varies inside the output (PDEC-002 recommendation).
public sealed record TimelineSequence(
    IReadOnlyList<TimelineClip> VideoClips,
    int OutputWidth,
    int OutputHeight,
    double OutputFrameRate)
{
    public long TotalDurationUs => VideoClips.Count == 0
        ? 0
        : VideoClips.Max(clip => clip.TimelineEndUs);

    // Recommended default: first clip owns output geometry; FPS stays an
    // explicit caller input so no silent per-clip variation can slip in.
    public static TimelineSequence Create(
        IReadOnlyList<TimelineClip> clips,
        IReadOnlyList<TimelineMediaAsset> assets,
        double outputFrameRate)
    {
        ArgumentNullException.ThrowIfNull(clips);
        ArgumentNullException.ThrowIfNull(assets);
        if (clips.Count == 0)
            throw new InvalidDataException("Timeline sequence cần ít nhất một clip.");
        if (!double.IsFinite(outputFrameRate) || outputFrameRate <= 0)
            throw new InvalidDataException("Sequence FPS phải là số dương hữu hạn.");
        var byId = assets.ToDictionary(asset => asset.Id, StringComparer.Ordinal);
        foreach (var clip in clips)
        {
            if (!byId.TryGetValue(clip.AssetId, out var asset))
                throw new InvalidDataException("Timeline clip trỏ asset không tồn tại.");
            clip.ValidateAgainst(asset);
        }
        ValidateSequential(clips);
        var firstAsset = byId[clips.OrderBy(clip => clip.TimelineStartUs).First().AssetId];
        return new TimelineSequence(
            clips.OrderBy(clip => clip.TimelineStartUs).ToArray(),
            firstAsset.Width,
            firstAsset.Height,
            outputFrameRate);
    }

    // Hard-cut invariant: sorted by start, never overlapping. Contiguity is
    // not required here; ReflowContiguous produces the joined layout.
    public static void ValidateSequential(IReadOnlyList<TimelineClip> clips)
    {
        ArgumentNullException.ThrowIfNull(clips);
        var ordered = clips.OrderBy(clip => clip.TimelineStartUs).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            ordered[index].ValidateShape();
            if (index > 0 && ordered[index].TimelineStartUs < ordered[index - 1].TimelineEndUs)
                throw new InvalidDataException("Timeline clips giao nhau; MVP chỉ cho hard cut nối tiếp.");
        }
    }

    // Recompute starts so clips join end-to-start from zero, preserving the
    // caller's list order. Operations that take position-based input (drag,
    // reorder) pass their already-ordered list; G3 storage normalization may
    // sort first. Pure function, no IO.
    public static IReadOnlyList<TimelineClip> ReflowContiguous(IReadOnlyList<TimelineClip> clips)
    {
        ArgumentNullException.ThrowIfNull(clips);
        var cursor = 0L;
        var result = new List<TimelineClip>(clips.Count);
        foreach (var clip in clips)
        {
            clip.ValidateShape();
            result.Add(clip with { TimelineStartUs = cursor });
            cursor = checked(cursor + clip.DurationUs);
        }
        return result;
    }

    public void Validate(IReadOnlyList<TimelineMediaAsset> assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        if (OutputWidth <= 0 || OutputHeight <= 0)
            throw new InvalidDataException("Sequence output resolution không hợp lệ.");
        if (!double.IsFinite(OutputFrameRate) || OutputFrameRate <= 0)
            throw new InvalidDataException("Sequence FPS phải là số dương hữu hạn.");
        if (VideoClips.Count == 0)
            throw new InvalidDataException("Timeline sequence cần ít nhất một clip.");
        var byId = assets.ToDictionary(asset => asset.Id, StringComparer.Ordinal);
        foreach (var clip in VideoClips)
        {
            if (!byId.TryGetValue(clip.AssetId, out var asset))
                throw new InvalidDataException("Timeline clip trỏ asset không tồn tại.");
            clip.ValidateAgainst(asset);
        }
        ValidateSequential(VideoClips);
    }
}
