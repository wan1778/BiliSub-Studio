#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
PAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages"
EDITOR = PAGES / "EditorPage.xaml.cs"
IMAGES = PAGES / "EditorPage.Images.cs"
VIDEO_EDITOR = ROOT / "csharp/src/BiliSubStudio.Core/Editor/VideoEditorService.cs"


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

# EXPORT-11 — completed Vietnamese subtitle + at least one Blur region must remain
# one exportable state and one FFmpeg visual graph. Blur is applied first; ASS is
# deliberately applied after region effects so the Vietnamese caption stays sharp.
require(
    "var subtitleReady = _subtitleSource is not null && _subtitleSource.Cues.All(x => !string.IsNullOrWhiteSpace(x.VietnameseText));"
    in editor,
    "EXPORT-11 Render state lost completed-subtitle readiness",
)
require(
    "_document.Regions.Count > 0 || subtitleReady || audioChanged || _voiceTrack is not null || hasImages"
    in editor,
    "EXPORT-11 Render state owner no longer accepts combined Subtitle + Blur",
)

# Only completed Vietnamese text is exportable.
require(
    "private EditorSubtitleBurn? CompletedSubtitleBurn() =>" in editor
    and "_subtitleSource.Cues.All(x => !string.IsNullOrWhiteSpace(x.VietnameseText))" in editor
    and "new EditorSubtitleBurn(_subtitleSource.Cues, _subtitlePlacement, _cueSpeechTiming, KaraokeToggle.IsOn)"
    in editor,
    "EXPORT-11 completed subtitle owner changed unexpectedly",
)

# CurrentEditRequest carries the same committed regions and completed subtitle together.
request_start = editor.find("private VideoEditRequest CurrentEditRequest(EditorSubtitleBurn? subtitle)")
require(request_start >= 0, "EXPORT-11 CurrentEditRequest is missing")
request_body = editor[request_start:request_start + 1800]
for token in (
    "_document.Regions.ToArray(),",
    "subtitle,",
    "_audioSettings,",
    "_voiceTrack);",
):
    require(token in request_body, f"EXPORT-11 combined request lost: {token}")

# Final orchestration snapshots the completed subtitle and counts either region or
# subtitle as base edit. With no Image/logo it must use the normal editor render once.
require(
    "var subtitle = CompletedSubtitleBurn();" in images,
    "EXPORT-11 final render no longer snapshots completed subtitle",
)
require(
    'var hasBaseEdit = _document.Regions.Count > 0 || subtitle is not null || _audioSettings.SourceMode != "keep" || _voiceTrack is not null;'
    in images,
    "EXPORT-11 combined Subtitle + Blur no longer qualifies as base edit",
)
require(
    "if (!hasImages)" in images
    and "_application.StartEditor(CurrentEditRequest(subtitle))" in images,
    "EXPORT-11 no-image combined render must use the single normal render service",
)

# RunAsync creates one ASS artifact, then one combined video filter graph.
run_start = video.find("public async Task<VideoEditResult> RunAsync(")
run_end = video.find("private async Task ValidateRenderedOutputAsync", run_start)
require(run_start >= 0 and run_end > run_start, "EXPORT-11 RunAsync block not found")
run_body = video[run_start:run_end]
for token in (
    "var subtitleAss = request.Subtitle is null ? null :",
    "BuildAss(request.Subtitle!, request.SourceWidth, request.SourceHeight)",
    "var graph = BuildFilter(request, subtitleAss);",
    '"-filter_complex", combinedGraph',
    '"-map", "[vout]"',
):
    require(token in run_body, f"EXPORT-11 single render graph lost: {token}")

# BuildFilterCore must process all committed regions first.
filter_start = video.find("private static string BuildFilterCore(")
filter_end = video.find("public static string BuildAss(", filter_start)
require(filter_start >= 0 and filter_end > filter_start, "EXPORT-11 BuildFilterCore block not found")
filter_body = video[filter_start:filter_end]
for token in (
    "for (var index = 0; index < request.Regions.Count; index++)",
    'else if (effect is "" or "blur")',
    'parts.Add($"[{current}]split=2[base{index}][fx{index}]");',
    "boxblur=luma_radius={strength}:luma_power=1",
    'parts.Add($"[base{index}][rendered{index}]overlay={x}:{y}{enable}[{output}]");',
    "current = output;",
):
    require(token in filter_body, f"EXPORT-11 Blur graph lost: {token}")

# Critical composition order: subtitle is appended after region loop and consumes the
# final region output label. This prevents the Blur ROI from blurring the new Vietsub.
loop_pos = filter_body.find("for (var index = 0; index < request.Regions.Count; index++)")
subtitle_pos = filter_body.find("if (request.Subtitle is not null)")
ass_pos = filter_body.find("ass=filename=")
require(loop_pos >= 0 and subtitle_pos > loop_pos and ass_pos > subtitle_pos,
        "EXPORT-11 subtitle must be appended after Blur/effect processing")
require(
    'parts.Add($"[{current}]ass=filename=\'{escaped}\'[vout]");' in filter_body,
    "EXPORT-11 ASS must consume the post-Blur current video label",
)

# Blur timing remains region-owned.
for token in (
    "var enable = RegionEnable(region, request.Duration);",
    "var normalized = EditorRegionTimeScope.Normalize(region, duration);",
    'return $":enable=\'between(t,{normalized.Start.ToString("0.000", CultureInfo.InvariantCulture)},{normalized.End.ToString("0.000", CultureInfo.InvariantCulture)})\'";',
):
    require(token in video, f"EXPORT-11 Blur timing contract lost: {token}")

# Subtitle hardsub remains Vietnamese-only and keeps cue timing/placement owner.
ass_start = video.find("public static string BuildAss(")
require(ass_start >= 0, "EXPORT-11 BuildAss missing")
ass_body = video[ass_start:]
for token in (
    "subtitle.Cues.Count == 0 || subtitle.Cues.Any(x => string.IsNullOrWhiteSpace(x.VietnameseText))",
    "subtitle.Placement",
    "cue.VietnameseText",
    "cue.Start",
    "cue.End",
):
    require(token in ass_body, f"EXPORT-11 ASS semantics lost: {token}")

# Default source audio policy remains orthogonal to visual composition.
require(
    'var arguments = new List<string> { "-map", "0:a?" };' in video,
    "EXPORT-11 Keep audio must continue mapping optional source audio",
)
require(
    'if (audio.SourceMode == "mute") return ["-an"];' in video,
    "EXPORT-11 source-audio policy owner changed unexpectedly",
)

# Safe finalization remains one temp artifact -> validation -> promotion.
for token in (
    'var temporary = output + ".rendering" + Path.GetExtension(output);',
    "await ValidateRenderedOutputAsync(temporary, request.Duration",
    "File.Move(temporary, output);",
):
    require(token in run_body, f"EXPORT-11 final output lifecycle lost: {token}")


# Portable behavior fixture for the exact Subtitle + Blur boundary.
def render_enabled(*, regions: int, subtitle_ready: bool, filename: str = "movie.mp4") -> bool:
    return (regions > 0 or subtitle_ready) and bool(filename.strip())


def region_enable(*, whole_video: bool, start: float, end: float) -> str:
    if whole_video:
        return ""
    return f":enable='between(t,{start:.3f},{end:.3f})'"


def subtitle_blur_graph(
    *,
    x: int,
    y: int,
    width: int,
    height: int,
    radius: int,
    start: float,
    end: float,
    ass_path: str,
) -> str:
    enable = region_enable(whole_video=False, start=start, end=end)
    parts = [
        "[0:v]split=2[base0][fx0]",
        f"[fx0]crop={width}:{height}:{x}:{y},boxblur=luma_radius={radius}:luma_power=1[rendered0]",
        f"[base0][rendered0]overlay={x}:{y}{enable}[v0]",
        f"[v0]ass=filename='{ass_path}'[vout]",
    ]
    return ";".join(parts)


require(render_enabled(regions=1, subtitle_ready=True),
        "EXPORT-11 Subtitle + Blur must enable Render")
require(render_enabled(regions=1, subtitle_ready=False),
        "EXPORT-11 Blur remains independently exportable")
require(render_enabled(regions=0, subtitle_ready=True),
        "EXPORT-11 completed Subtitle remains independently exportable")
require(not render_enabled(regions=0, subtitle_ready=False),
        "EXPORT-11 empty project must not enable Render")

graph = subtitle_blur_graph(
    x=120,
    y=700,
    width=900,
    height=170,
    radius=18,
    start=3.25,
    end=8.75,
    ass_path="caption.ass",
)
require("boxblur=luma_radius=18" in graph, "EXPORT-11 fixture lost Blur")
require("enable='between(t,3.250,8.750)'" in graph, "EXPORT-11 fixture lost Blur timing")
require("[v0]ass=filename='caption.ass'[vout]" in graph,
        "EXPORT-11 fixture must burn subtitle after Blur")
require(graph.index("boxblur") < graph.index("ass=filename="),
        "EXPORT-11 fixture ordering must be Blur first, subtitle second")
require(graph.endswith("[vout]"), "EXPORT-11 graph must finish at [vout]")

print("PASS: EXPORT-11 Subtitle + Blur export contract is locked")
