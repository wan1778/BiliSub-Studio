using BiliSubStudio.Core.Editor;

namespace BiliSubStudio.Core.ContractTests;

// CapCut/Premiere-style timeline in the Editor: the second <-> pixel mapping must round-trip
// exactly, zoom must be clampable, and only blocks inside the viewport may be built (a project has
// ~14k cues, so it cannot create every element).
internal static class EditorTimelineLayoutContract
{
    public static Task RunAsync()
    {
        Require(Close(EditorTimelineLayout.FitPixelsPerSecond(100, 1000), 10),
            "fit zoom must show 100s inside 1000px");
        // A 3.6-hour video must fit inside a ~1000px frame: the old 0.4 zoom ceiling made the "fit
        // frame" button never fit and clamped zoom at the min level.
        var longFit = EditorTimelineLayout.FitPixelsPerSecond(13032, 1000);
        Require(longFit > .05 && longFit < .1, "a 3.6h video must fit the viewport instead of clamping");
        Require(EditorTimelineLayout.ContentWidth(13032, longFit) <= 1100,
            "fit content width must stay inside the viewport");
        Require(EditorTimelineLayout.FitPixelsPerSecond(0, 1000) == EditorTimelineLayout.MinPixelsPerSecond,
            "empty media must not divide by zero");
        Require(EditorTimelineLayout.FitPixelsPerSecond(100, 0) == EditorTimelineLayout.MinPixelsPerSecond,
            "zero viewport must not divide by zero");

        Require(EditorTimelineLayout.ClampPixelsPerSecond(0) == EditorTimelineLayout.MinPixelsPerSecond,
            "zero zoom must clamp to the floor");
        Require(EditorTimelineLayout.ClampPixelsPerSecond(-5) == EditorTimelineLayout.MinPixelsPerSecond,
            "negative zoom must clamp to the floor");
        Require(EditorTimelineLayout.ClampPixelsPerSecond(1e9) == EditorTimelineLayout.MaxPixelsPerSecond,
            "huge zoom must clamp to the ceiling");
        Require(EditorTimelineLayout.ClampPixelsPerSecond(double.NaN) == EditorTimelineLayout.MinPixelsPerSecond,
            "NaN zoom must clamp to the floor");

        var pps = 24.5;
        var x = EditorTimelineLayout.XForTime(123.456, pps);
        Require(Close(x, 123.456 * pps), "time -> pixel must scale linearly");
        Require(Close(EditorTimelineLayout.TimeForX(x, pps), 123.456), "pixel -> time must invert exactly");

        Require(Close(EditorTimelineLayout.ContentWidth(60, 10), 600), "content width follows duration * zoom");
        Require(EditorTimelineLayout.ContentWidth(33789, 8) >= 33789 * 8,
            "a 9.4h video must stay fully representable at readable zoom");
        Require(EditorTimelineLayout.ContentWidth(1e9, 600) == EditorTimelineLayout.MaxContentWidth,
            "content width must stay inside the canvas limit");
        Require(EditorTimelineLayout.ContentWidth(0, 10) == 1, "empty media still needs a drawable canvas");

        Require(EditorTimelineLayout.MajorTickSeconds(10) == 10, "major tick must keep ~84px spacing");
        Require(EditorTimelineLayout.MajorTickSeconds(1) == 120, "low zoom needs a coarser tick");
        Require(Close(EditorTimelineLayout.MinorTickSeconds(10), 2), "minor ticks split the major step");
        Require(EditorTimelineLayout.MajorTickSeconds(10) >= EditorTimelineLayout.MinorTickSeconds(10),
            "major tick must never be finer than minor tick");

        var window = EditorTimelineLayout.VisibleWindow(2400, 800, 10, 3600);
        Require(Close(window.Start, 240) && Close(window.End, 320), "visible window follows scroll offset");
        var clamped = EditorTimelineLayout.VisibleWindow(0, 800, 10, 100);
        Require(Close(clamped.Start, 0) && Close(clamped.End, 80), "visible window must not exceed the media");

        Require(EditorTimelineLayout.IntersectsVisible(10, 20, 0, 30), "overlapping span must be drawn");
        Require(!EditorTimelineLayout.IntersectsVisible(40, 50, 0, 30), "span outside the window must be skipped");
        Require(EditorTimelineLayout.IntersectsVisible(30, 40, 0, 30), "touching the window edge still counts");
        Require(EditorTimelineLayout.IntersectsVisible(20, 10, 0, 30), "reversed span must still be visible");
        Require(!EditorTimelineLayout.IntersectsVisible(double.NaN, 10, 0, 30), "NaN span must be skipped");

        Require(EditorTimelineLayout.LaneTop(0) == 0, "first lane starts at the top");
        Require(EditorTimelineLayout.LaneTop(2) == 2 * (EditorTimelineLayout.LaneHeight + EditorTimelineLayout.LaneGap),
            "lane offset must account for the gap");
        Require(EditorTimelineLayout.TracksHeight(3)
            == 3 * EditorTimelineLayout.LaneHeight + 2 * EditorTimelineLayout.LaneGap,
            "tracks height must cover every lane plus gaps");
        Require(EditorTimelineLayout.TracksHeight(0) == 0, "no lanes means no track height");

        // Cursor zoom (Alt/Ctrl + wheel): the anchor keeps its exact screen coordinate, so changing pixel
        // density keeps the currently viewed moment in the same place on screen instead of jumping to
        // the middle of the timeline.
        const double oldPps = 10;
        const double newPps = 20;
        const double anchorContentX = 2400;
        const double scrollOffset = 2000;
        const double viewportWidth = 800;
        const double pointerViewportX = 400;
        Require(Close(EditorTimelineLayout.ZoomAnchorContentX(scrollOffset, pointerViewportX), anchorContentX),
            "the wheel anchor must add the pointer pixel to the content scroll pixel");
        Require(Close(EditorTimelineLayout.ZoomAnchorContentX(-20, -5), 0),
            "a negative wheel anchor must clamp to the content origin");
        Require(Close(EditorTimelineLayout.ZoomAnchorContentX(double.NaN, 120), 120),
            "an invalid scroll offset must anchor on the pointer pixel");
        var anchorViewportX = EditorTimelineLayout.ZoomAnchorViewportX(anchorContentX, scrollOffset);
        Require(Close(anchorViewportX, pointerViewportX), "anchor viewport x must follow the anchor position");
        Require(EditorTimelineLayout.IsZoomAnchorVisible(anchorViewportX, viewportWidth),
            "an anchor inside the viewport is a valid zoom anchor");
        Require(!EditorTimelineLayout.IsZoomAnchorVisible(-1, viewportWidth),
            "an anchor left of the viewport must not pull the view");
        Require(!EditorTimelineLayout.IsZoomAnchorVisible(viewportWidth + 1, viewportWidth),
            "an anchor right of the viewport must not pull the view");
        var zoomOffset = EditorTimelineLayout.ZoomAnchorOffset(
            anchorContentX, oldPps, newPps, scrollOffset, 9600, viewportWidth);
        Require(Close(zoomOffset, 4400),
            "zooming in must move the scroll by the anchor shift so the anchor stays put");
        Require(Close(EditorTimelineLayout.ZoomAnchorViewportX(anchorContentX * newPps / oldPps, zoomOffset), pointerViewportX),
            "the moment under the mouse must stay under the mouse after zooming in");
        var zoomedBack = EditorTimelineLayout.ZoomAnchorOffset(
            4800, newPps, oldPps, zoomOffset, 4800, viewportWidth);
        Require(Close(zoomedBack, scrollOffset), "zooming back must restore the original scroll");
        Require(Close(EditorTimelineLayout.ZoomAnchorOffset(anchorContentX, 0, newPps, scrollOffset, 4800, viewportWidth), scrollOffset),
            "an invalid zoom level must keep the scroll offset");
        Require(Close(EditorTimelineLayout.ZoomAnchorOffset(anchorContentX, oldPps, newPps, scrollOffset, 4800, 0), scrollOffset),
            "a collapsed viewport must keep the scroll offset");
        // A mark outside the viewport anchors to the middle of the viewport (like FilmCraft).
        var offScreen = EditorTimelineLayout.ZoomAnchorOffset(
            24000, oldPps, newPps, scrollOffset, 96000, viewportWidth);
        Require(Close(offScreen, 24000 * (newPps / oldPps) - viewportWidth / 2),
            "an off-screen anchor must fall back to the centre of the viewport");

        // The slider's and the +/- buttons' zoom anchors on the LEFT EDGE of the viewport: the moment at
        // the left edge must stay put after a pixel-density change, and the timeline only grows/shrinks
        // rightward.
        var leftOffset = EditorTimelineLayout.ZoomAnchorOffsetAtViewportLeft(
            oldPps, newPps, scrollOffset, 9600, viewportWidth);
        Require(Close(leftOffset, scrollOffset * newPps / oldPps),
            "zooming with the slider must move the scroll by the left edge shift");
        Require(Close(leftOffset / newPps, scrollOffset / oldPps),
            "the left edge of the viewport must show the same time after the zoom");
        var leftZoomOut = EditorTimelineLayout.ZoomAnchorOffsetAtViewportLeft(
            newPps, oldPps, leftOffset, 4800, viewportWidth);
        Require(Close(leftZoomOut, scrollOffset),
            "zooming back must restore the original scroll from the left edge");
        Require(Close(EditorTimelineLayout.ZoomAnchorOffsetAtViewportLeft(
            oldPps, newPps, scrollOffset, 9600, 0), scrollOffset),
            "a collapsed viewport must keep the scroll offset when zooming around the left edge");
        Require(Close(EditorTimelineLayout.ZoomAnchorOffsetAtViewportLeft(
            0, newPps, scrollOffset, 9600, viewportWidth), scrollOffset),
            "an invalid zoom level must keep the scroll offset when zooming around the left edge");
        // A timeline shorter than the viewport: the offset must return to 0, never go negative and
        // never leave blank space at the left edge.
        Require(EditorTimelineLayout.ZoomAnchorOffsetAtViewportLeft(
            oldPps, newPps, scrollOffset, 600, viewportWidth) == 0,
            "a strip shorter than the viewport must scroll back to the left edge");
        Require(EditorTimelineLayout.ZoomAnchorOffsetAtViewportLeft(
            oldPps, newPps, -500, 9600, viewportWidth) == 0,
            "a negative scroll offset must never survive the zoom");
        // The content canvas must fill the full width of the viewport.
        Require(EditorTimelineLayout.FillWidth(600, viewportWidth) == viewportWidth,
            "a strip shorter than the viewport must still fill the viewport width");
        Require(EditorTimelineLayout.FillWidth(9600, viewportWidth) == 9600,
            "a strip wider than the viewport keeps its own width");
        Require(EditorTimelineLayout.FillWidth(600, 0) == 600,
            "a collapsed viewport must not widen the strip");
        Require(EditorTimelineLayout.FillWidth(double.NaN, viewportWidth) == viewportWidth,
            "an invalid content width must fall back to the viewport width");

        // The scroll offset is never negative: a timeline longer than the viewport scrolls to the
        // overflow, a shorter one stays still at the left edge.
        Require(Close(EditorTimelineLayout.MaxScrollOffset(9600, viewportWidth), 8800),
            "the scroll range is the content that does not fit in the viewport");
        Require(EditorTimelineLayout.MaxScrollOffset(600, viewportWidth) == 0,
            "a strip shorter than the viewport must not scroll");
        Require(EditorTimelineLayout.MaxScrollOffset(9600, viewportWidth) >= 0,
            "the scroll range must never be negative");
        Require(EditorTimelineLayout.MaxScrollOffset(double.NaN, viewportWidth) == 0,
            "an invalid content width must not create a scroll range");
        Require(EditorTimelineLayout.MaxScrollOffset(9600, double.NaN) == 9600,
            "an invalid viewport width falls back to the full content width");
        Require(EditorTimelineLayout.MaxScrollOffset(-10, viewportWidth) == 0,
            "a negative content width must not create a scroll range");

        // Clip width = duration on the timeline x pixel density, not stretched to the viewport width;
        // only a 2 px floor keeps the block clickable.
        Require(Close(EditorTimelineLayout.ClipWidth(10, 20), 200),
            "a clip is as wide as its duration times the zoom");
        Require(EditorTimelineLayout.ClipWidth(10, 20) < viewportWidth,
            "a clip must never be stretched to the viewport width");
        Require(EditorTimelineLayout.ClipWidth(.01, 1) == 2,
            "a tiny clip keeps the 2px visibility floor");
        Require(EditorTimelineLayout.ClipWidth(0, 20) == 2,
            "an empty clip must not collapse to an unusable size");
        Require(EditorTimelineLayout.ClipWidth(double.NaN, 20) == 2,
            "an invalid clip duration must not collapse the block");

        // Professional ruler: label spacing must be wide enough, otherwise the ruler skips labels
        // rather than drawing them overlapping.
        Require(EditorTimelineLayout.MinimumLabelPixels >= 64 && EditorTimelineLayout.MinimumLabelPixels <= 84,
            "the label gap threshold must stay inside the readable 64-84px band");
        Require(Close(EditorTimelineLayout.TickSpacingPixels(10, 8), 80),
            "the tick spacing in pixels must follow the shared strip scale");
        Require(EditorTimelineLayout.LabelsFit(10, 8),
            "labels 80px apart must still fit");
        Require(!EditorTimelineLayout.LabelsFit(1, 8),
            "labels 8px apart must be dropped instead of overlapping");
        Require(EditorTimelineLayout.LabelsFit(1, 100),
            "labels 100px apart must fit");
        Require(EditorTimelineLayout.MinorTicksPerMajor == 5,
            "the minor tick ladder must keep five steps per major step");
        Require(Close(EditorTimelineLayout.MinorTickSeconds(10), 2),
            "the minor tick step must follow the shared minor ladder");

        // Ticks/grid may only be built inside the visible range.
        var ticks = EditorTimelineLayout.TickTimes(240, 320, 10);
        Require(ticks.Count == 9, "the visible window must own exactly its own ticks");
        Require(Close(ticks[0], 240) && Close(ticks[^1], 320),
            "the tick list must start and end on the window edges");
        var clipped = EditorTimelineLayout.TickTimes(240, 320, 1000);
        Require(clipped.Count == 0,
            "a coarse step must not invent ticks outside the window");
        Require(EditorTimelineLayout.TickTimes(0, 10, 0).Count == 0,
            "an invalid tick step must not produce ticks");
        Require(EditorTimelineLayout.TickTimes(0, 10, double.NaN).Count == 0,
            "an invalid tick step must not produce ticks");
        Require(EditorTimelineLayout.TickTimes(double.NaN, 10, 1).Count == 0,
            "an invalid window must not produce ticks");
        Require(EditorTimelineLayout.TickTimes(320, 240, 10).Count == 9,
            "a reversed window must still produce the same ticks");

        return Task.CompletedTask;
    }

    private static bool Close(double actual, double expected) => Math.Abs(actual - expected) <= 1e-9;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
