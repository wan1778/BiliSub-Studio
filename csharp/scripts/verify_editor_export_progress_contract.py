#!/usr/bin/env python3
from __future__ import annotations

import math
import sys
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
PAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages"
BOOTSTRAP = PAGES / "EditorPage.ParityBootstrap.cs"
IMAGES = PAGES / "EditorPage.Images.cs"
COMPOSER = ROOT / "csharp/src/BiliSubStudio.App/Services/EditorImageOverlayComposer.cs"
VIDEO_EDITOR = ROOT / "csharp/src/BiliSubStudio.Core/Editor/VideoEditorService.cs"


def fail(message: str) -> None:
    print("FAIL:", message, file=sys.stderr)
    raise SystemExit(1)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


bootstrap = read(BOOTSTRAP)
images = read(IMAGES)
composer = read(COMPOSER)
video = read(VIDEO_EDITOR)

# EXPORT-14 — the visible Export progress bar must follow every final-render path.
# Normal render already polls the editor AppJob directly. Image-only/full-combo must
# bridge the image AppJob into the same visible bar while keeping 100 reserved for
# successful final completion.
for token in (
    "private DispatcherQueueTimer? _editorExportProgressTimer;",
    "private string? _observedImageProgressJobId;",
    "private double _imageStageProgressFloor;",
    "private double _imageStageDisplayProgress;",
    "EnsureEditorExportProgressTimer();",
    "timer.Interval = TimeSpan.FromMilliseconds(150);",
    "timer.Tick += EditorExportProgressTimer_Tick;",
    "Unloaded += EditorProgress_Unloaded;",
):
    require(token in bootstrap, f"EXPORT-14 progress lifecycle lost: {token}")

# Only the image post-stage needs this bridge; normal/base editor jobs retain their
# existing direct polling owner in RenderProjectAsync.
for token in (
    'if (!string.Equals(snapshot.Kind, "editor-image", StringComparison.Ordinal)) return;',
    "_application.Jobs.TryGet(jobId, out var job)",
    "var snapshot = job.Snapshot();",
):
    require(token in bootstrap, f"EXPORT-14 image job bridge lost: {token}")

# New image jobs reset to the correct stage floor: 0 for Image-only, 68 for full
# combo after the base render. Base-edit state is sampled only once per image job.
for token in (
    "if (!string.Equals(_observedImageProgressJobId, jobId, StringComparison.Ordinal))",
    "var hasBaseEdit = _document.Regions.Count > 0",
    "|| CompletedSubtitleBurn() is not null",
    '|| _audioSettings.SourceMode != "keep"',
    "|| _voiceTrack is not null;",
    "_imageStageProgressFloor = hasBaseEdit ? 68d : 0d;",
    "Progress.Value = _imageStageProgressFloor;",
):
    require(token in bootstrap, f"EXPORT-14 image stage floor lost: {token}")

# Map child 0..99 into floor..99, never 100 before the composer has returned and the
# final output has been validated/promoted. Keep UI progress monotonic within stage.
for token in (
    "var childProgress = Math.Clamp(snapshot.Progress, 0, 99);",
    "+ (99d - _imageStageProgressFloor) * childProgress / 99d;",
    "_imageStageDisplayProgress = Math.Max(_imageStageDisplayProgress, mapped);",
    "Progress.Value = Math.Clamp(_imageStageDisplayProgress, _imageStageProgressFloor, 99d);",
):
    require(token in bootstrap, f"EXPORT-14 image progress mapping lost: {token}")
require("Giai đoạn 2/2 · " in bootstrap, "EXPORT-14 full-combo stage status lost")

# Timer must not keep the page alive after navigation/unload.
for token in (
    "_editorExportProgressTimer.Stop();",
    "_editorExportProgressTimer.Tick -= EditorExportProgressTimer_Tick;",
    "_editorExportProgressTimer = null;",
):
    require(token in bootstrap, f"EXPORT-14 timer cleanup lost: {token}")

# Existing normal and base-stage polling remain authoritative and must not regress.
require(
    "Progress.Value = snapshot.Progress;" in images,
    "EXPORT-14 normal render no longer exposes direct job progress",
)
require(
    "Progress.Value = Math.Clamp(snapshot.Progress * .68, 0, 68);" in images,
    "EXPORT-14 full-combo base stage must occupy 0..68%",
)

# 100 is still written only after a successful normal final result or after the image
# composer has returned. Do not let the timer manufacture completion.
require(images.count("Progress.Value = 100;") == 2,
        "EXPORT-14 expected exactly two successful final 100% assignments")
require("Progress.Value = 100;" not in bootstrap,
        "EXPORT-14 progress bridge must reserve 100% for final orchestration")

# Producers expose useful pre-final progress and validation/finalization milestones.
for token in (
    'job.Set("rendering", 1, "Đang chuẩn bị xuất video...");',
    'job.Set("validating", 97, "Đang kiểm tra stream, thời lượng và khả năng giải mã...");',
    'job.Set("finalizing", 99, "Đã xác minh file; đang hoàn tất...");',
):
    require(token in video, f"EXPORT-14 normal render milestone lost: {token}")
for token in (
    'job.Set("image-overlay", 2, "Đang ghép ảnh/logo vào video...");',
    'job.Set("image-validate", 97, "Đang kiểm tra video có ảnh/logo...");',
    'job.Set("image-complete", 99, "Đã ghép và xác minh ảnh/logo.");',
):
    require(token in composer, f"EXPORT-14 image render milestone lost: {token}")


# Portable behavior fixture for mapping and monotonicity.
def map_image_progress(floor: float, child_values: list[float]) -> list[float]:
    visible = floor
    result: list[float] = []
    for child in child_values:
        child = min(99.0, max(0.0, child))
        mapped = floor + (99.0 - floor) * child / 99.0
        visible = max(visible, mapped)
        visible = min(99.0, max(floor, visible))
        result.append(visible)
    return result


child = [0, 2, 20, 55, 96, 97, 99]
image_only = map_image_progress(0, child)
full_combo = map_image_progress(68, child)

require(image_only[0] == 0 and math.isclose(image_only[-1], 99),
        "EXPORT-14 Image-only must map 0..99 to 0..99")
require(full_combo[0] == 68 and math.isclose(full_combo[-1], 99),
        "EXPORT-14 full combo image stage must map 0..99 to 68..99")
require(all(a <= b for a, b in zip(image_only, image_only[1:])),
        "EXPORT-14 Image-only progress must be monotonic")
require(all(a <= b for a, b in zip(full_combo, full_combo[1:])),
        "EXPORT-14 full-combo progress must be monotonic")
require(max(image_only) < 100 and max(full_combo) < 100,
        "EXPORT-14 image job must not display 100 before final completion")

# A regressing producer sample must not make the visible bar move backwards.
regressing = map_image_progress(68, [2, 50, 40, 97])
require(all(a <= b for a, b in zip(regressing, regressing[1:])),
        "EXPORT-14 visible progress must resist child progress regressions")

print("PASS: EXPORT-14 visible export progress contract is locked")
