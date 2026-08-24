#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
IMAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.Images.cs"
CORE = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml.cs"
BOOTSTRAP = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.ParityBootstrap.cs"


def fail(message: str) -> None:
    print(f"FAIL: {message}", file=sys.stderr)
    raise SystemExit(1)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


images = read(IMAGES)
core = read(CORE)
bootstrap = read(BOOTSTRAP)

# IMAGE-09 — dragging a logo directly on the preview must use the selected
# image state, update its placement live, preserve its size for a body drag,
# keep the placement inside the video, and persist only when the drag commits.

for binding in (
    "ImageOverlayCanvas.PointerPressed += ImageOverlay_PointerPressed;",
    "ImageOverlayCanvas.PointerMoved += ImageOverlay_PointerMoved;",
    "ImageOverlayCanvas.PointerReleased += ImageOverlay_PointerReleased;",
):
    require(bootstrap.count(binding) == 1,
            f"IMAGE-09 preview drag binding must appear exactly once: {binding}")

pressed = images.split(
    "private void ImageOverlay_PointerPressed(object sender, PointerRoutedEventArgs e)", 1
)[1].split("private void ImageOverlay_PointerMoved", 1)[0]
require("_inspectorMode != InspectorMode.Image" in pressed,
        "IMAGE-09 direct drag must be owned by Image mode")
require("EditorBusy" in pressed and "_playback.IsPreviewMode" in pressed,
        "IMAGE-09 direct drag must stay disabled while editor/processed preview is busy")
require("var hit = HitTestImage(point);" in pressed and "if (hit.Index < 0) return;" in pressed,
        "IMAGE-09 drag must begin only from a concrete image hit")
require("_selectedImageIndex = hit.Index;" in pressed,
        "IMAGE-09 the directly hit logo must become the selected logo")
require("_imageDragKind = hit.Kind;" in pressed,
        "IMAGE-09 drag must preserve the hit-test drag kind")
require("_imageDragOriginal = _imageOverlays[hit.Index];" in pressed,
        "IMAGE-09 drag must snapshot the selected logo before moving")
require("_imageDragStart = normalized;" in pressed,
        "IMAGE-09 drag must retain the normalized pointer origin")
require("_imageOverlayCanvas.CapturePointer(e.Pointer);" in pressed,
        "IMAGE-09 direct drag must capture the pointer")

hit_test = images.split(
    "private (int Index, DragKind Kind) HitTestImage(Point point)", 1
)[1].split("private static DragKind HitImageHandles", 1)[0]
require("return (index, DragKind.Move);" in hit_test,
        "IMAGE-09 clicking the body of a logo must enter Move mode")

moved = images.split(
    "private void ImageOverlay_PointerMoved(object sender, PointerRoutedEventArgs e)", 1
)[1].split("private void ImageOverlay_PointerReleased", 1)[0]
require("IsLeftButtonPressed" in moved,
        "IMAGE-09 live movement must require the left pointer button")
require("TryNormalize(e.GetCurrentPoint(_imageOverlayCanvas).Position, out var current)" in moved,
        "IMAGE-09 pointer movement must use normalized video coordinates")
require("ResizeOrMove(" in moved and "_imageDragKind" in moved,
        "IMAGE-09 direct movement must use the shared placement geometry policy")
require("_imageOverlays[_selectedImageIndex] = original with" in moved,
        "IMAGE-09 live drag must mutate only the selected logo slot")
for field in ("X = placement.X", "Y = placement.Y", "Width = placement.Width", "Height = placement.Height"):
    require(field in moved,
            f"IMAGE-09 live drag must apply placement field: {field}")
require("LoadSelectedImageIntoInputs();" in moved,
        "IMAGE-09 inspector values must follow the live drag")
require("RenderImageOverlays();" in moved,
        "IMAGE-09 preview must redraw during the live drag")
require("SaveImageSidecarAsync" not in moved,
        "IMAGE-09 must not write the sidecar on every PointerMoved event")

geometry = core.split(
    "private static EditorSubtitlePlacement ResizeOrMove(EditorSubtitlePlacement original, Point start, Point current, DragKind kind)", 1
)[1].split("private bool TryNormalize(Point point, out Point normalized)", 1)[0]
require("if (kind == DragKind.Move)" in geometry,
        "IMAGE-09 shared placement helper must have a dedicated Move path")
require("X = Math.Clamp(original.X + dx, 0, 1 - original.Width)" in geometry,
        "IMAGE-09 direct horizontal movement must remain inside the video")
require("Y = Math.Clamp(original.Y + dy, 0, 1 - original.Height)" in geometry,
        "IMAGE-09 direct vertical movement must remain inside the video")

require("ImageOverlay_PointerReleased(object sender, PointerRoutedEventArgs e) => FinishImageDrag(e, commit: true);" in images,
        "IMAGE-09 releasing the pointer must commit the drag")
finish = images.split("private async void FinishImageDrag", 1)[1].split(
    "private (int Index, DragKind Kind) HitTestImage", 1
)[0]
require("_imageOverlayCanvas.ReleasePointerCapture(e.Pointer);" in finish,
        "IMAGE-09 completed drag must release pointer capture")
require("if (commit) await SaveImageSidecarAsync();" in finish,
        "IMAGE-09 committed direct drag must persist the new placement")
require("LoadSelectedImageIntoInputs();" in finish and "RenderImageOverlays();" in finish,
        "IMAGE-09 committed drag must leave inspector and preview synchronized")

# Synthetic fixture: move only logo-2, preserve its dimensions, and clamp it
# to the legal video area when the requested movement reaches an edge.
def clamp(value: float, minimum: float, maximum: float) -> float:
    return min(max(value, minimum), maximum)


def move(item: dict[str, float], start: tuple[float, float], current: tuple[float, float]) -> dict[str, float]:
    dx = current[0] - start[0]
    dy = current[1] - start[1]
    return {
        **item,
        "x": clamp(item["x"] + dx, 0.0, 1.0 - item["w"]),
        "y": clamp(item["y"] + dy, 0.0, 1.0 - item["h"]),
    }


overlays = [
    {"x": .10, "y": .10, "w": .20, "h": .15},
    {"x": .60, "y": .10, "w": .25, "h": .20},
]
untouched = dict(overlays[0])
original = dict(overlays[1])
overlays[1] = move(original, (.70, .20), (.40, .55))
require(overlays[0] == untouched,
        "IMAGE-09 fixture: dragging logo-2 mutated another logo")
require(abs(overlays[1]["x"] - .30) < 1e-12 and abs(overlays[1]["y"] - .45) < 1e-12,
        "IMAGE-09 fixture: direct drag delta was not applied")
require(overlays[1]["w"] == original["w"] and overlays[1]["h"] == original["h"],
        "IMAGE-09 fixture: body drag changed logo dimensions")

edge = move(original, (.70, .20), (1.0, 1.0))
require(abs(edge["x"] - (1.0 - original["w"])) < 1e-12,
        "IMAGE-09 fixture: horizontal drag was not clamped to the video edge")
require(abs(edge["y"] - (1.0 - original["h"])) < 1e-12,
        "IMAGE-09 fixture: vertical drag was not clamped to the video edge")

print("PASS: IMAGE-09 direct logo drag updates selected placement live and commits safely")
