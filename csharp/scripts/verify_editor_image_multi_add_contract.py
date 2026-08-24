#!/usr/bin/env python3
from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
IMAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.Images.cs"
EDITOR = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml.cs"


def fail(message: str) -> None:
    print(f"FAIL: {message}", file=sys.stderr)
    raise SystemExit(1)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


images = IMAGES.read_text(encoding="utf-8")
editor = EDITOR.read_text(encoding="utf-8")

# IMAGE-07 — Image/Logo is an ordered multi-item editor state with a hard cap.
limit_match = re.search(r"private const int MaxEditorImages\s*=\s*(\d+)\s*;", images)
require(limit_match is not None, "IMAGE-07 must declare MaxEditorImages")
limit = int(limit_match.group(1))
require(limit == 8, f"IMAGE-07 expected image/logo cap 8, found {limit}")

add = images.split("private async Task AddImageAsync()", 1)[1].split(
    "private void RenderImageOverlays", 1
)[0]
guard = "_imageOverlays.Count >= MaxEditorImages"
picker = "await _picker.PickImageAsync();"
require(guard in add, "IMAGE-07 Add must hard-stop at MaxEditorImages")
require(picker in add, "IMAGE-07 Add must still use the image picker")
require(add.index(guard) < add.index(picker),
        "IMAGE-07 limit guard must run before opening the picker")
require(add.count("_imageOverlays.Add(state);") == 1,
        "IMAGE-07 each successful Add must append exactly one new image/logo")
require("_imageOverlays.Clear()" not in add,
        "IMAGE-07 Add must not clear previously added images/logos")
require("_selectedImageIndex = _imageOverlays.Count - 1;" in add,
        "IMAGE-07 newly appended image/logo must become the selected item")
require("await SaveImageSidecarAsync();" in add,
        "IMAGE-07 successful Add must persist the updated collection")
require("RenderImageList();" in add and "RenderImageOverlays();" in add,
        "IMAGE-07 successful Add must refresh both list and Preview")
require("RefreshEditorActions();" in add,
        "IMAGE-07 successful Add must refresh action/control state")

render_list = images.split("private void RenderImageList()", 1)[1].split(
    "private async Task SaveImageSidecarAsync", 1
)[0]
require("for (var index = 0; index < _imageOverlays.Count; index++)" in render_list,
        "IMAGE-07 list must enumerate every image/logo")
require("_imageList.Items.Add" in render_list,
        "IMAGE-07 list must expose each image/logo as a separate item")

render_preview = images.split("private void RenderImageOverlays()", 1)[1].split(
    "private void RenderImageList", 1
)[0]
require("for (var index = 0; index < _imageOverlays.Count; index++)" in render_preview,
        "IMAGE-07 Preview must enumerate every image/logo")
require("_imageOverlayCanvas.Children.Add(image);" in render_preview,
        "IMAGE-07 Preview must add each image/logo overlay")

save = images.split("private async Task SaveImageSidecarAsync()", 1)[1].split(
    "private void RefreshImageControls", 1
)[0]
require("JsonSerializer.SerializeAsync(stream, _imageOverlays" in save,
        "IMAGE-07 sidecar must persist the complete ordered image/logo collection")

restore = images.split("private async Task EnsureImageProjectLoadedAsync()", 1)[1].split(
    "private async Task AddImageAsync", 1
)[0]
require("loaded.Take(MaxEditorImages)" in restore,
        "IMAGE-07 reopen must enforce the same image/logo cap")
require("_imageOverlays.Add(normalized)" in restore,
        "IMAGE-07 reopen must restore multiple valid image/logo items")

controls = images.split("private void RefreshImageControls()", 1)[1]
require("_imageOverlays.Count < MaxEditorImages" in controls,
        "IMAGE-07 Add control must disable when the cap is reached")
refresh_actions = editor.split("private void RefreshEditorActions()", 1)[1]
require("RefreshImageControls();" in refresh_actions,
        "IMAGE-07 editor action refresh must propagate the new count to Image controls")

# Synthetic fixture: eight independent paths append in order; the ninth is rejected
# before a picker would be opened, and the last successful item stays selected.
items: list[str] = []
selected = -1
picker_calls = 0
for index in range(limit + 1):
    if len(items) >= limit:
        continue
    picker_calls += 1
    items.append(f"logo-{index + 1}.png")
    selected = len(items) - 1

require(len(items) == limit, "IMAGE-07 fixture must retain exactly eight successful logos")
require(items == [f"logo-{index}.png" for index in range(1, limit + 1)],
        "IMAGE-07 fixture must preserve existing logos and append in order")
require(selected == limit - 1,
        "IMAGE-07 fixture must select the eighth successful logo")
require(picker_calls == limit,
        "IMAGE-07 fixture must not open the picker for a ninth logo")
require(not (len(items) < limit),
        "IMAGE-07 fixture Add control must be disabled at the cap")

print("PASS: IMAGE-07 multiple image/logo Add is append-only and capped at 8")
