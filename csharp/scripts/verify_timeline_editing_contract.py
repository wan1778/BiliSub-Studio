#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
CORE = ROOT / "csharp/src/BiliSubStudio.Core/Timeline"
CONTROLLER = CORE / "TimelineEditingController.cs"
PAGE_CS = ROOT / "csharp/src/BiliSubStudio.App/Pages/TimelineEditorPage.xaml.cs"
PAGE_XAML = ROOT / "csharp/src/BiliSubStudio.App/Pages/TimelineEditorPage.xaml"
TEST = ROOT / "csharp/tests/BiliSubStudio.Core.ContractTests/TimelineEditingContract.cs"
PROGRAM = ROOT / "csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs"
VERIFY = ROOT / "csharp/scripts/verify.ps1"
DECISIONS = ROOT / "docs/editor-timeline/DECISIONS.md"
EDITOR_STORE = ROOT / "csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs"


def require(condition: bool, message: str) -> None:
    if not condition:
        print("FAIL:", message, file=sys.stderr)
        raise SystemExit(1)


controller = CONTROLLER.read_text(encoding="utf-8")
page_cs = PAGE_CS.read_text(encoding="utf-8")
page_xaml = PAGE_XAML.read_text(encoding="utf-8")
test = TEST.read_text(encoding="utf-8")
program = PROGRAM.read_text(encoding="utf-8")
verify = VERIFY.read_text(encoding="utf-8")
decisions = DECISIONS.read_text(encoding="utf-8")
editor_store = EDITOR_STORE.read_text(encoding="utf-8")

for token in (
    "TimelineEditingController",
    "SelectedClipId",
    "TrimSelected",
    "SplitSelected",
    "DeleteSelected",
    "MoveSelected",
    "Undo(",
    "Redo(",
    "IsDirty",
    "SaveAsync",
    "TimelineAudioEditHistory",
    "TimelineAudioDependencyOps",
    "ApplyToProject",
    "FixSelection",
):
    require(token in controller, f"G15 controller token missing: {token}")

# The controller persists only through the V2 store and performs no IO or
# classic coupling itself.
for token in ("EditorPage", "EditorProjectStore.", "VideoEditorService",
              "TimelineOneClipParity", "ToolManager", "MainWindow",
              "File.", "Directory."):
    require(token not in controller, f"G15 controller must stay isolated: {token}")

for token in (
    "ClipList",
    "TrimClip_Click",
    "SplitClip_Click",
    "DeleteClip_Click",
    "Undo_Click",
    "Redo_Click",
    "SaveProject_Click",
    "EditStatusText",
    "TimelineEditingController",
    "ProjectList_SelectionChanged",
    "ClipList_SelectionChanged",
):
    require(token in page_cs, f"G15 page edit token missing: {token}")
for token in (
    "ClipList",
    "EditStatusText",
    "DeleteClipButton",
    "UndoButton",
    "RedoButton",
    "SaveProjectButton",
):
    require(token in page_xaml, f"G15 clip editing control missing from the V2 page XAML: {token}")
for token in ("new EditorPage", "EditorPage.", ": EditorPage", "new EditorProjectStore",
              "EditorProjectStore.", "new VideoEditorService", "VideoEditorService.",
              "Paths.Data", "_editorProjects"):
    require(token not in page_cs, f"G15 page must not touch classic state: {token}")
require("Timeline" not in editor_store,
        "classic schema-7 store must not reference Timeline")

for token in (
    "OpenSelectEditFlowAsync",
    "FailedOpKeepsStateAsync",
    "UndoRedoRoundtripAsync",
    "SavePersistsAsync",
    "OpenValidationAsync",
):
    require(token in test, f"G15 runtime contract missing: {token}")
require("TimelineEditingContract.RunAsync" in program,
        "G15 runtime contract is not registered")
require("verify_timeline_editing_contract.py" in verify,
        "official Windows verification does not run the G15 static gate")
require("DEC-025" in decisions,
        "G15 editing scope decision was not recorded")

print("PASS: G15 editing controller keeps selection, atomic undo and dirty save with wired clip UI")
