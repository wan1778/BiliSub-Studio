#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
PAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages"
STORE = ROOT / "csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs"
SERVICE = ROOT / "csharp/src/BiliSubStudio.Core/Editor/VideoEditorService.cs"
TESTS = ROOT / "csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs"


def require(condition: bool, message: str) -> None:
    if not condition:
        print("FAIL:", message, file=sys.stderr)
        raise SystemExit(1)


xaml = (PAGES / "EditorPage.xaml").read_text(encoding="utf-8")
editor = (PAGES / "EditorPage.xaml.cs").read_text(encoding="utf-8")
dialog = (PAGES / "EditorPage.ExportDialog.cs").read_text(encoding="utf-8")
playback = (PAGES / "EditorPage.Playback.cs").read_text(encoding="utf-8")
images = (PAGES / "EditorPage.Images.cs").read_text(encoding="utf-8")
store = STORE.read_text(encoding="utf-8")
service = SERVICE.read_text(encoding="utf-8")
tests = TESTS.read_text(encoding="utf-8")

for token in (
    'x:Name="ExportDialogTrimStartBox"',
    'x:Name="ExportDialogTrimEndBox"',
    'Click="ExportDialogTrimStartCurrent_Click"',
    'Click="ExportDialogTrimEndCurrent_Click"',
    'Click="ExportDialogTrimReset_Click"',
    'x:Name="ExportDialogTrimSummaryText"',
    'TextChanged="ExportDialogTrimBox_TextChanged"',
):
    require(token in xaml, f"TRIM-01 UI wiring lost: {token}")

for token in (
    "public sealed record EditorTrimRange(double Start, double End)",
    "Trim = NormalizeTrim(loaded.Trim, source.Duration)",
    "Trim = NormalizeTrim(project.Trim, normalizedSource.Duration)",
    "Trim: new EditorTrimRange(0, source.Duration)",
    "public static bool HasTrim(EditorTrimRange? trim, double sourceDuration)",
):
    require(token in store, f"TRIM-01 persisted range contract lost: {token}")

for token in (
    "Trim: CurrentTrimRange());",
    "var trimChanged = hasMedia && _trimRangeValid",
    "|| hasImages || trimChanged || timelineEdited)",
):
    require(token in editor, f"TRIM-01 Editor state owner lost: {token}")
for token in (
    "TryParseClockToSeconds(",
    "CommitDialogTrim(",
    "UpdateDialogTrimSummary(",
    "FormatClockFull(trim.Start)",
    "SetDialogTrimBoundaryFromCurrent(",
):
    require(token in dialog, f"TRIM-01 dialog trim owner lost: {token}")

for token in (
    "var trim = EditorProjectStore.NormalizeTrim(request.Trim, request.Duration);",
    "request, trim.Start, trim.Duration, request.SourceWidth, request.SourceHeight",
    '"-ss", trim.Start.ToString("0.000", CultureInfo.InvariantCulture), "-i", input',
    '"-t", trim.Duration.ToString("0.000", CultureInfo.InvariantCulture)',
    "BuildAudioArgumentsCore(audio, mp4: true, resetTimestamps: true)",
    "BuildVoiceAudioFilter(audio, voice, 1, trim.Start)",
    "await ValidateRenderedOutputAsync(",
    "trim.Duration, voice is not null",
):
    require(token in service, f"TRIM-01 Export timeline contract lost: {token}")

for token in (
    "PreviewWindow(trim.Start, trim.End, requestedStart)",
    # Preview starts at trim.Start and stops at trim.End, but that mark is
    # mapped to composed strip time (a multi-clip strip) instead of source time.
    "private double PreviewStartTime()",
    "return timelineStart + Math.Max(0, trim.Start - clip.SourceIn);",
    "limit = Math.Min(limit, trim.End);",
    'StatusText.Text = "Đã xem hết dải thời gian. Bấm Play để phát lại từ mốc đầu.";',
):
    require(token in service or token in playback, f"TRIM-01 Preview range contract lost: {token}")

for token in (
    "var trimChanged = EditorProjectStore.HasTrim(trim, _media.Duration);",
    "|| _voiceTrack is not null || trimChanged || hasComposedClips;",
    "ComposedExportDuration(trim)",
):
    require(token in images, f"TRIM-01 image export range contract lost: {token}")

require("editor trim range persists and shifts Preview Export timelines" in tests,
        "TRIM-01 executable contract test is missing")

# TRIM-02: the keep mark set at the "current position" must map from the
# composed strip coordinate to the source file coordinate of the primary clip,
# never using the strip value directly.
composition = (PAGES / "EditorPage.TimelineComposition.cs").read_text(encoding="utf-8")
for token in (
    "internal double? CurrentPrimarySourceTime()",
    "var (clip, sourceTime, _) = LocateTimelineClip(Timeline.Value);",
    "if (!IsPrimaryTimelineClip(clip)) return null;",
):
    require(token in composition, f"TRIM-02 the composed-time owner lost: {token}")

require("var sourceTime = CurrentPrimarySourceTime();" in dialog,
        "TRIM-02 the export dialog must map the current position to the source clip")
require("var sourceTime = CurrentPrimarySourceTime();" in editor,
        "TRIM-02 the effect region must map the current position to the source clip")
require("var value = Math.Clamp(Timeline.Value, 0, _media.Duration);" not in dialog,
        "TRIM-02 the export dialog must not use the timeline value as a source time")
require("var value = Math.Clamp(Timeline.Value, 0, _media.Duration);" not in editor,
        "TRIM-02 the effect region must not use the timeline value as a source time")

print("PASS: TRIM-01/02 keep-range UI, persistence, Preview, Export and composed-time contracts are locked")
