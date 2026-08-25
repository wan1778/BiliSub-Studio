#!/usr/bin/env python3
from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
APP = ROOT / "csharp/src/BiliSubStudio.App"
PAGES = APP / "Pages"
XAML = PAGES / "EditorPage.xaml"
MAIN_WINDOW = APP / "MainWindow.xaml.cs"
PARTIALS = sorted(PAGES.glob("EditorPage*.cs"))

REMOVED_DEAD_METHODS = (
    "InspectorMode_Click",
    "Refresh_Click",
    "RemoveImage_Click",
    "ImageTopLeft_Click",
    "ImageTopRight_Click",
    "MoveSelectedImageToCornerAsync",
    "RenderTimelineRegions",
)

LIVE_REPLACEMENTS = (
    "ShellTool_Click",
    "SelectShellTool",
    "RemoveImageSafe_Click",
    "ImageCornerPreset_Click",
    "UpdateFrameAsync",
    "SetInspectorMode",
)


def fail(message: str) -> None:
    print("FAIL:", message, file=sys.stderr)
    raise SystemExit(1)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


code = "\n".join(path.read_text(encoding="utf-8") for path in PARTIALS)
xaml = XAML.read_text(encoding="utf-8")
main_window = MAIN_WINDOW.read_text(encoding="utf-8")
combined = code + "\n" + xaml

for method in REMOVED_DEAD_METHODS:
    require(
        re.search(rf"\b{re.escape(method)}\b", combined) is None,
        f"CLEAN-04 dead method/reference returned: {method}",
    )

for method in LIVE_REPLACEMENTS:
    require(
        len(re.findall(rf"\b{re.escape(method)}\b", combined)) >= 2,
        f"CLEAN-04 live replacement lost its owner/caller map: {method}",
    )

# The removed image handlers had exactly one replacement owner path each. Lock the
# runtime subscriptions so cleanup cannot silently reconnect a legacy implementation.
bootstrap = (PAGES / "EditorPage.ParityBootstrap.cs").read_text(encoding="utf-8")
require("RemoveImageButton.Click += RemoveImageSafe_Click;" in bootstrap,
        "CLEAN-04 image delete must stay owned by RemoveImageSafe_Click")
require(bootstrap.count("ImageTopLeftButton.Click += ImageCornerPreset_Click;") == 1,
        "CLEAN-04 left image preset must stay owned by ImageCornerPreset_Click")
require(bootstrap.count("ImageTopRightButton.Click += ImageCornerPreset_Click;") == 1,
        "CLEAN-04 right image preset must stay owned by ImageCornerPreset_Click")

main = (PAGES / "EditorPage.xaml.cs").read_text(encoding="utf-8")
require("RenderTimelineRegions();" not in main,
        "CLEAN-04 no-op timeline caller must not remain after method removal")
require(main.count("SetInspectorMode(mode);") == 1,
        "CLEAN-04 SetInspectorMode must remain owned by layout smoke enumeration")
require(main.count("SetInspectorMode(InspectorMode.Subtitle);") == 1,
        "CLEAN-04 layout smoke must restore Subtitle mode")
require("await editorPage.RunLayoutSmokeAsync();" in main_window,
        "CLEAN-04 MainWindow must keep the external Editor layout-smoke owner")

# Synthetic negative fixture: declaration-only methods are the class of dead code
# CLEAN-04 removes; the contract must detect the forbidden identifier.
fixture = "private void Refresh_Click(object sender, RoutedEventArgs e) { }"
require(any(re.search(rf"\b{re.escape(method)}\b", fixture) for method in REMOVED_DEAD_METHODS),
        "CLEAN-04 negative fixture does not exercise the removed method inventory")

print("PASS: CLEAN-04 Editor dead method contract is locked")
