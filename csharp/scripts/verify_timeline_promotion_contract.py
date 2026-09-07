#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
CORE = ROOT / "csharp/src/BiliSubStudio.Core/Timeline"
SERVICE = CORE / "TimelinePromotionService.cs"
PAGE_CS = ROOT / "csharp/src/BiliSubStudio.App/Pages/TimelineEditorPage.xaml.cs"
PAGE_XAML = ROOT / "csharp/src/BiliSubStudio.App/Pages/TimelineEditorPage.xaml"
MEDIA = ROOT / "csharp/src/BiliSubStudio.Core/Media/MediaPreviewService.cs"
APPLICATION = ROOT / "csharp/src/BiliSubStudio.Core/Application/BiliSubApplication.cs"
TEST = ROOT / "csharp/tests/BiliSubStudio.Core.ContractTests/TimelinePromotionContract.cs"
PROGRAM = ROOT / "csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs"
VERIFY = ROOT / "csharp/scripts/verify.ps1"
MAIN_XAML = ROOT / "csharp/src/BiliSubStudio.App/MainWindow.xaml"
DECISIONS = ROOT / "docs/editor-timeline/DECISIONS.md"
EDITOR_STORE = ROOT / "csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs"


def require(condition: bool, message: str) -> None:
    if not condition:
        print("FAIL:", message, file=sys.stderr)
        raise SystemExit(1)


service = SERVICE.read_text(encoding="utf-8")
page_cs = PAGE_CS.read_text(encoding="utf-8")
page_xaml = PAGE_XAML.read_text(encoding="utf-8")
media = MEDIA.read_text(encoding="utf-8")
application = APPLICATION.read_text(encoding="utf-8")
test = TEST.read_text(encoding="utf-8")
program = PROGRAM.read_text(encoding="utf-8")
verify = VERIFY.read_text(encoding="utf-8")
main_xaml = MAIN_XAML.read_text(encoding="utf-8")
decisions = DECISIONS.read_text(encoding="utf-8")
editor_store = EDITOR_STORE.read_text(encoding="utf-8")

for token in (
    "TimelinePromotionService",
    "TimelinePromotionRequest",
    "TimelinePromotionReport",
    "PromoteAsync(",
    "BackupTag",
    "BackupManifestSha256",
    "MigrateSchema7(",
    "SaveAsync(",
    "SHA256",
    "SourceJsonSha256Before",
    "MediaSha256After",
    "TimelineMigrationWarning",
    "relink project classic",
):
    require(token in service, f"G11 promotion ownership token missing: {token}")

# The promotion service may hash-verify sources but must never write outside
# the V2 store and must never touch the classic render path.
for token in ("File.WriteAll", "File.Move", "File.Delete", "Directory.Create",
              "EditorProjectStore.", "VideoEditorService", "EditorPage", "MainWindow",
              "ffmpeg", "ProcessRunner"):
    require(token not in service, f"G11 promotion service must stay read-only and isolated: {token}")

# Shared additive probes: audio presence + schema-7 path facade.
require("ProbeHasAudioAsync" in media and "select_streams" in media and "a:0" in media,
        "G11 probed audio presence missing from the shared media service")
require("GetEditorProjectPath" in application,
        "G11 schema-7 path facade missing from the application service")

# Copy entry lives only in the flag-gated V2 dev page.
for token in (
    "CopyClassicProject_Click",
    "ClassicProjectCombo",
    "BackupTagBox",
    "BackupManifestBox",
    "ListEditorProjectsAsync",
    "LoadEditorProjectByIdAsync",
    "GetEditorProjectPath",
    "ProbeHasAudioAsync",
    "TimelinePromotionService",
):
    require(token in page_cs, f"G11 page copy-entry token missing: {token}")
require("CopyClassicProject" in page_xaml,
        "G11 copy entry missing from the V2 dev page XAML")
for token in ("new EditorPage", "EditorPage.", ": EditorPage", "new EditorProjectStore",
              "EditorProjectStore.", "new VideoEditorService", "VideoEditorService.",
              "Paths.Data", "_editorProjects"):
    require(token not in page_cs, f"G11 page must not touch classic state: {token}")

# G14 (DEC-024, user-approved) promotes the Timeline entry into the normal
# navigation. The kill-switch, classic order/default and fallback proofs now
# live in the G14 merge verifier; G11 keeps pinning the dev label stays out.
require('Tag="timelinev2"' in main_xaml and "Timeline V2 (dev)" not in main_xaml,
        "G11 navigation expectation drifted from the approved merge")
for tag in ('Tag="video"', 'Tag="ocr"', 'Tag="editor"', 'Tag="settings"'):
    require(tag in main_xaml, f"G11 must keep classic navigation item: {tag}")
require("DEC-021" in decisions and "PDEC-006" in decisions,
        "G11 fallback-duration decision was not recorded")
require("Timeline" not in editor_store,
        "classic schema-7 store must not reference Timeline")

for token in (
    "RealProjectCopyKeepsSourceUntouchedAsync",
    "BackupGateRejectsUnguardedPromotionAsync",
    "InvalidSourceRejectedAsync",
    "RegionsFlattened",
    "SubtitleDropped",
    "VoiceDropped",
    "without a backup tag",
    "relink guidance",
):
    require(token in test, f"G11 runtime contract missing: {token}")
require("TimelinePromotionContract.RunAsync" in program,
        "G11 runtime contract is not registered")
require("verify_timeline_promotion_contract.py" in verify,
        "official Windows verification does not run the G11 static gate")

print("PASS: G11 promotion copies real schema-7 projects behind a backup gate with source preservation and classic fallback")
