#!/usr/bin/env python3
from __future__ import annotations

import os
import sys
import tempfile
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
POLICY = ROOT / "csharp/src/BiliSubStudio.Core/IO/FileNamePolicy.cs"
VIDEO_EDITOR = ROOT / "csharp/src/BiliSubStudio.Core/Editor/VideoEditorService.cs"
IMAGE_COMPOSER = ROOT / "csharp/src/BiliSubStudio.App/Services/EditorImageOverlayComposer.cs"
EDITOR_IMAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.Images.cs"


def fail(message: str) -> None:
    print("FAIL:", message, file=sys.stderr)
    raise SystemExit(1)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


policy = read(POLICY)
video_editor = read(VIDEO_EDITOR)
image_composer = read(IMAGE_COMPOSER)
editor_images = read(EDITOR_IMAGES)

# One shared path policy owns source-overwrite prevention.
for token in (
    "public static string UniquePath(string candidate, string? forbiddenInput = null)",
    "candidate = Path.GetFullPath(candidate);",
    "Path.GetFullPath(forbiddenInput)",
    "!File.Exists(candidate) && !string.Equals(candidate, forbidden, StringComparison.OrdinalIgnoreCase)",
    "!File.Exists(next) && !string.Equals(next, forbidden, StringComparison.OrdinalIgnoreCase)",
):
    require(token in policy, f"EXPORT-05 source protection lost from FileNamePolicy: {token}")

# Both final render implementations must explicitly forbid their input path.
require("FileNamePolicy.UniquePath(Path.Combine(outputDirectory, fileName), input)" in video_editor,
        "EXPORT-05 normal Editor render no longer forbids its source input")
require("FileNamePolicy.UniquePath(Path.Combine(directory, sanitized), input)" in image_composer,
        "EXPORT-05 Image/Logo render no longer forbids its input")

# FFmpeg only writes a temporary artifact. Final promotion is fail-closed: the
# two-argument File.Move overload does not overwrite a destination that appeared
# after UniquePath selected the name.
require('var temporary = output + ".rendering" + Path.GetExtension(output);' in video_editor,
        "EXPORT-05 normal render lost separate temporary output")
require('args.AddRange(["-progress", "pipe:1", "-nostats", temporary]);' in video_editor,
        "EXPORT-05 normal FFmpeg must write the temporary path")
require("File.Move(temporary, output);" in video_editor,
        "EXPORT-05 normal render must promote without overwrite:true")
require("File.Move(temporary, output, overwrite: true)" not in video_editor,
        "EXPORT-05 normal render may overwrite an existing destination")

require('var temporary = output + ".rendering" + extension;' in image_composer,
        "EXPORT-05 Image/Logo render lost separate temporary output")
require('"-progress", "pipe:1", "-nostats", temporary,' in image_composer,
        "EXPORT-05 Image/Logo FFmpeg must write the temporary path")
require("File.Move(temporary, output);" in image_composer,
        "EXPORT-05 Image/Logo render must promote without overwrite:true")
require("File.Move(temporary, output, overwrite: true)" not in image_composer,
        "EXPORT-05 Image/Logo render may overwrite an existing destination")

# Combined base-edit + image flow uses a GUID-named temp base and deletes only
# that temp artifact. The real source (_path) is never deleted or moved.
for token in (
    'Path.Combine(_application.Paths.Temp, "Editor", "ImageBase")',
    '"editor-image-base-" + Guid.NewGuid().ToString("N") + ".mp4"',
    "OutputDirectory = temporaryDirectory, FileName = temporaryName",
    "composerInput = result.OutputPath;",
    "File.Delete(baseOutput)",
):
    require(token in editor_images, f"EXPORT-05 combined render lifecycle lost: {token}")
require("File.Delete(_path" not in editor_images,
        "EXPORT-05 combined render must never delete the source path")
require("File.Move(_path" not in editor_images,
        "EXPORT-05 combined render must never move the source path")

# Portable behavioral mirror of FileNamePolicy.UniquePath. The case-insensitive
# comparison is deliberate because the target product is Windows.
def same_path(left: str, right: str | None) -> bool:
    return right is not None and os.path.abspath(left).casefold() == os.path.abspath(right).casefold()


def unique_path(candidate: str, forbidden: str | None = None, exists=os.path.exists) -> str:
    candidate = os.path.abspath(candidate)
    forbidden = None if not forbidden else os.path.abspath(forbidden)
    if not exists(candidate) and not same_path(candidate, forbidden):
        return candidate
    directory = os.path.dirname(candidate)
    stem, extension = os.path.splitext(os.path.basename(candidate))
    for index in range(2, 10_000):
        next_path = os.path.join(directory, f"{stem} ({index}){extension}")
        if not exists(next_path) and not same_path(next_path, forbidden):
            return next_path
    raise OSError("Không thể tạo tên file đầu ra duy nhất.")


with tempfile.TemporaryDirectory() as td:
    source = os.path.join(td, "movie.mp4")
    Path(source).write_bytes(b"SOURCE-MUST-SURVIVE")

    # Exact source name: choose a sibling instead of the source itself.
    chosen = unique_path(source, source)
    require(chosen.endswith("movie (2).mp4"),
            f"EXPORT-05 exact-source fixture chose {chosen!r}")
    require(Path(source).read_bytes() == b"SOURCE-MUST-SURVIVE",
            "EXPORT-05 exact-source fixture modified source bytes")

    # Existing collision suffix must advance without touching source/existing output.
    collision = os.path.join(td, "movie (2).mp4")
    Path(collision).write_bytes(b"EXISTING-OUTPUT")
    chosen = unique_path(source, source)
    require(chosen.endswith("movie (3).mp4"),
            f"EXPORT-05 collision fixture chose {chosen!r}")
    require(Path(source).read_bytes() == b"SOURCE-MUST-SURVIVE",
            "EXPORT-05 collision fixture modified source bytes")
    require(Path(collision).read_bytes() == b"EXISTING-OUTPUT",
            "EXPORT-05 collision fixture modified existing output")

    # Even if filesystem existence is unavailable, case-only source aliases are
    # rejected by the explicit OrdinalIgnoreCase forbidden comparison.
    case_alias = os.path.join(td, "MOVIE.MP4")
    chosen = unique_path(case_alias, source, exists=lambda _: False)
    require(chosen.endswith("MOVIE (2).MP4"),
            "EXPORT-05 case-insensitive forbidden-source fixture failed")

    # Combined base+logo: composer forbids its temp base input, while the requested
    # final name equals the still-existing original source. File.Exists must force
    # a suffix, preserving the original source.
    temp_base = os.path.join(td, "editor-image-base-guid.mp4")
    Path(temp_base).write_bytes(b"TEMP-BASE")
    chosen = unique_path(source, temp_base)
    require(chosen.endswith("movie (3).mp4"),
            "EXPORT-05 combined base+logo fixture could target original source")
    require(Path(source).read_bytes() == b"SOURCE-MUST-SURVIVE",
            "EXPORT-05 combined fixture modified source bytes")

print("PASS: EXPORT-05 source and existing outputs cannot be overwritten by Editor render paths")
