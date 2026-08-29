#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
XAML = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml"
EDITOR = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml.cs"
PREVIEW = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.VoiceCuePreview.cs"
PARITY = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.ParityFixes.cs"


def require(condition: bool, message: str) -> None:
    if not condition:
        print(f"FAIL: {message}", file=sys.stderr)
        raise SystemExit(1)


xaml = XAML.read_text(encoding="utf-8")
editor = EDITOR.read_text(encoding="utf-8")
preview = PREVIEW.read_text(encoding="utf-8")
parity = PARITY.read_text(encoding="utf-8")

# VOICE-CUE-PREVIEW-01 — A compact, independently scrollable panel previews
# exact whole-cue windows from the already generated master track.
for marker in (
    'x:Name="VoiceCuePreviewPanel"',
    'x:Name="VoiceCuePreviewList"',
    'ScrollViewer.VerticalScrollBarVisibility="Auto"',
    'Click="VoiceCuePreview_Click"',
    'Text="{Binding TimecodeText}"',
    'Text="{Binding DurationText}"',
):
    require(marker in xaml, f"missing voice cue preview UI marker: {marker}")

require("RefreshVoiceCuePreview();" in editor,
        "editor state refresh must keep cue preview in sync with voice ownership")
require("timing.Words.Min(word => word.Start)" in preview
        and "timing.Words.Max(word => word.End)" in preview
        and "Math.Max(cue.Start" in preview
        and "Math.Min(cue.End" in preview,
        "cue preview must use the same Whisper word envelope as whole-cue TTS")
require("track.Path" in preview and "MediaSource.CreateFromStorageFile(file)" in preview,
        "cue preview must read the completed master voice track")
require("sender.PlaybackSession.Position = TimeSpan.FromSeconds(item.SourceStart)" in preview
        and "timer.Interval = TimeSpan.FromSeconds(item.Duration)" in preview
        and "timer.IsRepeating = false" in preview,
        "cue preview must bound playback to the selected cue window")

click = preview.split("private async void VoiceCuePreview_Click", 1)[1].split(
    "private void VoiceCuePreview_MediaEnded", 1
)[0]
for forbidden in ("StartEditorTts", "GenerateAsync", "GenerateSampleAsync"):
    require(forbidden not in click, "per-cue preview must not synthesize TTS again")

require("CleanupVoiceCuePreview();" in parity,
        "page unload must dispose the dedicated cue preview player")

print("PASS: VOICE-CUE-PREVIEW-01 bounded master-track cue preview")
