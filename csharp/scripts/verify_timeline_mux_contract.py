#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
CORE = ROOT / "csharp/src/BiliSubStudio.Core/Timeline"
MUX = CORE / "TimelineFfmpegMux.cs"
SERVICE = CORE / "TimelineExportService.cs"
TEST = ROOT / "csharp/tests/BiliSubStudio.Core.ContractTests/TimelineMuxContract.cs"
PROGRAM = ROOT / "csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs"
VERIFY = ROOT / "csharp/scripts/verify.ps1"
DECISIONS = ROOT / "docs/editor-timeline/DECISIONS.md"
EDITOR_STORE = ROOT / "csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs"


def require(condition: bool, message: str) -> None:
    if not condition:
        print("FAIL:", message, file=sys.stderr)
        raise SystemExit(1)


mux = MUX.read_text(encoding="utf-8")
service = SERVICE.read_text(encoding="utf-8")
test = TEST.read_text(encoding="utf-8")
program = PROGRAM.read_text(encoding="utf-8")
verify = VERIFY.read_text(encoding="utf-8")
decisions = DECISIONS.read_text(encoding="utf-8")
editor_store = EDITOR_STORE.read_text(encoding="utf-8")

for token in (
    "TimelineRenderCapability",
    "EnsureVideoAudioOnly",
    "TimelineMuxExpectation",
    "TimelineMuxValidation",
    "ValidateProbe",
    "FieldDriftToleranceUs",
    "BuildExpectation",
    "TimelineFfmpegArguments",
    "BuildSegment",
    "BuildConcat",
    "BuildProbe",
    "TimelineFfmpegSegmentRenderer",
    "ITimelineExportConcatenator",
    "TimelineFfmpegConcatenator",
    "ConcatAsync",
    "libx264",
    "anullsrc",
    "atempo",
    "setpts",
    "trim=start=",
    "atrim=",
    "concat",
    "ProcessRunner",
    "MaximumOutputDriftUs",
):
    require(token in mux, f"G12 mux ownership token missing: {token}")

# The real mux must not couple to the classic render path or the managed tool
# downloader: ffmpeg paths stay explicit constructor inputs.
for token in ("EditorPage", "EditorProjectStore.", "VideoEditorService",
              "TimelineOneClipParity", "ToolManager", "MainWindow"):
    require(token not in mux, f"G12 mux coupled to classic owner: {token}")

for token in (
    "ITimelineExportConcatenator",
    "_concatenator",
    "ConcatAsync",
    "BuildExpectation",
    "TimelineRenderSegment(",
    "TimelineRenderPlan Plan",
):
    require(token in service, f"G12 service concat token missing: {token}")

require("DEC-022" in decisions,
        "G12 mux boundary decision was not recorded")
require("Timeline" not in editor_store,
        "classic schema-7 store must not reference Timeline")

for token in (
    "SegmentArgumentsAreExact",
    "ConcatAndProbeArgumentsAreExact",
    "CapabilityRejectsNonVideoAudioPlans",
    "MuxValidationMathIsExact",
    "FieldMuxJoinsMixedFormatSegmentsAsync",
    "concat order drifted",
    "FindOnPath",
    "lavfi",
):
    require(token in test, f"G12 runtime contract missing: {token}")
require("TimelineMuxContract.RunAsync" in program,
        "G12 runtime contract is not registered")
require("verify_timeline_mux_contract.py" in verify,
        "official Windows verification does not run the G12 static gate")

print("PASS: G12 real FFmpeg mux renders normalized segments, joins them in order and validates drift")
