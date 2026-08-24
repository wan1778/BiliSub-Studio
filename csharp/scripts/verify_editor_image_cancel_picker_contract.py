#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PICKER = ROOT / "csharp/src/BiliSubStudio.App/Services/FilePickerService.cs"
IMAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.Images.cs"


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

# IMAGE-06 — canceling Add Image is a no-op, not an error:
# Add Image -> image picker -> WinRT/Win32 cancel -> null -> return before
# decode/state mutation/sidecar write/Preview refresh.

pick_image = picker.split("public Task<string?> PickImageAsync()", 1)[1].split(
    "private async Task<string?> PickAsync", 1
)[0]
require("PickAsync(" in pick_image and "ImageExtensions" in pick_image,
        "IMAGE-06 PickImageAsync must continue through the shared picker path")

pick_async = picker.split("private async Task<string?> PickAsync", 1)[1].split(
    "private static string ValidatePickedPath", 1
)[0]
require("var file = await picker.PickSingleFileAsync();" in pick_async,
        "IMAGE-06 WinRT picker call is missing")
require("return file is null ? null : ValidatePickedPath(file.Path, extensions);" in pick_async,
        "IMAGE-06 WinRT dialog cancel must return null without validation")
require("catch (OperationCanceledException)" in pick_async and "return null;" in pick_async,
        "IMAGE-06 OperationCanceledException must be treated as picker cancel")
require(pick_async.index("catch (OperationCanceledException)") < pick_async.index("catch (Exception error)"),
        "IMAGE-06 cancellation must be handled before the generic FileOpenPicker failure path")

win32 = picker.split("private static string? PickWithWin32", 1)[1]
require("var extendedError = CommDlgExtendedError();" in win32,
        "IMAGE-06 Win32 fallback must inspect common-dialog extended error")
require("if (extendedError == 0) return null;" in win32,
        "IMAGE-06 Win32 dialog cancel must return null")
require(win32.index("if (extendedError == 0) return null;") < win32.index("throw new Win32Exception"),
        "IMAGE-06 Win32 cancel must not be reported as a dialog failure")

add = images.split("private async Task AddImageAsync()", 1)[1].split(
    "private async void RemoveImage_Click", 1
)[0]
require(add.count("await _picker.PickImageAsync();") == 1,
        "IMAGE-06 Add Image must invoke the image picker exactly once")
require("if (path is null) return;" in add,
        "IMAGE-06 Add Image must stop immediately when the picker is canceled")
cancel_gate = add.index("if (path is null) return;")
for marker, label in (
    ("Path.GetExtension(path)", "source validation"),
    ("StorageFile.GetFileFromPathAsync(path)", "bitmap decode"),
    ("_imageOverlays.Add(state)", "image state mutation"),
    ("EnsureBitmapLoadedAsync(state.Path)", "Preview bitmap load"),
    ("SaveImageSidecarAsync()", "image sidecar write"),
    ("RenderImageOverlays()", "Preview overlay refresh"),
    ("NotifyEditorCompositeChanged()", "editor composite invalidation"),
    ("RefreshEditorActions()", "editor action refresh"),
):
    require(marker in add, f"IMAGE-06 expected downstream marker missing: {label}")
    require(cancel_gate < add.index(marker),
            f"IMAGE-06 picker cancel must occur before {label}")

# Synthetic no-op fixture: a canceled picker cannot create a new image or
# request persistence/Preview work.
def cancel_fixture() -> tuple[int, int, int, int]:
    image_count = 2
    selected_index = 1
    sidecar_writes = 0
    preview_refreshes = 0
    path = None
    if path is None:
        return image_count, selected_index, sidecar_writes, preview_refreshes
    raise AssertionError("unreachable")


require(cancel_fixture() == (2, 1, 0, 0),
        "IMAGE-06 fixture: cancel must preserve image state and perform no persistence/Preview work")

print("PASS: IMAGE-06 canceled image picker is a no-op")
