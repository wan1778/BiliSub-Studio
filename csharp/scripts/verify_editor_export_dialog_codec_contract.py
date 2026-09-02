#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
PAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages"
XAML = (PAGES / "EditorPage.xaml").read_text(encoding="utf-8")
DIALOG = (PAGES / "EditorPage.ExportDialog.cs").read_text(encoding="utf-8")
IMAGES = (PAGES / "EditorPage.Images.cs").read_text(encoding="utf-8")
SERVICE = (ROOT / "csharp/src/BiliSubStudio.Core/Editor/VideoEditorService.cs").read_text(encoding="utf-8")
POLICY = (ROOT / "csharp/src/BiliSubStudio.Core/Editor/EditorExportSettings.cs").read_text(encoding="utf-8")
COMPOSER = (ROOT / "csharp/src/BiliSubStudio.App/Services/EditorImageOverlayComposer.cs").read_text(encoding="utf-8")


def require(condition: bool, message: str) -> None:
    if not condition:
        print("FAIL:", message, file=sys.stderr)
        raise SystemExit(1)


for token in (
    'x:Name="ExportVideoDialog"',
    'Width="844"',
    'Content="Bắt đầu xuất" Click="ExportDialogStart_Click"',
    'Content="Hủy" Click="ExportDialogCancel_Click"',
    'x:Name="ExportDialogFileNameBox"',
    'x:Name="ExportDialogDirectoryBox"',
    'Content="H.264 (Tương thích)" Tag="h264"',
    'Content="H.265 (Nhẹ hơn)" Tag="hevc"',
    'x:Name="ExportDialogResolutionBox"',
    'x:Name="ExportDialogFpsBox"',
    'x:Name="ExportDialogGpuToggle"',
    'x:Name="ExportDialogAudioBitrateBox"',
):
    require(token in XAML, f"EXPORT-DIALOG UI lost: {token}")

require('ColumnDefinition Width="1.28*"' in XAML and
        'ColumnDefinition Width="0.92*"' in XAML and
        'ContentDialogMaxWidth' in XAML and
        'ExportCardBrush' in XAML,
        "EXPORT-DIALOG no longer follows the approved wide two-column mockup")

for token in (
    "ShowExportDialogAsync()",
    "ReadExportDialogSettings()",
    "new EditorOutputTarget(directory, fileName, settings)",
    "ExportVideoDialog.Hide()",
    "FileNamePolicy.Sanitize",
    "EditorExportPolicy.ResolveDimensions",
):
    require(token in DIALOG, f"EXPORT-DIALOG state owner lost: {token}")

render = IMAGES.split("private async Task RenderProjectAsync()", 1)[1].split(
    "private void RefreshImageControls()", 1
)[0]
require("outputTarget = await ShowExportDialogAsync();" in render,
        "EXPORT-DIALOG Render button bypassed the centered settings dialog")
require("Export = outputTarget.Settings" in render,
        "EXPORT-DIALOG direct final render lost selected settings")
require("exportSettings: outputTarget.Settings" in render,
        "EXPORT-DIALOG image/logo final render lost selected settings")

run = SERVICE.split("public async Task<VideoEditResult> RunAsync(", 1)[1].split(
    "private async Task ValidateRenderedOutputAsync", 1
)[0]
for token in (
    "EditorExportPolicy.BuildVideoGraph",
    "EditorExportPolicy.ResolveEncoderAsync",
    "BuildConfiguredRenderArguments",
    "exportGraph.Dimensions, export.Codec, export.FrameRate",
):
    require(token in run, f"EXPORT-DIALOG final service lost: {token}")

for token in (
    '"hevc_nvenc"',
    '"h264_nvenc"',
    '"libx265"',
    '"libx264"',
    '"-tag:v", "hvc1"',
    '"color=c=black:s=256x256:d=0.04"',
    'filters.Add("fps="',
    'filters.Add($"scale=',
):
    require(token in POLICY, f"EXPORT-DIALOG codec policy lost: {token}")

for token in (
    "EditorExportPolicy.BuildVideoGraph",
    "EditorExportPolicy.ResolveEncoderAsync",
    "EditorExportPolicy.BuildVideoEncoderArguments",
    "export.AudioBitrateKbps",
    "graph.Dimensions.Width, graph.Dimensions.Height",
):
    require(token in COMPOSER, f"EXPORT-DIALOG image/logo parity lost: {token}")

print("PASS: centered export dialog drives verified H.264/H.265 settings through direct and image/logo renders")
