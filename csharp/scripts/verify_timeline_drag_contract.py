#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
CORE = ROOT / "csharp/src/BiliSubStudio.Core/Timeline"
MATH = CORE / "TimelineDragMath.cs"
CONTROLLER = CORE / "TimelineEditingController.cs"
PAGE_CS = ROOT / "csharp/src/BiliSubStudio.App/Pages/TimelineEditorPage.xaml.cs"
PAGE_XAML = ROOT / "csharp/src/BiliSubStudio.App/Pages/TimelineEditorPage.xaml"
TEST = ROOT / "csharp/tests/BiliSubStudio.Core.ContractTests/TimelineDragContract.cs"
PROGRAM = ROOT / "csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs"
VERIFY = ROOT / "csharp/scripts/verify.ps1"
DECISIONS = ROOT / "docs/editor-timeline/DECISIONS.md"
EDITOR_STORE = ROOT / "csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs"


def require(condition: bool, message: str) -> None:
    if not condition:
        print("FAIL:", message, file=sys.stderr)
        raise SystemExit(1)


math_source = MATH.read_text(encoding="utf-8")
controller = CONTROLLER.read_text(encoding="utf-8")
page_cs = PAGE_CS.read_text(encoding="utf-8")
page_xaml = PAGE_XAML.read_text(encoding="utf-8")
test = TEST.read_text(encoding="utf-8")
program = PROGRAM.read_text(encoding="utf-8")
verify = VERIFY.read_text(encoding="utf-8")
decisions = DECISIONS.read_text(encoding="utf-8")
editor_store = EDITOR_STORE.read_text(encoding="utf-8")

for token in (
    "TimelineDragKind",
    "TimelineTrimProposal",
    "ReorderPosition",
    "TrimProposal",
    "TimelineTime.SnapToFrameGrid",
    "TimelineTime.SequenceToSourceUs",
    "clip.ValidateAgainst(asset)",
):
    require(token in math_source, f"G17 headless pointer math token missing: {token}")

for token in (
    "_history = new TimelineAudioEditHistory()",
    "discardUnsavedChanges",
    "openProjectId = _project?.Id",
    "_savedState = state",
    "public long Revision",
    "UpdateDirty()",
    "clip.SourceInUs == newSourceInUs",
    "currentPosition == newPosition",
    "var project = ApplyToProject(_project!, after)",
):
    require(token in controller, f"G17 controller hardening token missing: {token}")
require(controller.index("var project = ApplyToProject(_project!, after)")
        < controller.index("_history.Commit(before, after)"),
        "G17 controller commits history before the rebuilt project validates")

for token in (
    "TimelineSurface_PointerPressed",
    "TimelineSurface_PointerMoved",
    "TimelineSurface_PointerReleased",
    "TimelineSurface_PointerCanceled",
    "TimelineSurface_PointerCaptureLost",
    "CapturePointer",
    "ReleasePointerCapture",
    "FinishTimelineDrag",
    "RenderTimelineDragProposal",
    "TimelineDragThreshold",
    "gesture.Revision",
    "ConfirmProjectSwitchAsync",
    "_openingProject",
    "CancelActiveTimelineDrag",
    "AutoScrollTimeline",
    "PointerDeviceType.Touch",
):
    require(token in page_cs and token in page_xaml if token.startswith("TimelineSurface_Pointer") else token in page_cs,
            f"G17 pointer surface token missing: {token}")

moved = page_cs.split("private void TimelineSurface_PointerMoved", 1)[1].split(
    "private void TimelineSurface_PointerReleased", 1)[0]
for token in ("MoveSelected(", "TrimSelected(", "SaveAsync(",
              "TimelineSequenceOps", "TimelineAudioDependencyOps"):
    require(token not in moved, f"G17 PointerMoved must stay proposal-only: {token}")
require("FinishTimelineDrag(e, commit: false)" in page_cs,
        "G17 cancel/capture-lost path does not explicitly reject commit")
finish = page_cs.split("private void FinishTimelineDrag", 1)[1].split(
    "private (TimelineClip? Clip", 1)[0]
require("_editor.MoveSelected" in finish and "_editor.TrimSelected" in finish,
        "G17 release path does not commit through TimelineEditingController")
require("_editor.SaveAsync" not in finish,
        "G17 drag release must stay dirty until explicit save")
for token in ("TimelineSequenceOps", "TimelineAudioDependencyOps",
              "EditorProjectStore.", "VideoEditorService."):
    require(token not in page_cs, f"G17 page bypasses its controller boundary: {token}")
require("Timeline" not in editor_store,
        "classic schema-7 store must not reference Timeline")

for token in (
    "ReorderTargetsBothDirections",
    "TrimSnapsAtOneAndTwoTimes",
    "TrimClampsToOneFrame",
    "FractionalRatesKeepValidBounds",
    "CommitProposalCreatesOneHistoryEntryAsync",
):
    require(token in test, f"G17 runtime contract missing: {token}")
require("TimelineDragContract.RunAsync" in program,
        "G17 runtime contract is not registered")
require("verify_timeline_drag_contract.py" in verify,
        "official Windows verification does not run the G17 static gate")
require("DEC-027" in decisions,
        "G17 direct manipulation decision was not recorded")

print("PASS: G17 pointer drag previews proposals and commits one controller transaction on release")
