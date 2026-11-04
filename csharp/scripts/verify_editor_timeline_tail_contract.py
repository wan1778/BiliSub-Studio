#!/usr/bin/env python3
"""Kiểm tra dải Timeline luôn có một khoảng trống phía sau video, đúng bằng
thời lượng video (video 10 phút → dải làm việc tới phút 20). Khoảng trống này
là vùng làm việc thật, nằm trên cùng trục thời gian thật nên không bị kéo giãn
khi Zoom, và thời lượng video/clip/export giữ nguyên."""
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
XAMLCS = PAGES / "EditorPage.xaml.cs"
LAYOUT = CORE / "EditorTimelineLayout.cs"
TESTS = ROOT / "csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs"
TESTFILE = ROOT / "csharp/tests/BiliSubStudio.Core.ContractTests/EditorTimelineTailContract.cs"

WORKING = "var duration = TimelineWorkingDuration;"


def require(condition: bool, message: str) -> None:
    if not condition:
        print("FAIL:", message, file=sys.stderr)
        raise SystemExit(1)


def window(text: str, marker: str, length: int = 3200) -> str:
    index = text.find(marker)
    if index < 0:
        return ""
    return text[index:index + length]


for path in (TIMELINE, XAMLCS, LAYOUT, TESTS, TESTFILE):
    require(path.is_file(), f"TAIL-00 the contract owner is missing: {path}")
timeline = TIMELINE.read_text(encoding="utf-8")
xamlcs = XAMLCS.read_text(encoding="utf-8")
layout = LAYOUT.read_text(encoding="utf-8")
tests = TESTS.read_text(encoding="utf-8")
testfile = TESTFILE.read_text(encoding="utf-8")

# TAIL-01: working extent = video + an empty tail of the SAME real length.
span = window(layout, "public static double WorkingSpan(", 600)
require(span != "", "TAIL-01 EditorTimelineLayout.WorkingSpan is missing")
require("duration * 2" in span,
        "TAIL-01 the working span must double the video so the tail equals it")

# TAIL-02: the page exposes that span as its strip extent (real seconds).
prop = window(xamlcs, "private double TimelineWorkingDuration", 400)
if prop == "":
    prop = window(timeline, "private double TimelineWorkingDuration", 400)
require(prop != "", "TAIL-02 TimelineWorkingDuration is missing")
require("EditorTimelineLayout.WorkingSpan(" in prop,
        "TAIL-02 the strip extent must come from EditorTimelineLayout.WorkingSpan")

# TAIL-03: every extent consumer (content width, ruler range, scroll range,
# playhead range, clock) works through the doubled span, so the ruler and all
# tracks continue into the tail instead of stopping at the video's end.
require(timeline.count(WORKING) >= 7,
        "TAIL-03 the timeline extent sites must use the working span (tail)")
for token in (
    "EditorTimelineLayout.ContentWidth(duration, pps)",
    "Timeline.Maximum = Math.Max(0.1, duration);",
    "var timelineDuration = TimelineWorkingDuration;",
    "EditorTimelineLayout.ContentWidth(TimelineWorkingDuration, _timelinePixelsPerSecond)",
):
    require(token in timeline,
            f"TAIL-03 the strip extent lost the working span: {token}")
# The clock is a readout, not an extent site: it reports the REAL total
# (composed clips, else the media duration). Printing the doubled working span
# told a 5h video it was 10h long; the tail stays, the total must not lie.
clock = window(xamlcs, "private void UpdateClock(", 420)
require(clock != "", "TAIL-03 the UpdateClock owner is missing")
require("var duration = TimelineDuration > 0 ? TimelineDuration : _media?.Duration ?? 0;" in clock,
        "TAIL-03 the clock must report the real total duration")
require("TimelineWorkingDuration" not in clock,
        "TAIL-03 the clock must not print the doubled working span")
require('ClockText.Text = $"{FormatClock(Timeline.Value, duration >= 3600)} / {FormatClock(duration)}";' in clock,
        "TAIL-03 the clock format must stay exactly as reviewed")

# TAIL-04: the video itself is untouched - playback, export and the clip
# geometry keep the REAL composed duration so the tail is never exported and
# the video is never stretched.
playback = (PAGES / "EditorPage.Playback.cs").read_text(encoding="utf-8")
require("var duration = _page.TimelineDuration;" in playback,
        "TAIL-04 playback must keep the real composed duration (no stretch)")
require("Timeline.Value = _page.TimelineDuration;" in playback,
        "TAIL-04 playback must stop at the video's real end, not in the tail")
require("private double TimelineDuration => EditorTimelineComposition.TotalDuration(_timelineClips);"
        in timeline,
        "TAIL-04 the real composed duration must stay the clip sum (export truth)")

# TAIL-05: the behavior is pinned by a contract test.
require('editor timeline strip keeps an empty working tail as long as the video behind it' in tests,
        "TAIL-05 the working-tail contract test is not registered")
for token in ("WorkingSpan(600) == 1200", "WorkingSpan(3600) == 7200",
              "WorkingSpan(7200) == 14400", "WorkingSpan(0) == 0"):
    require(token in testfile, f"TAIL-05 the contract test lost: {token}")

print("PASS: timeline keeps an empty working tail as long as the video (real seconds, zoom-safe)")
