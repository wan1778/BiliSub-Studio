#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages"
EDITOR = PAGES / "EditorPage.xaml.cs"
TIMELINE = PAGES / "EditorPage.Timeline.cs"
COMPOSITION = PAGES / "EditorPage.TimelineComposition.cs"
PLAYBACK = PAGES / "EditorPage.Playback.cs"
CUE_EDITING = PAGES / "EditorPage.SubtitleCueEditing.cs"


def require(condition: bool, message: str) -> None:
    if not condition:
        print("FAIL:", message, file=sys.stderr)
        raise SystemExit(1)


def window(text: str, marker: str, length: int = 1400) -> str:
    index = text.find(marker)
    if index < 0:
        return ""
    return text[index:index + length]


editor = EDITOR.read_text(encoding="utf-8")
timeline = TIMELINE.read_text(encoding="utf-8")
composition = COMPOSITION.read_text(encoding="utf-8")
composition_clip = (ROOT / "csharp/src/BiliSubStudio.Core/Editor/EditorTimelineClip.cs").read_text(encoding="utf-8")
store = (ROOT / "csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs").read_text(encoding="utf-8")
playback = PLAYBACK.read_text(encoding="utf-8")
cue_editing = CUE_EDITING.read_text(encoding="utf-8")

# TIMELINE-01: syncing the strip duration must refresh the clock, otherwise
# after adding/removing a clip the clock still shows the old total duration.
sync = window(timeline, "private void SyncTimelineDuration()")
require(sync != "", "TIMELINE-01 SyncTimelineDuration owner is missing")
require("Timeline.Maximum = Math.Max(0.1, duration);" in sync,
        "TIMELINE-01 the timeline range must follow the composed duration")
require("UpdateClock();" in sync,
        "TIMELINE-01 the clock must refresh whenever the composed duration changes")

require('ClockText.Text = $"{FormatClock(Timeline.Value, duration >= 3600)} / {FormatClock(duration)}";' in editor,
        "TIMELINE-01 the clock must show the composed duration, not the single source duration")

# TIMELINE-02: add/split/remove clip must all go through the same strip owner.
apply_clips = window(timeline, "private void ApplyTimelineClips(")
for token in (
    "_timelineClips = clips;",
    "SyncTimelineDuration();",
    "QueueTimelineRender();",
):
    require(token in apply_clips, f"TIMELINE-02 clip owner lost: {token}")

for token in (
    "private async void TimelineAddClipButton_Click(",
    "private async void TimelineSplitClipButton_Click(",
    "private async void TimelineRemoveClipButton_Click(",
    "EditorTimelineComposition.SplitAtSourceTime(",
    "EditorTimelineComposition.RemoveAt(",
):
    require(token in timeline, f"TIMELINE-02 clip action lost: {token}")

# TIMELINE-03: the clip model follows FilmCraft: an in point inside the source
# file + a duration, the out point is always derived (not stored separately),
# and old JSON is still readable.
for token in (
    "double SourceIn,",
    "double Duration,",
    "public double End => SourceIn + Math.Max(0, Duration);",
):
    require(token in composition_clip, f"TIMELINE-03 the clip model lost: {token}")

require("Converters = { new EditorTimelineClipJsonConverter() }," in store,
        "TIMELINE-03 the store must read legacy clips through the timeline clip converter")
# subtitles and the voice track only apply to the project's primary clip;
# effect regions are mapped onto every clip they cover.
for token in (
    "internal (EditorTimelineClip Clip, double SourceTime, double TimelineStart) LocateTimelineClip(",
    "internal bool IsPrimaryTimelineClip(",
    "internal async Task<VideoEditRequest> TimelineClipEditRequestAsync(",
    "var primary = IsPrimaryTimelineClip(clip);",
    "var burn = primary ? subtitle : null;",
    "RegionsForTimelineClip(clip, TimelineStartOfClip(clip)),",
    "primary ? _voiceTrack : null,",
    "Trim: new EditorTrimRange(clip.SourceIn, clip.End)",
):
    require(token in composition, f"TIMELINE-03 composition owner lost: {token}")

for token in (
    "var (clip, sourceTime, timelineStart) = _page.LocateTimelineClip(requestedStart);",
    "await _page.TimelineClipEditRequestAsync(",
    "private async Task<bool> ContinueWithNextClipAsync()",
    "TimelinePositionForSource(",
):
    require(token in playback, f"TIMELINE-03 preview owner lost: {token}")

# TIMELINE-04: every coordinate must share one base (composed strip time for
# the playhead, source file time for effect regions/subtitles) and every seek
# must go through the clip-aware path.
for token in (
    "var regions = PreviewRegionsForClip(clip, timelineStart);",
    "var active = VideoEditorService.IsActiveAt(mapped, activeSourceTime);",
    "if (MapRegionToTimelineClip(region, activeClip, activeClipStart) is not { } mapped) continue;",
    "var previewTime = Math.Clamp(",
    "Math.Min(media.Duration, clip.End) - PreviewFrameTailGuardSeconds",
):
    require(token in editor, f"TIMELINE-04 preview overlay owner lost: {token}")

for token in (
    "private async Task SeekEditorToTimelineAsync(double timelineTime)",
    "var snapped = SnapTimelinePlayhead(timelineTime);",
    "var target = Math.Clamp(snapped, Timeline.Minimum, Timeline.Maximum);",
    "await SeekEditorToTimelineAsync(TimelineTimeForSource(sourceTime));",
):
    require(token in cue_editing, f"TIMELINE-04 seek owner lost: {token}")

for token in (
    "private void SeekTimelineFromPointer(PointerRoutedEventArgs e)",
    "_ = SeekEditorToTimelineAsync(timelineTime);",
    "if (sourceTime >= clip.SourceIn && sourceTime <= clip.End)",
    "private IReadOnlyList<(double Start, double End)> TrimExcludedTimelineRanges()",
    "foreach (var range in TrimExcludedTimelineRanges())",
):
    require(token in timeline, f"TIMELINE-04 composed-time owner lost: {token}")

# TIMELINE-05: the UI text must talk about the composed strip: after a trim it
# is no longer the playback boundary.
require('"Đã xem hết dải thời gian. Bấm Play để phát lại từ mốc đầu."' in playback,
        "TIMELINE-05 the end-of-playback message must talk about the composed timeline")
require("Preview sẽ tiếp tục đến hết dải thời gian." in playback,
        "TIMELINE-05 the preview announcement must talk about the composed timeline")

# TIMELINE-06: effect regions stay anchored to the primary clip's source file but
# may continue into the following clips. The timeline maps a composed position
# back to region time, clips the mapped range to each clip, and the project
# store must not truncate an extended region when it is saved or reopened.
for token in (
    "private (EditorTimelineClip Clip, double TimelineStart)? PrimaryTimelineAnchor()",
    "return anchor.Clip.SourceIn + (timelineTime - anchor.TimelineStart);",
    "private double RegionTimeLimit()",
    "internal IReadOnlyList<EditRegion> RegionsForTimelineClip(EditorTimelineClip clip, double timelineStart)",
    "private EditRegion? MapRegionToTimelineClip(",
    "Math.Clamp(clip.SourceIn + (start - timelineStart), clip.SourceIn, clip.End)",
    "Math.Clamp(clip.SourceIn + (end - timelineStart), clip.SourceIn, clip.End)",
):
    require(token in composition, f"TIMELINE-06 the cross-clip effect mapping lost: {token}")
require("EditorTimelineComposition.RegionTimeLimit(project.Timeline, project.Source.Path, sourceDuration)" in store,
        "TIMELINE-06 the project save/load must keep an effect region extended past the primary clip")
require("public static double RegionTimeLimit(" in
        (ROOT / "csharp/src/BiliSubStudio.Core/Editor/EditorTimelineComposition.cs").read_text(encoding="utf-8"),
        "TIMELINE-06 the region-time limit must be owned by the timeline composition")

print("PASS: TIMELINE-01..06 composed timeline duration, clip actions, clip-aware preview, cross-clip effects and time-base owners are locked")
