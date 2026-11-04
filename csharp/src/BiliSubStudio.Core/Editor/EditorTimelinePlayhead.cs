namespace BiliSubStudio.Core.Editor;

/// <summary>
/// Playhead of the timeline (following FilmCraft): it snaps to the media's FRAME
/// grid (snaptime = frame index / fps, not a free-floating real number), is saved per
/// project, and auto-scrolls the timeline to keep the playhead inside the viewport.
///
/// Every function here is pure: UI only calls into it, never recomputes the value itself.
/// </summary>
public static class EditorTimelinePlayhead
{
    /// <summary>Frames per second used when the media does not report its fps (30, like FilmCraft).</summary>
    public const double DefaultFrameRate = 30d;

    /// <summary>
    /// Safety margin when bringing the playhead into view: the playhead sits 5% of
    /// the viewport width from the left edge (like <c>scroll_to_reveal</c> in FilmCraft).
    /// </summary>
    public const double RevealMarginRatio = .05d;

    /// <summary>
    /// Mouse capture radius around the playhead (DIP - the logical unit of the
    /// interface, NOT image px): clicking within this range drags the playhead, outside
    /// the range it is a normal click on the ruler. The radius must be larger than half the
    /// playhead head width, otherwise the round-handle control covers the capture zone and
    /// there is no way to start a drag.
    /// </summary>
    public const double GrabPixels = 16d;

    /// <summary>
    /// Width of the playhead capture zone (DIP): the playhead head must be exactly as wide as the
    /// capture zone so the horizontal-drag cursor appears across the whole clickable area.
    /// </summary>
    public const double GrabZoneWidth = GrabPixels * 2d;

    /// <summary>
    /// Whether the pointer currently sits inside the mouse capture zone around the playhead: used
    /// to switch the cursor to the drag symbol and to start a playhead drag.
    /// </summary>
    /// <param name="pointerX">Pointer X coordinate within the timeline (DIP).</param>
    /// <param name="playheadX">Playhead X coordinate within the timeline (DIP).</param>
    public static bool IsWithinGrabRange(double pointerX, double playheadX)
    {
        if (!double.IsFinite(pointerX) || !double.IsFinite(playheadX)) return false;
        return Math.Abs(pointerX - playheadX) <= GrabPixels;
    }

    /// <summary>
    /// Whether the pointer sits inside the ruler band: the playhead capture zone only opens on the
    /// ruler band (where the round handle at the playhead head lives). Clicks in the lane area
    /// belong to clip edge trimming and cue dragging, and must not be hijacked.
    /// </summary>
    /// <param name="pointerY">Pointer Y coordinate within the timeline (DIP).</param>
    /// <param name="rulerHeight">Height of the ruler band (DIP).</param>
    public static bool IsInRulerBand(double pointerY, double rulerHeight)
    {
        if (!double.IsFinite(pointerY) || !double.IsFinite(rulerHeight) || rulerHeight <= 0) return false;
        return pointerY >= 0 && pointerY <= rulerHeight;
    }

    /// <summary>
    /// Chooses the frames-per-second of the frame grid for a position on the timeline:
    /// always preferring the fps of the clip currently containing the playhead, only falling back to the
    /// project media's fps when that clip does not know it yet (media not probed yet). A consistent
    /// fps source is what makes the frame grid of scrubbing and of restoration the
    /// same: two different grids make the playhead drift by about one frame.
    /// </summary>
    /// <param name="clipFrameRate">fps of the clip currently containing the playhead; null when unknown.</param>
    /// <param name="projectFrameRate">fps of the project media; null when unknown.</param>
    public static double ResolveFrameRate(double? clipFrameRate, double? projectFrameRate)
    {
        if (clipFrameRate is { } clip && clip > 0) return clip;
        if (projectFrameRate is { } project && project > 0) return project;
        return DefaultFrameRate;
    }

    /// <summary>
    /// Restores a saved playhead position: keep the exact saved value (do not
    /// re-snap to the frame grid), because the saved position was already frame-snapped when the user
    /// scrubbed; re-snapping here would drift by about one frame when the clip's fps and
    /// the project media's fps differ.
    /// </summary>
    /// <param name="savedTime">Saved playhead position (timeline time).</param>
    public static double RestoreTime(double savedTime)
    {
        if (!double.IsFinite(savedTime)) return 0;
        return Math.Max(0, savedTime);
    }

    /// <summary>
    /// Frame index containing the time (rounded down, like
    /// <c>FrameRate::snap</c> in FilmCraft).
    /// </summary>
    /// <param name="time">Time on the timeline (seconds).</param>
    /// <param name="frameRate">Frames per second of the media being scrubbed.</param>
    public static long FrameIndex(double time, double frameRate)
    {
        if (!double.IsFinite(time) || time <= 0) return 0;
        var index = (long)Math.Floor(time * NormalizeFrameRate(frameRate));
        return index < 0 ? 0 : index;
    }

    /// <summary>
    /// Start time of a frame: the frame index divided by the fps.
    /// </summary>
    /// <param name="frameIndex">Frame index (a negative value maps to frame 0).</param>
    /// <param name="frameRate">Frames per second of the media being scrubbed.</param>
    public static double FrameTime(long frameIndex, double frameRate)
    {
        var index = frameIndex < 0 ? 0 : frameIndex;
        return index / NormalizeFrameRate(frameRate);
    }

    /// <summary>
    /// Snaps a time onto the media's frame grid: the frame index divided by the
    /// fps, not a free-floating real number.
    /// </summary>
    /// <param name="time">Time on the timeline (seconds).</param>
    /// <param name="frameRate">Frames per second of the media being scrubbed.</param>
    public static double SnapToFrame(double time, double frameRate)
        => FrameTime(FrameIndex(time, frameRate), frameRate);

    /// <summary>
    /// Whether the playhead lies inside the viewport of the timeline.
    /// </summary>
    /// <param name="playheadTime">Playhead time on the timeline (seconds).</param>
    /// <param name="scrollOffset">Current scroll offset of the timeline (px).</param>
    /// <param name="pixelsPerSecond">Current pixel density of the timeline.</param>
    /// <param name="viewportWidth">Viewport width of the timeline (px).</param>
    public static bool IsVisible(
        double playheadTime,
        double scrollOffset,
        double pixelsPerSecond,
        double viewportWidth)
    {
        if (!double.IsFinite(playheadTime) || !double.IsFinite(scrollOffset)) return false;
        if (!double.IsFinite(pixelsPerSecond) || pixelsPerSecond <= 0) return false;
        if (!double.IsFinite(viewportWidth) || viewportWidth <= 0) return false;
        var x = (playheadTime - scrollOffset) * pixelsPerSecond;
        return x >= 0 && x <= viewportWidth;
    }

    /// <summary>
    /// Scroll offset that brings the playhead into view; null when the playhead already
    /// sits inside the viewport (like <c>scroll_to_reveal</c> in FilmCraft).
    /// </summary>
    /// <param name="playheadTime">Playhead time on the timeline (seconds).</param>
    /// <param name="scrollOffset">Current scroll offset of the timeline (px).</param>
    /// <param name="pixelsPerSecond">Current pixel density of the timeline.</param>
    /// <param name="viewportWidth">Viewport width of the timeline (px).</param>
    /// <param name="contentWidth">Content width of the timeline (px).</param>
    public static double? RevealScrollOffset(
        double playheadTime,
        double scrollOffset,
        double pixelsPerSecond,
        double viewportWidth,
        double contentWidth)
    {
        if (!double.IsFinite(playheadTime) || !double.IsFinite(scrollOffset)) return null;
        if (!double.IsFinite(pixelsPerSecond) || pixelsPerSecond <= 0) return null;
        if (!double.IsFinite(viewportWidth) || viewportWidth <= 1) return null;
        var x = (playheadTime - scrollOffset) * pixelsPerSecond;
        if (x >= 0 && x <= viewportWidth) return null;
        var target = Math.Max(0, playheadTime - viewportWidth * RevealMarginRatio / pixelsPerSecond);
        var maxOffset = Math.Max(0, contentWidth - viewportWidth);
        return Math.Clamp(target, 0, maxOffset);
    }

    /// <summary>
    /// Whether the playhead has just moved to another frame: the condition for the timeline
    /// to scroll along. When the user scrolls by hand the playhead stays put, so the timeline
    /// is not scrolled continuously (like FilmCraft).
    /// </summary>
    /// <param name="previousTime">Previous playhead position (seconds).</param>
    /// <param name="currentTime">Current playhead position (seconds).</param>
    /// <param name="frameRate">Frames per second of the media being scrubbed.</param>
    public static bool MovedToAnotherFrame(double previousTime, double currentTime, double frameRate)
    {
        if (!double.IsFinite(previousTime) || !double.IsFinite(currentTime)) return false;
        return FrameIndex(previousTime, frameRate) != FrameIndex(currentTime, frameRate);
    }

    /// <summary>
    /// Scroll offset that makes the timeline follow the playhead: scroll only when the playhead
    /// just moved to another frame (user scrubbing) and currently sits outside the viewport. When scrolling
    /// by hand the playhead stays put, so the timeline is not dragged along.
    /// </summary>
    /// <param name="previousTime">Previous playhead position (seconds).</param>
    /// <param name="playheadTime">Current playhead position (seconds).</param>
    /// <param name="scrollOffset">Current scroll offset of the timeline (px).</param>
    /// <param name="pixelsPerSecond">Current pixel density of the timeline.</param>
    /// <param name="viewportWidth">Viewport width of the timeline (px).</param>
    /// <param name="contentWidth">Content width of the timeline (px).</param>
    /// <param name="frameRate">Frames per second of the media being scrubbed.</param>
    public static double? FollowScrollOffset(
        double previousTime,
        double playheadTime,
        double scrollOffset,
        double pixelsPerSecond,
        double viewportWidth,
        double contentWidth,
        double frameRate)
    {
        if (!MovedToAnotherFrame(previousTime, playheadTime, frameRate)) return null;
        return RevealScrollOffset(playheadTime, scrollOffset, pixelsPerSecond, viewportWidth, contentWidth);
    }

    private static double NormalizeFrameRate(double frameRate)
        => double.IsFinite(frameRate) && frameRate > 0 ? frameRate : DefaultFrameRate;
}
