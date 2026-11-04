#!/usr/bin/env python3
"""Kiểm tra undo/redo thật của dải thời gian, kéo thân clip đổi thứ tự và thanh công cụ dải gom nhóm."""
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
xaml = XAML.read_text(encoding="utf-8")
core_shortcuts = (CORE / "EditorShortcuts.cs").read_text(encoding="utf-8")
history = (CORE / "EditorTimelineHistory.cs").read_text(encoding="utf-8")
move = (CORE / "EditorTimelineClipMove.cs").read_text(encoding="utf-8")

# HISTORY-01: the undo/redo history lives in Core, pure and testable: push the
# before/after state, undo, redo, cap the number of steps and clear the redo
# when a new action happens.
for token in (
    "public sealed class EditorTimelineHistory",
    "public sealed record EditorTimelineHistoryEntry(",
    "public const int DefaultMaxSteps = 50;",
    "public void Push(IReadOnlyList<EditorTimelineClip> before, IReadOnlyList<EditorTimelineClip> after)",
    "public IReadOnlyList<EditorTimelineClip>? Undo()",
    "public IReadOnlyList<EditorTimelineClip>? Redo()",
    "public bool CanUndo => _cursor > 0;",
    "public bool CanRedo => _cursor < _entries.Count;",
    "if (_cursor < _entries.Count) _entries.RemoveRange(_cursor, _entries.Count - _cursor);",
    "if (_entries.Count > MaxSteps) _entries.RemoveRange(0, _entries.Count - MaxSteps);",
):
    require(token in history, f"HISTORY-01 the core timeline history lost: {token}")

# HISTORY-02: every action that changes the composed strip is recorded to the
# history through a single owner, and the history is cleared when another
# project opens.
for token in (
    "private readonly EditorTimelineHistory _timelineHistory = new();",
    "private void CommitTimelineClips(IReadOnlyList<EditorTimelineClip> clips)",
    "_timelineHistory.Push(_timelineClips, clips);",
    "_timelineHistory.Clear();",
):
    require(token in timeline, f"HISTORY-02 the timeline history owner lost: {token}")

commit = method(timeline, "private void CommitTimelineClips(IReadOnlyList<EditorTimelineClip> clips)")
require("ApplyTimelineClips(clips);" in commit,
        "HISTORY-02 a committed action must apply the new clip list")

for owner, marker in (
    ("add", "private async void TimelineAddClipButton_Click("),
    ("split", "private async Task SplitTimelineClipAtPlayheadAsync()"),
    ("remove", "private async Task RemoveSelectedTimelineClipAsync()"),
):
    body = method(timeline, marker)
    require("CommitTimelineClips(clips);" in body,
            f"HISTORY-02 the {owner} action must go through the history owner")

trim_release = method(timeline, "private void TimelineVideoClipPointerReleased(")
require("CommitTimelineClips(clips);" in trim_release,
        "HISTORY-02 the clip edge trim must go through the history owner")
move_commit = method(timeline, "private void CommitTimelineClipMove(")
require("CommitTimelineClips(clips);" in move_commit,
        "HISTORY-02 the clip body move must go through the history owner")

# HISTORY-03: undo/redo bind to the REAL action through the registry and the
# "not supported" state is gone.
for token in (
    "[EditorShortcuts.UndoId] = UndoTimelineAsync,",
    "[EditorShortcuts.RedoId] = RedoTimelineAsync,",
):
    require(token in shortcuts, f"HISTORY-03 the shortcut action mapping lost: {token}")

undo_binding = window(core_shortcuts, "            UndoId,", 500)
redo_binding = window(core_shortcuts, "            RedoId,", 500)
require(undo_binding != "" and redo_binding != "",
        "HISTORY-03 the undo/redo bindings are missing")
require("IsSupported: false" not in undo_binding and "IsSupported: false" not in redo_binding,
        "HISTORY-03 the undo/redo bindings must not stay unsupported")
require("giai đoạn 4" not in core_shortcuts,
        "HISTORY-03 the unsupported note of stage 4 must be gone")

# HISTORY-04: undo/redo update the preview, the selection, the strip duration
# and save the project.
undo_command = method(timeline, "private async Task UndoTimelineAsync()")
redo_command = method(timeline, "private async Task RedoTimelineAsync()")
for body, name, action in (
    (undo_command, "undo", "_timelineHistory.Undo()"),
    (redo_command, "redo", "_timelineHistory.Redo()"),
):
    require(body != "", f"HISTORY-04 the {name} command is missing")
    for token in (
        action,
        "ApplyTimelineClips(",
        "SelectTimelineClipById(",
        "await SaveTimelineHistoryAsync(",
    ):
        require(token in body, f"HISTORY-04 the {name} command lost: {token}")

for token in (
    "private void SelectTimelineClipById(string? id)",
    "private string? TimelineSelectedClipId()",
    "private async Task SaveTimelineHistoryAsync(string message)",
    "await SaveProjectNowAsync();",
    "RefreshTimelinePreview();",
    "private void RefreshTimelinePreview()",
    "QueuePreviewRefresh();",
):
    require(token in timeline, f"HISTORY-04 the undo/redo side effects lost: {token}")

# HISTORY-05: edge trimming must also refresh the preview like undo/redo and
# reorder drags: a preview that is playing must be seeked back onto the
# playhead.
trim_save = method(timeline, "private async Task SaveTimelineClipTrimAsync(")
require(trim_save != "", "HISTORY-05 the clip trim save command is missing")
require("RefreshTimelinePreview();" in trim_save,
        "HISTORY-05 the clip trim must refresh the preview like undo/redo do")
require("if (!_playback.IsPreviewMode) QueuePreviewRefresh();" not in trim_save,
        "HISTORY-05 the clip trim must not skip the preview refresh while the preview plays")

# MOVE-01: the clip body drag math lives in Core: drag threshold, insertion
# index, insertion guide and reorder.
for token in (
    "public static class EditorTimelineClipMove",
    "public const double DragThresholdPixels = 5;",
    "public static int InsertionIndex(",
    "public static double InsertionBoundaryTime(",
    "public static IReadOnlyList<EditorTimelineClip> Move(",
):
    require(token in move, f"MOVE-01 the core clip move math is missing: {token}")

# MOVE-02: the UI must clearly distinguish edge drag (trim), body drag
# (reorder) and click (select), with a separate drag threshold for the clip
# body.
for token in (
    "private enum TimelineClipDragMode",
    "TimelineClipDragMode.Edge",
    "TimelineClipDragMode.Move",
    "private void StartTimelineClipMoveDrag(int index, TimelineClipBlock block, PointerRoutedEventArgs args)",
    "private void UpdateTimelineClipMoveDrag(int index)",
    "private void UpdateTimelineClipEdgeDrag(int index, TimelineClipBlock block)",
    "EditorTimelineTrim.ReachedDragThreshold(moved, EditorTimelineClipMove.DragThresholdPixels)",
    "EditorTimelineClipMove.InsertionIndex(_timelineClips, index, cursorContentX, pps)",
    "EditorTimelineClipMove.InsertionBoundaryTime(_timelineClips, index, insertion)",
    "EditorTimelineClipMove.Move(_timelineClips, index, insertion)",
):
    require(token in timeline, f"MOVE-02 the clip body drag lost: {token}")

moved = method(timeline, "private void TimelineVideoClipPointerMoved(")
require("TimelineClipDragMode.Move" in moved,
        "MOVE-02 the pointer move must branch between the edge drag and the body drag")
released = method(timeline, "private void TimelineVideoClipPointerReleased(")
require("CommitTimelineClipMove(index, insertion);" in released,
        "MOVE-02 dropping the clip body must commit the new order")
require("if (!active)" in released,
        "MOVE-02 a click under the drag threshold must not reorder the clips")

# MOVE-03: a dragged clip shows the insertion position on the strip.
for token in (
    "private void ShowTimelineDropIndicator(double boundaryTime, double pps)",
    "private void HideTimelineDropIndicator()",
    'x:Name="TimelineDropIndicator"',
):
    require(token in timeline or token in xaml, f"MOVE-03 the drop indicator lost: {token}")

# TOOLBAR-01: the strip toolbar groups into three clear groups and keeps the
# old AutomationIds for the UIA tests.
for group in ("TimelineClipToolsGroup", "TimelineZoomToolsGroup"):
    require(f'x:Name="{group}"' in xaml, f"TOOLBAR-01 the toolbar group '{group}' is missing")
require(xaml.count('<Rectangle Grid.Column="2" Width="1" Height="18"') == 1
        and xaml.count('<Rectangle Grid.Column="4" Width="1" Height="18"') == 1,
        "TOOLBAR-01 the toolbar groups must be separated by thin rules")

for control in (
    "TimelineAddClipButton",
    "TimelineSplitClipButton",
    "TimelineRemoveClipButton",
    "TimelineZoomSlider",
    "TimelineZoomOutButton",
    "TimelineZoomInButton",
    "TimelineFitButton",
    "TimelineZoomText",
    "EditorShortcutsButton",
):
    require(f'x:Name="{control}"' in xaml, f"TOOLBAR-01 the toolbar control '{control}' is missing")
    require(f'AutomationProperties.AutomationId="{control}"' in xaml,
            f"TOOLBAR-01 the toolbar control '{control}' must keep an AutomationId for UIA")

clip_group = window(xaml, 'x:Name="TimelineClipToolsGroup"', 2400)
require("TimelineAddClipButton" in clip_group and "TimelineSplitClipButton" in clip_group
        and "TimelineRemoveClipButton" in clip_group,
        "TOOLBAR-01 the clip editing group must own the add, split and remove buttons")
zoom_group = window(xaml, 'x:Name="TimelineZoomToolsGroup"', 2400)
require("TimelineZoomSlider" in zoom_group and "TimelineZoomOutButton" in zoom_group
        and "TimelineZoomInButton" in zoom_group and "TimelineFitButton" in zoom_group,
        "TOOLBAR-01 the zoom group must own the slider, the zoom buttons and the fit button")
require('x:Name="EditorShortcutsButton"' in xaml,
        "TOOLBAR-01 the help button must stay available in the toolbar")
require(xaml.count('x:Name="EditorShortcutsButton"') == 1,
        "TOOLBAR-01 the help button must exist exactly once")
help_button = window(xaml, 'x:Name="EditorShortcutsButton"', 900)
require('HorizontalAlignment="Left"' in help_button,
        "TOOLBAR-01 the help button must not stretch across the whole toolbar row")

# CONTRACT-01: the executable contract must lock in the history and the clip
# drag, and this script must be registered in verify.ps1.
tests = (TESTS / "Program.cs").read_text(encoding="utf-8")
require("editor timeline history undoes and redoes clip edits and drops the pending redo" in tests,
        "CONTRACT-01 the executable history contract test is missing")
require("editor timeline clip body drag reorders clips and marks the insertion slot" in tests,
        "CONTRACT-01 the executable clip move contract test is missing")
history_contract = (TESTS / "EditorTimelineHistoryContract.cs").read_text(encoding="utf-8")
for token in (
    "EditorTimelineHistory()",
    "history.Push(",
    "history.Undo()",
    "history.Redo()",
    "a new action must drop the pending redo",
):
    require(token in history_contract, f"CONTRACT-01 the history contract test lost: {token}")
move_contract = (TESTS / "EditorTimelineClipMoveContract.cs").read_text(encoding="utf-8")
for token in (
    "EditorTimelineClipMove.InsertionIndex(",
    "EditorTimelineClipMove.InsertionBoundaryTime(",
    "EditorTimelineClipMove.Move(",
):
    require(token in move_contract, f"CONTRACT-01 the clip move contract test lost: {token}")

verify = (ROOT / "csharp/scripts/verify.ps1").read_text(encoding="utf-8")
require('Invoke-Checked python @("csharp/scripts/verify_editor_timeline_history_move_contract.py")' in verify,
        "CONTRACT-01 the history/move contract script must be registered in verify.ps1")

print("PASS: HISTORY-01/02/03/04 the real timeline undo/redo, MOVE-01/02/03 the clip body drag "
      "with the insertion guide and TOOLBAR-01 the grouped timeline toolbar are locked")
