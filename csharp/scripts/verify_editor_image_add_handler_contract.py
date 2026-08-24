#!/usr/bin/env python3
from __future__ import annotations

import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages"
XAML = PAGES / "EditorPage.xaml"
BOOTSTRAP = PAGES / "EditorPage.ParityBootstrap.cs"
IMAGES = PAGES / "EditorPage.Images.cs"
EVENT_GATE = ROOT / "csharp/scripts/verify_editor_event_map.py"
EVENT_MAP = ROOT / "docs/engineering/EDITOR_EVENT_MAP.md"
XAML_NS = "http://schemas.microsoft.com/winfx/2006/xaml"


def fail(message: str) -> None:
    print(f"FAIL: {message}", file=sys.stderr)
    raise SystemExit(1)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


xaml = read(XAML)
bootstrap = read(BOOTSTRAP)
images = read(IMAGES)
event_gate = read(EVENT_GATE)
event_map = read(EVENT_MAP)

# IMAGE-02 — Add Image has exactly one event owner. The static XAML button must
# not declare Click, the guarded runtime shell binds it once, and the single
# AddImage_Click implementation delegates once to AddImageAsync.

root = ET.parse(XAML).getroot()
add_nodes = [
    element for element in root.iter()
    if element.attrib.get(f"{{{XAML_NS}}}Name") == "AddImageButton"
]
require(len(add_nodes) == 1,
        f"IMAGE-02 requires exactly one AddImageButton, found {len(add_nodes)}")
add_node = add_nodes[0]
require("Click" not in {key.rsplit("}", 1)[-1] for key in add_node.attrib},
        "IMAGE-02 AddImageButton must not also be XAML Click-bound")

loaded = bootstrap.split("private void EditorPage_Loaded", 1)[1].split(
    "void BindStaticUiShell()", 1
)[0]
require("if (!_editorCoreInitialized)" in loaded,
        "IMAGE-02 runtime bindings must remain behind one initialization guard")
require(loaded.count("BindStaticUiShell();") == 1,
        "IMAGE-02 EditorPage_Loaded must call BindStaticUiShell exactly once")

binding = "AddImageButton.Click += AddImage_Click;"
require(bootstrap.count(binding) == 1,
        "IMAGE-02 AddImageButton runtime Click binding must appear exactly once")

definitions = re.findall(
    r"\b(?:private|internal|public)\s+(?:async\s+)?void\s+AddImage_Click\s*\(",
    "\n".join((bootstrap, images)),
)
require(len(definitions) == 1,
        f"IMAGE-02 requires one AddImage_Click implementation, found {len(definitions)}")

handler = images.split(
    "private async void AddImage_Click(object sender, RoutedEventArgs e)", 1
)[1].split("private async Task AddImageAsync()", 1)[0]
require(handler.count("await AddImageAsync();") == 1,
        "IMAGE-02 AddImage_Click must delegate exactly once to AddImageAsync")
require("catch (Exception error)" in handler,
        "IMAGE-02 AddImage_Click must keep its error boundary")

require(images.count("private async Task AddImageAsync()") == 1,
        "IMAGE-02 AddImageAsync owner must have exactly one implementation")

runtime_tuple = '("AddImageButton", "Click", "AddImage_Click"),'
require(event_gate.count(runtime_tuple) == 1,
        "IMAGE-02 AUDIT-01 runtime inventory must contain AddImage exactly once")
require("double_bound" in event_gate and "control/event bound in both XAML and runtime" in event_gate,
        "IMAGE-02 global event audit must continue rejecting XAML/runtime double binding")
require("event handler must have one implementation and no handler-to-handler calls" in event_gate,
        "IMAGE-02 global event audit must continue rejecting duplicate/cross-called handlers")

require("| `AddImageButton` | runtime | `AddImage_Click` | 1 |" in event_map,
        "IMAGE-02 engineering event map must record one Add Image handler")

runtime_bindings = 1
xaml_bindings = 0
require(runtime_bindings + xaml_bindings == 1,
        "IMAGE-02 fixture: Add Image must have one effective Click owner")

print("PASS: IMAGE-02 Add Image has exactly one Click handler owner")
