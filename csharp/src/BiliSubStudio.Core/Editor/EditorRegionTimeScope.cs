namespace BiliSubStudio.Core.Editor;

public static class EditorRegionTimeScope
{
    public static EditRegion NormalizeWholeVideo(EditRegion region, double duration)
    {
        ArgumentNullException.ThrowIfNull(region);
        if (!region.WholeVideo) return region;
        if (!double.IsFinite(duration) || duration < 0)
            throw new ArgumentOutOfRangeException(nameof(duration), "Thời lượng video phải hữu hạn và không âm.");
        return region.Start == 0 && region.End == duration
            ? region
            : region with { Start = 0, End = duration };
    }
}
