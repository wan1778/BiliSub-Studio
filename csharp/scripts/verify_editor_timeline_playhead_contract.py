#!/usr/bin/env python3
"""Kiểm tra playhead dải thời gian: bám lưới khung hình, lưu theo project và tự cuộn theo."""
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages"
CORE = ROOT / "csharp/src/BiliSubStudio.Core/Editor"
TIMELINE = PAGES / "EditorPage.Timeline.cs"
COMPOSITION = PAGES / "EditorPage.TimelineComposition.cs"
CUE_EDITING = PAGES / "EditorPage.SubtitleCueEditing.cs"
PLAYHEAD_SAVE = PAGES / "EditorPage.PlayheadSave.cs"
PLAYBACK = PAGES / "EditorPage.Playback.cs"
EDITOR = PAGES / "EditorPage.xaml.cs"


def require(condition: bool, message: str) -> None:
    if not condition:
        print("FAIL:", message, file=sys.stderr)
        raise SystemExit(1)


def window(text: str, marker: str, length: int = 3200) -> str:
    index = text.find(marker)
    if index < 0:
        return ""
    return text[index:index + length]


timeline = TIMELINE.read_text(encoding="utf-8")
composition = COMPOSITION.read_text(encoding="utf-8")
cue_editing = CUE_EDITING.read_text(encoding="utf-8")
playhead_save = PLAYHEAD_SAVE.read_text(encoding="utf-8")
playback = PLAYBACK.read_text(encoding="utf-8")
editor = EDITOR.read_text(encoding="utf-8")
playhead = (CORE / "EditorTimelinePlayhead.cs").read_text(encoding="utf-8")
policy = (CORE / "EditorPlayheadSavePolicy.cs").read_text(encoding="utf-8")
store = (CORE / "EditorProjectStore.cs").read_text(encoding="utf-8")

# PLAYHEAD-01: the playhead math lives in Core as pure functions.
for token in (
    "public static class EditorTimelinePlayhead",
    "public const double DefaultFrameRate = 30d;",
    "public const double RevealMarginRatio = .05d;",
    "public static long FrameIndex(double time, double frameRate)",
    "public static double FrameTime(long frameIndex, double frameRate)",
    "public static double SnapToFrame(double time, double frameRate)",
    "public static bool IsVisible(",
    "public static double? RevealScrollOffset(",
    "public static bool MovedToAnotherFrame(",
    "public static double? FollowScrollOffset(",
):
    require(token in playhead, f"PLAYHEAD-01 the core playhead math is missing: {token}")

# PLAYHEAD-02: snap time = frame index / fps, the playhead always sits on the
# frame grid of the media being seeked (not a free-floating number).
require("public static double SnapToFrame(double time, double frameRate)\n        => FrameTime(FrameIndex(time, frameRate), frameRate);" in playhead,
        "PLAYHEAD-02 the snapped time must be frame index / fps")
require("var index = (long)Math.Floor(time * NormalizeFrameRate(frameRate));" in playhead,
        "PLAYHEAD-02 the frame index must floor the time (FilmCraft snap)")
require("double.IsFinite(frameRate) && frameRate > 0 ? frameRate : DefaultFrameRate" in playhead,
        "PLAYHEAD-02 an unknown frame rate must fall back to 30fps")

for token in (
    "private double TimelineFrameRateAt(double timelineTime)",
    "private double SnapTimelinePlayhead(double timelineTime)",
    "EditorTimelinePlayhead.SnapToFrame(timelineTime, TimelineFrameRateAt(timelineTime))",
    "TimelineClipMediaCached(clip)",
    "_media is { FrameRate: > 0 }",
):
    require(token in timeline, f"PLAYHEAD-02 the frame-grid owner is missing: {token}")

require("var snapped = SnapTimelinePlayhead(timelineTime);" in cue_editing,
        "PLAYHEAD-02 the seek owner must snap the playhead to a media frame")
require("var snapped = SnapTimelinePlayhead(value);" in editor,
        "PLAYHEAD-02 the slider owner must snap the playhead to a media frame")

# PLAYHEAD-03: the playhead is saved per project, an old project (missing the
# field) still opens and does not break the saved data.
for token in (
    "double PlayheadSeconds = 0);",
    "public static double NormalizePlayhead(double? playhead)",
    "PlayheadSeconds = NormalizePlayhead(loaded.PlayheadSeconds),",
    "PlayheadSeconds = NormalizePlayhead(project.PlayheadSeconds),",
    "public const int CurrentSchema = 7;",
):
    require(token in store, f"PLAYHEAD-03 the persisted playhead owner lost: {token}")
require("PlayheadSeconds = Timeline.Value," in editor,
        "PLAYHEAD-03 the project snapshot must carry the playhead position")

# PLAYHEAD-04: auto-follow when the playhead sits outside the viewport, but do
# NOT scroll continuously while the user is scrolling by hand (only scroll when
# the playhead changes frame) and it must use the exact Core function.
for token in (
    "private void FollowTimelinePlayhead(double timelineTime)",
    "private void RevealTimelinePlayhead(double timelineTime)",
    "EditorTimelinePlayhead.FollowScrollOffset(",
    "EditorTimelinePlayhead.RevealScrollOffset(",
    "var previous = _timelineSeenPlayhead;",
    "_timelineSeenPlayhead = timelineTime;",
):
    require(token in timeline, f"PLAYHEAD-04 the auto-follow owner lost: {token}")

require("EditorTimelinePlayhead.RevealScrollOffset(" not in window(timeline, "private void ApplyTimelineZoomPixels(double newPps"),
        "PLAYHEAD-04 the zoom path must not scroll the strip to the playhead (the zoom anchor stays put)")
require("FollowTimelinePlayhead(target);" in cue_editing,
        "PLAYHEAD-04 the seek path must move the strip to the playhead")
require("FollowTimelinePlayhead(value);" in editor,
        "PLAYHEAD-04 the slider path must move the strip to the playhead")
require("FollowTimelinePlayhead(saved)" not in timeline,
        "PLAYHEAD-04 the restore path must reveal the playhead even in the same frame")

# PLAYHEAD-05: the playhead is drawn at the composed strip coordinate, without
# mapping it again.
require("var x = EditorTimelineLayout.XForTime(Timeline.Value, _timelinePixelsPerSecond);" in timeline,
        "PLAYHEAD-05 the playhead must be drawn at the composed timeline position")
require("TimelineTimeForSource(Timeline.Value)" not in timeline,
        "PLAYHEAD-05 the playhead must not be mapped as a source time again")

# PLAYHEAD-06: the executable contract must lock the playhead behavior.
tests = (ROOT / "csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs").read_text(encoding="utf-8")
require("editor playhead snaps to media frames, persists per project and follows the viewport" in tests,
        "PLAYHEAD-06 the executable contract test is missing")
contract = (ROOT / "csharp/tests/BiliSubStudio.Core.ContractTests/EditorTimelinePlayheadContract.cs").read_text(encoding="utf-8")
for token in (
    "EditorTimelinePlayhead.SnapToFrame(1.017, 30)",
    "EditorTimelinePlayhead.FollowScrollOffset(",
    "EditorTimelinePlayhead.RevealScrollOffset(",
    "PlayheadSeconds = 12.5",
    "Equal(0d, legacyLoaded.PlayheadSeconds)",
):
    require(token in contract, f"PLAYHEAD-06 the contract test lost: {token}")

# PLAYHEAD-07: the seek slider rounds to the frame grid of the media, so mouse
# drags also land on an exact frame (WinUI rounds by StepFrequency itself).
for token in (
    "private void ApplyTimelineStepFrequency()",
    "Timeline.StepFrequency = frameRate > 0 ? 1d / frameRate : 0.001d;",
    "ApplyTimelineStepFrequency();",
):
    require(token in timeline, f"PLAYHEAD-07 the slider step grid owner is missing: {token}")

# PLAYHEAD-08: when the user seeks, the playhead position is saved per project,
# but the disk write must be debounced (one mouse drag writes once) and must
# absolutely never write continuously while playing.
for token in (
    "public sealed class EditorPlayheadSavePolicy",
    "public const double DefaultDebounceMilliseconds = 800d;",
    "public void Schedule(double nowMilliseconds)",
    "public bool IsDue(double nowMilliseconds, bool isPlaying)",
    "public bool TryConsume(double nowMilliseconds, bool isPlaying)",
    "public void Clear()",
):
    require(token in policy, f"PLAYHEAD-08 the playhead save policy is missing: {token}")

for token in (
    "private void SchedulePlayheadSave()",
    "internal void NotifyEditorPlaybackStopped()",
    "private async Task FlushPlayheadSaveAsync()",
    "private void ClearPlayheadSave()",
    "_playheadSavePolicy.Schedule(PlayheadSaveNowMilliseconds());",
    "if (_playback.IsPlaying) return;",
    "timer.Interval = TimeSpan.FromMilliseconds(EditorPlayheadSavePolicy.DefaultDebounceMilliseconds);",
    "timer.IsRepeating = false;",
    "await SaveProjectNowAsync();",
):
    require(token in playhead_save, f"PLAYHEAD-08 the debounce owner is missing: {token}")

require("SchedulePlayheadSave();" in editor,
        "PLAYHEAD-08 the slider seek must queue a playhead write")
require("SchedulePlayheadSave();" in cue_editing,
        "PLAYHEAD-08 the seek command (ruler, lane, cue) must queue a playhead write")
require("NotifyEditorPlaybackStopped();" in window(playback, "internal async Task SetModeAsync(bool enabled, bool play)"),
        "PLAYHEAD-08 stopping the preview must flush the queued playhead once")
require("SchedulePlayheadSave" not in playback,
        "PLAYHEAD-08 playback must never queue a playhead write by itself")
require("_page._syncingTimeline = true;" in window(playback, "private void PlayerPositionChanged"),
        "PLAYHEAD-08 the playback playhead update must not look like a user seek")
require("SchedulePlayheadSave" not in window(timeline, "private void RestoreTimelinePlayhead"),
        "PLAYHEAD-08 restoring the saved playhead must not queue a write")
require("RestoreTimelinePlayheadFrameGridAsync" not in timeline,
        "PLAYHEAD-08 restoring must keep the saved position instead of re-snapping it")
require("EditorTimelinePlayhead.RestoreTime(saved)" in timeline,
        "PLAYHEAD-08 restoring must use the saved position as it is")
require("ClearPlayheadSave();" in window(timeline, "private void ResetTimelineForNewSource"),
        "PLAYHEAD-08 opening another project must drop the queued playhead write")

# PLAYHEAD-09: the executable contract must lock the playhead save behavior
# when seeking.
require("editor playhead saves on seek with debounce and never while playing" in tests,
        "PLAYHEAD-09 the executable contract test is missing")
save_contract = (ROOT / "csharp/tests/BiliSubStudio.Core.ContractTests/EditorPlayheadSaveContract.cs").read_text(encoding="utf-8")
for token in (
    "new EditorPlayheadSavePolicy()",
    "policy.Schedule(",
    "policy.TryConsume(10_000d + 60_000d, isPlaying: true)",
    "stopping playback must write the queued playhead once",
    "a slider drag must write the playhead once the drag settles",
):
    require(token in save_contract, f"PLAYHEAD-09 the contract test lost: {token}")

print("PASS: PLAYHEAD-01/02/03/04/05/06/07/08/09 frame-snapped playhead, per-project persistence, debounced save on seek and viewport auto-follow are locked")
