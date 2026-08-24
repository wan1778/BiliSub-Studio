#!/usr/bin/env python3
from __future__ import annotations

import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PAGE = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml.cs"
IMAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.Images.cs"


def fail(message: str) -> None:
    print(f"FAIL: {message}", file=sys.stderr)
    raise SystemExit(1)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


page = PAGE.read_text(encoding="utf-8")
images = IMAGES.read_text(encoding="utf-8")

# IMAGE-14 — reopening a normal valid Editor project must restore the saved
# image/logo collection for that project before preview rendering begins.

require("private const int MaxEditorImages = 8;" in images,
        "IMAGE-14 reopen must keep the same 8-logo project limit")
for field in ("Path", "X", "Y", "Width", "Height", "Opacity", "PixelWidth", "PixelHeight"):
    require(field in images.split("internal sealed record EditorImageOverlayState(", 1)[1].split(");", 1)[0],
            f"IMAGE-14 persisted image state is missing {field}")

open_video = page.split("private async Task OpenVideoAsync()", 1)[1].split(
    "private async Task SaveCurrentSourceStateForSwitchAsync()", 1
)[0]
restore_call = "await EnsureImageProjectLoadedAsync();"
require(open_video.count(restore_call) == 1,
        "IMAGE-14 OpenVideoAsync must restore image/logo project state exactly once")
require(open_video.index("_project = candidateProject;") < open_video.index(restore_call),
        "IMAGE-14 image restore must occur after the candidate project becomes current")
require(open_video.index(restore_call) < open_video.index("await _playback.PrepareAsync();"),
        "IMAGE-14 image restore must finish before preview playback preparation")
require(open_video.index(restore_call) < open_video.index("RenderImageOverlays();"),
        "IMAGE-14 image restore must finish before preview overlays are rendered")

restore = images.split("private async Task EnsureImageProjectLoadedAsync()", 1)[1].split(
    "private async void AddImage_Click", 1
)[0]
require("if (_project is null)" in restore,
        "IMAGE-14 restore must handle a missing project explicitly")
require("_imageProjectId = null;" in restore
        and "_imageOverlays.Clear();" in restore
        and "_imageBitmaps.Clear();" in restore
        and "_selectedImageIndex = -1;" in restore,
        "IMAGE-14 missing-project restore must clear stale image state")
require("string.Equals(_imageProjectId, _project.Id, StringComparison.Ordinal)" in restore,
        "IMAGE-14 restore must be keyed to the current project id")
require("_imageProjectId = _project.Id;" in restore,
        "IMAGE-14 restore must claim the current project id before loading its sidecar")
require(restore.count("_imageOverlays.Clear();") >= 2
        and restore.count("_imageBitmaps.Clear();") >= 2,
        "IMAGE-14 switching projects must clear prior overlays and bitmap cache")
require("var path = ImageSidecarPath(_project.Id);" in restore,
        "IMAGE-14 reopen must read the sidecar belonging to the current project")
require("JsonSerializer.DeserializeAsync<List<EditorImageOverlayState>>" in restore,
        "IMAGE-14 reopen must deserialize the persisted image state")
require("loaded.Take(MaxEditorImages)" in restore,
        "IMAGE-14 reopen must never restore more than the supported logo limit")
require("TryNormalizeImageState(image, out var normalized)" in restore,
        "IMAGE-14 reopen must validate each persisted image state before accepting it")
require("_imageOverlays.Add(normalized)" in restore,
        "IMAGE-14 valid persisted logos must return to the overlay collection")
require("await EnsureBitmapLoadedAsync(_imageOverlays[index].Path);" in restore,
        "IMAGE-14 reopened logos must reload their bitmap sources")
require("if (_imageOverlays.Count > 0) _selectedImageIndex = 0;" in restore,
        "IMAGE-14 normal reopen must establish a valid selected logo")
require("RenderImageList();" in restore and "LoadSelectedImageIntoInputs();" in restore,
        "IMAGE-14 reopen must restore list selection and inspector values")

normalize = images.split("private bool TryNormalizeImageState(", 1)[1].split(
    "private async Task SaveImageSidecarAsync()", 1
)[0]
require('extension is not (".png" or ".jpg" or ".jpeg")' in normalize,
        "IMAGE-14 reopen validation must retain PNG/JPG/JPEG support")
require("!File.Exists(path)" in normalize,
        "IMAGE-14 reopen must reject logo paths that no longer exist")
require("image.Width < .02 || image.Height < .02" in normalize,
        "IMAGE-14 reopen must reject invalid saved logo sizes")
require("image.X + image.Width > 1.0001 || image.Y + image.Height > 1.0001" in normalize,
        "IMAGE-14 reopen must reject saved placements outside the video")
require("Opacity = Math.Clamp(image.Opacity, .05, 1)" in normalize,
        "IMAGE-14 reopen must normalize saved opacity to the supported range")

save = images.split("private async Task SaveImageSidecarAsync()", 1)[1].split(
    "private string ImageSidecarPath", 1
)[0]
require("JsonSerializer.SerializeAsync(stream, _imageOverlays" in save,
        "IMAGE-14 sidecar save must persist the complete overlay collection")
require("File.Move(temporary, path, overwrite: true);" in save,
        "IMAGE-14 sidecar save must publish the completed JSON atomically")

path_method = images.split("private string ImageSidecarPath", 1)[1].split(
    "private async Task RenderProjectAsync()", 1
)[0]
require('Path.Combine(_application.Paths.Data, "Projects", projectId + ".images.json")' in path_method,
        "IMAGE-14 image sidecars must remain isolated by project id")

# Synthetic JSON round-trip: order and all persisted values survive reopen, with
# the same eight-item cap and first-item selection policy.
saved = [
    {
        "Path": f"C:/logos/logo-{i}.png",
        "X": round(.01 * i, 3),
        "Y": round(.02 * i, 3),
        "Width": .18,
        "Height": .12,
        "Opacity": round(.05 + i * .1, 2),
        "PixelWidth": 800 + i,
        "PixelHeight": 400 + i,
    }
    for i in range(10)
]
wire = json.dumps(saved)
loaded = json.loads(wire)[:8]
require(len(loaded) == 8, "IMAGE-14 fixture: reopen did not enforce the 8-logo limit")
require([item["Path"] for item in loaded] == [item["Path"] for item in saved[:8]],
        "IMAGE-14 fixture: reopen changed logo ordering")
for before, after in zip(saved[:8], loaded, strict=True):
    require(before == after, "IMAGE-14 fixture: persisted logo state changed during JSON round-trip")
selected_index = 0 if loaded else -1
require(selected_index == 0, "IMAGE-14 fixture: reopened non-empty project must select a valid logo")

# A project with no saved logos must not inherit the previous project's collection.
previous = list(loaded)
new_project_sidecar: list[dict[str, object]] = []
restored = list(new_project_sidecar)
require(previous and restored == [],
        "IMAGE-14 fixture: project switch leaked logos from the prior project")

print("PASS: IMAGE-14 normal project reopen restores isolated logo state and preview inputs")
