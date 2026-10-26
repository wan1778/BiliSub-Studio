namespace BiliSubStudio.Core.Editor;

/// <summary>
/// Pure geometry helpers for the editor timeline strip (CapCut/Premiere style).
/// The strip maps source seconds to horizontal pixels; every conversion is
/// round-trippable so a click position can seek the player exactly.
/// </summary>
public static class EditorTimelineLayout
{
    public const double RulerHeight = 26;
    public const double LaneHeight = 26;
    public const double LaneGap = 3;
    public const double MinPixelsPerSecond = .4;
    public const double MaxPixelsPerSecond = 600;
    public const double MaxContentWidth = 120_000;
    public const double MinimumTickPixels = 84;

    private static readonly double[] TickLadder =
        [.1, .25, .5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600];

    public static double ClampPixelsPerSecond(double value)
    {
        if (!double.IsFinite(value) || value <= 0) return MinPixelsPerSecond;
        return Math.Clamp(value, MinPixelsPerSecond, MaxPixelsPerSecond);
    }

    /// <summary>Zoom that shows the whole video in the given viewport width.</summary>
    public static double FitPixelsPerSecond(double duration, double viewportWidth)
    {
        if (!double.IsFinite(duration) || duration <= 0 || !double.IsFinite(viewportWidth) || viewportWidth <= 1)
            return MinPixelsPerSecond;
        return ClampPixelsPerSecond(viewportWidth / duration);
    }

    public static double ContentWidth(double duration, double pixelsPerSecond)
    {
        if (!double.IsFinite(duration) || duration <= 0) return 1;
        var width = duration * ClampPixelsPerSecond(pixelsPerSecond);
        return Math.Clamp(width, 1, MaxContentWidth);
    }

    public static double XForTime(double time, double pixelsPerSecond)
    {
        if (!double.IsFinite(time)) return 0;
        return time * ClampPixelsPerSecond(pixelsPerSecond);
    }

    public static double TimeForX(double x, double pixelsPerSecond)
    {
        if (!double.IsFinite(x)) return 0;
        return x / ClampPixelsPerSecond(pixelsPerSecond);
    }

    /// <summary>Seconds between major ruler ticks at the given zoom.</summary>
    public static double MajorTickSeconds(double pixelsPerSecond)
    {
        var pps = ClampPixelsPerSecond(pixelsPerSecond);
        foreach (var step in TickLadder)
            if (step * pps >= MinimumTickPixels) return step;
        return TickLadder[^1];
    }

    /// <summary>Seconds between minor ruler ticks (5 per major tick).</summary>
    public static double MinorTickSeconds(double pixelsPerSecond) => MajorTickSeconds(pixelsPerSecond) / 5;

    /// <summary>Visible source-time window for the current scroll position.</summary>
    public static (double Start, double End) VisibleWindow(
        double scrollOffset,
        double viewportWidth,
        double pixelsPerSecond,
        double duration)
    {
        var pps = ClampPixelsPerSecond(pixelsPerSecond);
        var start = Math.Max(0, scrollOffset / pps);
        var end = start + Math.Max(1, viewportWidth) / pps;
        if (!double.IsFinite(duration) || duration <= 0) return (start, end);
        return (Math.Min(start, duration), Math.Min(end, duration));
    }

    /// <summary>True when a time span intersects the visible window.</summary>
    public static bool IntersectsVisible(double start, double end, double visibleStart, double visibleEnd)
    {
        if (!double.IsFinite(start) || !double.IsFinite(end)) return false;
        if (end < start) (start, end) = (end, start);
        return end >= visibleStart && start <= visibleEnd;
    }

    public static double LaneTop(int laneIndex)
    {
        if (laneIndex <= 0) return 0;
        return laneIndex * (LaneHeight + LaneGap);
    }

    public static double TracksHeight(int laneCount)
    {
        if (laneCount <= 0) return 0;
        return laneCount * LaneHeight + (laneCount - 1) * LaneGap;
    }
}
