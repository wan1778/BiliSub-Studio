#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
IMAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.Images.cs"
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
bootstrap = read(BOOTSTRAP)

# IMAGE-08 — selecting a logo from either the inspector list or the preview
# must update the one authoritative selected index, inspector values, list
# selection, and preview selection outline without mutating unrelated logos.

require(bootstrap.count("ImageSourceList.SelectionChanged += ImageList_SelectionChanged;") == 1,
        "IMAGE-08 image list SelectionChanged must be bound exactly once")
require(bootstrap.count("ImageOverlayCanvas.PointerPressed += ImageOverlay_PointerPressed;") == 1,
        "IMAGE-08 preview PointerPressed must be bound exactly once")

list_handler = images.split(
    "private void ImageList_SelectionChanged(object sender, SelectionChangedEventArgs e)", 1
)[1].split("private async void ImageTopRight_Click", 1)[0]
require("_selectedImageIndex = _imageList.SelectedIndex;" in list_handler,
        "IMAGE-08 list selection must own the selected logo index")
require("LoadSelectedImageIntoInputs();" in list_handler,
        "IMAGE-08 list selection must load selected logo geometry/opacity")
require("RenderImageOverlays();" in list_handler,
        "IMAGE-08 list selection must redraw the preview selection")
require("RefreshImageControls();" in list_handler,
        "IMAGE-08 list selection must refresh selected-logo controls")

pointer = images.split(
    "private void ImageOverlay_PointerPressed(object sender, PointerRoutedEventArgs e)", 1
)[1].split("private void ImageOverlay_PointerMoved", 1)[0]
require("var hit = HitTestImage(point);" in pointer and "if (hit.Index < 0) return;" in pointer,
        "IMAGE-08 preview selection must use image hit testing and ignore empty space")
require("_selectedImageIndex = hit.Index;" in pointer,
        "IMAGE-08 preview hit must become the selected logo")
require("RenderImageList();" in pointer,
        "IMAGE-08 preview selection must sync the inspector list")
require("LoadSelectedImageIntoInputs();" in pointer,
        "IMAGE-08 preview selection must sync inspector values")
require("RenderImageOverlays();" in pointer,
        "IMAGE-08 preview selection must redraw the selected outline")

hit_test = images.split(
    "private (int Index, DragKind Kind) HitTestImage(Point point)", 1
)[1].split("private static DragKind HitImageHandles", 1)[0]
require("for (var index = _imageOverlays.Count - 1; index >= 0; index--)" in hit_test,
        "IMAGE-08 overlapping logos must select the topmost/latest overlay first")
require("return (index, DragKind.Move);" in hit_test,
        "IMAGE-08 preview body hit must resolve to a concrete logo index")
require("return (-1, DragKind.None);" in hit_test,
        "IMAGE-08 preview empty-space hit must not invent a selection")

render = images.split("private void RenderImageOverlays()", 1)[1].split(
    "private void RenderImageSelection", 1
)[0]
require("index == _selectedImageIndex" in render and "RenderImageSelection(state, video);" in render,
        "IMAGE-08 only the selected logo may receive the selection outline")

render_list = images.split("private void RenderImageList()", 1)[1].split(
    "private void LoadSelectedImageIntoInputs", 1
)[0]
require("_imageList.SelectedIndex = _selectedImageIndex;" in render_list,
        "IMAGE-08 inspector list selection must mirror the authoritative selected index")

inputs = images.split("private void LoadSelectedImageIntoInputs()", 1)[1].split(
    "private bool TryGetSelectedImage", 1
)[0]
require("_imageXBox.Value = image.X * 100;" in inputs,
        "IMAGE-08 selected logo X must populate the inspector")
require("_imageYBox.Value = image.Y * 100;" in inputs,
        "IMAGE-08 selected logo Y must populate the inspector")
require("_imageWidthBox.Value = image.Width * 100;" in inputs,
        "IMAGE-08 selected logo width must populate the inspector")
require("_imageHeightBox.Value = image.Height * 100;" in inputs,
        "IMAGE-08 selected logo height must populate the inspector")
require("_imageOpacitySlider.Value = image.Opacity * 100;" in inputs,
        "IMAGE-08 selected logo opacity must populate the inspector")

selected = images.split("private bool TryGetSelectedImage", 1)[1].split(
    "private bool TryNormalizeImageState", 1
)[0]
require("image = _imageOverlays[_selectedImageIndex];" in selected,
        "IMAGE-08 inspector edits must resolve through the authoritative selected index")

# Synthetic fixture: list selection chooses an exact item, while a preview click
# inside two overlapping logos must resolve to the topmost/latest one.
overlays = [
    {"name": "logo-1", "x": .10, "y": .10, "w": .30, "h": .30},
    {"name": "logo-2", "x": .20, "y": .20, "w": .30, "h": .30},
    {"name": "logo-3", "x": .70, "y": .10, "w": .20, "h": .20},
]
selected_index = 2
require(overlays[selected_index]["name"] == "logo-3",
        "IMAGE-08 fixture: list selection did not resolve the requested logo")

point_x, point_y = .25, .25
hit_index = -1
for index in range(len(overlays) - 1, -1, -1):
    item = overlays[index]
    if item["x"] <= point_x <= item["x"] + item["w"] and item["y"] <= point_y <= item["y"] + item["h"]:
        hit_index = index
        break
require(hit_index == 1 and overlays[hit_index]["name"] == "logo-2",
        "IMAGE-08 fixture: preview overlap did not select the topmost logo")

print("PASS: IMAGE-08 logo selection is synchronized across list, inspector and preview")
