#!/usr/bin/env python3
"""Kiểm tra cắt cạnh clip và bám (snap) 9 px màn hình trên dải thời gian."""
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages"
CORE = ROOT / "csharp/src/BiliSubStudio.Core/Editor"
TIMELINE = PAGES / "EditorPage.Timeline.cs"
COMPOSITION = PAGES / "EditorPage.TimelineComposition.cs"
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


timeline = TIMELINE.read_text(encoding="utf-8")
composition = COMPOSITION.read_text(encoding="utf-8")
xaml = XAML.read_text(encoding="utf-8")
trim = (CORE / "EditorTimelineTrim.cs").read_text(encoding="utf-8")
snapping = (CORE / "EditorTimelineSnapping.cs").read_text(encoding="utf-8")

# TRIM-01: the edge trim math must live in Core as pure functions.
for token in (
    "public enum EditorTimelineTrimEdge",
    "public static class EditorTimelineTrim",
    "public static double ClampTrim(",
    "public static EditorTimelineClip MoveEdge(",
    "public static EditorTimelineClip Trim(",
    "public static double MinimumDuration(double frameRate)",
):
    require(token in trim, f"TRIM-01 the core trim math is missing: {token}")

# TRIM-02: dragging the In edge moves source_in by the delta and shortens the
# duration, dragging the Out edge only changes the duration (the out point in
# the source file stays put at the In edge).
require("clip with { SourceIn = clip.SourceIn + delta, Duration = Math.Max(0, clip.Duration - delta) }" in trim,
        "TRIM-02 trimming the In edge must move the source in point and shorten the clip")
require("clip with { Duration = Math.Max(0, clip.Duration + delta) }" in trim,
        "TRIM-02 trimming the Out edge must only change the duration")

# TRIM-03: clamp to the media handle, never cutting into the neighboring clip
# (Regular trim), and keep at least one frame.
for token in (
    "var low = -Math.Max(0, clip.SourceIn);",
    "var high = duration - minimum;",
    "var outLow = -(duration - minimum);",
    "var outHigh = Math.Max(0, media - clip.End);",
    "low = Math.Max(low, previousEnd - clip.SourceIn);",
    "outHigh = Math.Min(outHigh, nextSourceIn - clip.End);",
    "private static bool SameSource(",
):
    require(token in trim, f"TRIM-03 the trim clamp lost: {token}")

# TRIM-04: the UI must call Core, never recompute the trim algebra.
for token in (
    "EditorTimelineTrim.ClampTrim(",
    "EditorTimelineTrim.MoveEdge(",
    "EditorTimelineTrim.MinimumDuration(",
):
    require(token in timeline, f"TRIM-04 the timeline must use the core trim math: {token}")
require("with { SourceIn = sourceIn, Duration = duration }" not in timeline,
        "TRIM-04 the timeline must not re-implement the trim algebra in code-behind")

# TRIM-05: an edge drag must have its own handle and must take the fps/duration
# of the source file from the probed media so at least one frame is kept.
for token in (
    "private const double TimelineClipEdgeGrabPixels = EditorTimelineTrim.EdgeGrabPixels;",
    "block.PointerMoved += (_, args) => TimelineVideoClipPointerMoved(captured, block, args);",
    "block.PointerReleased += (_, args) => TimelineVideoClipPointerReleased(captured, block, args);",
    "var media = TimelineClipMediaCached(_timelineClips[index]);",
    "var minDuration = EditorTimelineTrim.MinimumDuration(media?.FrameRate ?? 0);",
):
    require(token in timeline, f"TRIM-05 the clip edge drag lost: {token}")
for token in (
    "internal MediaPreviewInfo? TimelineClipMediaCached(",
    "internal void PrefetchTimelineClipMedia(",
):
    require(token in composition, f"TRIM-05 the clip media owner lost: {token}")

# TRIM-06: the clip edge grab zone is 10 px wide (subtitle cues keep 6 px) and
# the edge grab rule lives in Core so the UI only calls into it.
for token in (
    "public const double EdgeGrabPixels = 10;",
    "public static EditorTimelineTrimEdge? EdgeAt(",
    "public static bool CanGrabEdge(",
):
    require(token in trim, f"TRIM-06 the core edge grab zone is missing: {token}")
for token in (
    "EditorTimelineTrim.EdgeAt(position.X, width, TimelineClipEdgeGrabPixels);",
    "EditorTimelineTrim.CanGrabEdge(width, TimelineClipEdgeGrabPixels)",
):
    require(token in timeline, f"TRIM-06 the timeline must use the core edge grab zone: {token}")
require("private const double TimelineCueEdgeGrabPixels = 6;" in timeline,
        "TRIM-06 the subtitle cue edge grab zone must stay at 6 pixels")

# TRIM-07: moving the mouse into the edge grab zone changes the cursor and
# draws an emphasis line at that edge, so the user knows the edge can be
# grabbed.
for token in (
    "block.PointerMoved += (_, args) => TimelineVideoClipHoverMoved(captured, block, args);",
    "block.PointerExited += (_, args) => TimelineVideoClipHoverExited(captured, block, args);",
    "private void ShowTimelineEdgeHover(int index, EditorTimelineTrimEdge edge, TimelineClipBlock block)",
    "private void ClearTimelineEdgeHover(TimelineClipBlock? block)",
    "block.EdgeCursor = TimelineEdgeResizeCursor;",
    "x:Name=\"TimelineEdgeHoverIndicator\"",
):
    require(token in timeline or token in xaml, f"TRIM-07 the clip edge hover feedback lost: {token}")

# TRIM-08: a light click must not create a junk trim: a drag below the minimum
# threshold leaves the edge unchanged.
for token in (
    "public const double MinimumDragPixels = 3;",
    "public static bool ReachedDragThreshold(",
):
    require(token in trim, f"TRIM-08 the minimum drag threshold is missing: {token}")
require("EditorTimelineTrim.ReachedDragThreshold(" in timeline,
        "TRIM-08 the timeline must use the core minimum drag threshold")

# SNAP-01: the snap threshold is 9 screen px, measured in the strip pixel space.
for token in (
    "public const double SnapPixels = 9;",
    "public static double SnapTime(",
    "public static IReadOnlyList<double> Candidates(",
    "var distance = Math.Abs(EditorTimelineLayout.XForTime(candidate, pixelsPerSecondValue) - x);",
    "if (distance >= threshold || distance >= bestDistance) continue;",
):
    require(token in snapping, f"SNAP-01 the core snap math is missing: {token}")

# SNAP-02: the snap points cover the head/tail of other clips, the playhead,
# markers and trim marks.
for token in (
    "candidates.Add(starts[index]);",
    "candidates.Add(starts[index] + Math.Max(0, clips[index].Duration));",
    "if (double.IsFinite(playheadTime)) candidates.Add(playheadTime);",
    "if (markers is not null)",
    "if (trimMarks is not null)",
):
    require(token in snapping, f"SNAP-02 the snap candidate sources lost: {token}")

# SNAP-03: the UI snaps through Core and shows the snap line, never computing a
# time threshold itself.
for token in (
    "EditorTimelineSnapping.SnapTime(",
    "EditorTimelineSnapping.Candidates(",
    "private IReadOnlyList<double> TimelineSnapCandidates(int clipIndex)",
    "private List<double> CollectTimelineSnapMarkers(double visibleStart, double visibleEnd)",
    "ShowTimelineSnapIndicator(snapped, pps);",
):
    require(token in timeline, f"SNAP-03 the timeline snapping owner lost: {token}")
require('x:Name="TimelineSnapIndicator"' in xaml,
        "SNAP-03 the snap indicator element is missing from the timeline XAML")

# SNAP-04: the dragged edge is pointer-driven, so it can snap at every zoom
# level.
for token in (
    "var pressTime = EditorTimelineLayout.TimeForX(_timelineDragClipOriginContentX, pps);",
    "var edgeTime = EditorTimelineLayout.TimeForX(cursorContentX, pps);",
    "var rawEdge = originEdge + (edgeTime - pressTime);",
    "var delta = snapped - originEdge;",
):
    require(token in timeline, f"SNAP-04 the pointer-driven edge lost: {token}")

print("PASS: TRIM-01/02/03/04/05/06/07/08 clip edge trim, 10px edge grab zone, hover feedback and SNAP-01/02/03/04 9px screen snapping are locked")
