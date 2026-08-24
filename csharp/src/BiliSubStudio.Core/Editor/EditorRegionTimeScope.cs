namespace BiliSubStudio.Core.Editor;

public readonly record struct EditorRegionTimeRange(double Start, double End);

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

    public static EditRegion Normalize(EditRegion region, double duration)
    {
        ArgumentNullException.ThrowIfNull(region);
        if (region.WholeVideo) return NormalizeWholeVideo(region, duration);
        ValidateDuration(duration);
        if (!double.IsFinite(region.Start) || !double.IsFinite(region.End)
            || region.Start < 0 || region.End > duration || region.End <= region.Start)
            throw new ArgumentException("Khoảng thời gian phải thỏa 0 ≤ bắt đầu < kết thúc ≤ thời lượng video.");
        return region;
    }

    public static EditRegion NormalizeStored(EditRegion region, double duration)
    {
        ArgumentNullException.ThrowIfNull(region);
        if (region.WholeVideo) return NormalizeWholeVideo(region, duration);
        ValidateDuration(duration);
        if (!double.IsFinite(region.Start) || !double.IsFinite(region.End))
            throw new InvalidDataException("Project Editor chứa khoảng thời gian không hữu hạn.");
        var start = Math.Clamp(region.Start, 0, duration);
        var end = Math.Clamp(region.End, 0, duration);
        if (end <= start) throw new InvalidDataException("Project Editor chứa khoảng thời gian không hợp lệ.");
        return start == region.Start && end == region.End ? region : region with { Start = start, End = end };
    }

    public static EditorRegionTimeRange CreateDefaultTimedRange(double currentPosition, double duration, double preferredDuration = 5)
    {
        ValidateDuration(duration);
        if (!double.IsFinite(currentPosition)) currentPosition = 0;
        if (!double.IsFinite(preferredDuration) || preferredDuration <= 0)
            throw new ArgumentOutOfRangeException(nameof(preferredDuration), "Độ dài range mặc định phải hữu hạn và lớn hơn 0.");
        if (duration == 0) return new EditorRegionTimeRange(0, 0);
        var start = Math.Clamp(currentPosition, 0, duration);
        var end = Math.Min(duration, start + preferredDuration);
        if (end > start) return new EditorRegionTimeRange(start, end);
        return new EditorRegionTimeRange(Math.Max(0, duration - preferredDuration), duration);
    }

    private static void ValidateDuration(double duration)
    {
        if (!double.IsFinite(duration) || duration < 0)
            throw new ArgumentOutOfRangeException(nameof(duration), "Thời lượng video phải hữu hạn và không âm.");
    }
}
