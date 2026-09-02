#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
EDITOR = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml.cs"
PLAYBACK = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.Playback.cs"
VIDEO_EDITOR = ROOT / "csharp/src/BiliSubStudio.Core/Editor/VideoEditorService.cs"


def fail(message: str) -> None:
    print(f"FAIL: {message}", file=sys.stderr)
    raise SystemExit(1)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


editor = read(EDITOR)
playback = read(PLAYBACK)
video_editor = read(VIDEO_EDITOR)

# VOICE-13 — Vietnamese voice and original audio must share one render graph.
# Keep/Duck/Mute are persisted source policy; player monitor mute/volume must not
# change that policy. Preview and Export must use the same timing, gain and mix owner.
current_request = editor.split("private VideoEditRequest CurrentEditRequest(", 1)[1].split(
    "private async void Render_Click", 1
)[0]
require("_audioSettings," in current_request and "_voiceTrack," in current_request,
        "VOICE-13 shared request must carry both source-audio policy and Vietnamese track")

preview_load = playback.split("private async Task LoadSegmentCoreAsync(", 1)[1].split(
    "private async Task ActivateSegmentAsync(", 1
)[0]
require("_page.CurrentEditRequest(_page.PreviewSubtitleBurn())" in preview_load,
        "VOICE-13 Preview must use the current shared request-owned audio and voice state")

preview_run = video_editor.split("public async Task<EditorPreviewSegment> CreatePreviewSegmentAsync(", 1)[1].split(
    "public async Task DeletePreviewSegmentAsync", 1
)[0]
require("var voice = NormalizeVoiceTrack(sliced.VoiceTrack, requireFile: true);" in preview_run,
        "VOICE-13 Preview must validate the generated Vietnamese track")
require("BuildVoiceAudioFilter(EditorProjectStore.NormalizeAudio(sliced.Audio), voice, 1, sourceStart)" in preview_run,
        "VOICE-13 Preview must use the shared voice/source mix graph")
require("BuildPreviewArguments(input, temporary, graph, sliced.Audio, sourceStart, segmentDuration, voice)" in preview_run,
        "VOICE-13 Preview must forward the same audio and voice state to FFmpeg")

export_run = video_editor.split("public async Task<VideoEditResult> RunAsync(", 1)[1].split(
    "private async Task ValidateRenderedOutputAsync", 1
)[0]
require("var audio = EditorProjectStore.NormalizeAudio(renderRequest.Audio);" in export_run,
        "VOICE-13 Export must normalize the persisted source-audio policy")
require("var voice = NormalizeVoiceTrack(renderRequest.VoiceTrack, requireFile: true);" in export_run,
        "VOICE-13 Export must validate the same generated Vietnamese track")
render_arguments = video_editor.split("internal static IReadOnlyList<string> BuildRenderArguments(", 1)[1].split(
    "internal static IReadOnlyList<string> BuildAudioArguments", 1
)[0]
require("BuildVoiceAudioFilter(audio, voice, 1, trim.Start)" in render_arguments,
        "VOICE-13 Export must use the same voice/source mix graph as Preview")
require("voice is not null || audio.SourceMode != \"mute\"" in export_run,
        "VOICE-13 final validation must expect audio when Voice is present")

preview_arguments = video_editor.split("internal static IReadOnlyList<string> BuildPreviewArguments(", 1)[1].split(
    "internal static IReadOnlyList<string> BuildRenderArguments", 1
)[0]
require("var voice = NormalizeVoiceTrack(voiceTrack, requireFile: false);" in preview_arguments,
        "VOICE-13 Preview argument owner must preserve the validated track")
require("var voiceSeek = Math.Max(0, sourceStart - voice.Start);" in preview_arguments
        and '"-ss", voiceSeek.ToString("0.000", CultureInfo.InvariantCulture), "-i", voice.Path' in preview_arguments,
        "VOICE-13 Preview must seek the voice master on the source timeline")
require('else arguments.AddRange(["-map", "[aout]", "-c:a", "aac", "-b:a", "192k"]);' in preview_arguments,
        "VOICE-13 Preview must map the mixed [aout] stream")

voice_core = video_editor.split("private static string BuildVoiceAudioFilter", 1)[1].split(
    "public static bool IsActiveAt", 1
)[0]
require("var relativeDelay = Math.Max(0, voice.Start - sourceStart);" in voice_core,
        "VOICE-13 mix must delay a voice track that starts after the preview window")
require("var voiceFilters = new List<string> { \"asetpts=PTS-STARTPTS\" };" in voice_core,
        "VOICE-13 mix must reset Vietnamese voice timestamps")
require('voiceFilters.Add($"adelay={milliseconds}:all=1");' in voice_core,
        "VOICE-13 mix must apply the validated voice start offset")
require('voiceFilters.Add("volume=" + voice.Gain.ToString("0.000", CultureInfo.InvariantCulture));' in voice_core,
        "VOICE-13 mix must preserve the Vietnamese track gain")
require('var voiceChain = $"[{voiceInputIndex}:a]{string.Join(\',\', voiceFilters)}[voicea]";' in voice_core,
        "VOICE-13 mix must keep the Vietnamese input in its own chain")

# Keep: source is full level and is mixed with Voice.
require('var sourceFilters = new List<string> { "asetpts=PTS-STARTPTS" };' in voice_core,
        "VOICE-13 Keep must retain the original source audio")
require("[sourcea][voicea]amix=inputs=2:duration=longest:dropout_transition=0:normalize=0[aout]" in voice_core,
        "VOICE-13 Keep must mix source and Vietnamese voice without normalization attenuation")

# Duck: only source audio receives the persisted gain; Voice stays at its own gain.
require('if (audio.SourceMode == "duck") sourceFilters.Add("volume=" + audio.SourceGain.ToString("0.000", CultureInfo.InvariantCulture));' in voice_core,
        "VOICE-13 Duck must attenuate only the original source chain")

# Mute: no source input is referenced and the Vietnamese voice remains audible.
require('if (audio.SourceMode == "mute") return voiceChain + ";[voicea]anull[aout]"' in voice_core,
        "VOICE-13 Mute must route Vietnamese voice only")

# Player monitor controls are a separate local playback owner and cannot alter
# persisted source/Voice render semantics.
require("_monitorAudio" not in export_run and "SetMuted(" not in export_run and "SetVolume(" not in export_run,
        "VOICE-13 Export must not consume Player monitor mute/volume state")

print("PASS: VOICE-13 Vietnamese voice/source audio mix contract")
