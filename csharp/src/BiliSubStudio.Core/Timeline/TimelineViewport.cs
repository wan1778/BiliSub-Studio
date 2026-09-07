namespace BiliSubStudio.Core.Timeline;

// Pure viewport math keeps scroll/zoom rendering proportional to the visible
// clip range rather than the total sequence duration.
public static class TimelineViewport
{
    public const double MinimumPixelsPerSecond = 1;
    public const double MaximumPixelsPerSecond = 320;
    public const double DefaultPixelsPerSecond = 80;
    public const double OverscanPixels = 80;

    public static double ClampPixelsPerSecond(double value) =>
        Math.Clamp(value, MinimumPixelsPerSecond, MaximumPixelsPerSecond);

    public static double SequenceX(long sequenceUs, double pixelsPerSecond)
    {
        if (sequenceUs < 0)
            throw new ArgumentOutOfRangeException(nameof(sequenceUs));
        ValidateScale(pixelsPerSecond);
        return sequenceUs / (double)TimelineTime.MicrosecondsPerSecond * pixelsPerSecond;
    }

    public static long SequenceUs(double x, double pixelsPerSecond, long maximumUs)
    {
        if (!double.IsFinite(x) || maximumUs < 0)
            throw new ArgumentOutOfRangeException(nameof(x));
        ValidateScale(pixelsPerSecond);
        var value = Math.Round(
            Math.Max(0, x) / pixelsPerSecond * TimelineTime.MicrosecondsPerSecond,
            MidpointRounding.AwayFromZero);
        return (long)Math.Clamp(value, 0, maximumUs);
    }

    public static double SurfaceWidth(long durationUs, double pixelsPerSecond, double minimumWidth)
    {
        if (!double.IsFinite(minimumWidth) || minimumWidth < 0)
            throw new ArgumentOutOfRangeException(nameof(minimumWidth));
        return Math.Max(minimumWidth, SequenceX(durationUs, pixelsPerSecond) + 20);
    }

    public static (int StartIndex, int EndExclusive) VisibleClipRange(
        IReadOnlyList<TimelineClip> clips,
        double horizontalOffset,
        double viewportWidth,
        double pixelsPerSecond)
    {
        ArgumentNullException.ThrowIfNull(clips);
        if (!double.IsFinite(horizontalOffset) || horizontalOffset < 0
            || !double.IsFinite(viewportWidth) || viewportWidth < 0)
            throw new ArgumentOutOfRangeException(nameof(horizontalOffset));
        ValidateScale(pixelsPerSecond);
        if (clips.Count == 0)
            return (0, 0);

        var startUs = Math.Max(0, horizontalOffset - OverscanPixels)
            / pixelsPerSecond * TimelineTime.MicrosecondsPerSecond;
        var endUs = (horizontalOffset + viewportWidth + OverscanPixels)
            / pixelsPerSecond * TimelineTime.MicrosecondsPerSecond;

        var low = 0;
        var high = clips.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (clips[middle].TimelineEndUs <= startUs)
                low = middle + 1;
            else
                high = middle;
        }
        var startIndex = low;

        high = clips.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (clips[middle].TimelineStartUs < endUs)
                low = middle + 1;
            else
                high = middle;
        }
        return (startIndex, low);
    }

    private static void ValidateScale(double pixelsPerSecond)
    {
        if (!double.IsFinite(pixelsPerSecond)
            || pixelsPerSecond < MinimumPixelsPerSecond
            || pixelsPerSecond > MaximumPixelsPerSecond)
            throw new ArgumentOutOfRangeException(nameof(pixelsPerSecond));
    }
}
