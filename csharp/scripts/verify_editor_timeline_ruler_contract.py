#!/usr/bin/env python3
"""Kiểm tra thước thời gian chuyên nghiệp, lưới dọc, phân cấp track và playhead."""
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages"
CORE = ROOT / "csharp/src/BiliSubStudio.Core/Editor"
TIMELINE = PAGES / "EditorPage.Timeline.cs"
XAML = PAGES / "EditorPage.xaml"
LAYOUT = CORE / "EditorTimelineLayout.cs"


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
layout = LAYOUT.read_text(encoding="utf-8")

# RULER-01: major and minor ticks must be clearly different (thickness, height,
# color), and the tick spacing must follow the zoom level, not be hard-coded.
ruler = window(timeline, "private void RenderTimelineRuler(", 6000)
require(ruler != "", "RULER-01 the ruler render owner is missing")
for token in (
    "var major = EditorTimelineLayout.MajorTickSeconds(pps);",
    "var minor = Math.Max(.01, EditorTimelineLayout.MinorTickSeconds(pps));",
    "Width = TimelineMajorTickWidth,",
    "Height = TimelineMajorTickHeight,",
    "Fill = TimelineMajorTickBrush,",
    "Width = TimelineMinorTickWidth,",
    "Height = TimelineMinorTickHeight,",
    "Fill = TimelineTickBrush,",
):
    require(token in ruler, f"RULER-01 the major/minor tick distinction lost: {token}")
require("private const double TimelineMajorTickWidth = 2;" in timeline,
        "RULER-01 the major tick must be thicker than the minor tick")
require("private const double TimelineMinorTickWidth = 1;" in timeline,
        "RULER-01 the minor tick must stay a hairline")
require("private static readonly SolidColorBrush TimelineMajorTickBrush" in timeline,
        "RULER-01 the major tick brush is missing")
require("private static readonly SolidColorBrush TimelineTickBrush" in timeline,
        "RULER-01 the minor tick brush is missing")

# RULER-02: time labels are only drawn when the spacing is wide enough,
# otherwise the label is dropped rather than drawn overlapping. The threshold
# lives in Core so it is testable.
for token in (
    "public const double MinimumLabelPixels = 72;",
    "public static bool LabelsFit(double stepSeconds, double pixelsPerSecond)",
    "public static double TickSpacingPixels(double stepSeconds, double pixelsPerSecond)",
):
    require(token in layout, f"RULER-02 the core label threshold is missing: {token}")
for token in (
    "var drawLabels = EditorTimelineLayout.LabelsFit(major, pps);",
    "if (!drawLabels) continue;",
    "if (labelLeft < lastLabelRight + TimelineLabelOverlapPadding) continue;",
):
    require(token in ruler, f"RULER-02 the label drop rule lost: {token}")
require("private const double TimelineLabelOverlapPadding = 4;" in timeline,
        "RULER-02 the label overlap padding is missing")

# RULER-03: the vertical grid only extends downward on major ticks, with a
# light contrast, and it sits below clip/cue so it does not cover content.
for token in (
    "private void RenderTimelineGridLines(double pps, double visibleStart, double visibleEnd)",
    "var major = EditorTimelineLayout.MajorTickSeconds(pps);",
    "EditorTimelineLayout.TickTimes(visibleStart, visibleEnd, major)",
    "Fill = TimelineGridBrush,",
):
    require(token in timeline, f"RULER-03 the vertical grid owner lost: {token}")
require("private static readonly SolidColorBrush TimelineGridBrush" in timeline,
        "RULER-03 the grid brush is missing")
lanes = window(timeline, "private void RenderTimelineLanes(")
require("RenderTimelineGridLines(pps, visibleStart, visibleEnd);" in lanes,
        "RULER-03 the lane area must draw the major-tick grid")
require(lanes.index("RenderTimelineGridLines(pps, visibleStart, visibleEnd);")
        < lanes.index("RenderTimelineVideoBlocks(pps, visibleStart, visibleEnd);"),
        "RULER-03 the grid must stay under the clips")

# RULER-04: the ruler background must differ from the lane background, and
# ticks/labels are only drawn inside the visible range (a long project must
# not build tens of thousands of elements).
for token in (
    "public static IReadOnlyList<double> TickTimes(double visibleStart, double visibleEnd, double stepSeconds)",
    "var first = Math.Ceiling(visibleStart / stepSeconds - 1e-9);",
    "var last = Math.Floor(visibleEnd / stepSeconds + 1e-9);",
):
    require(token in layout, f"RULER-04 the viewport-only tick math is missing: {token}")
require("Fill = TimelineRulerBrush," in ruler,
        "RULER-04 the ruler band must own its own background")
require("private static readonly SolidColorBrush TimelineRulerBrush" in timeline,
        "RULER-04 the ruler brush is missing")
require("Fill = lane % 2 == 0 ? TimelineLaneBrush : TimelineLaneAltBrush," in lanes,
        "RULER-04 the lane backgrounds must differ from the ruler band")

# LANE-02: lane edges must be slim, consistent and easy to see: 1 px light + 1
# px dark below, alternating light/dark lane backgrounds, and an empty lane
# still keeps its background.
for token in (
    "Fill = TimelineLaneSeparatorBrush,",
    "Fill = TimelineLaneSeparatorShadowBrush,",
    "Canvas.SetTop(separator, EditorTimelineLayout.LaneTop(lane) - EditorTimelineLayout.LaneGap);",
    "Canvas.SetTop(shadow, EditorTimelineLayout.LaneTop(lane) - EditorTimelineLayout.LaneGap + 1);",
):
    require(token in lanes, f"LANE-02 the lane edge lost: {token}")
require("private static readonly SolidColorBrush TimelineLaneSeparatorShadowBrush" in timeline,
        "LANE-02 the lane separator shadow brush is missing")
require("for (var lane = 0; lane < TimelineLaneCount; lane++)" in lanes,
        "LANE-02 every lane must keep its background even when empty")

# LANE-03: the track name column does not scroll with the horizontal content,
# and each label row matches the lane height exactly (including the 3 px gap
# between lanes).
header = window(xaml, 'x:Name="TimelineHeaderGrid"', 2400)
require(header != "", "LANE-03 the lane label column is missing from the XAML")
require('Grid.Column="0" x:Name="TimelineHeaderGrid"' in xaml
        and '<ColumnDefinition x:Name="TimelineHeaderColumn" Width="78"' in xaml,
        "LANE-03 the lane label column must stay in its own grid column")
for token in (
    'x:Name="TimelineLaneVideoLabel"',
    'x:Name="TimelineLaneEffectLabel"',
    'x:Name="TimelineLaneSubtitleLabel"',
    'x:Name="TimelineLaneVoiceLabel"',
):
    require(token in header, f"LANE-03 the lane label column lost: {token}")
require('Grid.Row="2"' in header and 'Grid.Row="4"' in header
        and 'Grid.Row="6"' in header and 'Grid.Row="8"' in header,
        "LANE-03 every lane label needs its own row")
require('Grid.Column="2" x:Name="TimelineScrollViewer"' in xaml,
        "LANE-03 the scroll viewer must stay in its own column")
for token in (
    "if (rows.Count < 2 + TimelineLaneCount * 2 - 1) return;",
    "rows[2 + lane * 2].Height = new GridLength(EditorTimelineLayout.LaneHeight);",
    "rows[gapRow].Height = new GridLength(EditorTimelineLayout.LaneGap);",
):
    require(token in timeline, f"LANE-03 the lane label row sync lost: {token}")

# CLIP-01: a video clip must have a clear content area and a readable file
# name, and the selected clip must have a clear border.
for token in (
    "block.SurfaceBorderBrush = isSelected ? TimelineClipSelectedBorderBrush : TimelineBrush(0, 0, 0, 0);",
    "block.SurfaceBorderThickness = new Thickness(isSelected ? 2 : 0);",
    "block.Label.Text = TimelineClipDisplayName(clip);",
    "private static string TimelineClipDisplayName(EditorTimelineClip clip)",
    "TextTrimming = TextTrimming.CharacterEllipsis,",
):
    require(token in timeline, f"CLIP-01 the clip readability lost: {token}")
require("private static readonly SolidColorBrush TimelineClipAccentBrush" in timeline,
        "CLIP-01 the clip content accent brush is missing")
require("Width = TimelineClipAccentWidth," in timeline,
        "CLIP-01 the clip content accent bar is missing")

# PLAYHEAD-05: the indicator line must span from the ruler down over the whole
# track area and sit ABOVE the lanes in the Canvas z-order, with a round head
# hugging the ruler edge.
for token in (
    'x:Name="TimelinePlayhead"',
    'x:Name="TimelinePlayheadHead"',
    'AutomationProperties.AutomationId="TimelinePlayhead"',
    'AutomationProperties.AutomationId="TimelinePlayheadHead"',
):
    require(token in xaml, f"PLAYHEAD-05 the playhead elements lost: {token}")
require(xaml.index('x:Name="TimelineTracksCanvas"') < xaml.index('x:Name="TimelinePlayhead"'),
        "PLAYHEAD-05 the playhead must be drawn above the lane canvas")
for token in (
    "if (Math.Abs(TimelinePlayhead.Height - contentHeight) > .5) TimelinePlayhead.Height = contentHeight;",
    "Canvas.SetLeft(TimelinePlayhead, x - TimelinePlayhead.Width / 2);",
    "Canvas.SetTop(TimelinePlayhead, 0);",
    "Canvas.SetLeft(TimelinePlayheadHead, x - TimelinePlayheadHead.Width / 2);",
):
    require(token in timeline, f"PLAYHEAD-05 the playhead owner lost: {token}")

# RULER-06: the ruler band is a separate band: its background spans the whole
# viewport width (it never falls short on the right edge) and a 1 px line
# separates it from the lane area.
for token in (
    "var band = new Rectangle",
    "Width = fillWidth,",
    "Fill = TimelineRulerBrush,",
    "Fill = TimelineLaneSeparatorBrush,",
    "Canvas.SetTop(rulerEdge, EditorTimelineLayout.RulerHeight - 1);",
):
    require(token in ruler, f"RULER-06 the ruler band lost: {token}")
require("EditorTimelineLayout.FillWidth(contentWidth, TimelineViewportWidth())" in timeline,
        "RULER-06 the ruler band must fill the whole viewport width")

# RULER-05: the executable contract must lock the label threshold and the
# drawing range.
tests = (ROOT / "csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs").read_text(encoding="utf-8")
require("editor timeline maps seconds to pixels, clamps zoom and virtualizes the viewport" in tests,
        "RULER-05 the executable contract test is missing")
contract = (ROOT / "csharp/tests/BiliSubStudio.Core.ContractTests/EditorTimelineLayoutContract.cs").read_text(encoding="utf-8")
for token in (
    "EditorTimelineLayout.MinimumLabelPixels",
    "EditorTimelineLayout.LabelsFit(",
    "EditorTimelineLayout.TickSpacingPixels(",
    "EditorTimelineLayout.TickTimes(",
):
    require(token in contract, f"RULER-05 the contract test lost: {token}")

print("PASS: RULER-01/02/03/04/05 major-minor ticks, zoom-driven spacing, label drop rule, "
      "vertical grid, viewport-only drawing, LANE-02/03 lane edges and label rows, "
      "CLIP-01 readable selected clip and PLAYHEAD-05 playhead coverage are locked")
