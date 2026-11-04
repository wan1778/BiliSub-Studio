#!/usr/bin/env python3
"""Kiểm tra thứ tự lane của dải thời gian (video · voice · phụ đề · hiệu ứng)
và chiều cao panel đủ cho mọi lane (lane dưới cùng không bị cắt)."""
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
XAML = PAGES / "EditorPage.xaml"
TESTS = ROOT / "csharp/tests/BiliSubStudio.Core.ContractTests"


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
xaml = XAML.read_text(encoding="utf-8")
lane = (CORE / "EditorTimelineLane.cs").read_text(encoding="utf-8")
layout = (CORE / "EditorTimelineLayout.cs").read_text(encoding="utf-8")

# LANEORDER-01: the lane order is one source of truth in Core: effects on top,
# then subtitles, video, and the audio (voice) track at the bottom.
for token in (
    "public enum EditorTimelineLane",
    "Effect = 0,",
    "Subtitle = 1,",
    "Video = 2,",
    "Voice = 3,",
):
    require(token in lane, f"LANEORDER-01 the lane order enum lost: {token}")
for token in (
    "public const int LaneCount = 4;",
    "public static double LaneTop(EditorTimelineLane lane) => LaneTop((int)lane);",
    "public static IReadOnlyList<EditorTimelineLane> LaneOrder { get; } =",
    "EditorTimelineLane.Effect,",
    "EditorTimelineLane.Subtitle,",
    "EditorTimelineLane.Video,",
    "EditorTimelineLane.Voice,",
):
    require(token in layout, f"LANEORDER-01 the core lane order owner lost: {token}")
# The list itself must read top to bottom: effects, subtitles, video, voice.
lane_order = layout.split("LaneOrder { get; } =", 1)[1].split("]", 1)[0]
order_positions = []
for token in (
    "EditorTimelineLane.Effect,",
    "EditorTimelineLane.Subtitle,",
    "EditorTimelineLane.Video,",
    "EditorTimelineLane.Voice,",
):
    require(token in lane_order, f"LANEORDER-01 the lane order list lost: {token}")
    order_positions.append(lane_order.index(token))
require(order_positions == sorted(order_positions),
        "LANEORDER-01 LaneOrder must read effects, subtitles, video, voice from top to bottom")

# LANEORDER-02: the lane constants in the App come from the enum, not
# hard-coded out of step with Core.
for token in (
    "private const int TimelineLaneVideo = (int)EditorTimelineLane.Video;",
    "private const int TimelineLaneVoice = (int)EditorTimelineLane.Voice;",
    "private const int TimelineLaneSubtitle = (int)EditorTimelineLane.Subtitle;",
    "private const int TimelineLaneEffect = (int)EditorTimelineLane.Effect;",
    "private const int TimelineLaneCount = EditorTimelineLayout.LaneCount;",
):
    require(token in timeline, f"LANEORDER-02 the app lane constants lost: {token}")

# LANEORDER-03: the lane label column must follow the exact lane order (effects
# on row 2, subtitles on row 4, video on row 6, audio on row 8).
for label, row in (
    ("TimelineLaneEffectLabel", 2),
    ("TimelineLaneSubtitleLabel", 4),
    ("TimelineLaneVideoLabel", 6),
    ("TimelineLaneVoiceLabel", 8),
):
    line = next((item for item in xaml.splitlines() if f'x:Name="{label}"' in item), "")
    require(line != "", f"LANEORDER-03 the lane label is missing: {label}")
    require(f'Grid.Row="{row}"' in line,
            f"LANEORDER-03 {label} must sit on row {row} of the lane label column")

# LANEORDER-04: the lanes are drawn in the standard order: effects, subtitles,
# video, voice (audio at the bottom).
lanes = window(timeline, "private void RenderTimelineLanes(", 3200)
require(lanes != "", "LANEORDER-04 the lane render owner is missing")
order = [
    "RenderTimelineEffectBlocks(pps, visibleStart, visibleEnd);",
    "RenderTimelineSubtitleBlocks(pps, visibleStart, visibleEnd);",
    "RenderTimelineVideoBlocks(pps, visibleStart, visibleEnd);",
    "RenderTimelineVoiceBlocks(pps, visibleStart, visibleEnd);",
]
positions = []
for token in order:
    require(token in lanes, f"LANEORDER-04 the lane render owner lost: {token}")
    positions.append(lanes.index(token))
require(positions == sorted(positions),
        "LANEORDER-04 the lanes must be drawn effects, subtitle, video, voice")

# LANEHEIGHT-01: the strip panel must be tall enough for the ruler + 4 lanes +
# horizontal scroll bar + toolbar + gaps + padding, otherwise the bottom-most
# lane gets cut off.
for token in (
    "public const double ScrollBarReserve = 18;",
    "public const double ToolbarHeight = 26;",
    "public const double ToolbarGap = 6;",
    "public const double PanelPaddingVertical = 14;",
    "public static double PanelHeight(int laneCount)",
):
    require(token in layout, f"LANEHEIGHT-01 the core panel height owner lost: {token}")
panel_height = layout.split("public static double PanelHeight(int laneCount)", 1)[1].split("}", 1)[0]
require("TracksHeight(laneCount)" in panel_height
        and "ScrollBarReserve" in panel_height
        and "ToolbarHeight" in panel_height
        and "ToolbarGap" in panel_height
        and "PanelPaddingVertical" in panel_height,
        "LANEHEIGHT-01 the panel height must cover the ruler, the lanes, the scroll bar and the toolbar")
panel = next((item for item in xaml.splitlines() if 'x:Name="TimelinePanel"' in item), "")
require(panel != "", "LANEHEIGHT-01 the timeline panel is missing from the XAML")
require('x:Name="TimelineRow" Height="206"' in xaml
        and "Grid.Row=\"2\" Grid.Column=\"0\" Grid.ColumnSpan=\"5\" x:Name=\"TimelinePanel\"" in panel,
        "LANEHEIGHT-01 the timeline panel must use the height the layout math asks for")

# LANEORDER-05: the executable contract must lock the lane order and the panel
# height, and this script must be registered in verify.ps1.
tests = (TESTS / "Program.cs").read_text(encoding="utf-8")
require("editor timeline lanes stack effects subtitles video voice and the panel fits them all" in tests,
        "LANEORDER-05 the executable lane order contract test is missing")
contract = (TESTS / "EditorTimelineLaneContract.cs").read_text(encoding="utf-8")
for token in (
    "EditorTimelineLayout.LaneOrder",
    "EditorTimelineLayout.LaneTop(EditorTimelineLane.Voice)",
    "EditorTimelineLayout.PanelHeight(EditorTimelineLayout.LaneCount)",
):
    require(token in contract, f"LANEORDER-05 the contract test lost: {token}")
verify = (ROOT / "csharp/scripts/verify.ps1").read_text(encoding="utf-8")
require('Invoke-Checked python @("csharp/scripts/verify_editor_timeline_lane_order_contract.py")' in verify,
        "LANEORDER-05 the lane order contract script must be registered in verify.ps1")

print("PASS: LANEORDER-01/02/03/04/05 the effects subtitles video voice lane stack and "
      "LANEHEIGHT-01 the panel height that keeps every lane visible are locked")
