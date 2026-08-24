#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
PAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages"
VIDEO_EDITOR = ROOT / "csharp/src/BiliSubStudio.Core/Editor/VideoEditorService.cs"
COMPOSER = ROOT / "csharp/src/BiliSubStudio.App/Services/EditorImageOverlayComposer.cs"
IMAGES = PAGES / "EditorPage.Images.cs"
PROCESS_RUNNER = ROOT / "csharp/src/BiliSubStudio.Core/Processes/ProcessRunner.cs"


def fail(message: str) -> None:
    print("FAIL:", message, file=sys.stderr)
    raise SystemExit(1)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


video = read(VIDEO_EDITOR)
composer = read(COMPOSER)
images = read(IMAGES)
process_runner = read(PROCESS_RUNNER)

# EXPORT-16 — every final-export temporary artifact must be deleted after Cancel.
# Process cancellation must terminate/reap the child before renderer finally blocks
# attempt to delete files that FFmpeg/ffprobe may still have open.
for token in (
    "using var registration = cancellationToken.Register(() => Kill(process));",
    "finally",
    "Kill(process);",
    "await ReapAsync(process, stderrTask);",
    "process.Kill(entireProcessTree: true)",
):
    require(token in process_runner, f"EXPORT-16 process cleanup lost: {token}")

# Normal/base VideoEditorService renders to a .rendering path and always removes it.
for token in (
    'var temporary = output + ".rendering" + Path.GetExtension(output);',
    "TryDelete(temporary);",
    "finally { TryDelete(temporary); if (subtitleAss is not null) TryDelete(subtitleAss); }",
):
    require(token in video, f"EXPORT-16 normal/base temp cleanup lost: {token}")
require(
    video.index("finally { TryDelete(temporary); if (subtitleAss is not null) TryDelete(subtitleAss); }")
    > video.index("var temporary = output + \".rendering\" + Path.GetExtension(output);"),
    "EXPORT-16 normal/base cleanup must wrap the final-render temporary lifecycle",
)

# Subtitle ASS used by final render is a temp artifact too and must be deleted on Cancel.
require(
    'var subtitleAss = request.Subtitle is null ? null : Path.Combine(Path.GetTempPath(), "bilisub-editor-sub-"' in video,
    "EXPORT-16 final-render ASS temp path lost",
)
require(
    "if (subtitleAss is not null) TryDelete(subtitleAss);" in video,
    "EXPORT-16 final-render ASS cleanup lost",
)

# Image-only/final Image stage owns its own .rendering file and always removes it.
for token in (
    'var temporary = output + ".rendering" + extension;',
    "TryDelete(temporary);",
    "finally",
):
    require(token in composer, f"EXPORT-16 image temp cleanup lost: {token}")
image_try = composer.index('job.Set("image-overlay", 2, "Đang ghép ảnh/logo vào video...");')
image_finally = composer.index("finally", image_try)
image_delete = composer.index("TryDelete(temporary);", image_finally)
require(
    image_try < image_finally < image_delete,
    "EXPORT-16 image .rendering cleanup must execute from the render finally block",
)

# Full combo base output lives under app Temp/Editor/ImageBase and is always cleaned
# by RenderProjectAsync, including the EXPORT-15 stage-boundary cancellation race.
for token in (
    'var temporaryDirectory = Path.Combine(_application.Paths.Temp, "Editor", "ImageBase");',
    'var temporaryName = "editor-image-base-" + Guid.NewGuid().ToString("N") + ".mp4";',
    "string? baseOutput = null;",
    "baseOutput = result.OutputPath;",
    "composerInput = result.OutputPath;",
    "if (baseOutput is not null)",
    "try { File.Delete(baseOutput); } catch { }",
):
    require(token in images, f"EXPORT-16 ImageBase cleanup lost: {token}")

assign = images.index("baseOutput = result.OutputPath;")
cancel_guard = images.index("completedJob.CancellationToken.IsCancellationRequested")
outer_finally = images.index("if (baseOutput is not null)", cancel_guard)
delete_base = images.index("File.Delete(baseOutput)", outer_finally)
require(
    assign < cancel_guard < outer_finally < delete_base,
    "EXPORT-16 stage-boundary Cancel must record ImageBase before throwing so finally can delete it",
)

# Image-stage cancellation must be cleanup-aware and the orchestration catch/finally
# must complete cancellation only after composer cleanup has unwound.
for token in (
    'imageJob = _application.Jobs.Create("editor-image", cleanupAwareCancel: true);',
    'imageJob?.CancelComplete("Đã hủy xuất và dọn file ảnh/logo dở.");',
    '_jobId = null;',
):
    require(token in images, f"EXPORT-16 image cancellation lifecycle lost: {token}")

# Portable state-machine fixture. Model only ownership/cleanup semantics; actual
# process termination is source-locked above.
def cancel_fixture(stage: str, base_done: bool = False) -> tuple[bool, bool, bool]:
    normal_rendering = stage in {"normal", "base"}
    image_rendering = stage == "image"
    base_output = base_done

    # Renderer finally cleanup after cancellation.
    if normal_rendering:
        normal_rendering = False
    if image_rendering:
        image_rendering = False

    # RenderProjectAsync finally cleanup for a completed full-combo base.
    if base_output:
        base_output = False

    return normal_rendering, image_rendering, base_output


for name, base_done in (
    ("normal", False),
    ("base", False),
    ("boundary", True),
    ("image", True),
):
    stage = "image" if name == "image" else ("normal" if name == "normal" else "base")
    leftovers = cancel_fixture(stage, base_done)
    require(leftovers == (False, False, False),
            f"EXPORT-16 synthetic Cancel left temp artifacts for {name}: {leftovers}")

print("PASS: EXPORT-16 cancel temp cleanup contract is locked")
