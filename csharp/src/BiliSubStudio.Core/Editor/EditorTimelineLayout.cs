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

    /// <summary>Gap between the ruler band and the lane area of the timeline.</summary>
    public const double RulerGap = 3;

    /// <summary>
    /// Number of lanes of the timeline. The lane order (top to bottom) is video,
    /// voice, subtitles, effects - see <see cref="EditorTimelineLane"/>.
    /// </summary>
    public const int LaneCount = 4;
    // 0.02 px/s is small enough for "Fit to frame" to show a long video whole (3.6 hours =
    // 13032s needs ~0.08 px/s for a ~1000px viewport). The old floor of 0.4 made the Fit
    // button never fit long videos.
    public const double MinPixelsPerSecond = .02;
    public const double MaxPixelsPerSecond = 600;
    // A 9-10 hour video at a readable zoom level (8 px/s) needs ~270,000 DIP of
    // content; the old ceiling of 120,000 cut off the second half of the timeline.
    public const double MaxContentWidth = 1_200_000;
    public const double MinimumTickPixels = 84;
    // Minimum distance between two time labels (px). Below this threshold
    // the labels would overlap, so the ruler drops the label instead of drawing it on top.
    public const double MinimumLabelPixels = 72;
    // Number of minor ticks dividing each major tick interval.
    public const int MinorTicksPerMajor = 5;

    /// <summary>Height of the toolbar above the timeline (top row of the panel).</summary>
    public const double ToolbarHeight = 26;

    /// <summary>Distance between the toolbar and the timeline area (the panel's RowSpacing).</summary>
    public const double ToolbarGap = 6;

    /// <summary>Vertical padding of the timeline panel (6 px on top + 8 px on the bottom).</summary>
    public const double PanelPaddingVertical = 14;

    /// <summary>
    /// Space reserved for the horizontal scrollbar: the scrollbar occupies height within the
    /// viewport, so without this reserve the bottom lane gets cut off.
    /// </summary>
    public const double ScrollBarReserve = 18;

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

    /// <summary>
    /// Working extent of the timeline strip: the video PLUS an empty tail of
    /// the same real length (10 minutes of video - the strip keeps working
    /// until minute 20; an hour - until hour 2). The tail lives on the same
    /// real second axis as the video, so zoom only scales the pixels drawn on
    /// it: the tail never stretches with the zoom, and the video's own
    /// duration, clips and export stay untouched.
    /// </summary>
    /// <param name="duration">Length of the composed video (seconds).</param>
    /// <returns>Working extent in seconds; 0 when there is no video.</returns>
    public static double WorkingSpan(double duration)
        => duration > 0 && double.IsFinite(duration) ? duration * 2 : 0;

    public static double XForTime(double time, double pixelsPerSecond)
    {
        if (!double.IsFinite(time)) return 0;
        return time * ClampPixelsPerSecond(pixelsPerSecond);
    }

    /// <summary>
    /// Content coordinate (px) of a pointer inside the timeline viewport. Both
    /// values are pixels: the scroll offset is the content pixel hidden at the
    /// left edge, the pointer is measured from that same edge. Mixing seconds
    /// with pixels here makes Ctrl+wheel jump to the wrong moment.
    /// </summary>
    /// <param name="scrollOffset">Current horizontal scroll offset (content px).</param>
    /// <param name="pointerViewportX">Pointer x measured inside the viewport (px).</param>
    public static double ZoomAnchorContentX(double scrollOffset, double pointerViewportX)
    {
        var scroll = double.IsFinite(scrollOffset) ? Math.Max(0, scrollOffset) : 0;
        var pointer = double.IsFinite(pointerViewportX) ? Math.Max(0, pointerViewportX) : 0;
        return scroll + pointer;
    }

    /// <summary>
    /// Scroll offset keeping the zoom anchor still when the pixel density changes: the anchor
    /// currently being viewed must keep its on-screen position, not jump to the middle of the timeline.
    /// An anchor outside the viewport is anchored to the middle of the viewport (like FilmCraft).
    /// </summary>
    public static double ZoomAnchorOffset(
        double anchorContentX,
        double oldPixelsPerSecond,
        double newPixelsPerSecond,
        double scrollOffset,
        double contentWidth,
        double viewportWidth)
    {
        if (!double.IsFinite(anchorContentX) || oldPixelsPerSecond <= 0 || viewportWidth <= 1)
            return Math.Max(0, scrollOffset);
        var anchorViewportX = anchorContentX - scrollOffset;
        var x = IsZoomAnchorVisible(anchorViewportX, viewportWidth) ? anchorViewportX : viewportWidth / 2;
        var anchorTime = anchorContentX / oldPixelsPerSecond;
        var target = anchorTime * newPixelsPerSecond - x;
        var maxOffset = MaxScrollOffset(contentWidth, viewportWidth);
        return Math.Clamp(target, 0, maxOffset);
    }

    /// <summary>
    /// Scroll offset when zooming anchored to the LEFT EDGE of the viewport: the moment at the
    /// left edge (the side right next to the ruler) must stay put across every zoom level, so the timeline grows/shrinks
    /// only to the right - exactly like Premiere/Resolve/CapCut and FilmCraft-style
    /// zoom bars. The offset is never negative, and when the timeline is shorter than the viewport the
    /// offset is 0 so no white gap appears.
    /// </summary>
    /// <param name="oldPixelsPerSecond">Old pixel density (px/second).</param>
    /// <param name="newPixelsPerSecond">New pixel density (px/second).</param>
    /// <param name="scrollOffset">Current scroll offset (content px).</param>
    /// <param name="contentWidth">New content width (px).</param>
    /// <param name="viewportWidth">Viewport width (px).</param>
    public static double ZoomAnchorOffsetAtViewportLeft(
        double oldPixelsPerSecond,
        double newPixelsPerSecond,
        double scrollOffset,
        double contentWidth,
        double viewportWidth)
    {
        if (oldPixelsPerSecond <= 0 || newPixelsPerSecond <= 0 || viewportWidth <= 1)
            return Math.Max(0, scrollOffset);
        var maxOffset = MaxScrollOffset(contentWidth, viewportWidth);
        var target = Math.Max(0, scrollOffset) * newPixelsPerSecond / oldPixelsPerSecond;
        return Math.Clamp(target, 0, maxOffset);
    }

    /// <summary>
    /// Width of the timeline's content canvas: a timeline shorter than the viewport still has to span
    /// the full viewport width so lane/fill are not cut short and no white
    /// gap is left at the left edge.
    /// </summary>
    /// <param name="contentWidth">Real content width (px).</param>
    /// <param name="viewportWidth">Viewport width (px).</param>
    public static double FillWidth(double contentWidth, double viewportWidth)
    {
        var content = double.IsFinite(contentWidth) ? Math.Max(0, contentWidth) : 0;
        var viewport = double.IsFinite(viewportWidth) ? Math.Max(0, viewportWidth) : 0;
        return Math.Max(content, viewport);
    }

    /// <summary>
    /// Maximum scroll offset of the timeline: a timeline longer than the viewport can scroll to the
    /// leftover part, a timeline shorter than the viewport cannot scroll. The offset is never
    /// negative, so a short timeline always starts from the left edge.
    /// </summary>
    /// <param name="contentWidth">Content width of the timeline (px).</param>
    /// <param name="viewportWidth">Viewport width (px).</param>
    public static double MaxScrollOffset(double contentWidth, double viewportWidth)
    {
        var content = double.IsFinite(contentWidth) ? Math.Max(0, contentWidth) : 0;
        var viewport = double.IsFinite(viewportWidth) ? Math.Max(0, viewportWidth) : 0;
        return Math.Max(0, content - viewport);
    }

    /// <summary>
    /// Width of one block on the timeline (clip, cue, voice): duration on the timeline x pixel
    /// density, NOT stretched to the viewport width. There is a 2 px floor so blocks
    /// stay clickable when zoomed far out.
    /// </summary>
    /// <param name="clipDuration">Duration of the block on the timeline (seconds).</param>
    /// <param name="pixelsPerSecond">Current pixel density (px/second).</param>
    public static double ClipWidth(double clipDuration, double pixelsPerSecond)
        => Math.Max(2, XForTime(clipDuration, pixelsPerSecond));

    /// <summary>Position of the playhead in the viewport (used as the zoom anchor).</summary>
    public static double ZoomAnchorViewportX(double anchorContentX, double scrollOffset)
        => anchorContentX - scrollOffset;

    /// <summary>Whether the playhead lies in the viewport (to avoid pulling a jump cut).</summary>
    public static bool IsZoomAnchorVisible(double anchorViewportX, double viewportWidth)
        => viewportWidth > 0 && anchorViewportX >= 0 && anchorViewportX <= viewportWidth;

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
    public static double MinorTickSeconds(double pixelsPerSecond)
        => MajorTickSeconds(pixelsPerSecond) / MinorTicksPerMajor;

    /// <summary>
    /// Distance between two ticks (px) when the step is <paramref name="stepSeconds"/>
    /// and the current pixel density is <paramref name="pixelsPerSecond"/>.
    /// </summary>
    /// <param name="stepSeconds">Step between two ticks (seconds).</param>
    /// <param name="pixelsPerSecond">Current pixel density (px/second).</param>
    public static double TickSpacingPixels(double stepSeconds, double pixelsPerSecond)
        => XForTime(stepSeconds, pixelsPerSecond);

    /// <summary>
    /// Whether two time labels are far enough apart not to overlap: the ruler
    /// draws a label only when this threshold is met, otherwise the label is dropped.
    /// </summary>
    /// <param name="stepSeconds">Step between two major ticks (seconds).</param>
    /// <param name="pixelsPerSecond">Current pixel density (px/second).</param>
    public static bool LabelsFit(double stepSeconds, double pixelsPerSecond)
        => TickSpacingPixels(stepSeconds, pixelsPerSecond) >= MinimumLabelPixels;

    /// <summary>
    /// The time positions of one step that fall within the visible range: UI only
    /// draws ticks, labels, and vertical gridlines inside the viewport, and builds no elements outside
    /// view (a project hours long must not build tens of thousands of ticks).
    /// </summary>
    /// <param name="visibleStart">Start of the visible range (seconds).</param>
    /// <param name="visibleEnd">End of the visible range (seconds).</param>
    /// <param name="stepSeconds">Step between two ticks (seconds).</param>
    public static IReadOnlyList<double> TickTimes(double visibleStart, double visibleEnd, double stepSeconds)
    {
        if (!double.IsFinite(stepSeconds) || stepSeconds <= 0) return [];
        if (!double.IsFinite(visibleStart) || !double.IsFinite(visibleEnd)) return [];
        if (visibleEnd < visibleStart) (visibleStart, visibleEnd) = (visibleEnd, visibleStart);
        var first = Math.Ceiling(visibleStart / stepSeconds - 1e-9);
        var last = Math.Floor(visibleEnd / stepSeconds + 1e-9);
        var count = last - first + 1;
        if (!double.IsFinite(count) || count <= 0) return [];
        var capped = Math.Min(count, 4096);
        var times = new List<double>((int)capped);
        for (var index = 0; index < capped; index++)
        {
            var time = (first + index) * stepSeconds;
            if (time < 0) continue;
            times.Add(time);
        }
        return times;
    }

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

    /// <summary>
    /// Top position of a lane in standard order (video, voice, subtitles,
    /// effects): the top lane is 0, the bottom lane is
    /// <see cref="LaneCount"/> - 1.
    /// </summary>
    /// <param name="lane">Lane to locate.</param>
    public static double LaneTop(EditorTimelineLane lane) => LaneTop((int)lane);

    /// <summary>
    /// Lane order of the timeline from top to bottom: effects, subtitles, video, voice (audio at the bottom).
    /// </summary>
    public static IReadOnlyList<EditorTimelineLane> LaneOrder { get; } =
    [
        EditorTimelineLane.Effect,
        EditorTimelineLane.Subtitle,
        EditorTimelineLane.Video,
        EditorTimelineLane.Voice,
    ];

    public static double TracksHeight(int laneCount)
    {
        if (laneCount <= 0) return 0;
        return laneCount * LaneHeight + (laneCount - 1) * LaneGap;
    }

    /// <summary>
    /// Minimum height of the timeline panel so every lane shows fully: the ruler
    /// + the lanes + room for the horizontal scrollbar + the toolbar + the gaps +
    /// the panel's padding. Missing any part would cut off the bottom lane.
    /// </summary>
    /// <param name="laneCount">Number of lanes of the timeline.</param>
    public static double PanelHeight(int laneCount)
        => RulerHeight + RulerGap + TracksHeight(laneCount) + ScrollBarReserve
            + ToolbarHeight + ToolbarGap + PanelPaddingVertical;
}
