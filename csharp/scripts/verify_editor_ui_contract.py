#!/usr/bin/env python3
"""Source contract for the Phase D Editor UI upgrades; not a visual PASS."""
from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parents[2]
XAML = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml"
MAIN = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml.cs"
PARITY = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.ParityFixes.cs"


def fail(message: str) -> None:
    print(f"FAIL: {message}", file=sys.stderr)
    raise SystemExit(1)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


xaml = read(XAML)
main = read(MAIN)
parity = read(PARITY)

# UID-01: MASTER.md requires monospace for timecodes, progress figures, zoom and ETA.
for name in ("ClockText", "VoiceProgressPercent", "RenderEtaText", "TimelineZoomText"):
    element = re.search(rf'x:Name="{name}"[^>]*', xaml)
    require(element is not None, f"missing readout control: {name}")
    require('FontFamily="Consolas"' in element.group(),
            f"timecode/progress readout must use a monospace face: {name}")

# UID-02: long-running auto preview must expose inline state at the owning toggle.
require('x:Name="AutoCompositeBusyText"' in xaml
        and "AutomationProperties.LiveSetting=\"Polite\"" in xaml,
        "auto preview needs a polite inline status surface")
require('SetAutoCompositeBusyText("Đang cập nhật bản xem trước theo thay đổi mới...");' in parity
        and 'SetAutoCompositeBusyText("Bản xem trước đã cập nhật.");' in parity
        and 'SetAutoCompositeBusyText("Không tự cập nhật được preview: " + error.Message);' in parity,
        "auto preview working/success/error must be mirrored inline, not only in the footer")

# UID-03: region list must describe the task, and keep precision coordinates discoverable
# in a tooltip instead of making them the primary row text.
require("{Binding Display}" in xaml and "{Binding Details}" in xaml,
        "region list must bind a short task label plus a precision detail tooltip")
require("private sealed record RegionListItem(string Display, string Details);" in main
        and "RegionList.Items.Add(new RegionListItem(" in main
        and 'var scope = region.WholeVideo ? "toàn video" : $"{region.Start:0.0}-{region.End:0.0}s";' in main
        and 'var details = $"Vị trí trên khung: x={region.X:P0}' in main,
        "region list rows must show scope, not raw coordinates as the only task language")

# UID-04: direct selection must stay usable without a drag: arrow nudges a selected region,
# with a larger Shift step, and only outside text entry/preview mode.
region_shortcut = main.split("private bool HandleEditorRegionShortcut(KeyRoutedEventArgs e)", 1)[1]
region_shortcut = region_shortcut.split("private bool TryNudgeSelectedRegion", 1)[0]
require("if (e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down)" in region_shortcut,
        "Editor must accept arrow keys as a keyboard region-move alternative")
require("if (_inspectorMode != InspectorMode.Effect || EditorBusy || _playback.IsPreviewMode) return false;" in region_shortcut
        and "if (IsEditorTextInputFocused()) return false;" in region_shortcut,
        "arrow region nudge must respect editing, busy and preview ownership")
nudge = main.split("private bool TryNudgeSelectedRegion(", 1)[1].split("private void DocumentChanged", 1)[0]
require("var step = largeStep ? .02 : .005;" in nudge
        and "Math.Clamp(selected.X + stepX * step, 0, Math.Max(0, 1 - selected.Width))" in nudge
        and "Math.Clamp(selected.Y + stepY * step, 0, Math.Max(0, 1 - selected.Height))" in nudge,
        "arrow nudge must be bounded and keep the region inside the source frame")

# UID-05: beginner-first progressive disclosure: ASR mode selection is advanced and collapsed
# until the user opens it, while the primary voice workflow stays visible.
voice_panel = xaml.split('x:Name="VoiceInspectorPanel"', 1)[1].split('x:Name="ImageInspectorPanel"', 1)[0]
require('<StackPanel x:Name="VoiceAdvancedPanel" Spacing="8" Visibility="Collapsed">' in voice_panel
        and 'x:Name="AsrExecutionModeBox"' in voice_panel
        and 'x:Name="AnalyzeTimingButton"' in voice_panel,
        "ASR advanced controls must live behind a collapsed disclosure by default")
require('x:Name="VoiceAdvancedToggleButton"' in voice_panel
        and "private void VoiceAdvancedToggle_Click" in parity
        and "VoiceAdvancedPanel.Visibility = _voiceAdvancedExpanded ? Visibility.Visible : Visibility.Collapsed;" in parity,
        "ASR advanced disclosure needs an explicit named owner")

# UID-06: destructive/history actions stay compact and subordinate to the one primary CTA.
primary = xaml.split('x:Name="AddRegionButton"', 1)[0].split("<Button", 1)[-1]
require('x:Name="AddRegionButton"' in xaml and "AccentPrimaryBrush" in primary,
        "Add region must remain the single filled primary CTA")
for name in ("UndoButton", "RedoButton", "RemoveRegionButton"):
    element = re.search(rf'x:Name="{name}"[^>]*', xaml)
    require(element is not None and 'Padding="8,3"' in element.group() and 'Height="26"' in element.group(),
            f"secondary/destructive action must be compact and subordinate: {name}")

# UID-07: spacing/surface hierarchy comes before borders. The core Voice workflow must not
# be a bordered card nested inside another bordered card.
app_xaml = read(ROOT / "csharp/src/BiliSubStudio.App/App.xaml")
require('x:Key="SectionGroupStyle"' in app_xaml
        and "BorderThickness" not in app_xaml.split('x:Key="SectionGroupStyle"', 1)[1].split("</Style>", 1)[0],
        "section group style must rely on surface/spacing, not another border")
require('VoiceCuePreviewPanel" Style="{StaticResource SectionGroupStyle}"' in xaml,
        "voice cue preview panel must use the low-border section style")

print("PASS: UI-01..07 Editor state clarity, task language, keyboard alternatives and containment")
