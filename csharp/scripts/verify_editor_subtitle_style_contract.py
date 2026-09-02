#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
PAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages"
STYLE = ROOT / "csharp/src/BiliSubStudio.Core/Editor/EditorSubtitleStyle.cs"
STORE = ROOT / "csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs"
SERVICE = ROOT / "csharp/src/BiliSubStudio.Core/Editor/VideoEditorService.cs"
TESTS = ROOT / "csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs"


def require(condition: bool, message: str) -> None:
    if not condition:
        print("FAIL:", message, file=sys.stderr)
        raise SystemExit(1)


xaml = (PAGES / "EditorPage.xaml").read_text(encoding="utf-8")
editor = (PAGES / "EditorPage.xaml.cs").read_text(encoding="utf-8")
style = STYLE.read_text(encoding="utf-8")
store = STORE.read_text(encoding="utf-8")
service = SERVICE.read_text(encoding="utf-8")
tests = TESTS.read_text(encoding="utf-8")

for token in (
    'Text="TEXT EFFECT"',
    'x:Name="SubtitleFontBox"',
    'x:Name="SubtitleFontScaleBox"',
    'x:Name="SubtitleBoldButton"',
    'x:Name="SubtitleUnderlineButton"',
    'x:Name="SubtitleItalicButton"',
    'x:Name="SubtitleTextColorButton"',
    'x:Name="SubtitleTextColorPicker"',
    'x:Name="SubtitleStylePresetGrid"',
    'x:Key="SubtitleStyleTileButtonStyle"',
    '<Setter Property="HorizontalAlignment" Value="Stretch"/>',
    '<Setter Property="HorizontalContentAlignment" Value="Center"/>',
    '<Setter Property="MinWidth" Value="0"/>',
    '<Setter Property="Height" Value="46"/>',
    '<Setter Property="CornerRadius" Value="7"/>',
    'x:Name="SubtitleOutlineColorBox"',
    'x:Name="SubtitleOutlineWidthBox"',
    'x:Name="SubtitleBackgroundColorBox"',
    'x:Name="SubtitleBackgroundOpacityBox"',
    'x:Name="SubtitleBackgroundPaddingBox"',
    'x:Name="SubtitleShadowBox"',
    'Click="SubtitleStylePreset_Click"',
    'Tag="plain"',
    'Tag="pink"',
    'Tag="red"',
    'Tag="green"',
    'Tag="yellow-box"',
    'Tag="blue-box"',
    'Tag="cinema"',
    'Tag="black-box"',
    'Tag="light-box"',
):
    require(token in xaml, f"TEXTFX-01 UI or preset wiring lost: {token}")

for token in (
    "private EditorSubtitleStyle _subtitleStyle = EditorSubtitleStylePolicy.Default;",
    "_subtitleStyle = EditorSubtitleStylePolicy.Normalize(saved.Style);",
    "ApplySubtitleStyleToUi(_subtitleStyle);",
    "CommitSubtitleStyle(style",
    "SubtitleTextColorPicker_ColorChanged",
    "SubtitleStyleFormat_Changed",
    "SubtitleFontScaleBox.Value = style.FontScale * 100;",
    "SubtitleFontScaleBox.Value / 100",
    "button.IsHitTestVisible = subtitleStyleEditable;",
    "button.IsTabStop = subtitleStyleEditable;",
    "SubtitleStylePresetGrid.Opacity = subtitleStyleEditable ? 1 : .72;",
    "KaraokeToggle.IsOn, _subtitleStyle)",
    "_project.Subtitle?.TranslationPolicyKey,\n            _subtitleStyle)",
):
    require(token in editor, f"TEXTFX-01 Editor style owner or persistence lost: {token}")

require(editor.count("KaraokeToggle.IsOn, _subtitleStyle)") == 3,
        "TEXTFX-01 Preview, frame Preview and Export do not share one style")

for token in (
    "public sealed record EditorSubtitleStyle(",
    "public static EditorSubtitleStyle FromPreset",
    "public static EditorSubtitleStyle Normalize",
    "public static string ToAssColor",
    'string FontName = "Arial"',
    "bool Italic = false",
    "bool Underline = false",
    "double FontScale = 1",
):
    require(token in style, f"TEXTFX-01 style policy lost: {token}")

for token in (
    "public const int CurrentSchema = 7;",
    "EditorSubtitleStyle? Style = null",
    "Style = EditorSubtitleStylePolicy.Normalize(subtitle.Style)",
):
    require(token in store, f"TEXTFX-01 persisted project style lost: {token}")

for token in (
    'builder.Append("Style: VietsubBox,").Append(style.FontName)',
    "automaticFontSize * style.FontScale",
    "var italic = style.Italic ? -1 : 0;",
    "var underline = style.Underline ? -1 : 0;",
    "var foregroundLayer = style.BackgroundOpacity > .001 ? 1 : 0;",
    'builder.Append("Dialogue: 0,")',
    'builder.Append("Dialogue: ").Append(foregroundLayer)',
):
    require(token in service, f"TEXTFX-01 two-layer ASS renderer lost: {token}")

require("editor subtitle text effects persist and share ASS Preview Export rendering" in tests,
        "TEXTFX-01 executable contract test is missing")

print("PASS: TEXTFX-01 presets and custom effects share persisted ASS Preview/Export rendering")
