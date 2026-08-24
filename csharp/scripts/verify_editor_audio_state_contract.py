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
EDITOR_PARITY = PAGES / "EditorPage.ParityFixes.cs"
PROJECT_STORE = ROOT / "csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs"
VIDEO_EDITOR = ROOT / "csharp/src/BiliSubStudio.Core/Editor/VideoEditorService.cs"
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
editor_parity = read(EDITOR_PARITY)
project_store = read(PROJECT_STORE)
video_editor = read(VIDEO_EDITOR)

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

# AUDIO-04 — Keep original audio. Keep is the project/render policy that preserves
# the complete source mix at unity gain in both processed Preview and final Export.
# It remains independent from the Player's monitor-only mute/volume state.
source_audio_mode = named_xaml_control("SourceAudioModeBox")
require(source_audio_mode.get("SelectedIndex") == "0",
        "AUDIO-04 Keep original audio must remain the default source-audio selection")
require(source_audio_mode.get("SelectionChanged") == "SourceAudioMode_SelectionChanged",
        "AUDIO-04 source-audio mode must keep one reviewed SelectionChanged handler")

keep_item = '<ComboBoxItem Tag="keep" Content="Giữ nguyên" />'
duck_item = '<ComboBoxItem Tag="duck" Content="Giảm âm lượng" />'
mute_item = '<ComboBoxItem Tag="mute" Content="Tắt tiếng gốc" />'
require(keep_item in editor_xaml and duck_item in editor_xaml and mute_item in editor_xaml
        and editor_xaml.index(keep_item) < editor_xaml.index(duck_item) < editor_xaml.index(mute_item),
        "AUDIO-04 Keep must remain the first explicit source-audio mode")
require('Text="Preview và video xuất sẽ giữ nguyên âm thanh gốc."' in editor_xaml,
        "AUDIO-04 UI copy must keep Preview/Export Keep semantics explicit")

audio_update = editor_main.split("private void UpdateAudioSettingsFromUi()", 1)[1].split(
    "private void ApplyAudioSettingsToUi()", 1)[0]
require('?? "keep";' in audio_update,
        "AUDIO-04 missing/unknown UI selection must fall back to Keep")
require('_ => 1,' in audio_update,
        "AUDIO-04 Keep UI path must force unity source gain")
require("_audioSettings = EditorProjectStore.NormalizeAudio(new EditorAudioSettings(mode, gain));" in audio_update,
        "AUDIO-04 UI must write Keep through the project-owned audio normalizer")
require('_ => "Preview và video xuất sẽ giữ nguyên âm thanh gốc.",' in audio_update,
        "AUDIO-04 Keep status must describe the shared Preview/Export policy")
require("QueueProjectSave();" in audio_update and "NotifyEditorCompositeChanged();" in audio_update,
        "AUDIO-04 changing source-audio mode must persist and refresh processed Preview")
for forbidden in ("_playback.SetMuted(", "_playback.SetVolume(", "_monitorAudio"):
    require(forbidden not in audio_update,
            f"AUDIO-04 Keep render policy must not mutate Player monitor state; found {forbidden}")

require('SourceAudioGainSlider.IsEnabled = editable && _audioSettings.SourceMode == "duck";' in editor_main,
        "AUDIO-04 Keep must not expose the Duck-only source gain control")

require('public static EditorAudioSettings Default { get; } = new("keep", 1);' in project_store,
        "AUDIO-04 fresh projects must default to Keep at unity gain")
normalize_audio = project_store.split("public static EditorAudioSettings NormalizeAudio", 1)[1].split(
    "private static EditorAsrProject? NormalizeAsr", 1)[0]
require('"keep" => new EditorAudioSettings("keep", 1),' in normalize_audio,
        "AUDIO-04 persisted Keep state must canonicalize to unity gain")

project_snapshot = editor_main.split("private EditorProject ProjectSnapshot()", 1)[1].split(
    "private void RefreshEditorActions()", 1)[0]
require("Audio = _audioSettings," in project_snapshot,
        "AUDIO-04 project snapshot must persist the active Keep policy")

current_request = editor_main.split("private VideoEditRequest CurrentEditRequest(", 1)[1].split(
    "private async void Render_Click", 1)[0]
require("_audioSettings," in current_request,
        "AUDIO-04 Preview/Export request must carry the project-owned Keep policy")

notify_composite = editor_parity.split("private void NotifyEditorCompositeChanged()", 1)[1].split(
    "private void EditorAutoComposite_Toggled", 1)[0]
require("QueueEditorCompositeRefresh();" in notify_composite,
        "AUDIO-04 Keep changes must reach the processed Preview rebuild owner")

audio_arguments = video_editor.split(
    "private static IReadOnlyList<string> BuildAudioArgumentsCore", 1)[1].split(
    "private static string BuildVoiceAudioFilter", 1)[0]
require('var audio = EditorProjectStore.NormalizeAudio(settings);' in audio_arguments,
        "AUDIO-04 render path must normalize source-audio policy once")
require('var arguments = new List<string> { "-map", "0:a?" };' in audio_arguments,
        "AUDIO-04 Keep must map the original source audio stream")
require('if (audio.SourceMode == "duck") filters.Add("volume=" + audio.SourceGain.ToString("0.000", CultureInfo.InvariantCulture));' in audio_arguments,
        "AUDIO-04 source gain filter must remain Duck-only")
require('if (mp4 || audio.SourceMode == "duck") arguments.AddRange(["-c:a", "aac", "-b:a", "192k"]);' in audio_arguments
        and 'else arguments.AddRange(["-c:a", "copy"]);' in audio_arguments,
        "AUDIO-04 Keep must preserve source level while allowing container-compatible audio encoding")

preview_arguments = video_editor.split("private static IReadOnlyList<string> BuildPreviewArguments", 1)[1].split(
    "internal static IReadOnlyList<string> BuildAudioArguments", 1)[0]
require("BuildAudioArgumentsCore(audio, mp4: true, resetTimestamps: true)" in preview_arguments,
        "AUDIO-04 processed Preview must use the same source-audio policy core as Export")

voice_audio = video_editor.split("private static string BuildVoiceAudioFilter", 1)[1].split(
    "public static bool IsActiveAt", 1)[0]
require('var sourceFilters = new List<string> { "asetpts=PTS-STARTPTS" };' in voice_audio,
        "AUDIO-04 Keep+Voice must retain the source audio chain")
require('if (audio.SourceMode == "duck") sourceFilters.Add("volume=" + audio.SourceGain.ToString("0.000", CultureInfo.InvariantCulture));' in voice_audio,
        "AUDIO-04 Keep+Voice must not inherit Duck attenuation")
require("[sourcea][voicea]amix=inputs=2:duration=longest:dropout_transition=0:normalize=0[aout]" in voice_audio,
        "AUDIO-04 Keep+Voice must mix full-level source audio with the Voice track without normalization attenuation")

require('audio.SourceMode != "mute"' in video_editor,
        "AUDIO-04 final render validation must require an audio stream for Keep")
require("_monitorAudio" not in video_editor,
        "AUDIO-04 render/export owner must remain independent from Player monitor mute/volume")

print("PASS: AUDIO-01 single monitor owner + AUDIO-02 Preview volume + AUDIO-03 Preview mute + AUDIO-04 Keep original audio contract")
