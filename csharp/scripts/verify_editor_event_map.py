#!/usr/bin/env python3
from __future__ import annotations

import re
import sys
import xml.etree.ElementTree as ET
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages"
XAML = PAGES / "EditorPage.xaml"
PARTIALS = sorted(PAGES.glob("EditorPage*.cs"))

XAML_NS = "http://schemas.microsoft.com/winfx/2006/xaml"
EVENT_NAMES = {
    "Click", "Checked", "Unchecked", "Toggled", "SelectionChanged", "TextChanged",
    "ValueChanged", "PointerPressed", "PointerMoved", "PointerReleased",
    "PointerCanceled", "Loaded", "Unloaded", "SizeChanged", "LayoutUpdated", "KeyDown",
}
EXPECTED_XAML_BINDINGS = 52
EXPECTED_XAML_CLICKS = 25
EXPECTED_USER_CLICK_BINDINGS = 37

RUNTIME_UI_BINDINGS = [
    ("ImageSourceList", "SelectionChanged", "ImageList_SelectionChanged"),
    ("ImageOverlayCanvas", "PointerPressed", "ImageOverlay_PointerPressed"),
    ("ImageOverlayCanvas", "PointerMoved", "ImageOverlay_PointerMoved"),
    ("ImageOverlayCanvas", "PointerReleased", "ImageOverlay_PointerReleased"),
    ("ImageOverlayCanvas", "PointerCanceled", "ImageOverlay_PointerCanceled"),
    ("ImageOverlayCanvas", "SizeChanged", "ImageOverlay_SizeChanged"),
    ("AddImageButton", "Click", "AddImage_Click"),
    ("RemoveImageButton", "Click", "RemoveImage_Click"),
    ("ImageTopLeftButton", "Click", "ImageTopLeft_Click"),
    ("ImageTopRightButton", "Click", "ImageTopRight_Click"),
    ("ImageXBox", "ValueChanged", "ImageGeometry_ValueChanged"),
    ("ImageYBox", "ValueChanged", "ImageGeometry_ValueChanged"),
    ("ImageWidthBox", "ValueChanged", "ImageGeometry_ValueChanged"),
    ("ImageHeightBox", "ValueChanged", "ImageGeometry_ValueChanged"),
    ("ImageOpacitySlider", "ValueChanged", "ImageOpacity_ValueChanged"),
    ("EditorAutoCompositeToggle", "Toggled", "EditorAutoComposite_Toggled"),
    ("EditorChooseOutputButton", "Click", "EditorChooseOutput_Click"),
    ("EditorOpenOutputButton", "Click", "EditorOpenOutput_Click"),
]
TOOL_BUTTONS = (
    "SubtitleModeButton", "BlurModeButton", "AudioModeButton",
    "VoiceModeButton", "ImageModeButton", "ExportModeButton",
)
LIFECYCLE_BINDINGS = [
    ("EditorPage", "Loaded", "EditorPage_Loaded"),
    ("EditorPage", "LayoutUpdated", "EditorPage_LayoutUpdated"),
    ("EditorPage", "Unloaded", "EditorPage_Unloaded"),
]


def fail(message: str) -> None:
    print(f"FAIL: {message}", file=sys.stderr)
    raise SystemExit(1)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


def xaml_bindings() -> list[tuple[str, str, str]]:
    root = ET.parse(XAML).getroot()
    bindings: list[tuple[str, str, str]] = []
    for element in root.iter():
        name = element.attrib.get(f"{{{XAML_NS}}}Name")
        if element is root:
            name = name or "EditorPage"
        for raw_name, handler in element.attrib.items():
            event = raw_name.rsplit("}", 1)[-1]
            if event in EVENT_NAMES and handler and not handler.startswith("{"):
                bindings.append((name or element.tag.rsplit("}", 1)[-1], event, handler))
    return bindings


code = "\n".join(read(path) for path in PARTIALS)
bindings = xaml_bindings()

require(len(bindings) == EXPECTED_XAML_BINDINGS,
        f"AUDIT-01 XAML event map drift: expected {EXPECTED_XAML_BINDINGS}, found {len(bindings)}")
require(sum(1 for _, event, _ in bindings if event == "Click") == EXPECTED_XAML_CLICKS,
        f"AUDIT-01 XAML click map drift: expected {EXPECTED_XAML_CLICKS}")

xaml_keys = [(source, event) for source, event, _ in bindings]
duplicates = [key for key, count in Counter(xaml_keys).items() if count > 1]
require(not duplicates, f"AUDIT-01 duplicate XAML event binding(s): {duplicates}")

direct_runtime = re.findall(
    r"\b([A-Za-z_]\w*)\s*\.\s*(" + "|".join(sorted(EVENT_NAMES)) + r")\s*\+=\s*([A-Za-z_]\w*)",
    code,
)
xaml_key_set = set(xaml_keys)
double_bound = sorted({(source, event) for source, event, _ in direct_runtime if (source, event) in xaml_key_set})
require(not double_bound, f"AUDIT-01 control/event bound in both XAML and runtime: {double_bound}")

bootstrap = read(PAGES / "EditorPage.ParityBootstrap.cs")
main = read(PAGES / "EditorPage.xaml.cs")
playback = read(PAGES / "EditorPage.Playback.cs")

require("if (!_editorCoreInitialized)" in bootstrap and "BindStaticUiShell();" in bootstrap,
        "AUDIT-01 runtime shell bindings must stay behind one Loaded-time initialization guard")
require(bootstrap.count("toolButton.Click += ShellTool_Click;") == 1,
        "AUDIT-01 tool selector must use one shared runtime Click subscription statement")
for button in TOOL_BUTTONS:
    require(button in bootstrap, f"AUDIT-01 missing tool selector in shared Click map: {button}")

for source, event, handler in RUNTIME_UI_BINDINGS:
    marker = f"{source}.{event} += {handler};"
    require(code.count(marker) == 1, f"AUDIT-01 runtime UI binding must appear exactly once: {marker}")
    require((source, event) not in xaml_key_set,
            f"AUDIT-01 runtime UI binding duplicates XAML: {source}.{event}")

for _, event, handler in LIFECYCLE_BINDINGS:
    marker = f"{event} += {handler};"
    require(main.count(marker) == 1, f"AUDIT-01 lifecycle binding must appear exactly once: {marker}")

for marker in (
    "player.PlaybackSession.PositionChanged += PlayerPositionChanged;",
    "player.MediaEnded += PlayerMediaEnded;",
    "player.MediaFailed += PlayerMediaFailed;",
):
    require(playback.count(marker) == 1, f"AUDIT-01 player binding must appear exactly once: {marker}")
for marker in (
    "player.PlaybackSession.PositionChanged -= PlayerPositionChanged;",
    "player.MediaEnded -= PlayerMediaEnded;",
    "player.MediaFailed -= PlayerMediaFailed;",
):
    require(playback.count(marker) == 1, f"AUDIT-01 player unbinding must appear exactly once: {marker}")
require(playback.count("RegisterPropertyChangedCallback(") == 1
        and playback.count("UnregisterPropertyChangedCallback(") == 1,
        "AUDIT-01 fullscreen property callback must have one register/unregister owner")

all_handlers = {handler for _, _, handler in bindings}
all_handlers.update(handler for _, _, handler in RUNTIME_UI_BINDINGS)
all_handlers.update(handler for _, _, handler in LIFECYCLE_BINDINGS)
all_handlers.update(("ShellTool_Click", "PlayerPositionChanged", "PlayerMediaEnded",
                     "PlayerMediaFailed", "PreviewPlayerFullWindowChanged"))
for handler in sorted(all_handlers):
    calls = len(re.findall(rf"\b{re.escape(handler)}\s*\(", code))
    require(calls == 1,
            f"AUDIT-01 event handler must have one implementation and no handler-to-handler calls: {handler} ({calls})")

runtime_clicks = sum(1 for _, event, _ in RUNTIME_UI_BINDINGS if event == "Click") + len(TOOL_BUTTONS)
user_clicks = EXPECTED_XAML_CLICKS + runtime_clicks
require(user_clicks == EXPECTED_USER_CLICK_BINDINGS,
        f"AUDIT-01 user click inventory drift: expected {EXPECTED_USER_CLICK_BINDINGS}, found {user_clicks}")

for dead in ("InspectorMode_Click", "Refresh_Click"):
    require(len(re.findall(rf"\b{dead}\s*\(", code)) == 1,
            f"AUDIT-01 recorded dead handler changed; update event-map audit before cleanup: {dead}")
    require(dead not in {handler for _, _, handler in bindings},
            f"AUDIT-01 recorded dead handler unexpectedly became XAML-bound: {dead}")
    require(not any(handler == dead for _, _, handler in direct_runtime),
            f"AUDIT-01 recorded dead handler unexpectedly became runtime-bound: {dead}")

print(
    "PASS: AUDIT-01 Editor Event Map · "
    f"{len(bindings)} XAML bindings · {len(RUNTIME_UI_BINDINGS) + len(TOOL_BUTTONS)} runtime UI bindings · "
    f"{EXPECTED_USER_CLICK_BINDINGS} user Click bindings · 0 duplicate control/event bindings"
)
