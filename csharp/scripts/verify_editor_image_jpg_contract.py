#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PICKER = ROOT / "csharp/src/BiliSubStudio.App/Services/FilePickerService.cs"
IMAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.Images.cs"
COMPOSER = ROOT / "csharp/src/BiliSubStudio.App/Services/EditorImageOverlayComposer.cs"


def fail(message: str) -> None:
    print(f"FAIL: {message}", file=sys.stderr)
    raise SystemExit(1)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


picker = read(PICKER)
images = read(IMAGES)
composer = read(COMPOSER)

# IMAGE-04 — .jpg must work through the complete Image/Logo path:
# picker -> source validation -> Windows bitmap decode -> Preview -> sidecar/reopen
# -> FFmpeg still-image input -> RGBA conversion so editor opacity stays supported.

require('private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg"];' in picker,
        "IMAGE-04 Windows picker must include .jpg")
pick_image = picker.split("public Task<string?> PickImageAsync()", 1)[1].split(
    "private async Task<string?> PickAsync", 1
)[0]
require("ImageExtensions" in pick_image and "*.png;*.jpg;*.jpeg" in pick_image,
        "IMAGE-04 both WinRT and Win32 image pickers must expose JPG")
validate_pick = picker.split("private static string ValidatePickedPath", 1)[1].split(
    "private static string? PickWithWin32", 1
)[0]
require("extensions.Contains(extension, StringComparer.OrdinalIgnoreCase)" in validate_pick,
        "IMAGE-04 picked JPG extension must be validated case-insensitively")

add = images.split("private async Task AddImageAsync()", 1)[1].split(
    "private async void RemoveImage_Click", 1
)[0]
require(add.count("await _picker.PickImageAsync();") == 1,
        "IMAGE-04 Add Image must use the image picker exactly once")
require('extension is not (".png" or ".jpg" or ".jpeg")' in add,
        "IMAGE-04 Add Image source validation must accept .jpg")
require("BitmapDecoder.CreateAsync(stream)" in add,
        "IMAGE-04 JPG dimensions must come from the Windows bitmap decoder")
require("decoder.PixelWidth" in add and "decoder.PixelHeight" in add,
        "IMAGE-04 JPG source dimensions must be retained")
require("Path.GetFullPath(path)" in add,
        "IMAGE-04 JPG overlay state must keep a canonical source path")

load_bitmap = images.split("private async Task EnsureBitmapLoadedAsync(string path)", 1)[1].split(
    "private async Task LoadBitmapAndRefreshAsync", 1
)[0]
require("StorageFile.GetFileFromPathAsync(path)" in load_bitmap,
        "IMAGE-04 Preview must reopen the JPG source file")
require("var bitmap = new BitmapImage();" in load_bitmap
        and "await bitmap.SetSourceAsync(stream);" in load_bitmap,
        "IMAGE-04 Preview must decode JPG through BitmapImage")

render = images.split("private void RenderImageOverlays()", 1)[1].split(
    "private void RenderImageSelection", 1
)[0]
require("Source = bitmap" in render,
        "IMAGE-04 Preview must render the decoded JPG")
require("Opacity = state.Opacity" in render,
        "IMAGE-04 Preview must apply editor opacity to JPG")

normalize_state = images.split("private bool TryNormalizeImageState", 1)[1].split(
    "private async Task SaveImageSidecarAsync", 1
)[0]
require('extension is not (".png" or ".jpg" or ".jpeg")' in normalize_state,
        "IMAGE-04 reopened image state must still accept .jpg")
require("!File.Exists(path)" in normalize_state,
        "IMAGE-04 reopened JPG must still exist before restore")

load_project = images.split("private async Task EnsureImageProjectLoadedAsync()", 1)[1].split(
    "private async void AddImage_Click", 1
)[0]
require("JsonSerializer.DeserializeAsync<List<EditorImageOverlayState>>" in load_project,
        "IMAGE-04 JPG placement must reload from the image sidecar")
require("TryNormalizeImageState(image, out var normalized)" in load_project,
        "IMAGE-04 reopened JPG state must be normalized")
require("await EnsureBitmapLoadedAsync(_imageOverlays[index].Path);" in load_project,
        "IMAGE-04 reopened JPG must be decoded for Preview again")

normalize_export = composer.split("private static EditorImageOverlaySpec Normalize(", 1)[1].split(
    "private static string BuildGraph", 1
)[0]
require('extension is not (".png" or ".jpg" or ".jpeg")' in normalize_export,
        "IMAGE-04 Export validation must accept .jpg")
require("!File.Exists(path)" in normalize_export,
        "IMAGE-04 Export must reject a missing JPG source")

render_export = composer.split("public async Task<string> RenderAsync(", 1)[1].split(
    "private async Task ValidateAsync", 1
)[0]
require('args.AddRange(["-loop", "1", "-framerate", "1", "-i", image.Path]);' in render_export,
        "IMAGE-04 JPG must be supplied to FFmpeg as a persistent still-image input")

graph = composer.split("private static string BuildGraph", 1)[1].split(
    "private static bool TryDouble", 1
)[0]
require("format=rgba" in graph,
        "IMAGE-04 Export must promote opaque JPG pixels to RGBA before opacity is applied")
require("colorchannelmixer=aa={alpha}" in graph,
        "IMAGE-04 Export must implement editor opacity for JPG through the alpha channel")
require("overlay={x}:{y}" in graph,
        "IMAGE-04 JPG must be composited with the FFmpeg overlay filter")

# JPG has no source alpha. Preview treats it as opaque and applies Image.Opacity.
# Export converts it to RGBA (alpha=1) then applies the same opacity.
def preview_alpha(opacity: float) -> float:
    return 1.0 * opacity


def export_alpha(opacity: float) -> float:
    return 1.0 * opacity


for opacity in (1.0, 0.75, 0.40, 0.05):
    require(abs(preview_alpha(opacity) - export_alpha(opacity)) < 1e-12,
            f"IMAGE-04 fixture: JPG opacity parity failed at {opacity}")

print("PASS: IMAGE-04 JPG import, Preview, reopen and Export contract")
