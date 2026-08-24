#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages"
PLAYBACK = PAGES / "EditorPage.Playback.cs"
EDITOR_MAIN = PAGES / "EditorPage.xaml.cs"
EDITOR_XAML = PAGES / "EditorPage.xaml"


def fail(message: str) -> None:
    print(f"FAIL: {message}", file=sys.stderr)
    raise SystemExit(1)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


playback = read(PLAYBACK)
editor_main = read(EDITOR_MAIN)
editor_xaml = read(EDITOR_XAML)

require('Toggled="PreviewMute_Toggled"' in editor_xaml,
        "AUDIO-01 mute UI must keep one reviewed XAML handler")
require('ValueChanged="PreviewVolume_ValueChanged"' in editor_xaml,
        "AUDIO-01 volume UI must keep one reviewed XAML handler")
require("_playback.SetMuted(sender is ToggleSwitch toggle && toggle.IsOn);" in playback,
        "AUDIO-01 mute handler must forward event state to the playback owner")
require("_playback.SetVolume(e.NewValue / 100);" in playback,
        "AUDIO-01 volume handler must forward event state to the playback owner")

owner_marker = "private (bool Muted, double Volume) _monitorAudio = (false, 1d);"
require(playback.count(owner_marker) == 1,
        "AUDIO-01 monitor mute/volume must have exactly one playback-owned state")

controller = playback.split("private sealed class EditorPlaybackController", 1)[1]
require("_page.PreviewMuteToggle" not in controller and "_page.PreviewVolumeSlider" not in controller,
        "AUDIO-01 playback owner must never reconstruct monitor state by reading UI controls")
require("_audioSettings" not in controller,
        "AUDIO-01 monitor state must remain separate from persisted Keep/Duck/Mute render policy")

muted_source = playback.split("internal void SetMuted(bool muted)", 1)[1].split("internal void SetVolume", 1)[0]
require("_monitorAudio.Muted = muted;" in muted_source
        and "_player.IsMuted = _monitorAudio.Muted;" in muted_source,
        "AUDIO-01 mute changes must update the owner before the live MediaPlayer")

volume_source = playback.split("internal void SetVolume(double volume)", 1)[1].split("internal async Task SetModeAsync", 1)[0]
require("_monitorAudio.Volume = Math.Clamp(volume, 0, 1);" in volume_source
        and "_player.Volume = _monitorAudio.Volume;" in volume_source,
        "AUDIO-01 volume changes must normalize into the owner before the live MediaPlayer")

create_player = playback.split("private void CreatePlayer()", 1)[1].split("private void PlayerPositionChanged", 1)[0]
require("IsMuted = _monitorAudio.Muted," in create_player
        and "Volume = _monitorAudio.Volume," in create_player,
        "AUDIO-01 recreated MediaPlayer must restore monitor audio from the single owner")

require("_audioSettings = EditorProjectStore.NormalizeAudio" in editor_main
        and "Audio = _audioSettings" in editor_main,
        "AUDIO-01 persisted source Keep/Duck/Mute policy must remain project-owned")

print("PASS: AUDIO-01 single monitor audio state owner contract")
