#!/usr/bin/env python3
"""Kiểm tra sóng âm thanh lane Voice và ảnh filmstrip của clip lane Video:
giải mã peak envelope (20ms) + cache tự mô tả, tile thumbnail theo viewport."""
from __future__ import annotations

import sys
from pathlib import Path

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8")

ROOT = Path(__file__).resolve().parents[2]
PAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages"
CORE = ROOT / "csharp/src/BiliSubStudio.Core/Editor"
TIMELINE = PAGES / "EditorPage.Timeline.cs"
COMPOSITION = PAGES / "EditorPage.TimelineComposition.cs"
XAMLCS = PAGES / "EditorPage.xaml.cs"
WAVEFORM = CORE / "EditorVoiceWaveform.cs"
TESTS = ROOT / "csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs"
TESTFILE = ROOT / "csharp/tests/BiliSubStudio.Core.ContractTests/EditorVoiceWaveformContract.cs"


def require(condition: bool, message: str) -> None:
    if not condition:
        print("FAIL:", message, file=sys.stderr)
        raise SystemExit(1)


def window(text: str, marker: str, length: int = 3200) -> str:
    index = text.find(marker)
    if index < 0:
        return ""
    return text[index:index + length]


def method(text: str, marker: str) -> str:
    index = text.find(marker)
    if index < 0:
        return ""
    end = text.find("\n    }", index)
    return text[index:end if end > 0 else index + 1200]


for path in (TIMELINE, COMPOSITION, XAMLCS, WAVEFORM, TESTS, TESTFILE):
    require(path.is_file(), f"MEDIA-00 the contract owner is missing: {path}")
timeline = TIMELINE.read_text(encoding="utf-8")
composition = COMPOSITION.read_text(encoding="utf-8")
xamlcs = XAMLCS.read_text(encoding="utf-8")
waveform = WAVEFORM.read_text(encoding="utf-8")
tests = TESTS.read_text(encoding="utf-8")

# MEDIA-01: peak envelope decode - 2 kHz mono s16le through ffmpeg, one 20 ms
# peak per bucket, self-describing cache named after the voice file identity.
for token in (
    "public sealed record EditorVoiceWaveform(double BucketSeconds, double DurationSeconds, byte[] Peaks)",
    "private const int BucketMilliseconds = 20;",
    "private const int DecodeRate = 2000;",
    '"BSWV"u8',
    'CachePrefix = "waveform-"',
    '"-map", "0:a:0", "-ac", "1", "-ar", DecodeRate.ToString(),',
    '"-f", "s16le", "-"',
    "internal static byte[] ComputePeaks(",
):
    require(token in waveform, f"MEDIA-01 the waveform decode lost: {token}")

# MEDIA-02: the render queue keeps the waveform in step with the voice track:
# the build runs off the UI thread, the finished peaks trigger exactly one more
# render, and a replaced voice starts a fresh build instead of reusing stale peaks.
queue = window(timeline, "private void QueueTimelineRender()", 900)
require(queue != "", "MEDIA-02 the render queue owner is missing")
require("EnsureVoiceWaveformForRender();" in queue,
        "MEDIA-02 the waveform hook is missing from the render queue")
for token in (
    "private void EnsureVoiceWaveformForRender()",
    "private async Task BuildVoiceWaveformAsync(string source)",
    "private void FinishVoiceWaveformBuild(string source, EditorVoiceWaveform? built)",
    "_voiceWaveformBuildRunning",
    "EditorVoiceWaveform.LoadOrBuildAsync(",
    "EditorVoiceWaveform? _voiceWaveform",
):
    require(token in timeline, f"MEDIA-02 the waveform builder lost: {token}")
require("_voiceWaveform = null;" in xamlcs,
        "MEDIA-02 clearing the voice state must also clear the waveform")

# MEDIA-03: the voice lane draws the envelope as columns over the green block,
# clipped to the visible range so long voice tracks stay cheap.
voice = window(timeline, "private void RenderTimelineVoiceBlocks(", 2400)
require(voice != "", "MEDIA-03 the voice lane render is missing")
require("RenderTimelineVoiceWaveform(track, left, width, pps, visibleStart, visibleEnd);" in voice,
        "MEDIA-03 the waveform draw call is missing from the voice lane")
for token in (
    "private void RenderTimelineVoiceWaveform(",
    "TimelineWaveformBrush",
):
    require(token in timeline, f"MEDIA-03 the waveform draw lost: {token}")

# MEDIA-04: every visible clip prefetches its first frame; the block tiles the
# thumbnail across the visible range (WinUI has no tiled brush) on a clipped
# canvas, and the selection tint stays visible as a translucent surface.
video = window(timeline, "private void RenderTimelineVideoBlocks(", 3600)
require(video != "", "MEDIA-04 the video lane render is missing")
for token in (
    "PrefetchTimelineClipThumbnail(clip);",
    "block.SetThumbTiles(",
    "TimelineTranslucent(",
    "TimelineThumbTileWidth(",
):
    require(token in video, f"MEDIA-04 the video lane lost: {token}")
for token in (
    "internal void PrefetchTimelineClipThumbnail(EditorTimelineClip clip)",
    "TimelineClipThumbnailKey(EditorTimelineClip clip)",
    "DecodeTimelineClipThumbnailAsync(bytes)",
    '"ClipThumbnails"',
    "_application.Media.GetFrameJpegAsync(",
):
    require(token in composition, f"MEDIA-04 the thumbnail pipeline lost: {token}")
for token in (
    "private readonly Canvas _thumbCanvas;",
    "public void SetThumbTiles(ImageSource? source, double tileWidth, double fromX, double toX)",
    "new RectangleGeometry { Rect = new Rect(0, 0, Width, Height) }",
):
    require(token in timeline, f"MEDIA-04 the filmstrip block lost: {token}")

# MEDIA-05: the waveform contract test is registered in the test list.
require("voice waveform peaks decode in 20 ms buckets" in tests,
        "MEDIA-05 the waveform contract test is missing from the registry")

# MEDIA-06: an SRT attached to a non-primary clip must be drawn in the
# subtitle lane under that clip, mapped onto the composed timeline and clamped
# to the clip's trimmed range. Before this, clip 2 could have a valid SRT (burn
# and voice worked) while the lane stayed empty because it only read the
# project's primary subtitle source.
subtitle_lane = method(timeline, "private void RenderTimelineSubtitleBlocks(")
require(subtitle_lane != "", "MEDIA-06 the subtitle lane render is missing")
require("RenderTimelineClipSubtitleBlocks(pps, visibleStart, visibleEnd);" in subtitle_lane,
        "MEDIA-06 the subtitle lane must render SRT bound to non-primary clips")
clip_lane = method(timeline, "private void RenderTimelineClipSubtitleBlocks(")
require(clip_lane != "", "MEDIA-06 the per-clip subtitle lane render is missing")
for token in (
    "if (IsPrimaryTimelineClip(clip)) continue;",
    "_clipSubtitleSources.TryGetValue(clip.Id, out var source)",
    "TryTimelineClipCueSpan(clip, clipStart, cue, out var timelineStart, out var timelineEnd)",
    "SRT riêng của đoạn",
    "TimelineClipSubtitlePressed(captured, seekTime, args)",
):
    require(token in clip_lane, f"MEDIA-06 the per-clip subtitle lane lost: {token}")
cue_span = method(timeline, "private static bool TryTimelineClipCueSpan(")
for token in (
    "clipStart + (cue.Start - clip.SourceIn)",
    "clipStart + (cue.End - clip.SourceIn)",
    "Math.Min(clipStart + clip.Duration",
    "return timelineEnd > timelineStart;",
):
    require(token in cue_span, f"MEDIA-06 the clip-cue time mapping lost: {token}")

print("PASS: MEDIA-01..06 the voice waveform envelope, the clip filmstrip thumbnails and the per-clip SRT lane are locked")
