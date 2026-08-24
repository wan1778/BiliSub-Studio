#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
PAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages"
EDITOR = PAGES / "EditorPage.xaml.cs"
IMAGES = PAGES / "EditorPage.Images.cs"
CORE_EDITOR = ROOT / "csharp/src/BiliSubStudio.Core/Editor"
VIDEO_EDITOR = CORE_EDITOR / "VideoEditorService.cs"
TIME_SCOPE = CORE_EDITOR / "EditorRegionTimeScope.cs"
BLUR_STRENGTH = CORE_EDITOR / "EditorBlurStrength.cs"


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
video = read(VIDEO_EDITOR)
time_scope = read(TIME_SCOPE)
blur_strength = read(BLUR_STRENGTH)

# UI/state gate: one committed Blur region alone is sufficient to enable Render.
require(
    "_document.Regions.Count > 0 || subtitleReady || audioChanged || _voiceTrack is not null || hasImages"
    in editor,
    "EXPORT-07 RenderButton no longer accepts region-only state",
)
request_start = editor.find("private VideoEditRequest CurrentEditRequest(EditorSubtitleBurn? subtitle)")
require(request_start >= 0, "EXPORT-07 CurrentEditRequest is missing")
request_body = editor[request_start:request_start + 1400]
require(
    "_document.Regions.ToArray()," in request_body,
    "EXPORT-07 CurrentEditRequest must carry committed Blur regions",
)
require(
    "_draftRegion" not in request_body,
    "EXPORT-07 export request must not include an uncommitted draft region",
)

# One Render orchestration: region-only counts as a base edit and, without images,
# uses the normal VideoEditorService path rather than the image composer.
require(
    '_document.Regions.Count > 0 || subtitle is not null || _audioSettings.SourceMode != "keep" || _voiceTrack is not null'
    in images,
    "EXPORT-07 Blur region no longer qualifies as base edit",
)
require(
    "_application.StartEditor(CurrentEditRequest(subtitle))" in images,
    "EXPORT-07 Blur-only must use the normal Editor render service",
)

# Backend accepts a region by itself as a real edit.
require(
    "request.Regions.Count > 0 || request.Subtitle is not null || request.VoiceTrack is not null"
    in video,
    "EXPORT-07 backend HasEdit no longer accepts region-only requests",
)

# Blur graph contract: split the current video, crop only the ROI, blur that crop,
# then overlay it back. Empty legacy effect continues to mean blur.
for token in (
    'else if (effect is "" or "blur")',
    "EditorBlurStrength.EffectiveRadius(region.Strength, width, height)",
    'parts.Add($"[{current}]split=2[base{index}][fx{index}]");',
    "crop={width}:{height}:{x}:{y},boxblur=luma_radius={strength}",
    'parts.Add($"[base{index}][rendered{index}]overlay={x}:{y}{enable}[{output}]");',
):
    require(token in video, f"EXPORT-07 Blur filter contract lost: {token}")

# Geometry stays inside source pixels and cannot collapse below 2x2.
for token in (
    "var x = (int)(region.X * width);",
    "var y = (int)(region.Y * height);",
    "Math.Min(1, region.X + region.Width)",
    "Math.Min(1, region.Y + region.Height)",
    'if (w < 2 || h < 2) throw new ArgumentException("Vùng quá nhỏ.");',
):
    require(token in video, f"EXPORT-07 ROI pixel contract lost: {token}")

# Timed Blur uses the same normalized region scope at export; WholeVideo has no
# FFmpeg enable expression and is normalized to [0,duration].
require(
    "var normalized = EditorRegionTimeScope.Normalize(region, duration);" in video,
    "EXPORT-07 export lost region time normalization",
)
require(
    "if (normalized.WholeVideo) return string.Empty;" in video,
    "EXPORT-07 WholeVideo Blur must stay active for the entire output",
)
require(
    "between(t,{normalized.Start.ToString" in video
    and "{normalized.End.ToString" in video,
    "EXPORT-07 timed Blur lost start/end enable expression",
)
for token in (
    "if (region.WholeVideo) return NormalizeWholeVideo(region, duration);",
    "region.Start < 0 || region.End > duration || region.End <= region.Start",
    "region with { Start = 0, End = duration }",
):
    require(token in time_scope, f"EXPORT-07 region time policy lost: {token}")

# Blur strength is bounded and can never exceed the legal radius of a small ROI.
for token in (
    "public const int Minimum = 2;",
    "public const int Maximum = 40;",
    "Math.Clamp(strength, Minimum, Maximum)",
    "var maximumRadius = (Math.Min(pixelWidth, pixelHeight) - 1) / 2;",
    "Math.Min(NormalizeStored(strength), maximumRadius)",
):
    require(token in blur_strength, f"EXPORT-07 blur strength contract lost: {token}")

# Blur-only must not imply audio mute. With default Keep, source audio is mapped
# optionally and output validation remains on the standard Editor path.
require(
    'if (audio.SourceMode == "mute") return ["-an"];' in video,
    "EXPORT-07 audio mute policy changed unexpectedly",
)
require(
    'var arguments = new List<string> { "-map", "0:a?" };' in video,
    "EXPORT-07 Blur-only Keep must preserve source audio when present",
)
require(
    'ValidateRenderedOutputAsync(temporary, request.Duration, voice is not null || audio.SourceMode != "mute", token)'
    in video,
    "EXPORT-07 output validation path changed unexpectedly",
)

# Standard safe finalization remains in force.
for token in (
    'var temporary = output + ".rendering" + Path.GetExtension(output);',
    'args.AddRange(["-progress", "pipe:1", "-nostats", temporary]);',
    "File.Move(temporary, output);",
):
    require(token in video, f"EXPORT-07 final render lifecycle lost: {token}")


# Portable behavioral fixtures for the EXPORT-07 task boundary.
def render_enabled(
    *,
    editable: bool = True,
    manual_dirty: bool = False,
    path: bool = True,
    regions: int = 0,
    subtitle_ready: bool = False,
    audio_changed: bool = False,
    voice: bool = False,
    images_count: int = 0,
    filename: str = "movie.mp4",
) -> bool:
    return (
        editable
        and not manual_dirty
        and path
        and (regions > 0 or subtitle_ready or audio_changed or voice or images_count > 0)
        and bool(filename.strip())
    )


require(render_enabled(regions=1), "EXPORT-07 one Blur region alone must enable Render")
require(not render_enabled(), "EXPORT-07 empty project fixture must not enable Render")


def normalize_time(start: float, end: float, duration: float, whole_video: bool) -> tuple[float, float]:
    if whole_video:
        return 0.0, duration
    if start < 0 or end > duration or end <= start:
        raise ValueError("invalid")
    return start, end


def region_enable(start: float, end: float, duration: float, whole_video: bool) -> str:
    start, end = normalize_time(start, end, duration, whole_video)
    if whole_video:
        return ""
    return f":enable='between(t,{start:.3f},{end:.3f})'"


require(
    region_enable(2.5, 7.0, 10.0, False) == ":enable='between(t,2.500,7.000)'",
    "EXPORT-07 timed Blur fixture lost exact activation range",
)
require(
    region_enable(8.0, 9.0, 10.0, True) == "",
    "EXPORT-07 WholeVideo Blur fixture should not carry a timed enable expression",
)


def effective_radius(strength: int, pixel_width: int, pixel_height: int) -> int:
    normalized = max(2, min(40, strength))
    maximum_radius = (min(pixel_width, pixel_height) - 1) // 2
    return min(normalized, maximum_radius)


require(effective_radius(18, 200, 100) == 18, "EXPORT-07 normal Blur strength fixture failed")
require(effective_radius(40, 8, 8) == 3, "EXPORT-07 small-ROI Blur radius fixture failed")

print("PASS: EXPORT-07 Blur-only export contract is locked")
