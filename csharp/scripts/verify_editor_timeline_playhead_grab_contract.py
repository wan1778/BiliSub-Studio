#!/usr/bin/env python3
"""Check the playhead drag grab zone on the timeline: there must be a drag
start path from a press on the ruler band, capture/move/release must exist, the
grab zone must be wide enough, and it must not steal clip-edge trim or cue
drag."""
from __future__ import annotations

import sys
from pathlib import Path

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8")

ROOT = Path(__file__).resolve().parents[2]
PAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages"
CORE = ROOT / "csharp/src/BiliSubStudio.Core/Editor"
TESTS = ROOT / "csharp/tests/BiliSubStudio.Core.ContractTests"
TIMELINE = PAGES / "EditorPage.Timeline.cs"
XAML = PAGES / "EditorPage.xaml"
PLAYHEAD = CORE / "EditorTimelinePlayhead.cs"


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
xaml = XAML.read_text(encoding="utf-8")
playhead = PLAYHEAD.read_text(encoding="utf-8")

# GRABPLAYHEAD-01: the grab zone must be wider than half the dot width, otherwise
# the dot hides the grab zone and no drag path can start.
require("public const double GrabPixels = 16d;" in playhead,
        "GRABPLAYHEAD-01 the grab radius must be 16 DIP")
require("public const double GrabZoneWidth = GrabPixels * 2d;" in playhead,
        "GRABPLAYHEAD-01 the grab zone width must be derived from the grab radius")
require("TimelinePlayheadHead.Width = EditorTimelinePlayhead.GrabZoneWidth;" in timeline,
        "GRABPLAYHEAD-01 the playhead head hit area must match the grab zone")

# GRABPLAYHEAD-02: there must be a drag start path from a press on the RULER
# (where the dot sits): the dot takes the press itself (it swallows the ruler
# event because it is a direct child of the content canvas) and forwards it to
# the same owning handler.
require('PointerPressed="TimelineRuler_PointerPressed"' in xaml,
        "GRABPLAYHEAD-02 the ruler must keep its own press handler")
require('PointerPressed="TimelinePlayheadHead_PointerPressed"' in xaml,
        "GRABPLAYHEAD-02 the playhead head must own its press")
require("private void TimelinePlayheadHead_PointerPressed(object sender, PointerRoutedEventArgs e)" in timeline,
        "GRABPLAYHEAD-02 the playhead head press owner is missing")
require("SeekTimelineFromPointer(e);" in window(timeline, "private void TimelinePlayheadHead_PointerPressed(", 400),
        "GRABPLAYHEAD-02 the playhead head press must reach the shared owner")
require("StartTimelinePlayheadDrag(e);" in window(timeline, "private void SeekTimelineFromPointer(", 2400),
        "GRABPLAYHEAD-02 a press inside the grab zone must start the drag")

# GRABPLAYHEAD-03: the grab zone only opens on the ruler band, so a press in a
# lane must not steal clip-edge trim or cue drag.
require("EditorTimelinePlayhead.IsInRulerBand(position.Y, EditorTimelineLayout.RulerHeight)" in timeline,
        "GRABPLAYHEAD-03 the grab zone must be limited to the ruler band")
require("public static bool IsInRulerBand(double pointerY, double rulerHeight)" in playhead,
        "GRABPLAYHEAD-03 the ruler band rule must live in Core")
clip_press = window(timeline, "private void TimelineVideoClipPressed(", 2600)
require("args.Handled = true;" in clip_press,
        "GRABPLAYHEAD-03 the clip press must still swallow the event for trim/move")
cue_press = window(timeline, "private void TimelineCuePointerPressed(", 1600)
require("args.Handled = true;" in cue_press,
        "GRABPLAYHEAD-03 the cue press must still swallow the event for cue drag")

# GRABPLAYHEAD-04: a drag needs capture + move + release, and must read the real
# cursor position (while the pointer is captured the coordinates in pointer
# events are frozen).
for token in (
    "TimelineContentGrid.CapturePointer(e.Pointer);",
    "private void TimelineContentGrid_PointerMoved(object sender, PointerRoutedEventArgs e)",
    "private async void TimelineContentGrid_PointerReleased(object sender, PointerRoutedEventArgs e)",
    "private void TimelineContentGrid_PointerCaptureLost(object sender, PointerRoutedEventArgs e)",
    "private double CurrentTimelinePlayheadCursorContentX()",
    "Win32Cursor.TryGetScreenPosition(out var screenX, out _)",
):
    require(token in timeline, f"GRABPLAYHEAD-04 the drag pipeline lost: {token}")
for token in (
    'PointerMoved="TimelineContentGrid_PointerMoved"',
    'PointerReleased="TimelineContentGrid_PointerReleased"',
    'PointerCanceled="TimelineContentGrid_PointerCanceled"',
    'PointerCaptureLost="TimelineContentGrid_PointerCaptureLost"',
):
    require(token in xaml, f"GRABPLAYHEAD-04 the drag handlers are not wired: {token}")

# GRABPLAYHEAD-05: the executable contract must lock the grab zone, and this
# script must be registered in verify.ps1.
tests = (TESTS / "Program.cs").read_text(encoding="utf-8")
require("editor playhead grab zone is wide enough and only opens on the ruler band" in tests,
        "GRABPLAYHEAD-05 the executable grab contract test is missing")
contract = (TESTS / "EditorTimelinePlayheadGrabContract.cs").read_text(encoding="utf-8")
for token in (
    "EditorTimelinePlayhead.GrabPixels >= 16d",
    "EditorTimelinePlayhead.GrabZoneWidth == EditorTimelinePlayhead.GrabPixels * 2d",
    "EditorTimelinePlayhead.IsWithinGrabRange(",
    "EditorTimelinePlayhead.IsInRulerBand(",
):
    require(token in contract, f"GRABPLAYHEAD-05 the contract test lost: {token}")
verify = (ROOT / "csharp/scripts/verify.ps1").read_text(encoding="utf-8")
require('Invoke-Checked python @("csharp/scripts/verify_editor_timeline_playhead_grab_contract.py")' in verify,
        "GRABPLAYHEAD-05 the playhead grab contract script must be registered in verify.ps1")

print("PASS: GRABPLAYHEAD-01/02/03/04/05 the playhead grab zone on the ruler band "
      "with capture, move, release and untouched trim/cue gestures are locked")
