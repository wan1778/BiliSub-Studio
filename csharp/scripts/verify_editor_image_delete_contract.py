#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
BOOTSTRAP = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.ParityBootstrap.cs"
DELETE = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.Images.Delete.cs"
IMAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.Images.cs"


def fail(message: str) -> None:
    print(f"FAIL: {message}", file=sys.stderr)
    raise SystemExit(1)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


bootstrap = BOOTSTRAP.read_text(encoding="utf-8")
delete = DELETE.read_text(encoding="utf-8")
images = IMAGES.read_text(encoding="utf-8")

# IMAGE-13 — deleting an image/logo removes exactly the selected overlay,
# preserves shared bitmap cache entries that are still used by duplicate
# instances, refreshes all visible state, persists the new collection and
# invalidates processed/composite preview.

require(bootstrap.count("RemoveImageButton.Click += RemoveImageSafe_Click;") == 1,
        "IMAGE-13 Remove button must bind the safe delete handler exactly once")
require("RemoveImageButton.Click += RemoveImage_Click;" not in bootstrap,
        "IMAGE-13 legacy delete handler must not remain bound")

require("_selectedImageIndex < 0 || _selectedImageIndex >= _imageOverlays.Count" in delete,
        "IMAGE-13 delete must reject an invalid selection")
require("EditorBusy || _playback.IsPreviewMode" in delete,
        "IMAGE-13 delete must not mutate state while Editor/processed preview is locked")
require("var removedIndex = _selectedImageIndex;" in delete,
        "IMAGE-13 delete must capture the exact selected index")
require("var removed = _imageOverlays[removedIndex];" in delete,
        "IMAGE-13 delete must capture the selected overlay")
require("_imageOverlays.RemoveAt(removedIndex);" in delete,
        "IMAGE-13 delete must remove exactly one selected overlay")

require("_imageOverlays.Any(image =>" in delete
        and "string.Equals(image.Path, removed.Path, StringComparison.OrdinalIgnoreCase)" in delete,
        "IMAGE-13 delete must detect duplicate overlays sharing the same source path")
require("if (!pathStillUsed) _imageBitmaps.Remove(removed.Path);" in delete,
        "IMAGE-13 bitmap cache must only be released when no remaining overlay uses the path")

require("_selectedImageIndex = Math.Min(removedIndex, _imageOverlays.Count - 1);" in delete,
        "IMAGE-13 selection must move to the next surviving item or previous item after deleting the last")
require("await SaveImageSidecarAsync();" in delete,
        "IMAGE-13 delete must persist the updated overlay collection")
require("RenderImageList();" in delete,
        "IMAGE-13 delete must refresh the image list")
require("LoadSelectedImageIntoInputs();" in delete,
        "IMAGE-13 delete must refresh inspector values")
require("RenderImageOverlays();" in delete,
        "IMAGE-13 delete must refresh direct preview")
require("NotifyEditorCompositeChanged();" in delete,
        "IMAGE-13 delete must invalidate processed/composite preview")
require("RefreshImageControls();" in delete,
        "IMAGE-13 delete must refresh Add/Delete/preset control state")

sidecar = images.split("private async Task SaveImageSidecarAsync()", 1)[1].split(
    "private string ImageSidecarPath", 1
)[0]
require("if (_imageOverlays.Count == 0)" in sidecar and "File.Delete(path)" in sidecar,
        "IMAGE-13 deleting the last overlay must remove the empty image sidecar")


def delete_fixture(overlays: list[str], selected: int, cache: set[str]) -> tuple[list[str], int, set[str]]:
    overlays = list(overlays)
    cache = set(cache)
    removed = overlays[selected]
    removed_index = selected
    overlays.pop(removed_index)
    path_still_used = any(path.casefold() == removed.casefold() for path in overlays)
    if not path_still_used:
        cache = {path for path in cache if path.casefold() != removed.casefold()}
    selected = min(removed_index, len(overlays) - 1)
    return overlays, selected, cache


# Middle delete selects the item that shifted into the same slot.
overlays, selected, cache = delete_fixture(
    ["a.png", "b.png", "c.png"], 1, {"a.png", "b.png", "c.png"}
)
require(overlays == ["a.png", "c.png"], "IMAGE-13 fixture: middle delete removed the wrong overlay")
require(selected == 1, "IMAGE-13 fixture: middle delete did not select the next surviving overlay")
require("b.png" not in cache, "IMAGE-13 fixture: unique removed bitmap remained cached")

# Last delete selects the previous item.
overlays, selected, cache = delete_fixture(
    ["a.png", "b.png", "c.png"], 2, {"a.png", "b.png", "c.png"}
)
require(overlays == ["a.png", "b.png"], "IMAGE-13 fixture: last delete removed the wrong overlay")
require(selected == 1, "IMAGE-13 fixture: deleting last overlay did not select previous item")

# Deleting the only item clears selection.
overlays, selected, cache = delete_fixture(["only.png"], 0, {"only.png"})
require(overlays == [] and selected == -1,
        "IMAGE-13 fixture: deleting the final overlay must leave no selection")
require("only.png" not in cache,
        "IMAGE-13 fixture: final removed bitmap must leave the cache")

# Duplicate source paths remain renderable without an unnecessary cache eviction.
overlays, selected, cache = delete_fixture(
    ["logo.png", "LOGO.PNG", "other.png"], 0, {"logo.png", "other.png"}
)
require(overlays == ["LOGO.PNG", "other.png"],
        "IMAGE-13 fixture: deleting one duplicate disturbed remaining overlays")
require(any(path.casefold() == "logo.png" for path in cache),
        "IMAGE-13 fixture: deleting one duplicate evicted bitmap still used by another overlay")
require(selected == 0,
        "IMAGE-13 fixture: duplicate delete should select the surviving overlay in the same slot")

print("PASS: IMAGE-13 delete removes only selected logo, preserves duplicates and refreshes preview state")
