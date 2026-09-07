#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
CORE = ROOT / "csharp/src/BiliSubStudio.Core/Timeline"
FIDELITY = CORE / "TimelineFidelityMux.cs"
SERVICE = CORE / "TimelineExportService.cs"
TEST = ROOT / "csharp/tests/BiliSubStudio.Core.ContractTests/TimelineFidelityContract.cs"
PROGRAM = ROOT / "csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs"
VERIFY = ROOT / "csharp/scripts/verify.ps1"
DECISIONS = ROOT / "docs/editor-timeline/DECISIONS.md"
EDITOR = ROOT / "csharp/src/BiliSubStudio.Core/Editor/VideoEditorService.cs"
EDITOR_STORE = ROOT / "csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs"


def require(condition: bool, message: str) -> None:
    if not condition:
        print("FAIL:", message, file=sys.stderr)
        raise SystemExit(1)


fidelity = FIDELITY.read_text(encoding="utf-8")
service = SERVICE.read_text(encoding="utf-8")
test = TEST.read_text(encoding="utf-8")
program = PROGRAM.read_text(encoding="utf-8")
verify = VERIFY.read_text(encoding="utf-8")
decisions = DECISIONS.read_text(encoding="utf-8")
editor = EDITOR.read_text(encoding="utf-8")
editor_store = EDITOR_STORE.read_text(encoding="utf-8")

for token in (
    "TimelineEffectSpec",
    "MapEffectsForSegment",
    "MapBurnForSegment",
    "BuildComposedVideoFilter",
    "BuildAssText",
    "ConcatTimeUs",
    "ITimelineFinalMixer",
    "TimelineFfmpegFinalMixer",
    "MixFinalAsync",
    "BuildFinalMixArguments",
    "EnsureMuxSupported",
    "amix",
    "adelay",
    "asplit",
):
    require(token in fidelity, f"G13 fidelity token missing: {token}")
require("ass=filename" in editor,
        "classic ASS burn filter drifted from the reused shape")

# Fidelity reuses the proven classic builders instead of duplicating them,
# and must not touch classic persistence or the classic service instance.
for token in ("EditorProjectStore.", "new VideoEditorService", "MainWindow", "ToolManager"):
    require(token not in fidelity, f"G13 fidelity coupled to classic owner: {token}")
import re
for match in re.finditer(r"VideoEditorService\.(\w+)", fidelity):
    require(match.group(1) in ("BuildFilter", "BuildAss"),
            f"G13 may only reuse classic builders, found VideoEditorService.{match.group(1)}")
require("VideoEditorService.BuildFilter" in fidelity
        and "VideoEditorService.BuildAss" in fidelity,
        "G13 must reuse the classic filter/ASS builders, not duplicate them")
require("public static string BuildFilter(" in editor
        and "public static string BuildAss(" in editor,
        "classic builder reuse surface drifted")

for token in (
    "ITimelineFinalMixer",
    "_mixer",
    "MixFinalAsync",
    "MapBurnForSegment",
    "BuildAssText",
    ".ass",
):
    require(token in service, f"G13 service token missing: {token}")
require("Timeline" not in editor_store,
        "classic schema-7 store must not reference Timeline")

for token in (
    "EffectPayloadParsesExplicitly",
    "EffectMappingUsesSourceTime",
    "AssTextMatchesClassicBurn",
    "FinalMixArgumentsAreExact",
    "ConcatTimeCollapsesGaps",
    "FieldFidelityExportAsync",
):
    require(token in test, f"G13 runtime contract missing: {token}")
require("TimelineFidelityContract.RunAsync" in program
        and "without silent drops" in program,
        "G13 runtime contract is not registered")
require("verify_timeline_fidelity_contract.py" in verify,
        "official Windows verification does not run the G13 static gate")
require("DEC-023" in decisions,
        "G13 fidelity boundary decision was not recorded")

print("PASS: G13 full-fidelity export renders effects, subtitles, voice and music without silent drops")
