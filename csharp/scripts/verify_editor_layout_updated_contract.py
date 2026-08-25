#!/usr/bin/env python3
from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
PAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages"
XAML = PAGES / "EditorPage.xaml"
PARTIALS = sorted(PAGES.glob("EditorPage*.cs"))


def fail(message: str) -> None:
    print("FAIL:", message, file=sys.stderr)
    raise SystemExit(1)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


code_by_file = {path.name: path.read_text(encoding="utf-8") for path in PARTIALS}
code = "\n".join(code_by_file.values())
main = code_by_file["EditorPage.xaml.cs"]
bootstrap = code_by_file["EditorPage.ParityBootstrap.cs"]
xaml = XAML.read_text(encoding="utf-8")

# CLEAN-07: LayoutUpdated must not be used as a high-frequency polling loop.
require(re.search(r"\bLayoutUpdated\s*\+=", code) is None,
        "CLEAN-07 runtime LayoutUpdated subscription returned")
require(re.search(r"\bLayoutUpdated\s*=", xaml) is None,
        "CLEAN-07 XAML LayoutUpdated binding returned")
require("EditorPage_LayoutUpdated" not in code,
        "CLEAN-07 old LayoutUpdated handler remains in Editor source")
require("_lastOverlayWidth" not in code and "_lastOverlayHeight" not in code,
        "CLEAN-07 old layout-polling cache fields remain in Editor source")

# Overlay geometry redraw belongs to the element whose size actually changed.
owner = "Overlay.SizeChanged += Overlay_SizeChanged;"
require(main.count(owner) == 1,
        "CLEAN-07 Overlay.SizeChanged must have exactly one runtime owner")
require("Overlay.SizeChanged -= Overlay_SizeChanged;" not in code,
        "CLEAN-07 persistent Overlay size owner must not churn across lifecycle")
handler = "private void Overlay_SizeChanged(object sender, SizeChangedEventArgs e) => RenderOverlays();"
require(main.count(handler) == 1,
        "CLEAN-07 Overlay size handler must redraw overlays directly and only once")

# Other reviewed SizeChanged owners are legitimate and must not be collateral damage.
require(code.count("ImageOverlayCanvas.SizeChanged += ImageOverlay_SizeChanged;") == 1,
        "CLEAN-07 image overlay SizeChanged owner drifted")
require(bootstrap.count("WorkspaceGrid.SizeChanged += ValidateUiShellLayoutForSmoke;") == 1,
        "CLEAN-07 startup layout-smoke SizeChanged owner drifted")

# Negative fixture represents the removed anti-pattern: page-wide layout passes poll ActualWidth.
fixture = """
LayoutUpdated += EditorPage_LayoutUpdated;
private void EditorPage_LayoutUpdated(object? sender, object e)
{
    if (Math.Abs(Overlay.ActualWidth - _lastOverlayWidth) >= .5) RenderOverlays();
}
"""
require(re.search(r"\bLayoutUpdated\s*\+=", fixture) is not None
        and "Overlay.ActualWidth" in fixture,
        "CLEAN-07 negative fixture no longer represents LayoutUpdated polling")

print("PASS: CLEAN-07 Editor has no LayoutUpdated polling; Overlay redraw is owned by Overlay.SizeChanged")
