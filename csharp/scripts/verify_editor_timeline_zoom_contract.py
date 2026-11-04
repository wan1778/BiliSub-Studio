#!/usr/bin/env python3
"""Kiểm tra zoom dải thời gian neo mép trái (slider/nút) và neo con trỏ (Alt+wheel)."""
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages"
TIMELINE = PAGES / "EditorPage.Timeline.cs"
EDITOR = PAGES / "EditorPage.xaml.cs"
IMAGES = PAGES / "EditorPage.Images.cs"
XAML = PAGES / "EditorPage.xaml"


def require(condition: bool, message: str) -> None:
    if not condition:
        print("FAIL:", message, file=sys.stderr)
        raise SystemExit(1)


def window(text: str, marker: str, length: int = 2600) -> str:
    index = text.find(marker)
    if index < 0:
        return ""
    return text[index:index + length]


def method(text: str, marker: str) -> str:
    index = text.find(marker)
    if index < 0:
        return ""
    end = text.find("\n    }", index)
    return text[index:end if end > 0 else index + 800]


timeline = TIMELINE.read_text(encoding="utf-8")
editor = EDITOR.read_text(encoding="utf-8")
images = IMAGES.read_text(encoding="utf-8")
xaml = XAML.read_text(encoding="utf-8")

# ZOOM-01: the slider and +/- buttons anchor on the LEFT EDGE of the visible area
# (the edge against the ruler): the time at the left edge stays put at every zoom
# level, the strip only grows/shrinks to the right. NEVER anchor on the middle of
# the viewport and NEVER anchor on the playhead.
zoom = window(timeline, "private void ApplyTimelineZoomPixels(double newPps")
require(zoom != "", "ZOOM-01 ApplyTimelineZoomPixels owner is missing")
for token in (
    "var scrollOffset = CurrentTimelineScrollOffset();",
    "EditorTimelineLayout.ZoomAnchorOffset(anchor, oldPps, newPps, scrollOffset, contentWidth, viewport)",
    "EditorTimelineLayout.ZoomAnchorOffsetAtViewportLeft(oldPps, newPps, scrollOffset, contentWidth, viewport)",
    "_timelinePendingScrollOffset = target;",
):
    require(token in zoom, f"ZOOM-01 the left-edge zoom anchor lost: {token}")
require("ZoomAnchorOffsetAtViewportCentre" not in zoom,
        "ZOOM-01 the zoom must not anchor on the viewport centre")
require("Canvas.GetLeft(TimelinePlayhead)" not in zoom,
        "ZOOM-01 the zoom must not anchor on the playhead by default")
require("EditorTimelinePlayhead.RevealScrollOffset(" not in zoom,
        "ZOOM-01 the zoom must not scroll the strip to the playhead")

layout = (ROOT / "csharp/src/BiliSubStudio.Core/Editor/EditorTimelineLayout.cs").read_text(encoding="utf-8")
for token in (
    "public static double ZoomAnchorContentX(",
    "public static double ZoomAnchorOffset(",
    "public static double ZoomAnchorOffsetAtViewportLeft(",
    "public static double FillWidth(",
    "public static double MaxScrollOffset(",
    "public static double ClipWidth(",
    "public static double ZoomAnchorViewportX(",
    "public static bool IsZoomAnchorVisible(",
    "var x = IsZoomAnchorVisible(anchorViewportX, viewportWidth) ? anchorViewportX : viewportWidth / 2;",
    "var maxOffset = MaxScrollOffset(contentWidth, viewportWidth);",
    "var target = Math.Max(0, scrollOffset) * newPixelsPerSecond / oldPixelsPerSecond;",
):
    require(token in layout, f"ZOOM-01 the shared zoom anchor math is missing: {token}")
require("ZoomAnchorOffsetAtViewportCentre" not in layout,
        "ZOOM-01 the viewport-centre zoom anchor must be gone")

# ZOOM-02: the computed offset must be applied after rendering (the content
# changes width), a scroll-to-cue command must take priority over the zoom
# offset, and the offset must never be negative: a strip shorter than the
# viewport gives offset = 0.
apply_scroll = window(timeline, "private void ApplyPendingTimelineScroll()")
require(apply_scroll != "", "ZOOM-02 ApplyPendingTimelineScroll owner is missing")
for token in (
    "if (_timelinePendingScrollTime is { } time)",
    "_timelinePendingScrollOffset = null;",
    "if (_timelinePendingScrollOffset is not { } pendingOffset) return;",
    "var clampedOffset = Math.Max(0, pendingOffset);",
    "TimelineScrollViewer.ChangeView(clampedOffset, null, null);",
    "var maxOffset = EditorTimelineLayout.MaxScrollOffset(",
):
    require(token in apply_scroll, f"ZOOM-02 pending scroll lost: {token}")
require("Math.Max(0, -point.X)" in timeline,
        "ZOOM-02 the measured scroll offset must never be negative")
for token in (
    "public static double MaxScrollOffset(double contentWidth, double viewportWidth)",
    "return Math.Max(0, content - viewport);",
):
    require(token in layout, f"ZOOM-02 the shared scroll clamp is missing: {token}")

# ZOOM-03: the +/- buttons and the zoom slider all use the same function that
# anchors on the left edge of the viewport (no separate anchor is passed).
zoom_owner = method(timeline, "private void ApplyTimelineZoom(double zoom)")
require(zoom_owner != "", "ZOOM-03 the zoom owner is missing")
require("ApplyTimelineZoomPixels(TimelinePixelsPerSecondForZoom(clamped));" in zoom_owner,
        "ZOOM-03 the zoom slider and buttons must zoom around the left edge of the viewport")
require("ApplyTimelineZoomPixels(TimelinePixelsPerSecondForZoom(e.NewValue));" in timeline,
        "ZOOM-03 the zoom slider must zoom around the left edge of the viewport")
require("ApplyTimelineZoom(TimelineZoomSlider.Value - 6);" in timeline,
        "ZOOM-03 zoom out must zoom around the left edge of the viewport")
require("ApplyTimelineZoom(TimelineZoomSlider.Value + 6);" in timeline,
        "ZOOM-03 zoom in must zoom around the left edge of the viewport")
require("_timelinePendingScrollOffset = null;\n        TimelineScrollViewer.ChangeView(0, null, null);" in timeline,
        "ZOOM-03 the fit button must reset the scroll instead of using the zoom anchor")

# ZOOM-04: the ValueChanged handler must not assign the slider Value again:
# WinUI assigns Value while loading the XAML, and a nested assignment makes the
# app fail at startup.
zoom_handler = method(timeline, "private void TimelineZoom_ValueChanged(")
require(zoom_handler != "", "ZOOM-04 the zoom slider handler is missing")
require("TimelineZoomSlider.Value" not in zoom_handler,
        "ZOOM-04 the zoom slider handler must not assign the slider value again")

# ZOOM-05: Alt or Ctrl + wheel on the timeline zooms, anchored on the pointer
# (like FilmCraft: wheel zoom about the pointer). The anchor is a content
# pixel coordinate: scroll offset + pointer, never seconds divided by pps.
require("TimelineScrollViewer.PointerWheelChanged += TimelineScrollViewer_PointerWheelChanged;" in timeline,
        "ZOOM-05 the timeline must listen to the wheel")
wheel = method(timeline, "private void TimelineScrollViewer_PointerWheelChanged(")
for token in (
    "point.Properties.MouseWheelDelta",
    "e.KeyModifiers.HasFlag(VirtualKeyModifiers.Menu)",
    "e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control)",
    "e.Handled = true;",
    "var anchorContentX = EditorTimelineLayout.ZoomAnchorContentX(scrollOffset, pointerX);",
    "ApplyTimelineZoomPixels(newPps, anchorContentX);",
):
    require(token in wheel, f"ZOOM-05 the pointer-anchored wheel zoom lost: {token}")
require("pointerX / pps" not in wheel,
        "ZOOM-05 the wheel anchor must stay in pixels, not mix seconds with pixels")

# ZOOM-06: a strip shorter than the viewport must align from the left edge and
# span the whole viewport width, never leaving blank space on the left: the
# content canvas, the lane backgrounds and the ruler are all as wide as
# max(contentWidth, viewportWidth).
render = window(timeline, "private void RenderTimeline()")
require(render != "", "ZOOM-06 the timeline render owner is missing")
for token in (
    "var contentWidth = EditorTimelineLayout.ContentWidth(duration, pps);",
    "var fillWidth = EditorTimelineLayout.FillWidth(contentWidth, TimelineViewportWidth());",
    "if (Math.Abs(TimelineContentGrid.Width - fillWidth) > .5) TimelineContentGrid.Width = fillWidth;",
    "if (Math.Abs(TimelineTracksCanvas.Width - fillWidth) > .5) TimelineTracksCanvas.Width = fillWidth;",
    "if (Math.Abs(TimelineRulerCanvas.Width - fillWidth) > .5) TimelineRulerCanvas.Width = fillWidth;",
    "RenderTimelineRuler(fillWidth, duration, pps, visibleStart, visibleEnd);",
    "RenderTimelineLanes(fillWidth, pps, visibleStart, visibleEnd);",
):
    require(token in render, f"ZOOM-06 the strip fill width lost: {token}")
require("TimelineRulerCanvas.Width = contentWidth" not in timeline,
        "ZOOM-06 the ruler must span the whole strip, not stop at the content width")
ruler = window(timeline, "private void RenderTimelineRuler(")
for token in (
    "Width = fillWidth,",
    "Fill = TimelineRulerBrush,",
):
    require(token in ruler, f"ZOOM-06 the ruler band must cover the whole strip: {token}")
scroll_viewer = window(xaml, 'x:Name="TimelineScrollViewer"', 600)
require(scroll_viewer != "", "ZOOM-06 the timeline scroll viewer is missing from the XAML")
require('HorizontalContentAlignment="Left"' in scroll_viewer,
        "ZOOM-06 the timeline scroll viewer must align its content to the left edge")
content_grid = window(xaml, 'x:Name="TimelineContentGrid"', 600)
require(content_grid != "", "ZOOM-06 the timeline content canvas is missing from the XAML")
require('HorizontalAlignment="Left"' in content_grid,
        "ZOOM-06 the strip canvas must be pinned to the left edge: a Canvas defaults to "
        "Stretch, and the WinUI ScrollViewer then centers a canvas narrower than the "
        "viewport (ScrollViewer::ComputeHorizontalAlignment)")

# ZOOM-07: the "Fit" button measures the REAL VIEWPORT WIDTH of the strip
# (the ScrollViewer), not the lane label column, not the toolbar, and always
# returns to the left edge.
fit = method(timeline, "private void ApplyTimelineFit()")
require(fit != "", "ZOOM-07 the fit owner is missing")
for token in (
    "var viewport = TimelineViewportWidth();",
    "var pps = EditorTimelineLayout.FitPixelsPerSecond(duration, viewport);",
    "_timelinePendingScrollOffset = null;",
    "TimelineScrollViewer.ChangeView(0, null, null);",
):
    require(token in fit, f"ZOOM-07 the fit must use the strip viewport: {token}")
require("TimelineHeaderGrid" not in fit,
        "ZOOM-07 the fit must not measure the lane label column")
require("TimelineZoomSlider" not in fit,
        "ZOOM-07 the fit must not measure the zoom toolbar")

# ZOOM-08: every component of the strip uses the SAME pixel density and the
# SAME coordinate system (x_content = time × pps, x_screen = x_content -
# scrollOffset); clip width = clip duration × pps, never stretched to the
# viewport.
for token in (
    "var x = EditorTimelineLayout.XForTime(time, pps);",
    "Canvas.SetLeft(block, EditorTimelineLayout.XForTime(timelineStart, pps));",
    "var width = EditorTimelineLayout.ClipWidth(clip.Duration, pps);",
    "var width = EditorTimelineLayout.ClipWidth(timelineEnd - timelineStart, pps);",
    "var x = EditorTimelineLayout.XForTime(Timeline.Value, _timelinePixelsPerSecond);",
    "var timelineTime = EditorTimelineLayout.TimeForX(position.X, _timelinePixelsPerSecond);",
):
    require(token in timeline, f"ZOOM-08 the shared strip scale is missing: {token}")
require("Math.Max(2, EditorTimelineLayout.XForTime(clip.Duration, pps))" not in timeline,
        "ZOOM-08 the clip width must come from the shared ClipWidth helper")

# ZOOM-09: the px/second readout must equal the pixel density the strip is
# using (logical DIP units): a screenshot at 125% comes out 1.25x larger, so
# the code must not use a different scale source for the ruler/clip/playhead.
zoom_text = method(timeline, "private void UpdateTimelineZoomText()")
require(zoom_text != "", "ZOOM-09 the zoom text owner is missing")
for token in (
    "var pps = EditorTimelineLayout.ClampPixelsPerSecond(_timelinePixelsPerSecond);",
    '$"{pps:0.##} px/giây"',
):
    require(token in zoom_text, f"ZOOM-09 the zoom text must show the strip scale: {token}")
require("RasterizationScale" not in zoom_text,
        "ZOOM-09 the zoom text must stay in logical pixels")

# LANE-01: the lane edges must stand out: a 1px separator at the lane boundary
# and alternating light/dark lane backgrounds, but they must not cover
# clip/cue/marker.
lanes = window(timeline, "private void RenderTimelineLanes(")
require(lanes != "", "LANE-01 the lane render owner is missing")
for token in (
    "Fill = lane % 2 == 0 ? TimelineLaneBrush : TimelineLaneAltBrush,",
    "for (var lane = 1; lane < TimelineLaneCount; lane++)",
    "Height = 1,",
    "Fill = TimelineLaneSeparatorBrush,",
    "Canvas.SetTop(separator, EditorTimelineLayout.LaneTop(lane) - EditorTimelineLayout.LaneGap);",
    "RenderTimelineVideoBlocks(pps, visibleStart, visibleEnd);",
):
    require(token in lanes, f"LANE-01 the lane boundary lost: {token}")
require("private static readonly SolidColorBrush TimelineLaneSeparatorBrush" in timeline,
        "LANE-01 the lane separator brush is missing")

# EXPORT-01: a composed strip is also an edit that needs exporting, so the
# export button must enable when the project only has composed clips and no
# subtitles/effect regions/trim.
require("var timelineEdited = TimelineNeedsComposedExport();" in editor,
        "EXPORT-01 the timeline edit state is missing")
require("|| trimChanged || timelineEdited)" in editor,
        "EXPORT-01 a composed timeline alone must enable the export button")
require("var hasComposedClips = TimelineNeedsComposedExport();" in images,
        "EXPORT-01 the export request must treat a composed timeline as an edit")

# EXPORT-02: removing the original clip from the strip must export according to
# the remaining strip, never silently falling back to exporting the old source file.
for token in (
    "private bool TimelineNeedsComposedExport()",
    "if (_timelineClips.Count > 1) return true;",
    "return !IsPrimaryTimelineClip(_timelineClips[0]);",
    "if (!TimelineNeedsComposedExport()) return request;",
):
    require(token in editor, f"EXPORT-02 the timeline export owner lost: {token}")

print("PASS: ZOOM-01/02/03/04/05/06/07/08/09 left-edge/pointer-anchored zoom, gap-free strip layout, viewport-only fit, one strip scale, LANE-01 lane boundaries and EXPORT-01 timeline-only export are locked")
