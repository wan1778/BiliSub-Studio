#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
XAML = (ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml").read_text(encoding="utf-8")
EDITOR = (ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml.cs").read_text(encoding="utf-8")
STORE = (ROOT / "csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs").read_text(encoding="utf-8")
SERVICE = (ROOT / "csharp/src/BiliSubStudio.Core/Editor/VideoEditorService.cs").read_text(encoding="utf-8")
TESTS = (ROOT / "csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs").read_text(encoding="utf-8")


def require(condition: bool, message: str) -> None:
    if not condition:
        print("FAIL:", message, file=sys.stderr)
        raise SystemExit(1)


require('Minimum="0" Maximum="200" Value="100"' in XAML,
        "EXPORT-AUDIO direct gain range drift")
require("EditorProjectStore.FromSourceGain(SourceAudioGainSlider.Value / 100)" in EDITOR,
        "EXPORT-AUDIO UI gain does not reach request state")
require("var audioChanged = _audioSettings.SourceMode != \"keep\";" in EDITOR,
        "EXPORT-AUDIO gain-only edit no longer enables Export")
require("Audio = _audioSettings," in EDITOR,
        "EXPORT-AUDIO request/snapshot lost source gain")

for token in (
    "var gain = Math.Clamp(sourceGain, 0, 2);",
    'new EditorAudioSettings("mute", 0)',
    'new EditorAudioSettings("keep", 1)',
    'new EditorAudioSettings("duck", Math.Clamp(gain, .01, 2))',
):
    require(token in STORE, f"EXPORT-AUDIO canonical direct gain lost: {token}")

audio_core = SERVICE.split("private static IReadOnlyList<string> BuildAudioArgumentsCore", 1)[1].split(
    "public async Task<byte[]> GetPreviewFrameJpegAsync", 1
)[0]
for token in (
    'if (audio.SourceMode == "mute") return ["-an"];',
    'filters.Add("volume=" + audio.SourceGain.ToString("0.000", CultureInfo.InvariantCulture))',
    'arguments.AddRange(["-c:a", "aac", "-b:a", "192k"])',
):
    require(token in audio_core, f"EXPORT-AUDIO FFmpeg gain path lost: {token}")

require("editor direct source volume maps 0 100 200 percent to exact FFmpeg policy" in TESTS,
        "EXPORT-AUDIO executable direct-gain contract missing")
require('volume=1.500' in TESTS,
        "EXPORT-AUDIO amplification above 100% is not tested")

print("PASS: EXPORT-AUDIO 0-200% direct source gain reaches Preview and final Export")
