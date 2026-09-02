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
playback = (PAGES / "EditorPage.Playback.cs").read_text(encoding="utf-8")
images = (PAGES / "EditorPage.Images.cs").read_text(encoding="utf-8")
store = STORE.read_text(encoding="utf-8")
service = SERVICE.read_text(encoding="utf-8")
tests = TESTS.read_text(encoding="utf-8")

for token in (
    'x:Name="TrimStartBox"',
    'x:Name="TrimEndBox"',
    'Click="TrimUseCurrentStart_Click"',
    'Click="TrimUseCurrentEnd_Click"',
    'Click="TrimReset_Click"',
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
    "|| hasImages || trimChanged)",
    "TrimStartBox.IsEnabled = TrimEndBox.IsEnabled = editable;",
):
    require(token in editor, f"TRIM-01 Editor state owner lost: {token}")

for token in (
    "var trim = EditorProjectStore.NormalizeTrim(request.Trim, request.Duration);",
    "request, trim.Start, trim.Duration, request.SourceWidth, request.SourceHeight",
    '"-ss", trim.Start.ToString("0.000", CultureInfo.InvariantCulture), "-i", input',
    '"-t", trim.Duration.ToString("0.000", CultureInfo.InvariantCulture)',
    "BuildAudioArgumentsCore(audio, mp4, resetTimestamps: true)",
    "BuildVoiceAudioFilter(audio, voice, 1, trim.Start)",
    "await ValidateRenderedOutputAsync(temporary, trim.Duration",
):
    require(token in service, f"TRIM-01 Export timeline contract lost: {token}")

for token in (
    "PreviewWindow(trim.Start, trim.End, requestedStart)",
    "_page.CurrentTrimRange().Start",
    "_page.CurrentTrimRange().End",
    'StatusText.Text = "Đã xem hết khoảng giữ lại. Bấm Play để phát lại từ mốc đầu.";',
):
    require(token in service or token in playback, f"TRIM-01 Preview range contract lost: {token}")

for token in (
    "var trimChanged = EditorProjectStore.HasTrim(trim, _media.Duration);",
    "|| _voiceTrack is not null || trimChanged;",
    "trimChanged ? trim.Duration : _media.Duration",
):
    require(token in images, f"TRIM-01 image export range contract lost: {token}")

require("editor trim range persists and shifts Preview Export timelines" in tests,
        "TRIM-01 executable contract test is missing")

print("PASS: TRIM-01 keep-range UI, persistence, Preview and Export contracts are locked")
