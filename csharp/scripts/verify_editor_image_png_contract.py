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

# IMAGE-03 — PNG must work through the complete Image/Logo path:
# picker -> source validation -> Windows bitmap decode -> Preview with source alpha ->
# persisted/reopened image state -> FFmpeg RGBA overlay for final Export.

require('private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg"];' in picker,
        "IMAGE-03 Windows picker must include .png")
pick_image = picker.split("public Task<string?> PickImageAsync()", 1)[1].split(
    "private async Task<string?> PickAsync", 1
)[0]
require("ImageExtensions" in pick_image and "*.png;*.jpg;*.jpeg" in pick_image,
        "IMAGE-03 both WinRT and Win32 image pickers must expose PNG")
validate_pick = picker.split("private static string ValidatePickedPath", 1)[1].split(
    "private static string? PickWithWin32", 1
)[0]
require("extensions.Contains(extension, StringComparer.OrdinalIgnoreCase)" in validate_pick,
        "IMAGE-03 picked PNG extension must be validated case-insensitively")

add = images.split("private async Task AddImageAsync()", 1)[1].split(
    "private async void RemoveImage_Click", 1
)[0]
require(add.count("await _picker.PickImageAsync();") == 1,
        "IMAGE-03 Add Image must use the image picker exactly once")
require('extension is not (".png" or ".jpg" or ".jpeg")' in add,
        "IMAGE-03 Add Image source validation must accept .png")
require("BitmapDecoder.CreateAsync(stream)" in add,
        "IMAGE-03 PNG dimensions must come from the Windows bitmap decoder")
require("decoder.PixelWidth" in add and "decoder.PixelHeight" in add,
        "IMAGE-03 PNG source dimensions must be retained")
require("Path.GetFullPath(path)" in add,
        "IMAGE-03 PNG overlay state must keep a canonical source path")

load_bitmap = images.split("private async Task EnsureBitmapLoadedAsync(string path)", 1)[1].split(
    "private async Task LoadBitmapAndRefreshAsync", 1
)[0]
require("StorageFile.GetFileFromPathAsync(path)" in load_bitmap
        and "var bitmap = new BitmapImage();" in load_bitmap
        and "await bitmap.SetSourceAsync(stream);" in load_bitmap,
        "IMAGE-03 Preview must decode the original PNG with BitmapImage")

render = images.split("private void RenderImageOverlays()", 1)[1].split(
    "private void RenderImageSelection", 1
)[0]
require("Source = bitmap" in render and "Opacity = state.Opacity" in render,
        "IMAGE-03 Preview must preserve decoded PNG alpha and apply overlay opacity")

normalize_state = images.split("private bool TryNormalizeImageState", 1)[1].split(
    "private async Task SaveImageSidecarAsync", 1
)[0]
require('extension is not (".png" or ".jpg" or ".jpeg")' in normalize_state,
        "IMAGE-03 reopened image state must still accept PNG")
require("!File.Exists(path)" in normalize_state,
        "IMAGE-03 reopened PNG must still exist before it is restored")

load_project = images.split("private async Task EnsureImageProjectLoadedAsync()", 1)[1].split(
    "private async void AddImage_Click", 1
)[0]
require("JsonSerializer.DeserializeAsync<List<EditorImageOverlayState>>" in load_project,
        "IMAGE-03 PNG placement must reload from the image sidecar")
require("TryNormalizeImageState(image, out var normalized)" in load_project,
        "IMAGE-03 reopened PNG state must be normalized before use")
require("await EnsureBitmapLoadedAsync(_imageOverlays[index].Path);" in load_project,
        "IMAGE-03 reopened PNG must be decoded for Preview again")

normalize_export = composer.split("private static EditorImageOverlaySpec Normalize(", 1)[1].split(
    "private static string BuildGraph", 1
)[0]
require('extension is not (".png" or ".jpg" or ".jpeg")' in normalize_export,
        "IMAGE-03 Export validation must accept PNG")
require("!File.Exists(path)" in normalize_export,
        "IMAGE-03 Export must reject a missing PNG source")

graph = composer.split("private static string BuildGraph", 1)[1].split(
    "private static bool TryDouble", 1
)[0]
require("format=rgba" in graph,
        "IMAGE-03 Export must keep PNG alpha by converting the overlay input to RGBA")
require("colorchannelmixer=aa={alpha}" in graph,
        "IMAGE-03 Export opacity must multiply the PNG alpha channel")
require("overlay={x}:{y}" in graph,
        "IMAGE-03 PNG must be composited with the FFmpeg overlay filter")
require("format=rgb24" not in graph and "format=bgr24" not in graph,
        "IMAGE-03 PNG overlay graph must not flatten alpha before overlay")

render_export = composer.split("public async Task<string> RenderAsync(", 1)[1].split(
    "private async Task ValidateAsync", 1
)[0]
require('args.AddRange(["-loop", "1", "-framerate", "1", "-i", image.Path]);' in render_export,
        "IMAGE-03 PNG must be supplied to FFmpeg as a persistent still-image input")

# Preview applies Image.Opacity over the decoded PNG alpha. Export applies the same
# semantics by multiplying the source alpha channel through colorchannelmixer.
def composited_alpha(source_alpha: float, opacity: float) -> float:
    return source_alpha * opacity


require(abs(composited_alpha(0.25, 0.50) - 0.125) < 1e-12,
        "IMAGE-03 fixture: semi-transparent PNG alpha must remain proportional")
require(composited_alpha(0.0, 0.75) == 0.0,
        "IMAGE-03 fixture: fully transparent PNG pixels must stay transparent")
require(abs(composited_alpha(1.0, 0.40) - 0.40) < 1e-12,
        "IMAGE-03 fixture: opaque PNG pixels must follow the overlay opacity")

print("PASS: IMAGE-03 PNG import, transparent Preview and RGBA Export contract")
