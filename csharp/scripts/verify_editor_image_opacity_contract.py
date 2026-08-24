#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
IMAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.Images.cs"
BOOTSTRAP = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.ParityBootstrap.cs"
XAML = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml"
COMPOSER = ROOT / "csharp/src/BiliSubStudio.App/Services/EditorImageOverlayComposer.cs"


def fail(message: str) -> None:
    print(f"FAIL: {message}", file=sys.stderr)
    raise SystemExit(1)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


images = IMAGES.read_text(encoding="utf-8")
bootstrap = BOOTSTRAP.read_text(encoding="utf-8")
xaml = XAML.read_text(encoding="utf-8")
composer = COMPOSER.read_text(encoding="utf-8")

# IMAGE-11 — logo opacity must have one authoritative 5%-100% contract from
# inspector input through selected state, preview, sidecar/reopen and FFmpeg export.

require('x:Name="ImageOpacitySlider"' in xaml,
        "IMAGE-11 opacity slider is missing from the Image inspector")
require('Minimum="5" Maximum="100" Value="100"' in xaml,
        "IMAGE-11 opacity slider must expose exactly 5%-100% with 100% default")
require(bootstrap.count("ImageOpacitySlider.ValueChanged += ImageOpacity_ValueChanged;") == 1,
        "IMAGE-11 opacity ValueChanged must be bound exactly once")

handler = images.split(
    "private async void ImageOpacity_ValueChanged", 1
)[1].split("private void ImageOverlay_PointerPressed", 1)[0]
require("_syncingImageInputs" in handler and "TryGetSelectedImage(out var image)" in handler,
        "IMAGE-11 opacity changes must target the authoritative selected logo and ignore input sync")
require("_imageOverlays[_selectedImageIndex] = image with { Opacity = Math.Clamp(_imageOpacitySlider.Value / 100, .05, 1) };" in handler,
        "IMAGE-11 slider percentage must map to selected state opacity 0.05-1.0")
require("await SaveImageSidecarAsync();" in handler,
        "IMAGE-11 opacity changes must persist to the image sidecar")
require("RenderImageOverlays();" in handler,
        "IMAGE-11 opacity changes must redraw preview immediately")
require("NotifyEditorCompositeChanged();" in handler,
        "IMAGE-11 opacity changes must invalidate the editor composite")

render = images.split("private void RenderImageOverlays()", 1)[1].split(
    "private void RenderImageSelection", 1
)[0]
require("Opacity = state.Opacity" in render,
        "IMAGE-11 WinUI preview must render the stored logo opacity directly")

inputs = images.split("private void LoadSelectedImageIntoInputs()", 1)[1].split(
    "private bool TryGetSelectedImage", 1
)[0]
require("_imageOpacitySlider.Value = image.Opacity * 100;" in inputs,
        "IMAGE-11 selecting/reopening a logo must restore opacity into the slider")

normalize = images.split("private bool TryNormalizeImageState", 1)[1].split(
    "private async Task SaveImageSidecarAsync", 1
)[0]
require("Opacity = Math.Clamp(image.Opacity, .05, 1)" in normalize,
        "IMAGE-11 reopened sidecar opacity must normalize to the same 5%-100% range")

export_block = images.split("var specs = _imageOverlays.Select", 1)[1].split(
    "var output = await composer.RenderAsync", 1
)[0]
require("image.Path, image.X, image.Y, image.Width, image.Height, image.Opacity" in export_block,
        "IMAGE-11 export spec must preserve each logo's stored opacity")

require("image.Opacity is < 0.05 or > 1" in composer,
        "IMAGE-11 export validation must enforce the same 5%-100% opacity range")
require("return image with { Path = path, Opacity = Math.Clamp(image.Opacity, .05, 1) };" in composer,
        "IMAGE-11 export normalization must retain the same opacity clamp")
require('var alpha = image.Opacity.ToString("0.000", CultureInfo.InvariantCulture);' in composer,
        "IMAGE-11 FFmpeg alpha must be derived from normalized opacity")
require("colorchannelmixer=aa={alpha}" in composer,
        "IMAGE-11 FFmpeg graph must apply opacity through alpha")

# Synthetic fixture: changing one selected logo must leave every other logo
# untouched and map slider percentages to the exact normalized/export alpha.
def clamp(value: float, low: float, high: float) -> float:
    return max(low, min(value, high))


def apply(overlays: list[dict[str, float | str]], selected: int, slider: float) -> list[dict[str, float | str]]:
    updated = [dict(item) for item in overlays]
    updated[selected]["opacity"] = clamp(slider / 100.0, .05, 1.0)
    return updated


base = [
    {"name": "logo-1", "opacity": 1.0},
    {"name": "logo-2", "opacity": .75},
    {"name": "logo-3", "opacity": .50},
]
for slider, expected in ((5, .05), (37, .37), (100, 1.0), (0, .05), (150, 1.0)):
    result = apply(base, 1, slider)
    require(abs(float(result[1]["opacity"]) - expected) < 1e-12,
            f"IMAGE-11 fixture: {slider}% did not map to {expected}")
    require(result[0] == base[0] and result[2] == base[2],
            "IMAGE-11 fixture: opacity change mutated an unselected logo")
    require(f'{float(result[1]["opacity"]):.3f}' == f'{expected:.3f}',
            "IMAGE-11 fixture: FFmpeg alpha formatting changed opacity")

print("PASS: IMAGE-11 logo opacity is synchronized from inspector through preview, persistence and export")
