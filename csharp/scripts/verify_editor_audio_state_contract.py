#!/usr/bin/env python3
from __future__ import annotations

import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages"
PLAYBACK = PAGES / "EditorPage.Playback.cs"
EDITOR_MAIN = PAGES / "EditorPage.xaml.cs"
EDITOR_XAML = PAGES / "EditorPage.xaml"
XAML_NS = "http://schemas.microsoft.com/winfx/2006/xaml"


def fail(message: str) -> None:
    print(f"FAIL: {message}", file=sys.stderr)
    raise SystemExit(1)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


def named_xaml_control(name: str) -> dict[str, str]:
    root = ET.parse(EDITOR_XAML).getroot()
    for element in root.iter():
        if element.attrib.get(f"{{{XAML_NS}}}Name") != name:
            continue
        return {key.rsplit("}", 1)[-1]: value for key, value in element.attrib.items()}
    fail(f"missing XAML control {name}")
    raise AssertionError("unreachable")


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

# AUDIO-02 — Preview volume. The compact Player volume is monitor-only state:
# 0..100 in the UI maps directly to 0..1 MediaPlayer.Volume, applies immediately,
# survives player recreation through the AUDIO-01 owner, and never mutates render policy.
preview_volume = named_xaml_control("PreviewVolumeSlider")
require(preview_volume.get("Minimum") == "0"
        and preview_volume.get("Maximum") == "100"
        and preview_volume.get("Value") == "100",
        "AUDIO-02 Preview volume must expose an exact 0..100 range with 100% default")
require(preview_volume.get("ValueChanged") == "PreviewVolume_ValueChanged",
        "AUDIO-02 Preview volume must keep exactly one reviewed ValueChanged handler")
require(playback.count("private void PreviewVolume_ValueChanged(") == 1,
        "AUDIO-02 Preview volume handler must have exactly one implementation")

preview_volume_handler = playback.split("private void PreviewVolume_ValueChanged(", 1)[1].split(
    "private sealed class EditorPlaybackController", 1)[0]
require("_playback.SetVolume(e.NewValue / 100);" in preview_volume_handler,
        "AUDIO-02 slider percent must map directly to the playback owner's 0..1 volume")
for forbidden in (
    "_audioSettings", "SourceAudioModeBox", "SourceAudioGainSlider", "QueueProjectSave",
    "QueuePreviewRefresh", "NotifyEditorCompositeChanged", "SetMuted(",
):
    require(forbidden not in preview_volume_handler,
            f"AUDIO-02 Preview volume handler must remain monitor-only; found {forbidden}")

require("_monitorAudio.Volume = Math.Clamp(volume, 0, 1);" in volume_source,
        "AUDIO-02 playback owner must clamp Preview volume to MediaPlayer's 0..1 domain")
require("_player.Volume = _monitorAudio.Volume;" in volume_source,
        "AUDIO-02 Preview volume must apply immediately to the current MediaPlayer")
for forbidden in ("_monitorAudio.Muted", "_player.IsMuted", "_audioSettings"):
    require(forbidden not in volume_source,
            f"AUDIO-02 changing Preview volume must not alter mute/render state; found {forbidden}")

require("Volume = _monitorAudio.Volume," in create_player,
        "AUDIO-02 recreated MediaPlayer must retain the selected Preview volume")
require(editor_main.count("_playback.SetVolume(") == 0,
        "AUDIO-02 project/source-audio code must never write monitor Preview volume")
require(playback.count("_playback.SetVolume(") == 1,
        "AUDIO-02 Preview volume must have one UI-to-owner write path")

# AUDIO-03 — Preview mute. The compact Player mute is monitor-only state:
# the ToggleSwitch boolean maps directly to MediaPlayer.IsMuted, applies immediately,
# preserves the selected Preview volume, survives player recreation, and never changes
# project-owned source Keep/Duck/Mute render policy.
preview_mute = named_xaml_control("PreviewMuteToggle")
require(preview_mute.get("Toggled") == "PreviewMute_Toggled",
        "AUDIO-03 Preview mute must keep exactly one reviewed Toggled handler")
require(preview_mute.get("IsOn") in (None, "False", "false"),
        "AUDIO-03 Preview mute must default to unmuted")
require(playback.count("private void PreviewMute_Toggled(") == 1,
        "AUDIO-03 Preview mute handler must have exactly one implementation")

preview_mute_handler = playback.split("private void PreviewMute_Toggled(", 1)[1].split(
    "private void PreviewVolume_ValueChanged(", 1)[0]
require("_playback.SetMuted(sender is ToggleSwitch toggle && toggle.IsOn);" in preview_mute_handler,
        "AUDIO-03 toggle state must map directly to the playback mute owner")
for forbidden in (
    "_audioSettings", "SourceAudioModeBox", "SourceAudioGainSlider", "QueueProjectSave",
    "QueuePreviewRefresh", "NotifyEditorCompositeChanged", "SetVolume(",
):
    require(forbidden not in preview_mute_handler,
            f"AUDIO-03 Preview mute handler must remain monitor-only; found {forbidden}")

require("_monitorAudio.Muted = muted;" in muted_source,
        "AUDIO-03 playback owner must store the selected Preview mute state")
require("_player.IsMuted = _monitorAudio.Muted;" in muted_source,
        "AUDIO-03 Preview mute must apply immediately to the current MediaPlayer")
for forbidden in ("_monitorAudio.Volume", "_player.Volume", "_audioSettings"):
    require(forbidden not in muted_source,
            f"AUDIO-03 changing Preview mute must preserve volume/render state; found {forbidden}")

require("IsMuted = _monitorAudio.Muted," in create_player,
        "AUDIO-03 recreated MediaPlayer must retain the selected Preview mute state")
require(editor_main.count("_playback.SetMuted(") == 0,
        "AUDIO-03 project/source-audio code must never write monitor Preview mute")
require(playback.count("_playback.SetMuted(") == 1,
        "AUDIO-03 Preview mute must have one UI-to-owner write path")

print("PASS: AUDIO-01 single monitor owner + AUDIO-02 Preview volume + AUDIO-03 Preview mute contract")
