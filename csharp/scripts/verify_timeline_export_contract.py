#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
CORE = ROOT / "csharp/src/BiliSubStudio.Core/Timeline"
SERVICE = CORE / "TimelineExportService.cs"
PLAN = CORE / "TimelineRenderPlan.cs"
TEST = ROOT / "csharp/tests/BiliSubStudio.Core.ContractTests/TimelineExportContract.cs"
PROGRAM = ROOT / "csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs"
VERIFY = ROOT / "csharp/scripts/verify.ps1"
EDITOR_STORE = ROOT / "csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs"


def require(condition: bool, message: str) -> None:
    if not condition:
        print("FAIL:", message, file=sys.stderr)
        raise SystemExit(1)


service = SERVICE.read_text(encoding="utf-8")
plan = PLAN.read_text(encoding="utf-8")
test = TEST.read_text(encoding="utf-8")
program = PROGRAM.read_text(encoding="utf-8")
verify = VERIFY.read_text(encoding="utf-8")
editor_store = EDITOR_STORE.read_text(encoding="utf-8")

for token in (
    "OutputWidth",
    "OutputHeight",
    "OutputFrameRate",
    "ValidateOutputGeometry",
):
    require(token in plan, f"G10 render-plan output token missing: {token}")

for token in (
    "ITimelineExportSegmentRenderer",
    "TimelineRenderSegment",
    "TimelineExportConcatList",
    "TimelineConcatListArtifact",
    "TimelineExportResultV2",
    "CleanupAwareCancel",
    "ValidateOutputGeometry",
    "ManagedSegmentPrefix",
    '".rendering.mp4"',
    "File.Move(renderingPath, segmentFinalPath)",
    "new OwnedProcessGroup()",
    "processes.StopAsync()",
    "CleanupJobArtifactsAsync",
    "job.Finish(",
    "job.Set(",
    "file '",
):
    require(token in service, f"G10 segment/concat/job token missing: {token}")

for body, name in ((service, "export service"), (plan, "render plan")):
    for token in ("EditorPage", "EditorProjectStore", "VideoEditorService",
                  "TimelineOneClipParity", "ToolManager", "EnsureFfmpegAsync"):
        require(token not in body, f"G10 {name} coupled to classic owner: {token}")
require("Timeline" not in editor_store,
        "classic schema-7 store must not reference Timeline")

for token in (
    "RenderPlanGeometryAndMixedFormatAsync",
    "ConcatListMatchesCompilerOrderAsync",
    "SegmentServiceDrivesCleanupAwareAppJobAsync",
    "first clip and keep explicit FPS",
    "concat demuxer",
    "out of compiler order",
    "CleanupAwareCancel=true",
    "terminal cancelled state",
    "owned segment artifacts",
):
    require(token in test, f"G10 runtime contract missing: {token}")
require("TimelineExportContract.RunAsync" in program,
        "G10 runtime contract is not registered")
require("verify_timeline_export_contract.py" in verify,
        "official Windows verification does not run the G10 static gate")

print("PASS: G10 segmented export keeps plan geometry, concat order and cleanup-aware jobs")
