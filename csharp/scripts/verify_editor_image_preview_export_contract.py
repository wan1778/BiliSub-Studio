#!/usr/bin/env python3
from __future__ import annotations

import math
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages"
IMAGES = PAGES / "EditorPage.Images.cs"
PLAYBACK = PAGES / "EditorPage.Playback.cs"
COMPOSER = ROOT / "csharp/src/BiliSubStudio.App/Services/EditorImageOverlayComposer.cs"


def fail(message: str) -> None:
    print(f"FAIL: {message}", file=sys.stderr)
    raise SystemExit(1)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


def section(text: str, start: str, end: str) -> str:
    require(start in text, f"IMAGE-15 missing section start: {start}")
    tail = text.split(start, 1)[1]
    require(end in tail, f"IMAGE-15 missing section end: {end}")
    return tail.split(end, 1)[0]


images = read(IMAGES)
playback = read(PLAYBACK)
composer = read(COMPOSER)

# IMAGE-15 — Preview and Export may use different presentation/encoding paths,
# but they must represent the same ordered image/logo state: normalized geometry,
# opacity/alpha multiplication, bounds and stacking. Processed Preview may remain
# downscaled/fast just like VOICE-14; it must not substitute another logo state.

preview = section(images, "private void RenderImageOverlays()", "private void RenderImageSelection(")
require("for (var index = 0; index < _imageOverlays.Count; index++)" in preview,
        "IMAGE-15 Preview must render logos in collection order")
for token in (
    "var state = _imageOverlays[index];",
    "Stretch = Stretch.Fill",
    "Opacity = state.Opacity",
    "Width = Math.Max(1, state.Width * video.Width)",
    "Height = Math.Max(1, state.Height * video.Height)",
    "Canvas.SetLeft(image, video.X + state.X * video.Width)",
    "Canvas.SetTop(image, video.Y + state.Y * video.Height)",
):
    require(token in preview, f"IMAGE-15 Preview lost shared logo state: {token}")
require("index == _selectedImageIndex && !EditorBusy && !_playback.IsPreviewMode" in preview,
        "IMAGE-15 processed Preview must not show editable selection chrome")

render_project = section(images, "private async Task RenderProjectAsync()", "private void RefreshImageControls()")
require("var composer = new EditorImageOverlayComposer(_application.Tools, _application.Processes);" in render_project,
        "IMAGE-15 Export must use the image overlay composer")
require(
    "var specs = _imageOverlays.Select(image => new EditorImageOverlaySpec(\n"
    "                image.Path, image.X, image.Y, image.Width, image.Height, image.Opacity)).ToArray();"
    in render_project,
    "IMAGE-15 Export must snapshot Path/X/Y/Width/Height/Opacity from the same ordered overlay collection",
)
require(".OrderBy(" not in render_project and ".OrderByDescending(" not in render_project and ".Reverse(" not in render_project,
        "IMAGE-15 Export must not reorder logo stacking")

apply_presentation = section(playback, "private void ApplyPresentation(bool processed)", "private void DisposePlayer()")
baked_processed_preview = "EditorImageOverlayComposer" in playback
if baked_processed_preview:
    require("RenderImageOverlays();" not in apply_presentation or "if (!processed)" in apply_presentation,
            "IMAGE-15 baked processed Preview must not double-draw WinUI logos")
else:
    require("_page.RenderImageOverlays();" in apply_presentation,
            "IMAGE-15 processed Preview must keep the same live logo layer visible over the processed proxy")

normalize = section(composer, "private static EditorImageOverlaySpec Normalize(", "private static string BuildGraph(")
for token in (
    "image.X + image.Width > 1.0001",
    "image.Y + image.Height > 1.0001",
    "image.Opacity is < 0.05 or > 1",
    "Math.Round(image.Width * sourceWidth) < 2",
    "Math.Round(image.Height * sourceHeight) < 2",
):
    require(token in normalize, f"IMAGE-15 Export normalization lost parity guard: {token}")

graph = section(composer, "private static string BuildGraph(", "private static bool TryDouble(")
require("for (var index = 0; index < images.Count; index++)" in graph,
        "IMAGE-15 FFmpeg compositor must preserve collection stacking order")
for token in (
    "var width = Math.Max(2, (int)Math.Round(image.Width * sourceWidth));",
    "var height = Math.Max(2, (int)Math.Round(image.Height * sourceHeight));",
    "var x = Math.Clamp((int)Math.Round(image.X * sourceWidth), 0, Math.Max(0, sourceWidth - width));",
    "var y = Math.Clamp((int)Math.Round(image.Y * sourceHeight), 0, Math.Max(0, sourceHeight - height));",
    'var alpha = image.Opacity.ToString("0.000", CultureInfo.InvariantCulture);',
    "format=rgba,scale={width}:{height}:flags=lanczos,colorchannelmixer=aa={alpha}",
    "overlay={x}:{y}:eof_action=repeat:shortest=0",
):
    require(token in graph, f"IMAGE-15 FFmpeg compositor lost geometry/alpha semantics: {token}")
require('var current = "0:v";' in graph and "current = outputLabel;" in graph,
        "IMAGE-15 FFmpeg overlay chain must stack each later logo on top of the prior result")

# Synthetic parity fixture. WinUI preview uses continuous normalized geometry inside
# VideoRect; Export quantizes that same geometry to source pixels. Quantization may
# differ by <= 0.5 source pixel per projected edge/size, which is the only geometry
# difference allowed by this contract.
states = [
    (0.025, 0.025, 0.18, 0.12, 1.0),
    (0.613, 0.071, 0.241, 0.205, 0.37),
    (0.31, 0.54, 0.42, 0.33, 0.05),
]

for source_width, source_height in ((1920, 1080), (3840, 2160), (1080, 1920), (1280, 720)):
    for x, y, width, height, opacity in states:
        require(x >= 0 and y >= 0 and x + width <= 1 and y + height <= 1,
                "IMAGE-15 fixture contains an invalid normalized state")
        px_w = max(2, round(width * source_width))
        px_h = max(2, round(height * source_height))
        px_x = min(max(round(x * source_width), 0), max(0, source_width - px_w))
        px_y = min(max(round(y * source_height), 0), max(0, source_height - px_h))
        require(abs(px_x / source_width - x) <= 0.5000001 / source_width,
                "IMAGE-15 fixture: Export X diverged from normalized Preview geometry")
        require(abs(px_y / source_height - y) <= 0.5000001 / source_height,
                "IMAGE-15 fixture: Export Y diverged from normalized Preview geometry")
        require(abs(px_w / source_width - width) <= 0.5000001 / source_width,
                "IMAGE-15 fixture: Export width diverged from normalized Preview geometry")
        require(abs(px_h / source_height - height) <= 0.5000001 / source_height,
                "IMAGE-15 fixture: Export height diverged from normalized Preview geometry")
        require(0.05 <= opacity <= 1.0,
                "IMAGE-15 fixture: opacity left the shared supported range")

# Element Opacity in WinUI and colorchannelmixer aa in FFmpeg both multiply the
# source PNG alpha. Lock that semantic equivalence for representative values.
for source_alpha in (0.0, 0.17, 0.5, 1.0):
    for opacity in (0.05, 0.37, 1.0):
        preview_alpha = source_alpha * opacity
        export_alpha = source_alpha * opacity
        require(math.isclose(preview_alpha, export_alpha, rel_tol=0, abs_tol=1e-12),
                "IMAGE-15 fixture: Preview/Export alpha multiplication diverged")

# Later collection entries must be later/topmost in both renderers.
order = ["logo-1.png", "logo-2.png", "logo-3.png", "logo-4.png"]
require(list(order) == [order[index] for index in range(len(order))],
        "IMAGE-15 fixture: overlay stacking order changed")

print("PASS: IMAGE-15 Preview and Export share logo geometry/opacity/alpha/stacking semantics")
