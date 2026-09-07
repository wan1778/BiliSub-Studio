#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
CORE = ROOT / "csharp/src/BiliSubStudio.Core/Timeline"
RELINK = CORE / "TimelineMediaRelink.cs"
CONTROLLER = CORE / "TimelineEditingController.cs"
STORE = CORE / "TimelineProjectStoreV2.cs"
PAGE_CS = ROOT / "csharp/src/BiliSubStudio.App/Pages/TimelineEditorPage.xaml.cs"
PAGE_XAML = ROOT / "csharp/src/BiliSubStudio.App/Pages/TimelineEditorPage.xaml"
MAIN = ROOT / "csharp/src/BiliSubStudio.App/MainWindow.xaml.cs"
TEST = ROOT / "csharp/tests/BiliSubStudio.Core.ContractTests/TimelineResilienceContract.cs"
PROGRAM = ROOT / "csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs"
VERIFY = ROOT / "csharp/scripts/verify.ps1"
DECISIONS = ROOT / "docs/editor-timeline/DECISIONS.md"
EDITOR_STORE = ROOT / "csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs"


def require(condition: bool, message: str) -> None:
    if not condition:
        print("FAIL:", message, file=sys.stderr)
        raise SystemExit(1)


relink = RELINK.read_text(encoding="utf-8")
controller = CONTROLLER.read_text(encoding="utf-8")
store = STORE.read_text(encoding="utf-8")
page_cs = PAGE_CS.read_text(encoding="utf-8")
page_xaml = PAGE_XAML.read_text(encoding="utf-8")
main = MAIN.read_text(encoding="utf-8")
test = TEST.read_text(encoding="utf-8")
program = PROGRAM.read_text(encoding="utf-8")
verify = VERIFY.read_text(encoding="utf-8")
decisions = DECISIONS.read_text(encoding="utf-8")
editor_store = EDITOR_STORE.read_text(encoding="utf-8")

# Pure relink helper: identity stays, fingerprint swaps, wrong files rejected.
for token in (
    "FindMissingMediaAssets",
    "RelinkMediaAsset",
    "DurationToleranceSeconds",
    "File được chọn không khớp nguồn gốc",
    "relinked.Validate()",
    "updated.Validate()",
):
    require(token in relink, f"G19 relink token missing: {token}")
for token in ("EditorPage", "EditorProjectStore.", "MediaPreviewService"):
    require(token not in relink, f"G19 relink must stay pure: {token}")
require("new TimelineMediaAsset(" not in relink,
        "G19 relink must keep the original asset record identity")

for token in (
    "RelinkMediaAssetAsync",
    "FindMissingMediaAssets()",
    "Project Timeline có thay đổi chưa lưu; hãy lưu hoặc bỏ thay đổi trước khi nối lại nguồn",
    "_revision != revision",
):
    require(token in controller, f"G19 controller relink token missing: {token}")

# Store crash recovery: abandoned atomic-write temporaries reaped by ListAsync.
require("CleanupAbandonedTemporaries" in store and '"*.json.tmp-*"' in store,
        "G19 store does not reap abandoned atomic-write temporaries")
require(store.index("CleanupAbandonedTemporaries();") < store.index("if (!Directory.Exists(_root)) return [];"),
        "G19 temporary reap must run inside ListAsync before enumeration")

for token in (
    "_timelineAutosaveTimer",
    "QueueTimelineAutosave",
    "TimelineAutosave_Tick",
    "_timelineAutosavePending",
    "UpdateTimelineAutosaveText",
    "RelinkMedia_Click",
    "UpdateRelinkMediaButton",
    "ProbeHasAudioAsync",
    "EditorSourceSelection.NormalizeCandidatePath",
    "IFilePickerService",
):
    require(token in page_cs, f"G19 page wiring token missing: {token}")
require("Interval = TimeSpan.FromSeconds(2)" in page_cs,
        "G19 autosave debounce interval drifted from the classic 2s policy")
# Autosave must never fire while preview locks edits or the app is closing.
tick = page_cs.split("private async void TimelineAutosave_Tick", 1)[1].split("private void UpdateTimelineAutosaveText", 1)[0]
for token in ("TimelinePreviewLocked", "_timelineClosing", "_savingProject", "_openingProject"):
    require(token in tick, f"G19 autosave tick must gate on {token}")
for token in ("RelinkMediaButton", "TimelineAutosaveText"):
    require(token in page_xaml, f"G19 control missing from XAML: {token}")
require("new TimelineEditorPage(_application, folderPicker, filePicker)" in main,
        "G19 file picker is not wired into the Timeline page constructor")

for token in (
    "MissingAssetDetectionAndRelinkAsync",
    "RelinkRejectsWrongFileAsync",
    "RelinkRejectedWhileDirtyAsync",
    "AbandonedTemporariesAreReapedAsync",
    "relink changed project/asset/clip identity",
):
    require(token in test, f"G19 runtime contract missing: {token}")
require("TimelineResilienceContract.RunAsync" in program,
        "G19 runtime contract is not registered")
require("verify_timeline_resilience_contract.py" in verify,
        "official Windows verification does not run the G19 static gate")
require("DEC-033" in decisions,
        "G19 resilience decision was not recorded")
require("Timeline" not in editor_store,
        "classic schema-7 store must not reference Timeline")

print("PASS: G19 relink restores missing media and crash-safe autosave owns atomic writes")
