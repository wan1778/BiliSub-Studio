#!/usr/bin/env python3
"""Check that effect regions on the timeline can be resized by dragging either
edge: a visible grip/edge cursor, pointer capture, live width feedback while
dragging, and persistence through the region document on release."""
from __future__ import annotations

import sys
from pathlib import Path

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8")

ROOT = Path(__file__).resolve().parents[2]
TIMELINE = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.Timeline.cs"


def require(condition: bool, message: str) -> None:
    if not condition:
        print("FAIL:", message, file=sys.stderr)
        raise SystemExit(1)


def window(text: str, marker: str, length: int = 3200) -> str:
    index = text.find(marker)
    if index < 0:
        return ""
    return text[index:index + length]


timeline = TIMELINE.read_text(encoding="utf-8")

# EFFECTRESIZE-01: the effect block owns its cursor and two visible grips, and
# narrow blocks still split into a left/right grab zone.
for token in (
    "private sealed class TimelineEffectBlock : Grid",
    "public InputCursor? EdgeCursor",
    "public void HighlightEdge(EditorCueEdge? edge)",
    "private static EditorCueEdge? TimelineEffectEdgeAt(",
    "return x < width / 2 ? EditorCueEdge.Start : EditorCueEdge.End;",
):
    require(token in timeline, f"EFFECTRESIZE-01 the effect block affordance is missing: {token}")

# EFFECTRESIZE-02: every rendered effect block wires press/move/release/cancel
# and tells the user that both edges are draggable.
render = window(timeline, "private void RenderTimelineEffectBlocks(", 2600)
for token in (
    "new TimelineEffectBlock",
    "block.PointerPressed += (_, args) => TimelineRegionPointerPressed(captured, block, args);",
    "block.PointerMoved += (_, args) => TimelineRegionPointerMoved(captured, block, args);",
    "block.PointerReleased += (_, args) => TimelineRegionPointerReleased(captured, block, args);",
    "block.PointerCanceled += (_, args) => TimelineRegionPointerCanceled(captured, block, args);",
    "Kéo mép trái hoặc mép phải",
):
    require(token in render, f"EFFECTRESIZE-02 the effect block drag pipeline is missing: {token}")

# EFFECTRESIZE-03: the press must capture the pointer and the move must repaint
# the block at its new width instead of waiting for the release.
press = window(timeline, "private void TimelineRegionPointerPressed(", 2200)
for token in (
    "var edge = TimelineEffectEdgeAt(block, position.X);",
    "block.CapturePointer(args.Pointer);",
    "block.EdgeCursor = TimelineEdgeResizeCursor;",
    "_timelineDragRegionOriginStart = originStart;",
    "_timelineDragRegionOriginEnd = originEnd;",
    "RegionTimeForTimeline(TimelineDuration)",
):
    require(token in press, f"EFFECTRESIZE-03 the effect edge press is missing: {token}")
drag = window(timeline, "private void UpdateTimelineRegionDrag(", 2200)
for token in (
    "var duration = RegionTimeLimit();",
    "Math.Clamp(start + deltaSeconds, 0, maximum)",
    "Math.Clamp(end + deltaSeconds, minimum, duration)",
    "TimelineTimeForSource(start)",
    "TimelineTimeForSource(end)",
    "Canvas.SetLeft(block, left);",
    "block.Width = Math.Max(2, right - left);",
):
    require(token in drag, f"EFFECTRESIZE-03 the live effect resize is missing: {token}")

# EFFECTRESIZE-04: on release the region keeps the RegionList selection owner,
# becomes a timed region and persists through ReplaceSelected/undo/save.
commit = window(timeline, "private void CommitTimelineRegionScope(", 1600)
for token in (
    "RegionList.SelectedIndex = index;",
    "_document.ReplaceSelected(updated)",
    "region with { WholeVideo = false, Start = start, End = end }",
    "DocumentChanged(",
):
    require(token in commit, f"EFFECTRESIZE-04 the effect scope commit is missing: {token}")

# EFFECTRESIZE-05: the gate must stay registered in the aggregate verification.
verify = (ROOT / "csharp/scripts/verify.ps1").read_text(encoding="utf-8")
require(
    'Invoke-Checked python @("csharp/scripts/verify_editor_timeline_effect_resize_contract.py")' in verify,
    "EFFECTRESIZE-05 the effect resize contract must be registered in verify.ps1",
)

# EFFECTRESIZE-06: a timed region may continue past the end of the primary
# video into segment 2. The block spans the composed end, the drag limit is the
# project's region-time limit, and the whole-video origin maps to the composed
# strip instead of the primary file's duration.
require("var timelineEnd = region.WholeVideo ? duration : TimelineTimeForSource(region.End);" in timeline,
        "EFFECTRESIZE-06 the effect block must draw a region that continues into the next clip")
require("var duration = TimelineDuration > 0 ? TimelineDuration : _media?.Duration ?? 0;" in timeline,
        "EFFECTRESIZE-06 a whole-video effect must cover the composed output")
require("Math.Max(0, RegionTimeForTimeline(0) ?? 0)" in press
        and "RegionTimeForTimeline(TimelineDuration)" in press,
        "EFFECTRESIZE-06 dragging a whole-video edge must start from the composed strip bounds")

print("PASS: EFFECTRESIZE-01/02/03/04/05/06 effect regions resize by dragging either "
      "edge with live width feedback, persistence, undo history and cross-clip scope")
