#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
PAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages"
PAGE_CS = PAGES / "TimelineEditorPage.xaml.cs"
PAGE_XAML = PAGES / "TimelineEditorPage.xaml"
MAIN_CS = ROOT / "csharp/src/BiliSubStudio.App/MainWindow.xaml.cs"
MAIN_XAML = ROOT / "csharp/src/BiliSubStudio.App/MainWindow.xaml"
TEST = ROOT / "csharp/tests/BiliSubStudio.Core.ContractTests/TimelineMergeContract.cs"
PROGRAM = ROOT / "csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs"
VERIFY = ROOT / "csharp/scripts/verify.ps1"
DECISIONS = ROOT / "docs/editor-timeline/DECISIONS.md"
EDITOR_STORE = ROOT / "csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs"


def require(condition: bool, message: str) -> None:
    if not condition:
        print("FAIL:", message, file=sys.stderr)
        raise SystemExit(1)


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


page_cs = read(PAGE_CS)
page_xaml = read(PAGE_XAML)
main_cs = read(MAIN_CS)
main_xaml = read(MAIN_XAML)
test = read(TEST)
program = read(PROGRAM)
verify = read(VERIFY)
decisions = read(DECISIONS)
editor_store = read(EDITOR_STORE)

# Permanent navigation entry with classic order and default untouched.
require('Tag="timelinev2"' in main_xaml and 'Content="Timeline"' in main_xaml,
        "G14 navigation must carry a permanent Timeline entry")
for tag in ('Tag="video"', 'Tag="ocr"', 'Tag="editor"', 'Tag="settings"'):
    require(tag in main_xaml, f"G14 must keep classic navigation item: {tag}")
require('Navigation.SelectedItem = Navigation.MenuItems[0];' in main_cs
        and 'ContentFrame.Content = _pages["video"];' in main_cs,
        "G14 must keep video as the default navigation selection")
require("IsSuppressed" in main_cs and "timelinev2" in main_cs,
        "G14 kill-switch wiring missing from MainWindow")

# Real export path on the promoted page, classic state still untouched.
for token in (
    "ExportProject_Click",
    "ExportStatusText",
    "IFolderPickerService",
    "PickFolderAsync",
    "EnsureFfmpegAsync",
    "EnsureFfprobeAsync",
    'Jobs.Create("timeline-export"',
    "cleanupAwareCancel: true",
    "FileNamePolicy.Sanitize",
    "service.ExportAsync",
    "CancelComplete",
    "TimelineFfmpegFinalMixer",
):
    require(token in page_cs, f"G14 page export token missing: {token}")
for token in ("new EditorPage", "EditorPage.", ": EditorPage", "new EditorProjectStore",
              "EditorProjectStore.", "new VideoEditorService", "VideoEditorService.",
              "Paths.Data", "_editorProjects"):
    require(token not in page_cs, f"G14 page must not touch classic state: {token}")

# The classic Editor surface must not couple back to Timeline V2. EditorPage
# files already use a seek Slider named Timeline, so pin the V2-only tokens.
for name in sorted((PAGES).glob("EditorPage*")):
    body = name.read_text(encoding="utf-8")
    for token in ("Core.Timeline", "TimelineEditorPage", "timelinev2", "TimelineV2"):
        require(token not in body, f"G14 classic Editor coupled to V2: {name.name}:{token}")
require("Timeline" not in editor_store,
        "classic schema-7 store must not reference Timeline")

for token in (
    "KillSwitchMatrix",
    "ExportWiringPins",
    "IsEntryVisible",
    "IsSuppressed",
    "timeline-export",
):
    require(token in test, f"G14 runtime contract missing: {token}")
require("TimelineMergeContract.RunAsync" in program,
        "G14 runtime contract is not registered")
require("verify_timeline_merge_contract.py" in verify,
        "official Windows verification does not run the G14 static gate")
require("DEC-024" in decisions,
        "G14 promotion decision was not recorded")

print("PASS: G14 promotion keeps classic navigation default with kill-switch and pinned export wiring")
