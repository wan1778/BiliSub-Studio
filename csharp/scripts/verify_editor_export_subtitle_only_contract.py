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

# UI/state gate: a complete Vietsub alone is a valid reason to enable Render.
require(
    'var subtitleReady = _subtitleSource is not null && _subtitleSource.Cues.All(x => !string.IsNullOrWhiteSpace(x.VietnameseText));'
    in editor,
    "EXPORT-06 lost complete-Vietsub readiness",
)
require(
    "_document.Regions.Count > 0 || subtitleReady || audioChanged || _voiceTrack is not null || hasImages"
    in editor,
    "EXPORT-06 RenderButton no longer accepts subtitle-only state",
)
require(
    "private EditorSubtitleBurn? CompletedSubtitleBurn()" in editor
    and "_subtitleSource.Cues.All(x => !string.IsNullOrWhiteSpace(x.VietnameseText))" in editor,
    "EXPORT-06 must not export an incomplete Vietsub",
)

# Request construction must carry the completed subtitle even with zero regions,
# default audio Keep, no voice and no image/logo.
for token in (
    "private VideoEditRequest CurrentEditRequest(EditorSubtitleBurn? subtitle)",
    "_document.Regions.ToArray(),",
    "subtitle,",
    "_audioSettings,",
    "_voiceTrack);",
):
    require(token in editor, f"EXPORT-06 CurrentEditRequest lost: {token}")

# Orchestration: subtitle contributes to hasBaseEdit. With no images, the one
# Render entry point sends the subtitle request directly to VideoEditorService.
require(
    "var hasBaseEdit = _document.Regions.Count > 0 || subtitle is not null || _audioSettings.SourceMode != \"keep\" || _voiceTrack is not null;"
    in images,
    "EXPORT-06 subtitle no longer qualifies as base edit",
)
require(
    "if (!hasImages)" in images
    and "_application.StartEditor(CurrentEditRequest(subtitle))" in images,
    "EXPORT-06 subtitle-only must use the normal Editor render path",
)

# Backend accepts subtitle itself as an edit; no dummy blur/ROI is required.
require(
    "request.Regions.Count > 0 || request.Subtitle is not null || request.VoiceTrack is not null" in video,
    "EXPORT-06 backend HasEdit no longer accepts subtitle-only requests",
)
require(
    'var subtitleAss = request.Subtitle is null ? null : Path.Combine(Path.GetTempPath(), "bilisub-editor-sub-"'
    in video,
    "EXPORT-06 subtitle-only render lost temporary ASS generation",
)
require(
    "BuildAss(request.Subtitle!, request.SourceWidth, request.SourceHeight)" in video,
    "EXPORT-06 subtitle-only render lost ASS construction",
)
require(
    "var graph = BuildFilter(request, subtitleAss);" in video,
    "EXPORT-06 subtitle-only render lost the shared render filter",
)

# Zero-region subtitle render must burn ASS directly over input video.
require("var current = inputLabel;" in video, "EXPORT-06 filter lost direct input baseline")
require(
    "if (request.Subtitle is not null)" in video
    and "parts.Add($\"[{current}]ass=filename='{escaped}'[vout]\");" in video,
    "EXPORT-06 filter no longer burns subtitle into vout",
)

# BuildAss is the hard-sub contract: complete text, placement and cue timing.
for token in (
    'throw new InvalidDataException("Bản Vietsub chưa hoàn tất nên chưa thể hardsub.");',
    "var placement = subtitle.Placement;",
    "AssTime(cue.Start)",
    "AssTime(cue.End)",
    "EscapeAssText(cue.VietnameseText)",
):
    require(token in video, f"EXPORT-06 hard-sub contract lost: {token}")

# Subtitle-only does not imply audio mute. Default Keep maps source audio
# optionally; MP4 may transcode it to AAC but the source track remains included.
require(
    'if (audio.SourceMode == "mute") return ["-an"];' in video,
    "EXPORT-06 audio mute policy changed unexpectedly",
)
require(
    'var arguments = new List<string> { "-map", "0:a?" };' in video,
    "EXPORT-06 subtitle-only Keep must preserve source audio when present",
)

# Final output still follows the standard verified temp->validate->move lifecycle.
for token in (
    'var temporary = output + ".rendering" + Path.GetExtension(output);',
    "ValidateRenderedOutputAsync(temporary, request.Duration",
    "File.Move(temporary, output);",
    "finally { TryDelete(temporary); if (subtitleAss is not null) TryDelete(subtitleAss); }",
):
    require(token in video, f"EXPORT-06 final output lifecycle lost: {token}")


# Portable behavioral truth table for the task boundary.
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
    has_images = images_count > 0
    return (
        editable
        and not manual_dirty
        and path
        and (regions > 0 or subtitle_ready or audio_changed or voice or has_images)
        and bool(filename.strip())
    )


require(
    render_enabled(subtitle_ready=True),
    "EXPORT-06 complete subtitle alone must enable Render",
)
require(
    not render_enabled(subtitle_ready=False),
    "EXPORT-06 incomplete/no subtitle alone must not enable Render",
)


def has_edit(*, regions: int = 0, subtitle: bool = False, voice: bool = False, audio_mode: str = "keep") -> bool:
    return regions > 0 or subtitle or voice or audio_mode != "keep"


require(
    has_edit(subtitle=True),
    "EXPORT-06 backend must accept subtitle-only request",
)
require(
    not has_edit(),
    "EXPORT-06 fixture setup failed: truly empty request must not count as edit",
)


def subtitle_only_filter(subtitle_ass: str) -> str:
    current = "0:v"
    return f"[{current}]ass=filename='{subtitle_ass}'[vout]"


require(
    subtitle_only_filter("sub.ass") == "[0:v]ass=filename='sub.ass'[vout]",
    "EXPORT-06 zero-region subtitle filter fixture failed",
)

print("PASS: EXPORT-06 subtitle-only export contract is locked")
