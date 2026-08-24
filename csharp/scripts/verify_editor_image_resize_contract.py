#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
IMAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.Images.cs"


def fail(message: str) -> None:
    print(f"FAIL: {message}", file=sys.stderr)
    raise SystemExit(1)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


images = IMAGES.read_text(encoding="utf-8")

# IMAGE-10 — direct logo resize must use all eight preview handles, keep the
# resized logo inside the video, and share the same 2% minimum geometry contract
# used by numeric image inputs / sidecar validation. It must not inherit the
# subtitle placement's larger 5% x 4% resize floor.

pointer = images.split(
    "private void ImageOverlay_PointerMoved(object sender, PointerRoutedEventArgs e)", 1
)[1].split("private void ImageOverlay_PointerReleased", 1)[0]
require("ResizeImagePlacement(original, _imageDragStart.Value, current, _imageDragKind)" in pointer,
        "IMAGE-10 image PointerMoved must use the image-specific resize policy")
require("LoadSelectedImageIntoInputs();" in pointer and "RenderImageOverlays();" in pointer,
        "IMAGE-10 resize must update inspector values and preview live")
require("SaveImageSidecarAsync" not in pointer,
        "IMAGE-10 PointerMoved must not persist sidecar on every mouse move")

handles = images.split(
    "private static DragKind HitImageHandles(Point point, EditorImageOverlayState image, Rect video)", 1
)[1].split("private static EditorSubtitlePlacement ResizeImagePlacement", 1)[0]
for kind in ("NorthWest", "NorthEast", "SouthWest", "SouthEast", "North", "South", "West", "East"):
    require(f"DragKind.{kind}" in handles, f"IMAGE-10 missing {kind} resize handle")

resize = images.split(
    "private static EditorSubtitlePlacement ResizeImagePlacement", 1
)[1].split("private void RenderImageOverlays()", 1)[0]
require("const double minimum = .02;" in resize,
        "IMAGE-10 image resize minimum must be exactly 2%")
require("Math.Clamp(original.X + dx, 0, 1 - original.Width)" in resize,
        "IMAGE-10 image move must remain bounded horizontally")
require("Math.Clamp(original.Y + dy, 0, 1 - original.Height)" in resize,
        "IMAGE-10 image move must remain bounded vertically")
require("Math.Clamp(x1 + dx, 0, x2 - minimum)" in resize,
        "IMAGE-10 west resize must respect left edge and 2% minimum")
require("Math.Clamp(x2 + dx, x1 + minimum, 1)" in resize,
        "IMAGE-10 east resize must respect right edge and 2% minimum")
require("Math.Clamp(y1 + dy, 0, y2 - minimum)" in resize,
        "IMAGE-10 north resize must respect top edge and 2% minimum")
require("Math.Clamp(y2 + dy, y1 + minimum, 1)" in resize,
        "IMAGE-10 south resize must respect bottom edge and 2% minimum")

geometry = images.split(
    "private async void ImageGeometry_ValueChanged", 1
)[1].split("private async void ImageOpacity_ValueChanged", 1)[0]
require("width < .02 || height < .02" in geometry,
        "IMAGE-10 numeric image geometry must keep the same 2% minimum")

finish = images.split(
    "private async void FinishImageDrag", 1
)[1].split("private (int Index, DragKind Kind) HitTestImage", 1)[0]
require("if (commit) await SaveImageSidecarAsync();" in finish,
        "IMAGE-10 resize must persist only when the pointer interaction commits")
require("if (!commit && _imageDragOriginal is not null" in finish,
        "IMAGE-10 canceled resize must restore the pre-drag image state")


def clamp(value: float, low: float, high: float) -> float:
    return max(low, min(value, high))


def resize_fixture(original: tuple[float, float, float, float], start: tuple[float, float], current: tuple[float, float], kind: str) -> tuple[float, float, float, float]:
    x, y, width, height = original
    dx = current[0] - start[0]
    dy = current[1] - start[1]
    if kind == "Move":
        return clamp(x + dx, 0, 1 - width), clamp(y + dy, 0, 1 - height), width, height
    minimum = .02
    x1, y1, x2, y2 = x, y, x + width, y + height
    if kind in {"West", "NorthWest", "SouthWest"}:
        x1 = clamp(x1 + dx, 0, x2 - minimum)
    if kind in {"East", "NorthEast", "SouthEast"}:
        x2 = clamp(x2 + dx, x1 + minimum, 1)
    if kind in {"North", "NorthEast", "NorthWest"}:
        y1 = clamp(y1 + dy, 0, y2 - minimum)
    if kind in {"South", "SouthEast", "SouthWest"}:
        y2 = clamp(y2 + dy, y1 + minimum, 1)
    return x1, y1, x2 - x1, y2 - y1


original = (.25, .25, .30, .30)
shrink_cases = {
    "West": ((.25, .40), (.99, .40)),
    "East": ((.55, .40), (-.99, .40)),
    "North": ((.40, .25), (.40, .99)),
    "South": ((.40, .55), (.40, -.99)),
    "NorthWest": ((.25, .25), (.99, .99)),
    "NorthEast": ((.55, .25), (-.99, .99)),
    "SouthWest": ((.25, .55), (.99, -.99)),
    "SouthEast": ((.55, .55), (-.99, -.99)),
}
for kind, (start, current) in shrink_cases.items():
    x, y, width, height = resize_fixture(original, start, current, kind)
    require(x >= -1e-9 and y >= -1e-9 and x + width <= 1 + 1e-9 and y + height <= 1 + 1e-9,
            f"IMAGE-10 fixture: {kind} escaped video bounds")
    require(width >= .02 - 1e-9 and height >= .02 - 1e-9,
            f"IMAGE-10 fixture: {kind} shrank below 2%")

left = resize_fixture(original, (.25, .40), (-.50, .40), "West")
right = resize_fixture(original, (.55, .40), (1.50, .40), "East")
top = resize_fixture(original, (.40, .25), (.40, -.50), "North")
bottom = resize_fixture(original, (.40, .55), (.40, 1.50), "South")
require(abs(left[0]) < 1e-9, "IMAGE-10 fixture: west resize did not clamp to left edge")
require(abs(right[0] + right[2] - 1) < 1e-9, "IMAGE-10 fixture: east resize did not clamp to right edge")
require(abs(top[1]) < 1e-9, "IMAGE-10 fixture: north resize did not clamp to top edge")
require(abs(bottom[1] + bottom[3] - 1) < 1e-9, "IMAGE-10 fixture: south resize did not clamp to bottom edge")

print("PASS: IMAGE-10 direct logo resize uses eight handles, 2% minimum and video bounds")
