#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
PAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages"
EDITOR = PAGES / "EditorPage.xaml.cs"
IMAGES = PAGES / "EditorPage.Images.cs"
COMPOSER = ROOT / "csharp/src/BiliSubStudio.App/Services/EditorImageOverlayComposer.cs"


def fail(message: str) -> None:
    print("FAIL:", message, file=sys.stderr)
    raise SystemExit(1)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


editor = read(EDITOR)
images = read(IMAGES)
composer = read(COMPOSER)

# EXPORT-10 — Image/logo must be a complete final-render edit on its own.
require(
    "var hasImages = _imageFeatureInitialized && _imageOverlays.Count > 0;" in editor,
    "EXPORT-10 Render state owner lost Image/logo state",
)
require(
    "_document.Regions.Count > 0 || subtitleReady || audioChanged || _voiceTrack is not null || hasImages"
    in editor,
    "EXPORT-10 Image-only state no longer enables Render",
)

# One final-render orchestrator accepts images even when no base edit exists.
for token in (
    'var hasBaseEdit = _document.Regions.Count > 0 || subtitle is not null || _audioSettings.SourceMode != "keep" || _voiceTrack is not null;',
    "var hasImages = _imageOverlays.Count > 0;",
    "if (!hasBaseEdit && !hasImages)",
):
    require(token in images, f"EXPORT-10 image/base edit gate lost: {token}")

# Image-only uses the original source directly. Base render is only created when
# another edit exists, avoiding an unnecessary first H.264 encode.
require("var composerInput = _path;" in images, "EXPORT-10 composer input must start from the source")
require(
    "if (hasBaseEdit)" in images
    and 'var temporaryDirectory = Path.Combine(_application.Paths.Temp, "Editor", "ImageBase");' in images
    and "composerInput = snapshot.Result.OutputPath;" in images,
    "EXPORT-10 base-render branch changed unexpectedly",
)

# The exact ordered overlay state is transferred into the final composer.
require(
    "var specs = _imageOverlays.Select(image => new EditorImageOverlaySpec(" in images,
    "EXPORT-10 ordered image state no longer feeds final composer",
)
for token in (
    "image.Path, image.X, image.Y, image.Width, image.Height, image.Opacity",
    "_media.Width, _media.Height, _media.Duration, specs, copyAudio: hasBaseEdit",
):
    require(token in images, f"EXPORT-10 final image state lost: {token}")

# Composer accepts 1..8 real PNG/JPG/JPEG overlays with bounded geometry/opacity.
for token in (
    "private const int MaxImages = 8;",
    "if (images.Count is 0 or > MaxImages)",
    'extension is not (".png" or ".jpg" or ".jpeg")',
    "image.X + image.Width > 1.0001 || image.Y + image.Height > 1.0001",
    "image.Opacity is < 0.05 or > 1",
):
    require(token in composer, f"EXPORT-10 image validation lost: {token}")

# Sequential FFmpeg graph preserves stacking order and normalized placement.
for token in (
    'var current = "0:v";',
    "for (var index = 0; index < images.Count; index++)",
    'var outputLabel = index == images.Count - 1 ? "vout" : $"logoout{index}";',
    'format=rgba,scale={width}:{height}:flags=lanczos,colorchannelmixer=aa={alpha}',
    'overlay={x}:{y}:eof_action=repeat:shortest=0',
    "current = outputLabel;",
):
    require(token in composer, f"EXPORT-10 ordered image graph lost: {token}")

# Image-only must keep source-audio content when present. Optional audio mapping also
# permits legitimate video-only sources. The current Image-only path transcodes source
# audio once to AAC for output compatibility; a base-edited temporary can stream-copy.
require('"-map", "[vout]", "-map", "0:a?"' in composer,
        "EXPORT-10 composer must retain optional source audio")
require('if (copyAudio) args.AddRange(["-c:a", "copy"]);' in composer,
        "EXPORT-10 base-edited image path lost audio stream-copy")
require('else args.AddRange(["-c:a", "aac", "-b:a", "192k"]);' in composer,
        "EXPORT-10 Image-only source audio compatibility encode changed")
require('"-an"' not in composer,
        "EXPORT-10 image composer must never mute source audio")

# Final image output is source-safe and promoted only after validation.
for token in (
    "var output = FileNamePolicy.UniquePath(Path.Combine(directory, sanitized), input);",
    'var temporary = output + ".rendering" + extension;',
    "await ValidateAsync(temporary, sourceWidth, sourceHeight, duration, job.CancellationToken);",
    "File.Move(temporary, output);",
    "TryDelete(temporary);",
):
    require(token in composer, f"EXPORT-10 safe output lifecycle lost: {token}")

# Validation locks the final picture size and duration.
for token in (
    "if (width == expectedWidth && height == expectedHeight) validVideo = true;",
    'if (!validVideo) throw new InvalidDataException("Kích thước video sau khi ghép ảnh/logo bị thay đổi ngoài dự kiến.");',
    "if (Math.Abs(duration - expectedDuration) > tolerance)",
):
    require(token in composer, f"EXPORT-10 output validation lost: {token}")


# Portable behavior fixtures for the EXPORT-10 boundary.
def render_enabled(*, images: int, other_edits: bool = False, filename: str = "movie.mp4") -> bool:
    return (other_edits or images > 0) and bool(filename.strip())


def image_route(*, has_base_edit: bool, image_count: int) -> tuple[str, bool, bool]:
    if not has_base_edit and image_count <= 0:
        return ("reject", False, False)
    composer_input = "source"
    base_render = False
    if has_base_edit:
        base_render = True
        composer_input = "temporary-base"
    copy_audio = has_base_edit
    return (composer_input, base_render, copy_audio)


def audio_args(*, copy_audio: bool) -> list[str]:
    args = ["-map", "[vout]", "-map", "0:a?"]
    if copy_audio:
        args += ["-c:a", "copy"]
    else:
        args += ["-c:a", "aac", "-b:a", "192k"]
    return args


def overlay_graph(items: list[tuple[float, float, float, float, float]], source_width: int, source_height: int) -> str:
    parts: list[str] = []
    current = "0:v"
    for index, (x_n, y_n, w_n, h_n, opacity) in enumerate(items):
        width = max(2, round(w_n * source_width))
        height = max(2, round(h_n * source_height))
        x = min(max(round(x_n * source_width), 0), max(0, source_width - width))
        y = min(max(round(y_n * source_height), 0), max(0, source_height - height))
        output = "vout" if index == len(items) - 1 else f"logoout{index}"
        parts.append(
            f"[{index + 1}:v]format=rgba,scale={width}:{height}:flags=lanczos,"
            f"colorchannelmixer=aa={opacity:.3f}[logo{index}]"
        )
        parts.append(
            f"[{current}][logo{index}]overlay={x}:{y}:eof_action=repeat:shortest=0[{output}]"
        )
        current = output
    return ";".join(parts)


require(render_enabled(images=1), "EXPORT-10 one Image alone must enable Render")
require(not render_enabled(images=0), "EXPORT-10 empty project must not enable Render")

route = image_route(has_base_edit=False, image_count=1)
require(route == ("source", False, False),
        "EXPORT-10 Image-only must skip base render and compose directly over source")
require("0:a?" in audio_args(copy_audio=route[2]),
        "EXPORT-10 Image-only must retain optional source audio")
require("aac" in audio_args(copy_audio=route[2]) and "copy" not in audio_args(copy_audio=route[2]),
        "EXPORT-10 Image-only fixture must use one compatibility audio encode")

base_route = image_route(has_base_edit=True, image_count=1)
require(base_route == ("temporary-base", True, True),
        "EXPORT-10 image+other-edit must compose over the finished base render")
require("copy" in audio_args(copy_audio=base_route[2]),
        "EXPORT-10 image post-stage must not re-encode already-rendered base audio")

graph = overlay_graph(
    [(0.10, 0.20, 0.25, 0.20, 0.50), (0.60, 0.05, 0.15, 0.10, 1.00)],
    1920,
    1080,
)
require("[0:v][logo0]" in graph and "[logoout0][logo1]" in graph,
        "EXPORT-10 overlap stacking order fixture failed")
require("colorchannelmixer=aa=0.500" in graph and "colorchannelmixer=aa=1.000" in graph,
        "EXPORT-10 opacity fixture failed")
require(graph.endswith("[vout]"), "EXPORT-10 final image graph must end at [vout]")

print("PASS: EXPORT-10 Image-only export contract is locked")
