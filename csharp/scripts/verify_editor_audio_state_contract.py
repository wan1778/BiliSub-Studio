#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
PAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages"
XAML = (PAGES / "EditorPage.xaml").read_text(encoding="utf-8")
EDITOR = (PAGES / "EditorPage.xaml.cs").read_text(encoding="utf-8")
PLAYBACK = (PAGES / "EditorPage.Playback.cs").read_text(encoding="utf-8")
STORE = (ROOT / "csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs").read_text(encoding="utf-8")
SERVICE = (ROOT / "csharp/src/BiliSubStudio.Core/Editor/VideoEditorService.cs").read_text(encoding="utf-8")


def require(condition: bool, message: str) -> None:
    if not condition:
        print("FAIL:", message, file=sys.stderr)
        raise SystemExit(1)


# Player monitor volume/mute remain independent from the persisted source gain.
for token in ('x:Name="PreviewMuteToggle"', 'x:Name="PreviewVolumeSlider"'):
    require(token in XAML, f"AUDIO-DIRECT monitor control lost: {token}")
require("_monitorAudio.Muted = muted;" in PLAYBACK and "_monitorAudio.Volume = Math.Clamp(volume, 0, 1);" in PLAYBACK,
        "AUDIO-DIRECT Player monitor owner lost")

# Source audio is now one direct, always-available slider with no mode picker.
for token in (
    'x:Name="SourceAudioGainSlider"',
    'Header="Âm lượng gốc (%)"',
    'Minimum="0" Maximum="200" Value="100"',
    'StepFrequency="1"',
    'ValueChanged="SourceAudioGain_ValueChanged"',
    '0% · Tắt',
    '100% · Nguyên bản',
    '200% · Tăng',
):
    require(token in XAML, f"AUDIO-DIRECT slider contract lost: {token}")
require("SourceAudioModeBox" not in XAML and "SourceAudioMode_SelectionChanged" not in EDITOR,
        "AUDIO-DIRECT obsolete source mode picker returned")

audio_update = EDITOR.split("private void UpdateAudioSettingsFromUi()", 1)[1].split(
    "private void ApplyAudioSettingsToUi()", 1
)[0]
require("EditorProjectStore.FromSourceGain(SourceAudioGainSlider.Value / 100)" in audio_update,
        "AUDIO-DIRECT slider does not directly own persisted source gain")
require("QueueProjectSave();" in audio_update and "NotifyEditorCompositeChanged();" in audio_update,
        "AUDIO-DIRECT change does not refresh Preview and persistence")

apply_audio = EDITOR.split("private void ApplyAudioSettingsToUi()", 1)[1].split(
    "private EditorSubtitleBurn? CompletedSubtitleBurn", 1
)[0]
require("SourceAudioGainSlider.Value = _audioSettings.SourceGain * 100;" in apply_audio,
        "AUDIO-DIRECT reopen does not restore the exact slider position")
require("_syncingAudio = true;" in apply_audio and "finally { _syncingAudio = false; }" in apply_audio,
        "AUDIO-DIRECT owner-to-UI restore lost its event-loop guard")
require("SourceAudioGainSlider.IsEnabled = editable;" in EDITOR,
        "AUDIO-DIRECT slider still requires a separate mode selection")

for token in (
    "public static EditorAudioSettings FromSourceGain(double sourceGain)",
    'if (gain <= 0) return new EditorAudioSettings("mute", 0);',
    'if (Math.Abs(gain - 1) <= .0005) return new EditorAudioSettings("keep", 1);',
    'return new EditorAudioSettings("duck", Math.Clamp(gain, .01, 2));',
):
    require(token in STORE, f"AUDIO-DIRECT canonical gain policy lost: {token}")

for token in (
    'if (audio.SourceMode == "mute") return ["-an"];',
    'if (audio.SourceMode == "duck") filters.Add("volume=" + audio.SourceGain.ToString("0.000", CultureInfo.InvariantCulture));',
    'if (audio.SourceMode == "duck") sourceFilters.Add("volume=" + audio.SourceGain.ToString("0.000", CultureInfo.InvariantCulture));',
):
    require(token in SERVICE, f"AUDIO-DIRECT shared Preview/Export FFmpeg policy lost: {token}")

print("PASS: AUDIO-DIRECT one 0-200% source slider owns persisted Preview/Export gain")
