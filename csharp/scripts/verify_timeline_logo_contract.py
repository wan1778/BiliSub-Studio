#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
CORE = ROOT / "csharp/src/BiliSubStudio.Core/Timeline"
FIDELITY = CORE / "TimelineFidelityMux.cs"
MUX = CORE / "TimelineFfmpegMux.cs"
TEST = ROOT / "csharp/tests/BiliSubStudio.Core.ContractTests/TimelineFidelityContract.cs"
PROGRAM = ROOT / "csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs"
VERIFY = ROOT / "csharp/scripts/verify.ps1"
DECISIONS = ROOT / "docs/editor-timeline/DECISIONS.md"
EDITOR_STORE = ROOT / "csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs"


def require(condition: bool, message: str) -> None:
    if not condition:
        print("FAIL:", message, file=sys.stderr)
        raise SystemExit(1)


fidelity = FIDELITY.read_text(encoding="utf-8")
mux = MUX.read_text(encoding="utf-8")
test = TEST.read_text(encoding="utf-8")
program = PROGRAM.read_text(encoding="utf-8")
verify = VERIFY.read_text(encoding="utf-8")
decisions = DECISIONS.read_text(encoding="utf-8")
editor_store = EDITOR_STORE.read_text(encoding="utf-8")

for token in (
    "TimelineLogoSpec",
    "TimelineLogoOverlay",
    "MapLogosForSegment",
    "logo|{path}|{x},{y},{w},{h}|{opacity}",
):
    require(token in fidelity, f"G16 logo ownership token missing: {token}")

for token in (
    "BuildLogoChain",
    "MapLogosForSegment",
    "overlay=",
    "eof_action=repeat",
    "colorchannelmixer",
    "-loop",
    "AssPath",
):
    require(token in mux, f"G16 mux logo token missing: {token}")

# Logo overlays mirror the classic composer semantics without touching it.
# Builder reuse (BuildFilter/BuildAss) is intentional, as pinned by G13.
for token in ("EditorProjectStore.", "new VideoEditorService",
              "EditorImageOverlayComposer", "MainWindow", "ToolManager"):
    require(token not in fidelity and token not in mux,
            f"G16 must not couple to classic owners: {token}")
import re
for match in re.finditer(r"VideoEditorService\.(\w+)", fidelity):
    require(match.group(1) in ("BuildFilter", "BuildAss"),
            f"G16 may only reuse classic builders, found VideoEditorService.{match.group(1)}")
require("Timeline" not in editor_store,
        "classic schema-7 store must not reference Timeline")

for token in (
    "LogoPayloadParsesExplicitly",
    "LogoMappingUsesSourceGeometry",
    "FieldLogoExportAsync",
    "logo overlay did not land in its rect",
):
    require(token in test, f"G16 runtime contract missing: {token}")
require("TimelineFidelityContract.RunAsync" in program,
        "G16 runtime contract is not registered")
require("verify_timeline_logo_contract.py" in verify,
        "official Windows verification does not run the G16 static gate")
require("DEC-026" in decisions,
        "G16 logo boundary decision was not recorded")

print("PASS: G16 logo overlays render with documented payloads, source windows and rect proof")
