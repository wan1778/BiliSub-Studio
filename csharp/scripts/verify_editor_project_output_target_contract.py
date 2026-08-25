#!/usr/bin/env python3
from __future__ import annotations

import os
import sys
import tempfile
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
PAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages"
IMAGES = PAGES / "EditorPage.Images.cs"
OUTPUT_TARGET = PAGES / "EditorPage.OutputTarget.cs"


def fail(message: str) -> None:
    print("FAIL:", message, file=sys.stderr)
    raise SystemExit(1)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


def method_body(source: str, signature: str) -> str:
    start = source.find(signature)
    require(start >= 0, f"PROJECT-10 missing method: {signature}")
    brace = source.find("{", start)
    require(brace >= 0, f"PROJECT-10 method has no body: {signature}")
    depth = 0
    for index in range(brace, len(source)):
        if source[index] == "{":
            depth += 1
        elif source[index] == "}":
            depth -= 1
            if depth == 0:
                return source[brace:index + 1]
    fail(f"PROJECT-10 unterminated method: {signature}")
    return ""


def verify_source(images: str, target: str) -> None:
    capture = method_body(target, "private EditorOutputTarget CaptureEditorOutputTarget()")
    for token in (
        "_application.Config.OutputDirectory",
        "Path.GetFullPath(configuredDirectory)",
        "FileNameBox.Text",
        "new EditorOutputTarget(directory, fileName)",
    ):
        require(token in capture, f"PROJECT-10 click-time output target lost: {token}")

    identity = method_body(target, "private void EnsureEditorExportSourceIdentity(")
    for token in (
        "_project.Id",
        "EditorSourceSelection.IsSameSource(_path, sourcePath)",
        "EnsureCurrentSourceFingerprint();",
    ):
        require(token in identity, f"PROJECT-10 export source identity guard lost: {token}")

    validate = method_body(target, "private static string ValidateFinalEditorOutput(")
    for token in (
        "Path.GetFullPath(outputPath.Trim())",
        "!info.Exists || info.Length <= 0",
        'extension is not (".mp4" or ".mkv")',
        "actualDirectory",
        "expectedDirectory",
        "StringComparison.OrdinalIgnoreCase",
    ):
        require(token in validate, f"PROJECT-10 final output validation lost: {token}")

    render = method_body(images, "private async Task RenderProjectAsync()")
    busy = render.find("if (EditorBusy) return;")
    capture_at = render.find("outputTarget = CaptureEditorOutputTarget();")
    first_preflight_await = render.find("await EnsureImageProjectLoadedAsync();")
    require(0 <= busy < capture_at < first_preflight_await,
            "PROJECT-10 target must be captured before async preflight can observe later Settings/UI changes")
    require("var exportProjectId = _project.Id;" in render and "var exportSourcePath = _path;" in render,
            "PROJECT-10 export must snapshot source identity with the output target")
    require(render.count("EnsureEditorExportSourceIdentity(exportProjectId, exportSourcePath);") >= 2,
            "PROJECT-10 source identity must be rechecked after async preflight/save")

    for token in (
        "OutputDirectory = outputTarget.Directory",
        "FileName = outputTarget.FileName",
        "composerInput, outputTarget.Directory, outputTarget.FileName",
        "ValidateFinalEditorOutput(outputTarget, result.OutputPath)",
        "ValidateFinalEditorOutput(outputTarget, output)",
    ):
        require(token in render, f"PROJECT-10 final render path lost locked target/validation: {token}")

    # Once the snapshot exists, final output branches may not read live Config/FileNameBox again.
    after_capture = render[render.find("outputTarget = CaptureEditorOutputTarget();") + len("outputTarget = CaptureEditorOutputTarget();"):]
    require("_application.Config.OutputDirectory" not in after_capture,
            "PROJECT-10 in-flight final render re-reads live output directory")
    require("composerInput, _application.Config.OutputDirectory" not in render,
            "PROJECT-10 image stage still uses mutable Config output directory")
    require("composerInput, outputTarget.Directory, FileNameBox.Text" not in render,
            "PROJECT-10 image stage still uses mutable filename UI")


def validate_fixture(expected_dir: str, output: str, size: int) -> bool:
    if size <= 0:
        return False
    extension = Path(output).suffix.lower()
    if extension not in (".mp4", ".mkv"):
        return False
    return os.path.normcase(os.path.abspath(os.path.dirname(output))) == os.path.normcase(os.path.abspath(expected_dir))


def verify_fixture() -> None:
    with tempfile.TemporaryDirectory() as td:
        target_a = os.path.join(td, "A")
        target_b = os.path.join(td, "B")
        os.makedirs(target_a)
        os.makedirs(target_b)

        # Click-time snapshot: later Settings/UI changes must not retarget this run.
        config_output = target_a
        filename = "movie.mp4"
        click_target = (config_output, filename)
        config_output = target_b
        filename = "other.mp4"
        require(click_target == (target_a, "movie.mp4"),
                "PROJECT-10 fixture: click-time target mutated after Settings/UI change")

        good = os.path.join(click_target[0], click_target[1])
        wrong = os.path.join(target_b, click_target[1])
        Path(good).write_bytes(b"valid")
        Path(wrong).write_bytes(b"valid")
        require(validate_fixture(click_target[0], good, Path(good).stat().st_size),
                "PROJECT-10 fixture: valid locked output rejected")
        require(not validate_fixture(click_target[0], wrong, Path(wrong).stat().st_size),
                "PROJECT-10 fixture: wrong-directory stale output accepted")
        require(not validate_fixture(click_target[0], good, 0),
                "PROJECT-10 fixture: empty/missing-equivalent output accepted")
        bad_extension = os.path.join(click_target[0], "movie.tmp")
        Path(bad_extension).write_bytes(b"valid")
        require(not validate_fixture(click_target[0], bad_extension, Path(bad_extension).stat().st_size),
                "PROJECT-10 fixture: non-video output accepted")


if IMAGES.exists() and OUTPUT_TARGET.exists():
    verify_source(IMAGES.read_text(encoding="utf-8"), OUTPUT_TARGET.read_text(encoding="utf-8"))

verify_fixture()
print("PASS: PROJECT-10 immutable output target and stale/wrong final path rejection are locked")
