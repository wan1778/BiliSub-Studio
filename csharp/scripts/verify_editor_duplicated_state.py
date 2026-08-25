#!/usr/bin/env python3
from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
PAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages"
PARTIALS = sorted(PAGES.glob("EditorPage*.cs"))


def fail(message: str) -> None:
    print("FAIL:", message, file=sys.stderr)
    raise SystemExit(1)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


code_by_file = {path.name: path.read_text(encoding="utf-8") for path in PARTIALS}
code = "\n".join(code_by_file.values())
bootstrap = code_by_file["EditorPage.ParityBootstrap.cs"]
parity = code_by_file["EditorPage.ParityFixes.cs"]
images = code_by_file["EditorPage.Images.cs"]

# CLEAN-08: the static Editor shell has one stored initialization owner only.
require(len(re.findall(r"private\s+bool\s+_editorCoreInitialized\s*;", code)) == 1,
        "CLEAN-08 _editorCoreInitialized must be the single stored shell-init flag")
require(re.search(r"private\s+bool\s+_editorParityInitialized\s*;", code) is None,
        "CLEAN-08 duplicate parity initialization field returned")
require(re.search(r"private\s+bool\s+_imageFeatureInitialized\s*;", code) is None,
        "CLEAN-08 duplicate image initialization field returned")
require("private bool _imageFeatureInitialized => _editorCoreInitialized;" in images,
        "CLEAN-08 image readiness must be a read-through projection of the core owner")
require(re.search(r"\b_imageFeatureInitialized\s*=(?![=>])", code) is None,
        "CLEAN-08 image readiness must not have an independent assignment path")
require("EnsureEditorParityInitialized" not in code,
        "CLEAN-08 retired parity init shim must not remain")
require("EnsureImageFeatureInitialized" not in code,
        "CLEAN-08 retired image init shim must not remain")

# Lifecycle must bind once and flip the sole stored owner only after shell binding returns.
require(bootstrap.count("if (!_editorCoreInitialized)") == 1,
        "CLEAN-08 Loaded must have exactly one core initialization guard")
require(bootstrap.count("BindStaticUiShell();") == 1,
        "CLEAN-08 static shell binding entry drifted")
require(bootstrap.count("_editorCoreInitialized = true;") == 1,
        "CLEAN-08 core initialization state must be committed exactly once")
require("if (!_editorCoreInitialized) return;" in parity,
        "CLEAN-08 parity refresh must read the canonical core readiness owner")
require("if (!_imageFeatureInitialized) return;" in images,
        "CLEAN-08 image refresh must read the core-derived readiness projection")

# Do not confuse duplicated-init cleanup with real Editor working state.
for live_owner in (
    "_imageOverlays",
    "_selectedImageIndex",
    "_editorAutoCompositeCancellation",
    "_projectSaveTimer",
    "_voiceArtifactWatcher",
):
    require(code.count(live_owner) > 1,
            f"CLEAN-08 real working-state owner was removed accidentally: {live_owner}")

# Negative fixture models the rejected pattern: three independently stored booleans for one lifecycle.
fixture = """
private bool _editorCoreInitialized;
private bool _editorParityInitialized;
private bool _imageFeatureInitialized;
"""
require(len(re.findall(r"private\s+bool\s+_[A-Za-z]\w*Initialized\s*;", fixture)) == 3,
        "CLEAN-08 negative fixture no longer represents duplicated initialization state")
assignment_fixture = "_imageFeatureInitialized = true;"
require(re.search(r"\b_imageFeatureInitialized\s*=(?![=>])", assignment_fixture) is not None,
        "CLEAN-08 negative assignment fixture is not detected")

print("PASS: CLEAN-08 Editor shell initialization has one stored owner (_editorCoreInitialized)")
