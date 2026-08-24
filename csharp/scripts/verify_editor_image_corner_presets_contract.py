#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
BOOTSTRAP = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.ParityBootstrap.cs"
CORNER = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.Images.CornerPresets.cs"
XAML = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml"


def fail(message: str) -> None:
    print(f"FAIL: {message}", file=sys.stderr)
    raise SystemExit(1)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


bootstrap = BOOTSTRAP.read_text(encoding="utf-8")
corner = CORNER.read_text(encoding="utf-8")
xaml = XAML.read_text(encoding="utf-8")

# IMAGE-12 — retained top-left / top-right presets must have one authoritative
# bounded handler. Large logos must never be pushed outside the video merely by
# applying a corner preset.

require(xaml.count('x:Name="ImageTopLeftButton"') == 1,
        "IMAGE-12 top-left preset button must exist exactly once")
require(xaml.count('x:Name="ImageTopRightButton"') == 1,
        "IMAGE-12 top-right preset button must exist exactly once")
require('x:Name="ImageTopLeftButton" Content="Góc trái"' in xaml,
        "IMAGE-12 top-left preset label changed unexpectedly")
require('x:Name="ImageTopRightButton" Content="Góc phải"' in xaml,
        "IMAGE-12 top-right preset label changed unexpectedly")

require(bootstrap.count("ImageTopLeftButton.Click += ImageCornerPreset_Click;") == 1,
        "IMAGE-12 top-left preset must bind the bounded handler exactly once")
require(bootstrap.count("ImageTopRightButton.Click += ImageCornerPreset_Click;") == 1,
        "IMAGE-12 top-right preset must bind the bounded handler exactly once")
require("ImageTopLeftButton.Click += ImageTopLeft_Click;" not in bootstrap,
        "IMAGE-12 unsafe legacy top-left handler must not remain bound")
require("ImageTopRightButton.Click += ImageTopRight_Click;" not in bootstrap,
        "IMAGE-12 legacy right handler must not remain bound separately")

require("if (!TryGetSelectedImage(out var image) || EditorBusy) return;" in corner,
        "IMAGE-12 preset handler must require a valid editable selected logo")
require("ReferenceEquals(sender, ImageTopLeftButton)" in corner,
        "IMAGE-12 handler must identify the left preset explicitly")
require("ReferenceEquals(sender, ImageTopRightButton)" in corner,
        "IMAGE-12 handler must identify the right preset explicitly")
require("var maxX = Math.Max(0, 1 - image.Width);" in corner,
        "IMAGE-12 preset must derive the maximum legal X from logo width")
require("var maxY = Math.Max(0, 1 - image.Height);" in corner,
        "IMAGE-12 preset must derive the maximum legal Y from logo height")
require("var preferredX = right ? 1 - image.Width - .025 : .025;" in corner,
        "IMAGE-12 preset must retain the normal 2.5% corner inset")
require("X = Math.Clamp(preferredX, 0, maxX)" in corner,
        "IMAGE-12 horizontal preset position must be clamped inside video")
require("Y = Math.Min(.025, maxY)" in corner,
        "IMAGE-12 vertical preset position must be clamped inside video")
require("Width =" not in corner and "Height =" not in corner and "Opacity =" not in corner,
        "IMAGE-12 corner presets must not resize or change opacity")
require("_imageOverlays[_selectedImageIndex] = image;" in corner,
        "IMAGE-12 preset must update only the selected logo slot")
require("await SaveImageSidecarAsync();" in corner,
        "IMAGE-12 preset placement must persist to sidecar")
require("LoadSelectedImageIntoInputs();" in corner and "RenderImageOverlays();" in corner,
        "IMAGE-12 preset must refresh inspector and preview")
require("NotifyEditorCompositeChanged();" in corner,
        "IMAGE-12 preset must invalidate composite preview consistently")


def place(width: float, height: float, right: bool) -> tuple[float, float]:
    max_x = max(0.0, 1.0 - width)
    max_y = max(0.0, 1.0 - height)
    preferred_x = 1.0 - width - .025 if right else .025
    x = min(max(preferred_x, 0.0), max_x)
    y = min(.025, max_y)
    return x, y


for width in (.02, .18, .45, .90, 1.0):
    for height in (.02, .18, .45, .90, 1.0):
        for right in (False, True):
            x, y = place(width, height, right)
            require(x >= -1e-12 and y >= -1e-12,
                    "IMAGE-12 fixture: preset generated a negative coordinate")
            require(x + width <= 1.0000001,
                    "IMAGE-12 fixture: preset pushed logo outside video horizontally")
            require(y + height <= 1.0000001,
                    "IMAGE-12 fixture: preset pushed logo outside video vertically")

left_x, left_y = place(.18, .18, False)
right_x, right_y = place(.18, .18, True)
require(abs(left_x - .025) < 1e-12 and abs(left_y - .025) < 1e-12,
        "IMAGE-12 fixture: normal logo did not land at top-left inset")
require(abs(right_x - .795) < 1e-12 and abs(right_y - .025) < 1e-12,
        "IMAGE-12 fixture: normal logo did not land at top-right inset")

print("PASS: IMAGE-12 corner presets stay inside video for normal and oversized logos")
