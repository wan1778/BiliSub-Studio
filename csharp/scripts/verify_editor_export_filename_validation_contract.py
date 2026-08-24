#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
PAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages"
SERVICES = ROOT / "csharp/src/BiliSubStudio.App/Services"
CORE = ROOT / "csharp/src/BiliSubStudio.Core"
XAML = PAGES / "EditorPage.xaml"
BOOTSTRAP = PAGES / "EditorPage.ParityBootstrap.cs"
FILE_NAME_UI = PAGES / "EditorPage.Export.FileName.cs"
EDITOR = PAGES / "EditorPage.xaml.cs"
POLICY = CORE / "IO/FileNamePolicy.cs"
VIDEO_EDITOR = CORE / "Editor/VideoEditorService.cs"
IMAGE_COMPOSER = SERVICES / "EditorImageOverlayComposer.cs"


def fail(message: str) -> None:
    print("FAIL:", message, file=sys.stderr)
    raise SystemExit(1)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


xaml = read(XAML)
bootstrap = read(BOOTSTRAP)
ui = read(FILE_NAME_UI)
editor = read(EDITOR)
policy = read(POLICY)
video_editor = read(VIDEO_EDITOR)
image_composer = read(IMAGE_COMPOSER)
all_editor_parts = "\n".join(read(path) for path in sorted(PAGES.glob("EditorPage*.cs")))

require(xaml.count('x:Name="FileNameBox"') == 1,
        "EXPORT-04 requires exactly one output filename field")
require('TextChanged="FileNameBox_TextChanged"' in xaml,
        "EXPORT-04 filename edits must remain connected to project/state refresh")
require(bootstrap.count("FileNameBox.LostFocus += EditorFileName_LostFocus;") == 1,
        "EXPORT-04 filename normalization must bind exactly once")
require("private void EditorFileName_LostFocus(" in ui,
        "EXPORT-04 filename LostFocus handler is missing")
require("string.IsNullOrWhiteSpace(current)" in ui,
        "EXPORT-04 empty filename must be detected")
require("FileNamePolicy.NormalizeVideoOutputName(current)" in ui,
        "EXPORT-04 UI must use the shared filename policy")
require("FileNameBox.Text = normalized;" in ui,
        "EXPORT-04 corrected filename must be visible before render")
require("RenderButton.IsEnabled =" not in ui,
        "EXPORT-04 must not create a second RenderButton state owner")

require(all_editor_parts.count("RenderButton.IsEnabled =") == 1,
        "EXPORT-04 regressed EXPORT-02 by adding another RenderButton state owner")
require("!string.IsNullOrWhiteSpace(FileNameBox.Text)" in editor,
        "EXPORT-04 must retain the empty-name RenderButton gate")

for token in (
    "MaxSanitizedLength = 150",
    "Path.GetInvalidFileNameChars()",
    "IsReservedWindowsName(text)",
    "var dot = text.IndexOf('.');",
    "NormalizeVideoOutputName(string? value",
    'is not (".mp4" or ".mkv")',
    'fileName += ".mp4";',
):
    require(token in policy, f"EXPORT-04 shared filename policy lost: {token}")
require("Path.GetFileNameWithoutExtension(text)" not in policy,
        "EXPORT-04 reserved-name check must use the first dot")

require('FileNamePolicy.Sanitize(request.FileName, "BiliSub_edited.mp4")' in video_editor,
        "EXPORT-04 normal render lost backend filename sanitization")
require("FileNamePolicy.UniquePath(Path.Combine(outputDirectory, fileName), input)" in video_editor,
        "EXPORT-04 normal render lost collision/source-overwrite protection")
require('FileNamePolicy.Sanitize(fileName, "BiliSub_edited.mp4")' in image_composer,
        "EXPORT-04 image render lost backend filename sanitization")
require("FileNamePolicy.UniquePath(Path.Combine(directory, sanitized), input)" in image_composer,
        "EXPORT-04 image render lost collision/source-overwrite protection")

INVALID = set('<>:"/\\|?*') | {chr(i) for i in range(32)}
RESERVED = {
    "CON", "PRN", "AUX", "NUL",
    *(f"COM{i}" for i in range(1, 10)),
    *(f"LPT{i}" for i in range(1, 10)),
}


def sanitize(value: str | None, fallback: str = "BiliSub_edited.mp4") -> str:
    text = (value or "").strip()
    for char in INVALID:
        text = text.replace(char, "_")
    text = text.strip(" .")[:150]
    if not text.strip():
        return fallback
    if text.split(".", 1)[0].upper() in RESERVED:
        text = "_" + text
    text = text[:150].rstrip(" .")
    return text if text.strip() else fallback


def extension(name: str) -> str:
    index = name.rfind(".")
    return "" if index <= 0 else name[index:].lower()


def normalize(value: str | None) -> str:
    name = sanitize(value)
    if extension(name) not in (".mp4", ".mkv"):
        name += ".mp4"
    return name


traversal = ".." + "/video"
cases = {
    "movie.mp4": "movie.mp4",
    "movie.mkv": "movie.mkv",
    "movie": "movie.mp4",
    " video?.mp4 ": "video_.mp4",
    traversal: "_video.mp4",
    "CON.mp4": "_CON.mp4",
    "NUL.tar.gz": "_NUL.tar.gz.mp4",
    "COM1.test.mkv": "_COM1.test.mkv",
    "movie.avi": "movie.avi.mp4",
    "..........": "BiliSub_edited.mp4",
}
for raw, expected in cases.items():
    actual = normalize(raw)
    require(actual == expected, f"EXPORT-04 fixture {raw!r}: expected {expected!r}, got {actual!r}")
    require(extension(actual) in (".mp4", ".mkv"),
            f"EXPORT-04 fixture lost a supported video extension: {actual!r}")
    require(not any(char in INVALID for char in actual),
            f"EXPORT-04 fixture retained an invalid Windows filename character: {actual!r}")
    require(actual.split(".", 1)[0].upper() not in RESERVED,
            f"EXPORT-04 fixture retained a reserved Windows device name: {actual!r}")

long_name = normalize("A" * 400)
require(len(long_name) <= 154, "EXPORT-04 long filename exceeded the bounded policy")
require(extension(long_name) == ".mp4", "EXPORT-04 long filename lost final video extension")

old_stem = "NUL.tar"
require(old_stem.upper() not in RESERVED and "NUL" in RESERVED,
        "EXPORT-04 reserved compound-name negative fixture failed")

print("PASS: EXPORT-04 filename validation/canonicalization is locked")
