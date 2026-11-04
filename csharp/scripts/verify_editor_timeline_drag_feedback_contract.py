#!/usr/bin/env python3
"""Kiểm tra phản hồi trực quan khi kéo clip, vạch chèn đúng bố cục sau khi chèn, ưu tiên lịch sử timeline, nút undo/redo timeline, khoá nút khi chạy job và click thân clip chỉ chọn."""
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
TIMELINE = PAGES / "EditorPage.Timeline.cs"
SHORTCUTS = PAGES / "EditorPage.Shortcuts.cs"
EDITOR = PAGES / "EditorPage.xaml.cs"
XAML = PAGES / "EditorPage.xaml"
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


def method(text: str, marker: str, length: int = 3200) -> str:
    index = text.find(marker)
    if index < 0:
        return ""
    end = text.find("\n    }", index)
    return text[index:end if end > 0 else index + length]


timeline = TIMELINE.read_text(encoding="utf-8")
shortcuts = SHORTCUTS.read_text(encoding="utf-8")
editor = EDITOR.read_text(encoding="utf-8")
xaml = XAML.read_text(encoding="utf-8")
move = (CORE / "EditorTimelineClipMove.cs").read_text(encoding="utf-8")

# DRAG-01: the clip body drag math lives in Core: the insertion index, the
# layout after the move and the insertion boundary must be one single source
# of truth.
for token in (
    "public static class EditorTimelineClipMove",
    "public sealed record EditorTimelineClipMoveLayout(",
    "public const double DragThresholdPixels = 5;",
    "public static int InsertionIndex(",
    "public static EditorTimelineClipMoveLayout Preview(",
    "public static double InsertionBoundaryTime(",
    "public static IReadOnlyList<EditorTimelineClip> Move(",
):
    require(token in move, f"DRAG-01 the core clip move math is missing: {token}")

# DRAG-02: the insertion guide must sit exactly on the insertion boundary of
# the layout AFTER the move, that is on the list with the new position applied,
# not on the shortened list.
require("=> Preview(clips, clipIndex, insertionIndex).InsertionStart;" in move,
        "DRAG-02 the insertion guide must be computed on the layout after the move")
require("var moved = new List<EditorTimelineClip>(clips.Count);" in move
        and "foreach (var index in order) moved.Add(clips[index]);" in move,
        "DRAG-02 the preview layout must be built from the moved clip list")
require("var order = MovedOrder(clips.Count, clipIndex, insertionIndex);" in move,
        "DRAG-02 the preview layout must use the moved order")

# DRAG-03: dragging a clip must have visual feedback: the lane is redrawn at
# the pointer position, and the clip being dragged follows the pointer at its
# true length.
for token in (
    "private readonly List<(int Index, TimelineClipBlock Block)> _timelineClipBlocks = [];",
    "private void ApplyTimelineClipMovePreview(int index, int insertion, double cursorContentX, double pps)",
    "private void ApplyTimelineClipPositions(",
    "private void RestoreTimelineClipLayout()",
    "private TimelineClipBlock? TimelineClipBlockAt(int clipIndex)",
    "var draggedLeft = _timelineDragClipOriginLeft + (cursorContentX - _timelineDragClipOriginContentX);",
    "Canvas.SetLeft(block, EditorTimelineLayout.XForTime(starts[position], pps));",
):
    require(token in timeline, f"DRAG-03 the clip drag feedback lost: {token}")

preview = method(timeline, "private void ApplyTimelineClipMovePreview(")
require("EditorTimelineClipMove.Preview(_timelineClips, index, insertion)" in preview,
        "DRAG-03 the drag preview must use the core layout after the move")
require("ApplyTimelineClipPositions(index, layout.Order, layout.Starts, draggedLeft, pps);" in preview,
        "DRAG-03 the drag preview must move the clip blocks on the lane")

move_drag = method(timeline, "private void UpdateTimelineClipMoveDrag(int index)")
for token in (
    "EditorTimelineTrim.ReachedDragThreshold(moved, EditorTimelineClipMove.DragThresholdPixels)",
    "EditorTimelineClipMove.InsertionIndex(_timelineClips, index, cursorContentX, pps)",
    "ApplyTimelineClipMovePreview(index, insertion, cursorContentX, pps);",
    "EditorTimelineClipMove.InsertionBoundaryTime(_timelineClips, index, insertion)",
    "RestoreTimelineClipLayout();",
):
    require(token in move_drag, f"DRAG-03 the clip body drag lost: {token}")

# DRAG-04: dragging a clip must not change the in point or the duration of the
# source file: a body drag only changes the order on the strip.
require("public static IReadOnlyList<EditorTimelineClip> Move(" in move
        and "result.Add(clips[index]);" in move,
        "DRAG-04 the clip move must reuse the original clip records")
require("SourceIn =" not in method(timeline, "private void ApplyTimelineClipMovePreview("),
        "DRAG-04 the clip drag preview must not rewrite the source in point")
require("Duration =" not in method(timeline, "private void ApplyTimelineClipMovePreview("),
        "DRAG-04 the clip drag preview must not rewrite the clip duration")

# DRAG-05: dropping back onto the exact same slot changes nothing and writes no
# history.
commit_move = method(timeline, "private void CommitTimelineClipMove(")
require("if (insertion == index)" in commit_move,
        "DRAG-05 dropping a clip on its own slot must be a no-op")
require("CommitTimelineClips(clips);" in commit_move,
        "DRAG-05 a real move must go through the timeline history owner")

# SELECT-01: clicking the clip body only selects the clip, it does NOT seek the
# playhead. Seeking only happens when pressing the ruler or an empty area of
# the lane.
pressed = method(timeline, "private void TimelineVideoClipPressed(")
require(pressed != "", "SELECT-01 the clip press owner is missing")
body_branch = pressed[pressed.find("if (edge is null)"):]
require("args.Handled = true;" in body_branch,
        "SELECT-01 clicking the clip body must not seek the playhead")
require("StartTimelineClipMoveDrag(index, block, args);" in body_branch,
        "SELECT-01 clicking the clip body must select the clip and prepare the move drag")
for token in (
    "private void TimelineRuler_PointerPressed(object sender, PointerRoutedEventArgs e) => SeekTimelineFromPointer(e);",
    "private void TimelineTracks_PointerPressed(object sender, PointerRoutedEventArgs e) => SeekTimelineFromPointer(e);",
    "private void SeekTimelineFromPointer(PointerRoutedEventArgs e)",
):
    require(token in timeline, f"SELECT-01 the seek paths lost: {token}")

# UNDO-01: Ctrl+Z/Ctrl+Y always prefer the timeline history; only when the
# strip history is empty do they fall back to the effect region history.
undo = method(timeline, "private async Task UndoTimelineAsync()")
redo = method(timeline, "private async Task RedoTimelineAsync()")
for body, name in ((undo, "undo"), (redo, "redo")):
    require(body != "", f"UNDO-01 the {name} command is missing")
    require("if (!_timelineHistory.CanUndo)" in body or "if (!_timelineHistory.CanRedo)" in body,
            f"UNDO-01 the {name} command must check the timeline history first")
    require("TryUndoDocument()" in body or "TryRedoDocument()" in body,
            f"UNDO-01 the {name} command must fall back to the region history")
require(undo.find("if (!_timelineHistory.CanUndo)") < undo.find("if (TryUndoDocument())"),
        "UNDO-01 the timeline history must win over the region history")
require(redo.find("if (!_timelineHistory.CanRedo)") < redo.find("if (TryRedoDocument())"),
        "UNDO-01 the timeline history must win over the region history on redo")

region_shortcut = method(editor, "private bool HandleEditorRegionShortcut(KeyRoutedEventArgs e)")
require("if (_timelineHistory.CanUndo)" in region_shortcut,
        "UNDO-01 the effect region shortcut must prefer the timeline history")
require("if (_timelineHistory.CanRedo)" in region_shortcut,
        "UNDO-01 the effect region shortcut must prefer the timeline history on redo")
require("RunTimelineHistoryCommandAsync(UndoTimelineAsync)" in region_shortcut,
        "UNDO-01 the effect region shortcut must run the shared timeline undo command")
require("RunTimelineHistoryCommandAsync(RedoTimelineAsync)" in region_shortcut,
        "UNDO-01 the effect region shortcut must run the shared timeline redo command")
for token in (
    "[EditorShortcuts.UndoId] = UndoTimelineAsync,",
    "[EditorShortcuts.RedoId] = RedoTimelineAsync,",
):
    require(token in shortcuts, f"UNDO-01 the shortcut action mapping lost: {token}")

# UNDO-02: the command that just ran must be reported clearly in the status text.
for token in (
    "Đã hoàn tác thao tác trên dải thời gian.",
    "Đã làm lại thao tác trên dải thời gian.",
    "Đã hoàn tác thay đổi vùng hiệu ứng.",
    "Đã làm lại thay đổi vùng hiệu ứng.",
):
    require(token in timeline or token in editor,
            f"UNDO-02 the status text of the command is missing: {token}")

# BUTTON-01: the TIMELINE undo/redo buttons live in the add & edit clip group,
# with an AutomationId and a Vietnamese AutomationProperties.Name.
clip_group = window(xaml, 'x:Name="TimelineClipToolsGroup"', 3600)
require(clip_group != "", "BUTTON-01 the clip editing group is missing")
for control, label in (
    ("TimelineUndoButton", "Hoàn tác thao tác trên dải thời gian"),
    ("TimelineRedoButton", "Làm lại thao tác trên dải thời gian"),
):
    require(f'x:Name="{control}"' in clip_group,
            f"BUTTON-01 the timeline {control} must live in the clip editing group")
    require(f'AutomationProperties.AutomationId="{control}"' in xaml,
            f"BUTTON-01 the timeline {control} must keep an AutomationId for UIA")
    require(f'AutomationProperties.Name="{label}"' in clip_group,
            f"BUTTON-01 the timeline {control} must carry a Vietnamese AutomationProperties.Name")
    require(f'Click="TimelineUndoButton_Click"' in clip_group
            or f'Click="TimelineRedoButton_Click"' in clip_group,
            f"BUTTON-01 the timeline {control} must be wired to its handler")

for control in ("TimelineAddClipButton", "TimelineSplitClipButton", "TimelineRemoveClipButton"):
    require(f'AutomationProperties.AutomationId="{control}"' in xaml,
            f"BUTTON-01 the existing {control} AutomationId must stay untouched")

# BUTTON-02: the enable/disable state of the timeline undo/redo buttons
# follows the history, and the add/split/remove buttons are locked while the
# app is running a job.
for token in (
    "private void RefreshTimelineClipTools()",
    "TimelineAddClipButton.IsEnabled = editable;",
    "TimelineSplitClipButton.IsEnabled = editable;",
    "TimelineRemoveClipButton.IsEnabled = editable;",
    "TimelineUndoButton.IsEnabled = editable && _timelineHistory.CanUndo;",
    "TimelineRedoButton.IsEnabled = editable && _timelineHistory.CanRedo;",
    "private bool TimelineEditingAllowed()",
):
    require(token in timeline, f"BUTTON-02 the timeline toolbar state owner lost: {token}")
require("var editable = !EditorBusy && _media is not null && !_playback.IsPreviewMode;" in timeline,
        "BUTTON-02 the timeline toolbar must be locked while a job runs")
require("RefreshTimelineClipTools();" in editor,
        "BUTTON-02 the app action refresh must update the timeline toolbar state")
for owner, marker in (
    ("add", "private async void TimelineAddClipButton_Click("),
    ("split", "private async Task SplitTimelineClipAtPlayheadAsync()"),
    ("remove", "private async Task RemoveSelectedTimelineClipAsync()"),
):
    body = method(timeline, marker)
    require("if (!TimelineEditingAllowed()) return;" in body,
            f"BUTTON-02 the {owner} command must refuse to run while a job runs")
require("if (EditorBusy || _playback.IsPreviewMode)" in method(timeline, "private bool TimelineEditingAllowed()"),
        "BUTTON-02 the timeline edit guard must check the running job")

# BUTTON-03: the timeline undo/redo buttons use the same shared command as the
# shortcuts.
for token in (
    "private async void TimelineUndoButton_Click(object sender, RoutedEventArgs args)",
    "private async void TimelineRedoButton_Click(object sender, RoutedEventArgs args)",
    "RunTimelineHistoryCommandAsync(UndoTimelineAsync);",
    "RunTimelineHistoryCommandAsync(RedoTimelineAsync);",
    "private async Task RunTimelineHistoryCommandAsync(Func<Task> command)",
):
    require(token in timeline, f"BUTTON-03 the timeline undo/redo buttons lost: {token}")

# CONTRACT-01: the executable contract must lock in clip drag with visual
# feedback and the insertion guide at the right position, and this script must
# be registered in verify.ps1.
tests = (TESTS / "Program.cs").read_text(encoding="utf-8")
require("editor timeline clip body drag reorders clips and marks the insertion slot" in tests,
        "CONTRACT-01 the executable clip move contract test is missing")
move_contract = (TESTS / "EditorTimelineClipMoveContract.cs").read_text(encoding="utf-8")
for token in (
    "EditorTimelineClipMove.InsertionIndex(",
    "EditorTimelineClipMove.InsertionBoundaryTime(",
    "EditorTimelineClipMove.Preview(",
    "EditorTimelineClipMove.Move(",
    "the insertion guide must sit on the start of the moved clip in the new layout",
):
    require(token in move_contract, f"CONTRACT-01 the clip move contract test lost: {token}")

verify = (ROOT / "csharp/scripts/verify.ps1").read_text(encoding="utf-8")
require('Invoke-Checked python @("csharp/scripts/verify_editor_timeline_drag_feedback_contract.py")' in verify,
        "CONTRACT-01 the drag feedback contract script must be registered in verify.ps1")

print("PASS: DRAG-01/02/03/04/05 the pointer driven clip drag with the exact "
      "insertion guide, SELECT-01 the clip body click only selects, UNDO-01/02 the timeline history "
      "priority and BUTTON-01/02/03 the locked timeline toolbar buttons are locked")
