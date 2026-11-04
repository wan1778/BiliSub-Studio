#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CORE = ROOT / "csharp/src/BiliSubStudio.Core/Editor"
APP = ROOT / "csharp/src/BiliSubStudio.App/Pages"
SERVICE = CORE / "VideoEditorService.cs"
COMPOSED = CORE / "VideoEditorService.Composed.cs"
SEGMENT = CORE / "VideoEditSegment.cs"
EDITOR = APP / "EditorPage.xaml.cs"
IMAGES = APP / "EditorPage.Images.cs"
DIALOG = APP / "EditorPage.ExportDialog.cs"
TESTS = ROOT / "csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs"


def require(condition: bool, message: str) -> None:
    if not condition:
        print("FAIL:", message, file=sys.stderr)
        raise SystemExit(1)


service = SERVICE.read_text(encoding="utf-8")
composed = COMPOSED.read_text(encoding="utf-8")
segment = SEGMENT.read_text(encoding="utf-8")
editor = EDITOR.read_text(encoding="utf-8")
images = IMAGES.read_text(encoding="utf-8")
dialog = DIALOG.read_text(encoding="utf-8")
tests = TESTS.read_text(encoding="utf-8")

# EXPORT-MULTI-01: the export segment model.
for token in (
    "public sealed record VideoEditSegment(",
    "public double Duration => Math.Max(0, End - Start);",
):
    require(token in segment, f"EXPORT-MULTI-01 segment model lost: {token}")
require("IReadOnlyList<VideoEditSegment>? Segments = null);" in service,
        "EXPORT-MULTI-01 the edit request must carry the composed segments")

# EXPORT-MULTI-02: the core that concatenates several segments — the old
# single-source export path stays unchanged.
require("public sealed partial class VideoEditorService" in service,
        "EXPORT-MULTI-02 the video editor service must own the composed path")
require("if (request.Segments is { Count: > 1 } segments)" in service
        and "return await RunComposedAsync(job, request, segments);" in service,
        "EXPORT-MULTI-02 a multi-segment request must take the composed export path")
for token in (
    "private async Task<VideoEditResult> RunComposedAsync(",
    "private async Task<string> RenderComposedSegmentAsync(",
    "internal static IReadOnlyList<string> BuildComposedSegmentArguments(",
    "internal static string BuildConcatList(",
    "internal static IReadOnlyList<string> BuildConcatArguments(",
    "scale=",
    ":force_original_aspect_ratio=decrease",
    "pad=",
    "anullsrc=r=",
    '"aresample=" + ComposedAudioSampleRate',
    "private const int ComposedAudioSampleRate = 48000;",
):
    require(token in composed, f"EXPORT-MULTI-02 composed owner lost: {token}")
require('"-c", "copy",' in composed,
        "EXPORT-MULTI-02 the concat must copy the normalized streams instead of encoding twice")
require('"-f", "concat", "-safe", "0", "-i", listPath,' in composed,
        "EXPORT-MULTI-02 the composed segments must be joined with the concat demuxer")
# FFmpeg requires the concat file to have a header, a duration directive and
# the same time base.
require('builder.Append("ffconcat version 1.0\\n");' in composed
        and 'builder.Append("duration ")' in composed,
        "EXPORT-MULTI-02 the concat list must follow the FFmpeg format (header + duration directive)")
require('"-video_track_timescale"' in composed
        and "private const int ComposedVideoTrackTimescale = 90000;" in composed,
        "EXPORT-MULTI-02 every segment must share one time base for the concat demuxer")
require("BuildConcatList(rendered, prepared.Select(segment => segment.Duration).ToArray())" in composed,
        "EXPORT-MULTI-02 the concat list must carry the real duration of every segment")

# EXPORT-MULTI-03: the App builds the export segments from the composed strip.
for token in (
    "private async Task<VideoEditRequest> CurrentComposedEditRequestAsync(",
    "var segments = new List<VideoEditSegment>(_timelineClips.Count);",
    "RegionsForTimelineClip(clip, starts[index]),",
    "primary ? subtitle : null,",
    "primary ? _voiceTrack : null));",
    "return request with { Segments = segments };",
):
    require(token in editor, f"EXPORT-MULTI-03 App owner lost: {token}")
require(images.count("await CurrentComposedEditRequestAsync(subtitle)") == 2,
        "EXPORT-MULTI-03 both export stages must use the composed request")
require("|| hasComposedClips;" in images,
        "EXPORT-MULTI-03 adding clips alone must be enough to export")
require("private double ComposedExportDuration(EditorTrimRange trim)" in dialog,
        "EXPORT-MULTI-03 the export dialog must show the composed duration")

# EXPORT-MULTI-04: a real regression run with FFmpeg.
require("EditorComposedExportContract.RunAsync" in tests,
        "EXPORT-MULTI-04 the composed export contract test is missing")
require("editor composed export normalizes every segment and joins them into one file" in tests,
        "EXPORT-MULTI-04 the composed export contract test is not registered")

print("PASS: EXPORT-MULTI-01/02/03/04 composed multi-clip export is locked")
