#!/usr/bin/env python3
"""Kiểm tra thanh điều hướng 6 chức năng (Văn bản/Effect/Âm nhạc/Giọng nói/
Hình ảnh/Xuất): bố cục không chồng lấn ở mọi kích thước, đúng một tab Active,
phân biệt rõ Active/Hover/Focus."""
from __future__ import annotations

import sys
from pathlib import Path

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8")

ROOT = Path(__file__).resolve().parents[2]
PAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages"
XAML = PAGES / "EditorPage.xaml"
BOOTSTRAP = PAGES / "EditorPage.ParityBootstrap.cs"
EDITOR = PAGES / "EditorPage.xaml.cs"
LAYOUT = PAGES / "EditorPage.WorkspaceLayout.cs"

TABS = ("SubtitleModeButton", "EffectModeButton", "AudioModeButton",
        "VoiceModeButton", "ImageModeButton", "ExportModeButton")


def require(condition: bool, message: str) -> None:
    if not condition:
        print("FAIL:", message, file=sys.stderr)
        raise SystemExit(1)


def window(text: str, marker: str, length: int = 3600) -> str:
    index = text.find(marker)
    if index < 0:
        return ""
    return text[index:index + length]


for path in (XAML, BOOTSTRAP, EDITOR, LAYOUT):
    require(path.is_file(), f"NAV-00 the navigation owner is missing: {path}")
xaml = XAML.read_text(encoding="utf-8")
bootstrap = BOOTSTRAP.read_text(encoding="utf-8")
editor = EDITOR.read_text(encoding="utf-8")

# The strip under audit: the Details toolbar inside its pill.
pill = window(xaml, '<Border Background="#0F1724"', 16000)
require(pill != "", "NAV-00 the six-tool strip is missing from the XAML")
strip = pill.split("</ScrollViewer>", 1)[0]

# NAV-01: responsive layout. The tab CONTENT keeps its fixed design width (56px,
# six tabs of 56px plus 30px of strip padding need 336px), every COLUMN declares
# that same min width so the strip can never squeeze the tabs into each other
# (the old overlap bug at narrow windows), and the Details column floor matches
# the strip's real minimum so its content never overflows the panel either.
require(strip.count('<ColumnDefinition MinWidth="56"/>') == 6,
        "NAV-01 every tab column must declare a min width (no squeeze/overlap)")
require(strip.count('<Grid Width="56" VerticalAlignment="Center">') == 6,
        "NAV-01 every tab keeps its fixed 56px content so the strip does not"
        " reflow its labels and the columns spread evenly as designed")
require('x:Name="DetailsColumn" Width="1.22*" MinWidth="366"' in xaml,
        "NAV-01 the Details panel floor must cover the strip minimum"
        " (336 of tabs + 30 of padding) so tools never overflow it")
require('_detailsColumnMinWidth = 366' in (PAGES / "EditorPage.WorkspaceLayout.cs").read_text(encoding="utf-8"),
        "NAV-01 the Details drag clamp must agree with the panel floor")

# NAV-02: exactly one active tab, in sync with the open panel. Both state
# owners must drive ALL SIX tools (Voice used to be skipped, which left the
# Voice tab lit next to another open panel).
set_mode = window(editor, "private void SetInspectorMode(", 2400)
require(set_mode != "", "NAV-02 SetInspectorMode owner is missing")
for button in TABS:
    # Image keeps its cached field owner from the image tool lifecycle.
    token = "_imageModeButton.IsChecked" if button == "ImageModeButton" else f"{button}.IsChecked"
    require(token in set_mode,
            f"NAV-02 SetInspectorMode must drive {button} (stale active tab)")
require("VoiceModeButton.IsChecked = false;" in set_mode,
        "NAV-02 any non-voice mode must clear the Voice tab")
require("VoiceInspectorPanel.Visibility = Visibility.Collapsed;" in set_mode,
        "NAV-02 any non-voice mode must close the Voice panel")
select = window(bootstrap, "void SelectShellTool(", 2400)
require(select != "", "NAV-02 SelectShellTool owner is missing")
for button in TABS:
    require(f"{button}.IsChecked" in select,
            f"NAV-02 the shared tool owner must drive {button}")
for panel in ("Subtitle", "Effect", "Audio", "Voice", "Image", "Export"):
    require(f"{panel}InspectorPanel.Visibility" in select,
            f"NAV-02 the shared tool owner must own {panel}InspectorPanel")

# NAV-03: one navigation owner for the whole strip; no second Click path.
bind = window(bootstrap, "void BindStaticUiShell()", 2600)
require(bind != "", "NAV-03 BindStaticUiShell owner is missing")
require(bootstrap.count("toolButton.Click += ShellTool_Click;") == 1,
        "NAV-03 the strip must have exactly one shared Click owner")
for button in TABS:
    require(f"{button}.Click +=" not in bootstrap,
            f"NAV-03 {button} must not gain a second dedicated Click owner")
    require(f"{button}.GotFocus +=" not in bootstrap,
            f"NAV-03 {button} must not attach focus per button (shared owner)")
require("toolButton.GotFocus += ShellTool_GotFocus;" in bootstrap
        and "toolButton.LosingFocus += ShellTool_LostFocus;" in bootstrap,
        "NAV-03 the shared owner must drive the focus cue for every tool")

# NAV-04: active, hover and focus are three different states that cannot
# override each other: focus drops the system border and shows its own accent
# cue, hover stays neutral, active keeps the blue pill.
require(strip.count('Tag="FocusCue"') == 6,
        "NAV-04 every tab needs its own focus cue")
require('Setter Property="UseSystemFocusVisuals" Value="False"' in strip,
        "NAV-04 the system focus border must not masquerade as the active tool")
require('x:Key="ToggleButtonBackgroundPointerOver"' in strip
        and 'x:Key="ToggleButtonForegroundPointerOver"' in strip,
        "NAV-04 hover needs its own brushes, distinct from the checked pill")
require("SetShellToolFocusCue" in bootstrap,
        "NAV-04 the focus cue needs a real runtime owner")

# NAV-05: the invariant is pinned at startup smoke: exactly one active tab.
smoke = window(editor, "foreach (var mode in Enum.GetValues<InspectorMode>())", 1400)
require(smoke != "", "NAV-05 the startup smoke loop is missing")
require("activeTools" in smoke and "activeTools != 1" in smoke,
        "NAV-05 startup smoke must fail when the strip shows more than one active tab")

print("PASS: navigation strip reflows without overlap and keeps exactly one active tool")
