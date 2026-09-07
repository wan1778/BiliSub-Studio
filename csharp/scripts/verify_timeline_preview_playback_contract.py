#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
CORE = ROOT / "csharp/src/BiliSubStudio.Core/Timeline"
RENDERER = CORE / "TimelineFfmpegProcessedPreviewRenderer.cs"
PLAYBACK = ROOT / "csharp/src/BiliSubStudio.App/Pages/TimelineEditorPage.Playback.cs"
PAGE_CS = ROOT / "csharp/src/BiliSubStudio.App/Pages/TimelineEditorPage.xaml.cs"
PAGE_XAML = ROOT / "csharp/src/BiliSubStudio.App/Pages/TimelineEditorPage.xaml"
MAIN = ROOT / "csharp/src/BiliSubStudio.App/MainWindow.xaml.cs"
TEST = ROOT / "csharp/tests/BiliSubStudio.Core.ContractTests/TimelinePreviewPlaybackContract.cs"
PROGRAM = ROOT / "csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs"
VERIFY = ROOT / "csharp/scripts/verify.ps1"
DECISIONS = ROOT / "docs/editor-timeline/DECISIONS.md"
EDITOR_STORE = ROOT / "csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs"


def require(condition: bool, message: str) -> None:
    if not condition:
        print("FAIL:", message, file=sys.stderr)
        raise SystemExit(1)


renderer = RENDERER.read_text(encoding="utf-8")
playback = PLAYBACK.read_text(encoding="utf-8")
page_cs = PAGE_CS.read_text(encoding="utf-8")
page_xaml = PAGE_XAML.read_text(encoding="utf-8")
main = MAIN.read_text(encoding="utf-8")
test = TEST.read_text(encoding="utf-8")
program = PROGRAM.read_text(encoding="utf-8")
verify = VERIFY.read_text(encoding="utf-8")
decisions = DECISIONS.read_text(encoding="utf-8")
editor_store = EDITOR_STORE.read_text(encoding="utf-8")

for token in (
    "ITimelineProcessedPreviewRenderer",
    "TimelineExportService",
    "_export.ExportAsync",
    "request.SequenceStartUs != 0",
    "request.DurationUs != request.Plan.TotalDurationUs",
    'new AppJob(',
    '"timeline-preview"',
    "cleanupAwareCancel: true",
):
    require(token in renderer, f"G18 shared render delegation token missing: {token}")
for token in ("TimelineFfmpegArguments", "ProcessRunner", "BuildSegment(", "BuildConcat("):
    require(token not in renderer, f"G18 renderer duplicated the export graph: {token}")

for token in (
    "TimelinePreviewController",
    "TimelineProcessedPreviewService",
    "_editor.Project",
    "_editor.Revision",
    "TimelineCompiler.Compile(project)",
    "controller.RequestAsync",
    "StorageFile.GetFileFromPathAsync",
    "MediaSource.CreateFromStorageFile",
    "TimelinePreviewPlayer.SetMediaPlayer(player)",
    "EnsureTimelinePreviewRequestCurrent",
    "StopTimelinePreviewAsync",
    "TimelineEditorPage_Unloaded",
    "FlushForAppCloseAsync",
):
    require(token in playback, f"G18 playback lifecycle token missing: {token}")
detach = playback.split("private void DetachTimelinePreviewPlayer", 1)[1].split(
    "private void TimelinePreviewPlayer_PositionChanged", 1)[0]
for token in ("player.Pause()", "player.Source = null",
              "TimelinePreviewPlayer.SetMediaPlayer(null)", "player.Dispose()"):
    require(token in detach, f"G18 player detach token missing: {token}")
require(detach.index("player.Source = null") < detach.index("TimelinePreviewPlayer.SetMediaPlayer(null)")
        < detach.index("player.Dispose()"),
        "G18 player must release its source and element before disposal")
stop = playback.split("private async Task StopTimelinePreviewAsync", 1)[1].split(
    "private void DetachTimelinePreviewPlayer", 1)[0]
require(stop.index("DetachTimelinePreviewPlayer()") < stop.index("controller.DisposeAsync()"),
        "G18 proxy may be deleted before the player releases its file handle")
require("TimelinePreviewLocked" in page_cs
        and "await StopTimelinePreviewAsync(updateStatus: false)" in page_cs,
        "G18 edit/project-switch lock is not wired")
require("timelinePage.FlushForAppCloseAsync" in main,
        "G18 app close does not await Timeline player cleanup")

for token in (
    "TimelinePreviewPlayer",
    "TimelinePreviewPlayButton",
    "TimelinePreviewPauseButton",
    "TimelinePreviewStopButton",
    'AreTransportControlsEnabled="False"',
):
    require(token in page_xaml, f"G18 player control missing: {token}")
for body, name in ((renderer, "renderer"), (playback, "playback")):
    for token in ("new EditorPage", "EditorPage.", ": EditorPage", "EditorProjectStore.",
                  "TimelineOneClipParity", "VideoEditorService"):
        require(token not in body, f"G18 {name} coupled to classic owner: {token}")
require("Timeline" not in editor_store,
        "classic schema-7 store must not reference Timeline")

for token in ("RendererDelegatesWholeSequenceAsync", "RendererDelegatesFinalMixAsync",
              "UnsavedControllerSnapshotDrivesPlanAsync"):
    require(token in test, f"G18 runtime contract missing: {token}")
require("TimelinePreviewPlaybackContract.RunAsync" in program,
        "G18 runtime contract is not registered")
require("verify_timeline_preview_playback_contract.py" in verify,
        "official Windows verification does not run the G18 static gate")
require("DEC-028" in decisions,
        "G18 preview playback decision was not recorded")
for token in ("AbortAppClose", "timelinePage.AbortAppClose",
              "_timelinePreviewStopping", "_timelinePreviewStartTask",
              "controller.CancelAsync()", "controller.DisposeAsync()"):
    found = token in playback or (token == "timelinePage.AbortAppClose" and token in main)
    require(found, f"G18 lifecycle ownership token missing: {token}")

print("PASS: G18 processed playback uses the unsaved V2 plan, shared export graph and owned teardown")
