#!/usr/bin/env python3
from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
XAML = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml"
BOOTSTRAP = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.ParityBootstrap.cs"
EDITOR = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml.cs"
IMAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.Images.cs"
EVENT_MAP = ROOT / "docs/engineering/EDITOR_EVENT_MAP.md"


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
editor = read(EDITOR)
images = read(IMAGES)
event_map = read(EVENT_MAP)

# IMAGE-01 — Image/Logo must live in the same Editor tool-state owner as
# Subtitle / Blur / Audio / Voice / Export. Static XAML owns the controls;
# one guarded shell binding owns navigation; Image must not introduce a
# second Click owner or a separately inserted visual tree.

for control, tag in (
    ("SubtitleModeButton", "Subtitle"),
    ("BlurModeButton", "Blur"),
    ("AudioModeButton", "Audio"),
    ("VoiceModeButton", "Voice"),
    ("ImageModeButton", "Image"),
    ("ExportModeButton", "Export"),
):
    require(xaml.count(f'x:Name="{control}"') == 1,
            f"IMAGE-01 requires exactly one static {control}")
    require(f'Tag="{tag}"' in xaml,
            f"IMAGE-01 {control} lost tool-state tag {tag}")

require(xaml.count('x:Name="ImageInspectorPanel"') == 1,
        "IMAGE-01 requires exactly one static ImageInspectorPanel")
require(xaml.count('x:Name="DetailsPanelHost"') == 1,
        "IMAGE-01 requires one shared DetailsPanelHost")
details = xaml.split('x:Name="DetailsPanelHost"', 1)[1]
require(details.index('x:Name="ImageInspectorPanel"') >= 0,
        "IMAGE-01 ImageInspectorPanel must live in the shared Details host")

loaded = bootstrap.split("private void EditorPage_Loaded", 1)[1].split(
    "void BindStaticUiShell()", 1
)[0]
require("_editorCoreInitialized" in loaded and "BindStaticUiShell();" in loaded,
        "IMAGE-01 shell binding must remain guarded by the Editor core lifecycle")

bind = bootstrap.split("void BindStaticUiShell()", 1)[1].split(
    "void ShellTool_Click", 1
)[0]
for control in (
    "SubtitleModeButton",
    "BlurModeButton",
    "AudioModeButton",
    "VoiceModeButton",
    "ImageModeButton",
    "ExportModeButton",
):
    require(control in bind,
            f"IMAGE-01 shared tool-selector binding lost {control}")
require("toolButton.Click += ShellTool_Click;" in bind,
        "IMAGE-01 all tool buttons must share ShellTool_Click")
require("ImageModeButton.Click +=" not in bind,
        "IMAGE-01 Image must not gain a second dedicated Click owner")

shell_click = bootstrap.split("void ShellTool_Click", 1)[1].split(
    "void SelectShellTool", 1
)[0]
require("SelectShellTool(tag)" in shell_click,
        "IMAGE-01 ShellTool_Click must route tags to the shared state owner")

select = bootstrap.split("void SelectShellTool", 1)[1].split(
    "void SyncShellPlayerControls", 1
)[0]
require('var image = string.Equals(tag, "Image", StringComparison.OrdinalIgnoreCase);' in select,
        "IMAGE-01 shared tool state must recognize Image")
require("image ? InspectorMode.Image" in select,
        "IMAGE-01 Image must map into the core InspectorMode state")
for token in (
    "SubtitleModeButton.IsChecked = subtitle;",
    "BlurModeButton.IsChecked = blur;",
    "AudioModeButton.IsChecked = audio;",
    "VoiceModeButton.IsChecked = voice;",
    "ImageModeButton.IsChecked = image;",
    "ExportModeButton.IsChecked = export;",
    "SubtitleInspectorPanel.Visibility = subtitle ? Visibility.Visible : Visibility.Collapsed;",
    "BlurInspectorPanel.Visibility = blur ? Visibility.Visible : Visibility.Collapsed;",
    "AudioInspectorPanel.Visibility = audio ? Visibility.Visible : Visibility.Collapsed;",
    "VoiceInspectorPanel.Visibility = voice ? Visibility.Visible : Visibility.Collapsed;",
    "ImageInspectorPanel.Visibility = image ? Visibility.Visible : Visibility.Collapsed;",
    "ExportInspectorPanel.Visibility = export ? Visibility.Visible : Visibility.Collapsed;",
):
    require(token in select, f"IMAGE-01 shared tool state lost: {token}")

require("private enum InspectorMode" in editor and "Image" in editor.split(
    "private enum InspectorMode", 1
)[1].split("}", 1)[0],
        "IMAGE-01 core InspectorMode must include Image")

require("_inspectorMode != InspectorMode.Image" in images,
        "IMAGE-01 image pointer editing must be gated by the shared InspectorMode")
require("_inspectorMode == InspectorMode.Image" in images,
        "IMAGE-01 selected image overlay must follow shared InspectorMode")
require("new ToggleButton" not in images and "new StackPanel" not in images,
        "IMAGE-01 Image must not dynamically build a second tool button/panel")
ensure = images.split("private void EnsureImageFeatureInitialized()", 1)[1].split(
    "private async Task EnsureImageProjectLoadedAsync", 1
)[0]
require(".Children.Add" not in ensure and "Content =" not in ensure,
        "IMAGE-01 image initialization must stay state-only and never insert UI")

require("| `ImageModeButton` | runtime | `ShellTool_Click` | 1 |" in event_map,
        "IMAGE-01 event map must record Image under the shared ShellTool owner")
require("six tool buttons\n  -> ShellTool_Click\n  -> SelectShellTool\n  -> exactly one Details panel visible" in event_map,
        "IMAGE-01 event map must preserve the single tool-state call map")

# Small semantic fixture for the exact one-panel selection invariant.
tools = ("Subtitle", "Blur", "Audio", "Voice", "Image", "Export")
for selected in tools:
    visible = [tool for tool in tools if tool == selected]
    require(len(visible) == 1 and visible[0] == selected,
            f"IMAGE-01 fixture failed selecting {selected}")

print("PASS: IMAGE-01 Image/Logo uses the shared Editor tool state")
