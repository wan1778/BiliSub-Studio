#!/usr/bin/env python3
"""Kiểm tra kéo được playhead trên dải thời gian và preview phát từ vị trí
playhead hiện tại."""
from __future__ import annotations

import sys
from pathlib import Path

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8")

ROOT = Path(__file__).resolve().parents[2]
PAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages"
APP = ROOT / "csharp/src/BiliSubStudio.App"
CORE = ROOT / "csharp/src/BiliSubStudio.Core/Editor"
TIMELINE = PAGES / "EditorPage.Timeline.cs"
PLAYBACK = PAGES / "EditorPage.Playback.cs"
XAML = PAGES / "EditorPage.xaml"
HANDLE = APP / "Controls/TimelinePlayheadHandle.cs"
TESTS = ROOT / "csharp/tests/BiliSubStudio.Core.ContractTests"


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
playback = PLAYBACK.read_text(encoding="utf-8")
xaml = XAML.read_text(encoding="utf-8")
handle = HANDLE.read_text(encoding="utf-8")
playhead = (CORE / "EditorTimelinePlayhead.cs").read_text(encoding="utf-8")
start = (CORE / "EditorTimelinePlaybackStart.cs").read_text(encoding="utf-8")

# DRAGPLAYHEAD-01: the grab zone around the playhead is a constant in Core so
# it is testable and so the UI does not invent another number of its own.
for token in (
    "public const double GrabPixels = 16d;",
    "public const double GrabZoneWidth = GrabPixels * 2d;",
    "public static bool IsWithinGrabRange(double pointerX, double playheadX)",
    "return Math.Abs(pointerX - playheadX) <= GrabPixels;",
    "public static bool IsInRulerBand(double pointerY, double rulerHeight)",
):
    require(token in playhead, f"DRAGPLAYHEAD-01 the core grab zone is missing: {token}")

# DRAGPLAYHEAD-02: the playhead head is a hittable control (not a circle with
# IsHitTestVisible=False) and has a horizontal drag cursor.
require('xmlns:controls="using:BiliSubStudio.App.Controls"' in xaml,
        "DRAGPLAYHEAD-02 the editor page must declare the custom control namespace")
head_start = xaml.find("<controls:TimelinePlayheadHandle")
head = xaml[head_start:xaml.find("/>", head_start) + 2] if head_start >= 0 else ""
require(head != "", "DRAGPLAYHEAD-02 the playhead head must be the draggable handle")
for token in (
    'x:Name="TimelinePlayheadHead"',
    'AutomationProperties.AutomationId="TimelinePlayheadHead"',
):
    require(token in head, f"DRAGPLAYHEAD-02 the playhead handle lost: {token}")
require("IsHitTestVisible=\"False\"" not in head,
        "DRAGPLAYHEAD-02 the playhead handle must stay hit testable")
for token in (
    "public sealed class TimelinePlayheadHandle : Grid",
    "public InputCursor? DragCursor",
    "get => ProtectedCursor;",
    "set => ProtectedCursor = value;",
):
    require(token in handle, f"DRAGPLAYHEAD-02 the playhead handle lost: {token}")
require("TimelinePlayheadHead.DragCursor = TimelineEdgeResizeCursor;" in timeline,
        "DRAGPLAYHEAD-02 the playhead head must show the horizontal drag cursor")

# DRAGPLAYHEAD-03: pressing inside the zone around the playhead grabs the
# playhead, pressing elsewhere still seeks as before.
seek = window(timeline, "private void SeekTimelineFromPointer(", 2400)
require(seek != "", "DRAGPLAYHEAD-03 the pointer seek owner is missing")
require("EditorTimelinePlayhead.IsWithinGrabRange(position.X, playheadX)" in seek,
        "DRAGPLAYHEAD-03 the seek owner must check the playhead grab zone")
require("EditorTimelinePlayhead.IsInRulerBand(position.Y, EditorTimelineLayout.RulerHeight)" in seek,
        "DRAGPLAYHEAD-03 the playhead grab zone must only open on the ruler band")
require("e.Handled = true;" in seek,
        "DRAGPLAYHEAD-03 a press on the playhead must not travel further up the tree")
require("StartTimelinePlayheadDrag(e);" in seek,
        "DRAGPLAYHEAD-03 a press on the playhead must start the drag")
require("_ = SeekEditorToTimelineAsync(timelineTime);" in seek,
        "DRAGPLAYHEAD-03 a press outside the playhead must still seek")

# DRAGPLAYHEAD-03b: the round playhead head is a DIRECT child of the content
# canvas so it swallows the ruler's press events: it must handle the press
# itself and forward it to the same decision path (grab the playhead) as the
# ruler.
require("private void TimelinePlayheadHead_PointerPressed(object sender, PointerRoutedEventArgs e)" in timeline,
        "DRAGPLAYHEAD-03b the playhead head must own its own press")
head_press = window(timeline, "private void TimelinePlayheadHead_PointerPressed(", 400)
require("SeekTimelineFromPointer(e);" in head_press,
        "DRAGPLAYHEAD-03b the playhead head press must reach the shared seek/grab owner")
require('PointerPressed="TimelinePlayheadHead_PointerPressed"' in xaml,
        "DRAGPLAYHEAD-03b the playhead head press handler must be wired in the page")
require("TimelinePlayheadHead.Width = EditorTimelinePlayhead.GrabZoneWidth;" in timeline,
        "DRAGPLAYHEAD-03b the playhead head hit area must match the grab zone")

# DRAGPLAYHEAD-04: dragging the playhead changes its position on the strip
# (snapped to the frame grid); the player is only seeked and the position saved
# to the project on mouse release.
for token in (
    "private void StartTimelinePlayheadDrag(PointerRoutedEventArgs e)",
    "private void TimelineContentGrid_PointerMoved(object sender, PointerRoutedEventArgs e)",
    "private async void TimelineContentGrid_PointerReleased(object sender, PointerRoutedEventArgs e)",
    "private void TimelineContentGrid_PointerCanceled(object sender, PointerRoutedEventArgs e)",
    "private void TimelineContentGrid_PointerCaptureLost(object sender, PointerRoutedEventArgs e)",
    "private void SetTimelinePlayheadTime(double timelineTime)",
    "TimelineContentGrid.CapturePointer(e.Pointer);",
    "var snapped = SnapTimelinePlayhead(timelineTime);",
    "var target = Math.Clamp(snapped, Timeline.Minimum, Timeline.Maximum);",
):
    require(token in timeline, f"DRAGPLAYHEAD-04 the drag owner lost: {token}")
move = window(timeline, "private void TimelineContentGrid_PointerMoved(", 1400)
require("SetTimelinePlayheadTime(EditorTimelineLayout.TimeForX(contentX, _timelinePixelsPerSecond));" in move,
        "DRAGPLAYHEAD-04 the drag must convert the pointer position to timeline time")
require("var contentX = CurrentTimelinePlayheadCursorContentX();" in move,
        "DRAGPLAYHEAD-04 the drag must read the live cursor position")
cursor = window(timeline, "private double CurrentTimelinePlayheadCursorContentX()", 1200)
require("Win32Cursor.TryGetScreenPosition(out var screenX, out _)" in cursor,
        "DRAGPLAYHEAD-04 the drag must read the real cursor from the operating system")
require("var scale = TimelineContentGrid.XamlRoot?.RasterizationScale ?? 1d;" in cursor,
        "DRAGPLAYHEAD-04 the drag must convert screen pixels to DIP")
release = window(timeline, "private async void TimelineContentGrid_PointerReleased(", 900)
require("await SeekEditorToTimelineAsync(_timelinePlayheadDragTime);" in release,
        "DRAGPLAYHEAD-04 dropping the playhead must seek the player to the new position")
setter = window(timeline, "private void SetTimelinePlayheadTime(", 900)
require("_syncingTimeline = true;" in setter,
        "DRAGPLAYHEAD-04 the drag must not look like a user seek while the pointer is down")
require("try { Timeline.Value = target; }" in setter,
        "DRAGPLAYHEAD-04 the drag must move the playhead on the strip")
for token in (
    'PointerMoved="TimelineContentGrid_PointerMoved"',
    'PointerReleased="TimelineContentGrid_PointerReleased"',
    'PointerCanceled="TimelineContentGrid_PointerCanceled"',
    'PointerCaptureLost="TimelineContentGrid_PointerCaptureLost"',
):
    require(token in xaml, f"DRAGPLAYHEAD-04 the drag handlers are not wired: {token}")

# DRAGPLAYHEAD-04b: losing pointer capture mid-drag must clear the "dragging"
# state, the strip must not get stuck.
capture_lost = window(timeline, "private void TimelineContentGrid_PointerCaptureLost(", 700)
require("_timelinePlayheadDragging = false;" in capture_lost,
        "DRAGPLAYHEAD-04b losing the pointer capture must end the drag")

# DRAGPLAYHEAD-05: a player that is playing must not drag the playhead back
# while the user is holding the mouse on the strip.
position_changed = window(playback, "private void PlayerPositionChanged(", 1200)
require("if (_page._timelinePlayheadDragging) return;" in position_changed,
        "DRAGPLAYHEAD-05 the player must not fight the playhead drag")

# PLAYSTART-01: pressing play starts from the current playhead position, never
# earlier than the start marker of the strip.
for token in (
    "public static class EditorTimelinePlaybackStart",
    "public static double Resolve(double playheadTime, double earliestAllowedTime, double duration)",
    "return Math.Clamp(Math.Max(playhead, earliest), 0, total);",
):
    require(token in start, f"PLAYSTART-01 the core playback start math is missing: {token}")
require("private Task PlayFromStartAsync() => LoadSegmentAsync(PlaybackStartTime(), play: true);" in playback,
        "PLAYSTART-01 the first Play must start from the current playhead")
playback_start = window(playback, "private double PlaybackStartTime()", 900)
for token in (
    "EditorTimelinePlaybackStart.Resolve(_page.Timeline.Value, PreviewStartTime(), duration);",
    "var duration = _page.TimelineDuration;",
):
    require(token in playback_start, f"PLAYSTART-01 the playback start owner lost: {token}")
require("private Task ReplayFromStartAsync() => LoadSegmentAsync(PreviewStartTime(), play: true);" in playback,
        "PLAYSTART-01 the replay after the end must restart from the start marker")

# PLAYSTART-02: the executable contract must lock both the playhead drag
# behavior and the preview start marker, and this script must be registered in
# verify.ps1.
tests = (TESTS / "Program.cs").read_text(encoding="utf-8")
require("editor playhead can be dragged on the strip and playback starts from it" in tests,
        "PLAYSTART-02 the executable playhead drag contract test is missing")
contract = (TESTS / "EditorTimelinePlaybackStartContract.cs").read_text(encoding="utf-8")
for token in (
    "EditorTimelinePlaybackStart.Resolve(12, 5, 60)",
    "EditorTimelinePlaybackStart.Resolve(2, 5, 60)",
    "EditorTimelinePlayhead.IsWithinGrabRange(",
    "EditorTimelinePlayhead.GrabPixels",
):
    require(token in contract, f"PLAYSTART-02 the contract test lost: {token}")
verify = (ROOT / "csharp/scripts/verify.ps1").read_text(encoding="utf-8")
require('Invoke-Checked python @("csharp/scripts/verify_editor_timeline_playhead_drag_contract.py")' in verify,
        "PLAYSTART-02 the playhead drag contract script must be registered in verify.ps1")

print("PASS: DRAGPLAYHEAD-01/02/03/04/05 the draggable playhead with a real grab zone "
      "and PLAYSTART-01/02 playback that starts at the current playhead are locked")
