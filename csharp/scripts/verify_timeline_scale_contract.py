#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
CORE = ROOT / "csharp/src/BiliSubStudio.Core/Timeline"
VIEWPORT = CORE / "TimelineViewport.cs"
PAGE = ROOT / "csharp/src/BiliSubStudio.App/Pages/TimelineEditorPage.xaml.cs"
XAML = ROOT / "csharp/src/BiliSubStudio.App/Pages/TimelineEditorPage.xaml"
PLAYBACK = ROOT / "csharp/src/BiliSubStudio.App/Pages/TimelineEditorPage.Playback.cs"
SERVICE = CORE / "TimelineProcessedPreviewService.cs"
ITEMS = CORE / "TimelineItems.cs"
PREVIEW_TEST = ROOT / "csharp/tests/BiliSubStudio.Core.ContractTests/TimelineProcessedPreviewContract.cs"
SCALE_TEST = ROOT / "csharp/tests/BiliSubStudio.Core.ContractTests/TimelineScaleContract.cs"
PROGRAM = ROOT / "csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs"
VERIFY = ROOT / "csharp/scripts/verify.ps1"
DECISIONS = ROOT / "docs/editor-timeline/DECISIONS.md"
EDITOR_STORE = ROOT / "csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs"


def require(condition: bool, message: str) -> None:
    if not condition:
        print("FAIL:", message, file=sys.stderr)
        raise SystemExit(1)


viewport = VIEWPORT.read_text(encoding="utf-8")
page = PAGE.read_text(encoding="utf-8")
xaml = XAML.read_text(encoding="utf-8")
playback = PLAYBACK.read_text(encoding="utf-8")
service = SERVICE.read_text(encoding="utf-8")
items = ITEMS.read_text(encoding="utf-8")
preview_test = PREVIEW_TEST.read_text(encoding="utf-8")
scale_test = SCALE_TEST.read_text(encoding="utf-8")
program = PROGRAM.read_text(encoding="utf-8")
verify = VERIFY.read_text(encoding="utf-8")
decisions = DECISIONS.read_text(encoding="utf-8")
editor_store = EDITOR_STORE.read_text(encoding="utf-8")

# The four documented scale targets must execute real validation/compiler/ops.
for token in (
    "HundredClipsCompileAndOps",
    "SixHourSequenceIsExact",
    "TenThousandSubtitleCuesValidateAndCompile",
    "TwoThousandEffectItemsRideOps",
    "new List<TimelineClip>(100)",
    "new List<TimelineSubtitleCue>(10_000)",
    "new List<TimelineItem>(2_000)",
    "6L * 3_600 * 1_000_000",
    "TimelineCompiler.Compile",
    "TimelineDependencyOps.Split",
    "TimelineDependencyOps.Trim",
    "LargeProjectRoundTripsThroughStoreAsync",
    "TimeSpan.FromSeconds(30)",
):
    require(token in scale_test, f"G20 runtime scale token missing: {token}")
require("TimelineScaleContract.RunAsync" in program,
        "G20 runtime scale contract is not registered")

# Scroll/zoom events only coalesce a low-priority redraw. The redraw uses a
# binary-searched visible range and must never compile, persist or block on IO.
for token in (
    "MinimumPixelsPerSecond",
    "MaximumPixelsPerSecond",
    "VisibleClipRange",
    "while (low < high)",
    "SequenceUs",
    "SurfaceWidth",
):
    require(token in viewport, f"G20 viewport token missing: {token}")
require(viewport.count("while (low < high)") == 2,
        "G20 viewport culling must retain two binary-search bounds")
for token in (
    "TimelineZoomSlider",
    "TimelineZoomText",
    'Minimum="1"',
    'Maximum="320"',
):
    require(token in xaml, f"G20 zoom control token missing: {token}")
for token in (
    "TimelineZoomSlider.ValueChanged += TimelineZoomSlider_ValueChanged",
    "TimelineScroller.ViewChanged += TimelineScroller_ViewChanged",
    "QueueTimelineViewportRender",
    "DispatcherQueuePriority.Low",
    "TimelineViewport.VisibleClipRange",
    "visible.StartIndex",
    "visible.EndExclusive",
    "_pendingTimelineZoomCenterUs",
    "TimelineSurface.SizeChanged += TimelineSurface_SizeChanged",
    "ApplyPendingTimelineZoomCenter",
):
    require(token in page, f"G20 viewport wiring token missing: {token}")
handlers = page.split("private void TimelineZoomSlider_ValueChanged", 1)[1].split(
    "private void AutoScrollTimeline", 1)[0]
for token in ("TimelineCompiler", "SaveAsync", ".Wait(", ".Result", "File.", "Directory."):
    require(token not in handlers, f"G20 scroll/zoom handler performs blocking work: {token}")
render = page.split("private void RenderTimeline()", 1)[1].split(
    "private void RenderTimelineDragProposal", 1)[0]
require("TimelineViewport.VisibleClipRange" in render,
        "G20 Timeline Canvas redraw is not viewport-culling clips")
require("clips.OrderBy(clip => clip.TimelineStartUs).ToArray()" in items,
        "G20 edit state does not canonicalize clip order before viewport binary search")

# Existing lifecycle contracts close the remaining section-13 targets.
autosave = page.split("private async void TimelineAutosave_Tick", 1)[1].split(
    "private void UpdateTimelineAutosaveText", 1)[0]
require("TimelinePreviewLocked" in autosave,
        "G20 autosave can run while preview playback owns the project")
play = playback.split("private async void TimelinePreviewPlay_Click", 1)[1].split(
    "private async Task GenerateAndPlayTimelinePreviewAsync", 1)[0]
require("_timelineAutosaveBusy" in play,
        "G20 preview can start while autosave already owns persistence")
controls = playback.split("private void RefreshTimelinePreviewControls", 1)[1].split(
    "internal async Task FlushForAppCloseAsync", 1)[0]
require("!_timelineAutosaveBusy" in controls,
        "G20 preview button stays enabled while autosave is active")
require("await service.CleanupCacheAsync(cancellationToken)" in playback,
        "G20 preview startup does not reap crash-leftover proxies")
require(playback.index("await service.CleanupCacheAsync(cancellationToken)")
        < playback.index("controller = new TimelinePreviewController(service)"),
        "G20 proxy cleanup must finish before a new preview owner starts")
for token in ("CleanupCacheAsync", "IsManagedFileName", "DeleteManagedFileAsync",
              "ProcessOwnerPrefix", "HasLiveOwnerOrRecentLegacy",
              "IsProcessOwnerAlive", "Task.Run"):
    require(token in service, f"G20 bounded proxy cleanup token missing: {token}")
cleanup = service.split("public async Task CleanupCacheAsync", 1)[1].split(
    "private static void ValidateRequest", 1)[0]
require(cleanup.count("cancellationToken.ThrowIfCancellationRequested()") >= 2,
        "G20 proxy discovery does not observe cancellation while enumerating")
require("File.Exists(live)" in preview_test and "!File.Exists(dead)" in preview_test,
        "G20 proxy cleanup does not prove live-owner preservation and dead-owner reap")
for token in ("CancellationReapsOwnedProcessTreeAsync", "ActiveCount == 0",
              "abandoned a nested child process"):
    require(token in preview_test, f"G20 child-process cleanup proof missing: {token}")

# Timeline V2 does not create thumbnail or waveform caches yet. Introducing one
# must fail this gate until its own bounded cleanup policy is added.
timeline_core = "\n".join(path.read_text(encoding="utf-8") for path in CORE.glob("*.cs"))
for token in ("ThumbnailCache", "WaveformCache"):
    require(token not in timeline_core,
            f"G20 found a Timeline cache without an approved bounded policy: {token}")
require("verify_timeline_scale_contract.py" in verify,
        "official Windows verification does not run the G20 static gate")
require("DEC-034" in decisions, "G20 performance decision was not recorded")
require("Timeline" not in editor_store,
        "classic schema-7 store must not reference Timeline")

print("PASS: G20 scale, viewport responsiveness and bounded cleanup targets are pinned")
