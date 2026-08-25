#!/usr/bin/env python3
from __future__ import annotations

import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
PAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages"
XAML = PAGES / "EditorPage.xaml"
PARTIALS = sorted(PAGES.glob("EditorPage*.cs"))
XAML_NS = "http://schemas.microsoft.com/winfx/2006/xaml"

REMOVED_DEAD_CONTROL_NAMES = (
    "PageRoot",
    "PreviewPane",
    "ToolSelectorGrid",
    "InspectorScroll",
    "DetailsPanelHost",
)


def fail(message: str) -> None:
    print("FAIL:", message, file=sys.stderr)
    raise SystemExit(1)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


xaml = XAML.read_text(encoding="utf-8")
code = "\n".join(path.read_text(encoding="utf-8") for path in PARTIALS)
root = ET.parse(XAML).getroot()
parents = {child: parent for parent in root.iter() for child in parent}

# CLEAN-05: these five visual containers are still required for layout, but their
# x:Name identities generated unused WinUI fields. Keep the containers, remove only
# the dead generated identities.
for name in REMOVED_DEAD_CONTROL_NAMES:
    require(
        re.search(rf'x:Name="{re.escape(name)}"', xaml) is None,
        f"CLEAN-05 dead generated control identity returned: {name}",
    )
    require(
        re.search(rf"\b{re.escape(name)}\b", code) is None,
        f"CLEAN-05 removed control identity gained a code owner unexpectedly: {name}",
    )

# Every remaining named Editor control must have a real owner/reference in code-behind
# or an explicit XAML-side reference (ElementName/TargetName/etc.). A lone x:Name is a
# generated field with no owner and is therefore a CLEAN-05 regression.
orphaned: list[str] = []
seen: set[str] = set()
for element in root.iter():
    name = element.attrib.get(f"{{{XAML_NS}}}Name")
    if not name:
        continue
    require(name not in seen, f"CLEAN-05 duplicate x:Name: {name}")
    seen.add(name)
    code_refs = len(re.findall(rf"\b{re.escape(name)}\b", code))
    xaml_refs = len(re.findall(rf"\b{re.escape(name)}\b", xaml)) - 1
    if code_refs == 0 and xaml_refs == 0:
        orphaned.append(name)

require(not orphaned, f"CLEAN-05 orphan named control(s): {orphaned}")

# Protect the live layout-smoke controls that are intentionally named because code
# reads their geometry. This prevents a broad x:Name cleanup from deleting real owners.
for live_layout_name in (
    "WorkspaceGrid",
    "SourceColumn",
    "PlayerColumn",
    "DetailsColumn",
    "PreviewSurface",
    "PlayerControlBar",
):
    require(live_layout_name in seen, f"CLEAN-05 live layout control lost x:Name: {live_layout_name}")
    require(
        re.search(rf"\b{re.escape(live_layout_name)}\b", code) is not None,
        f"CLEAN-05 live layout control lost its code owner: {live_layout_name}",
    )

# The preview surface contains only the visual preview and direct-edit overlays.
# PREVIEW-LAYOUT-01 keeps the transport below it, so controls never cover the
# user-visible video frame.
preview_surface = next(element for element in root.iter()
                       if element.attrib.get(f"{{{XAML_NS}}}Name") == "PreviewSurface")
player_control_bar = next(element for element in root.iter()
                          if element.attrib.get(f"{{{XAML_NS}}}Name") == "PlayerControlBar")
ancestor = player_control_bar
while ancestor in parents:
    ancestor = parents[ancestor]
    require(ancestor is not preview_surface,
            "PREVIEW-LAYOUT-01 transport bar must not be inside PreviewSurface")
require(player_control_bar.attrib.get("Grid.Row") == "1",
        "PREVIEW-LAYOUT-01 transport bar must occupy the row below PreviewSurface")

fixture_xaml = '<Grid xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" x:Name="DeadContainer" />'
fixture_code = "private void SomethingElse() { }"
fixture_name = "DeadContainer"
require(
    len(re.findall(rf"\b{fixture_name}\b", fixture_code)) == 0
    and len(re.findall(rf"\b{fixture_name}\b", fixture_xaml)) == 1,
    "CLEAN-05 negative fixture failed to represent an orphan generated control identity",
)

print(f"PASS: CLEAN-05 Editor control ownership locked ({len(seen)} named controls, 0 orphan identities)")
