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
UI_EVENT_NAMES = {
    "Click", "Checked", "Unchecked", "Toggled", "SelectionChanged", "TextChanged",
    "ValueChanged", "LostFocus", "PointerPressed", "PointerMoved", "PointerReleased",
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
    ("RemoveImageButton", "Click", "RemoveImageSafe_Click"),
    ("ImageTopLeftButton", "Click", "ImageCornerPreset_Click"),
    ("ImageTopRightButton", "Click", "ImageCornerPreset_Click"),
    ("ImageXBox", "ValueChanged", "ImageGeometry_ValueChanged"),
    ("ImageYBox", "ValueChanged", "ImageGeometry_ValueChanged"),
    ("ImageWidthBox", "ValueChanged", "ImageGeometry_ValueChanged"),
    ("ImageHeightBox", "ValueChanged", "ImageGeometry_ValueChanged"),
    ("ImageOpacitySlider", "ValueChanged", "ImageOpacity_ValueChanged"),
    ("EditorAutoCompositeToggle", "Toggled", "EditorAutoComposite_Toggled"),
    ("EditorChooseOutputButton", "Click", "EditorChooseOutput_Click"),
    ("EditorOpenOutputButton", "Click", "EditorOpenOutput_Click"),
    ("FileNameBox", "LostFocus", "EditorFileName_LostFocus"),
]
CONDITIONAL_RUNTIME_UI_BINDINGS = [
    ("WorkspaceGrid", "SizeChanged", "ValidateUiShellLayoutForSmoke"),
]
TOOL_BUTTONS = (
    "SubtitleModeButton", "BlurModeButton", "AudioModeButton",
    "VoiceModeButton", "ImageModeButton", "ExportModeButton",
)
LIFECYCLE_BINDINGS = [
    ("Loaded", "EditorPage_Loaded"),
    ("LayoutUpdated", "EditorPage_LayoutUpdated"),
    ("Unloaded", "EditorPage_Unloaded"),
]
LEGACY_NON_OWNERS = (
    "InspectorMode_Click",
    "Refresh_Click",
    "RemoveImage_Click",
    "ImageTopLeft_Click",
    "ImageTopRight_Click",
)


def fail(message: str) -> None:
    print(f"FAIL: {message}", file=sys.stderr)
    raise SystemExit(1)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


def method_body(source: str, signature: str) -> str:
    start = source.find(signature)
    require(start >= 0, f"CLEAN-01 missing method: {signature}")
    brace = source.find("{", start)
    require(brace >= 0, f"CLEAN-01 method has no body: {signature}")
    depth = 0
    for index in range(brace, len(source)):
        if source[index] == "{":
            depth += 1
        elif source[index] == "}":
            depth -= 1
            if depth == 0:
                return source[brace:index + 1]
    fail(f"CLEAN-01 unterminated method: {signature}")
    return ""


def xaml_bindings() -> list[tuple[str, str, str]]:
    root = ET.parse(XAML).getroot()
    bindings: list[tuple[str, str, str]] = []
    for element in root.iter():
        name = element.attrib.get(f"{{{XAML_NS}}}Name")
        if element is root:
            name = name or "EditorPage"
        for raw_name, handler in element.attrib.items():
            event = raw_name.rsplit("}", 1)[-1]
            if event in UI_EVENT_NAMES and handler and not handler.startswith("{"):
                bindings.append((name or element.tag.rsplit("}", 1)[-1], event, handler))
    return bindings


code_by_file = {path.name: read(path) for path in PARTIALS}
code = "\n".join(code_by_file.values())
bindings = xaml_bindings()
bootstrap = code_by_file["EditorPage.ParityBootstrap.cs"]
parity = code_by_file["EditorPage.ParityFixes.cs"]
main = code_by_file["EditorPage.xaml.cs"]
playback = code_by_file["EditorPage.Playback.cs"]

# Declarative XAML owner map.
require(len(bindings) == EXPECTED_XAML_BINDINGS,
        f"CLEAN-01 XAML event map drift: expected {EXPECTED_XAML_BINDINGS}, found {len(bindings)}")
require(sum(1 for _, event, _ in bindings if event == "Click") == EXPECTED_XAML_CLICKS,
        f"CLEAN-01 XAML Click map drift: expected {EXPECTED_XAML_CLICKS}")
xaml_keys = [(source, event) for source, event, _ in bindings]
duplicates = [key for key, count in Counter(xaml_keys).items() if count > 1]
require(not duplicates, f"CLEAN-01 duplicate XAML source/event owner(s): {duplicates}")
xaml_key_set = set(xaml_keys)

# Runtime UI bindings must match one reviewed inventory. The six tool buttons intentionally
# share one loop subscription statement and therefore appear as toolButton.Click here.
direct_runtime = re.findall(
    r"\b([A-Za-z_]\w*)\s*\.\s*(" + "|".join(sorted(UI_EVENT_NAMES)) + r")\s*\+=\s*([A-Za-z_]\w*)",
    code,
)
expected_direct_runtime = set(RUNTIME_UI_BINDINGS + CONDITIONAL_RUNTIME_UI_BINDINGS)
expected_direct_runtime.add(("toolButton", "Click", "ShellTool_Click"))
require(set(direct_runtime) == expected_direct_runtime,
        "CLEAN-01 direct runtime UI event inventory drift: "
        f"missing={sorted(expected_direct_runtime - set(direct_runtime))}, "
        f"extra={sorted(set(direct_runtime) - expected_direct_runtime)}")

for source, event, handler in RUNTIME_UI_BINDINGS + CONDITIONAL_RUNTIME_UI_BINDINGS:
    marker = f"{source}.{event} += {handler};"
    require(code.count(marker) == 1, f"CLEAN-01 runtime UI owner must appear exactly once: {marker}")
    require((source, event) not in xaml_key_set,
            f"CLEAN-01 runtime UI owner duplicates XAML: {source}.{event}")
require(bootstrap.count("toolButton.Click += ShellTool_Click;") == 1,
        "CLEAN-01 tool selector must keep one shared Click subscription statement")
for button in TOOL_BUTTONS:
    require(button in bootstrap, f"CLEAN-01 missing tool in shared ShellTool_Click owner map: {button}")

# Page lifecycle has one owner per event. No secondary Unloaded subscriber is allowed.
for event, handler in LIFECYCLE_BINDINGS:
    marker = f"{event} += {handler};"
    require(main.count(marker) == 1, f"CLEAN-01 lifecycle owner must appear exactly once: {marker}")
require(len(re.findall(r"(?<!\.)\bLoaded\s*\+=", code)) == 1,
        "CLEAN-01 Page.Loaded must have exactly one owner")
require(len(re.findall(r"(?<!\.)\bLayoutUpdated\s*\+=", code)) == 1,
        "CLEAN-01 Page.LayoutUpdated must have exactly one owner")
require(len(re.findall(r"(?<!\.)\bUnloaded\s*\+=", code)) == 1,
        "CLEAN-01 Page.Unloaded must have exactly one owner")
require("Unloaded += EditorProgress_Unloaded;" not in code,
        "CLEAN-01 progress cleanup must not remain a second Unloaded event owner")

unloaded = method_body(main, "private async void EditorPage_Unloaded(")
cleanup_parity = method_body(parity, "private void CleanupEditorParity()")
cleanup_progress = method_body(bootstrap, "private void CleanupEditorProgress()")
require("CleanupEditorParity();" in unloaded,
        "CLEAN-01 Page.Unloaded owner must delegate parity/progress cleanup")
require("CleanupEditorProgress();" in cleanup_parity,
        "CLEAN-01 progress cleanup must be a subordinate lifecycle call, not an event owner")
require("StopVoiceArtifactMonitor();" in cleanup_progress,
        "CLEAN-01 progress cleanup must stop the voice artifact event owners")
require("_editorExportProgressTimer.Tick -= EditorExportProgressTimer_Tick;" in cleanup_progress,
        "CLEAN-01 progress cleanup must detach its timer owner")

# Startup smoke SizeChanged is conditional but must still have one named auditable owner.
require("WorkspaceGrid.SizeChanged += (_, _)" not in bootstrap,
        "CLEAN-01 anonymous WorkspaceGrid.SizeChanged owner must not return")
require("WorkspaceGrid.SizeChanged += ValidateUiShellLayoutForSmoke;" in bootstrap,
        "CLEAN-01 startup smoke SizeChanged must use the named layout-smoke owner")

# Non-UI infrastructure event sources have symmetric subscribe/unsubscribe owners.
for add, remove in (
    ("timer.Tick += ProjectSaveTimer_Tick;", "_projectSaveTimer.Tick -= ProjectSaveTimer_Tick;"),
    ("timer.Tick += EditorExportProgressTimer_Tick;", "_editorExportProgressTimer.Tick -= EditorExportProgressTimer_Tick;"),
    ("timer.Tick += VoiceArtifactMonitor_Tick;", "_voiceArtifactMonitorTimer.Tick -= VoiceArtifactMonitor_Tick;"),
    ("watcher.Deleted += VoiceArtifactWatcher_Deleted;", "_voiceArtifactWatcher.Deleted -= VoiceArtifactWatcher_Deleted;"),
    ("watcher.Renamed += VoiceArtifactWatcher_Renamed;", "_voiceArtifactWatcher.Renamed -= VoiceArtifactWatcher_Renamed;"),
):
    require(code.count(add) == 1, f"CLEAN-01 infrastructure subscribe owner drift: {add}")
    require(code.count(remove) == 1, f"CLEAN-01 infrastructure unsubscribe owner drift: {remove}")

for marker in (
    "player.PlaybackSession.PositionChanged += PlayerPositionChanged;",
    "player.MediaEnded += PlayerMediaEnded;",
    "player.MediaFailed += PlayerMediaFailed;",
):
    require(playback.count(marker) == 1, f"CLEAN-01 MediaPlayer owner must subscribe exactly once: {marker}")
for marker in (
    "player.PlaybackSession.PositionChanged -= PlayerPositionChanged;",
    "player.MediaEnded -= PlayerMediaEnded;",
    "player.MediaFailed -= PlayerMediaFailed;",
):
    require(playback.count(marker) == 1, f"CLEAN-01 MediaPlayer owner must unsubscribe exactly once: {marker}")
require(playback.count("RegisterPropertyChangedCallback(") == 1
        and playback.count("UnregisterPropertyChangedCallback(") == 1,
        "CLEAN-01 fullscreen property callback must have one register/unregister owner")

# Every active handler must have one implementation and must not call another event handler directly.
all_handlers = {handler for _, _, handler in bindings}
all_handlers.update(handler for _, _, handler in RUNTIME_UI_BINDINGS)
all_handlers.update(handler for _, _, handler in CONDITIONAL_RUNTIME_UI_BINDINGS)
all_handlers.update(handler for _, handler in LIFECYCLE_BINDINGS)
all_handlers.update((
    "ShellTool_Click",
    "ProjectSaveTimer_Tick",
    "EditorExportProgressTimer_Tick",
    "VoiceArtifactMonitor_Tick",
    "VoiceArtifactWatcher_Deleted",
    "VoiceArtifactWatcher_Renamed",
    "PlayerPositionChanged",
    "PlayerMediaEnded",
    "PlayerMediaFailed",
    "PreviewPlayerFullWindowChanged",
))
for handler in sorted(all_handlers):
    implementations_or_calls = len(re.findall(rf"\b{re.escape(handler)}\s*\(", code))
    require(implementations_or_calls == 1,
            f"CLEAN-01 event handler must have one implementation and no handler-to-handler calls: "
            f"{handler} ({implementations_or_calls})")

# These old methods are not Event Map owners. CLEAN-01 prevents either XAML or runtime
# bindings from silently handing authority back to them; source dead-code removal is separate.
bound_handlers = {handler for _, _, handler in bindings}
bound_handlers.update(handler for _, _, handler in RUNTIME_UI_BINDINGS + CONDITIONAL_RUNTIME_UI_BINDINGS)
bound_handlers.update(handler for _, handler in LIFECYCLE_BINDINGS)
bound_handlers.add("ShellTool_Click")
for legacy in LEGACY_NON_OWNERS:
    require(legacy not in bound_handlers,
            f"CLEAN-01 legacy non-owner became active again: {legacy}")
    require(not any(handler == legacy for _, _, handler in direct_runtime),
            f"CLEAN-01 legacy non-owner gained a runtime event subscription: {legacy}")

runtime_clicks = sum(1 for _, event, _ in RUNTIME_UI_BINDINGS if event == "Click") + len(TOOL_BUTTONS)
user_clicks = EXPECTED_XAML_CLICKS + runtime_clicks
require(user_clicks == EXPECTED_USER_CLICK_BINDINGS,
        f"CLEAN-01 user Click inventory drift: expected {EXPECTED_USER_CLICK_BINDINGS}, found {user_clicks}")

# Negative fixture: a second Unloaded owner is exactly the condition this gate rejects.
fixture = "Unloaded += EditorPage_Unloaded;\nUnloaded += Other_Unloaded;\n"
require(len(re.findall(r"(?<!\.)\bUnloaded\s*\+=", fixture)) == 2,
        "CLEAN-01 verifier fixture failed to represent duplicate lifecycle ownership")

print(
    "PASS: CLEAN-01 Editor Event Map · "
    f"{len(bindings)} XAML bindings · {len(RUNTIME_UI_BINDINGS) + len(CONDITIONAL_RUNTIME_UI_BINDINGS) + len(TOOL_BUTTONS)} runtime UI bindings · "
    f"{EXPECTED_USER_CLICK_BINDINGS} user Click bindings · one Loaded/LayoutUpdated/Unloaded owner each · "
    "symmetric timer/watcher/player event owners"
)
