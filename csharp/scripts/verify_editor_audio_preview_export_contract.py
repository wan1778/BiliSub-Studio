#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages"
PLAYBACK = PAGES / "EditorPage.Playback.cs"
EDITOR_MAIN = PAGES / "EditorPage.xaml.cs"
EDITOR_IMAGES = PAGES / "EditorPage.Images.cs"
VIDEO_EDITOR = ROOT / "csharp/src/BiliSubStudio.Core/Editor/VideoEditorService.cs"
IMAGE_COMPOSER = ROOT / "csharp/src/BiliSubStudio.App/Services/EditorImageOverlayComposer.cs"


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
editor_images = read(EDITOR_IMAGES)
video_editor = read(VIDEO_EDITOR)
image_composer = read(IMAGE_COMPOSER)

# AUDIO-08 — Processed Preview and final Export must originate from one request owner.
current_request = editor_main.split("private VideoEditRequest CurrentEditRequest(", 1)[1].split(
    "private async void Render_Click", 1)[0]
require("_audioSettings," in current_request and "_voiceTrack" in current_request,
        "AUDIO-08 shared VideoEditRequest must carry both source-audio policy and Voice track")

preview_load = playback.split("private async Task LoadSegmentCoreAsync(", 1)[1].split(
    "private async Task ActivateSegmentAsync(", 1)[0]
require("_page.CurrentEditRequest(_page.CompletedSubtitleBurn())" in preview_load,
        "AUDIO-08 processed Preview must originate from the same exportable CurrentEditRequest")

image_export = editor_images.split("private async Task RenderProjectAsync()", 1)[1].split(
    "private void RefreshImageControls()", 1)[0]
require("_application.StartEditor(CurrentEditRequest(subtitle))" in image_export,
        "AUDIO-08 direct Export must originate from CurrentEditRequest")
require("var request = CurrentEditRequest(subtitle) with" in image_export,
        "AUDIO-08 image/logo base Export must retain CurrentEditRequest audio state")

# Preview slicing may change timeline/dimensions only; it must not replace audio policy.
preview_slice = video_editor.split("internal static VideoEditRequest BuildPreviewSlice(", 1)[1].split(
    "internal static IReadOnlyList<string> BuildPreviewArguments", 1)[0]
require("Audio =" not in preview_slice and "VoiceTrack =" not in preview_slice,
        "AUDIO-08 preview slicing must preserve Audio/VoiceTrack from the shared request")

# Export consumes the shared request and delegates source audio to one core.
export_run = video_editor.split("public async Task<VideoEditResult> RunAsync(", 1)[1].split(
    "private async Task ValidateRenderedOutputAsync", 1)[0]
require("var audio = EditorProjectStore.NormalizeAudio(request.Audio);" in export_run,
        "AUDIO-08 Export must normalize source-audio policy from the shared request")
require("var voice = NormalizeVoiceTrack(request.VoiceTrack, requireFile: true);" in export_run,
        "AUDIO-08 Export must consume Voice from the shared request")
require("BuildAudioArguments(audio, mp4)" in export_run,
        "AUDIO-08 Export no-Voice path must enter the shared source-audio policy core")
require("BuildVoiceAudioFilter(audio, voice, 1, sourceStart: 0)" in export_run,
        "AUDIO-08 Export Voice path must enter the shared Voice policy core")

export_audio_wrapper = video_editor.split(
    "internal static IReadOnlyList<string> BuildAudioArguments(EditorAudioSettings? settings, bool mp4)", 1)[1].split(
    "private static IReadOnlyList<string> BuildAudioArgumentsCore", 1)[0]
require("=> BuildAudioArgumentsCore(settings, mp4, resetTimestamps: false);" in export_audio_wrapper,
        "AUDIO-08 Export source audio must delegate to BuildAudioArgumentsCore")

# Preview consumes the sliced copy of the same request and delegates to the same cores.
preview_run = video_editor.split("public async Task<EditorPreviewSegment> CreatePreviewSegmentAsync(", 1)[1].split(
    "public async Task DeletePreviewSegmentAsync", 1)[0]
require("BuildVoiceAudioFilter(EditorProjectStore.NormalizeAudio(sliced.Audio), voice, 1, sourceStart)" in preview_run,
        "AUDIO-08 Preview Voice path must use the same Voice policy core with segment timing")
require("BuildPreviewArguments(input, temporary, graph, sliced.Audio, sourceStart, segmentDuration, voice)" in preview_run,
        "AUDIO-08 Preview must forward the same audio/Voice state into FFmpeg arguments")

preview_arguments = video_editor.split("internal static IReadOnlyList<string> BuildPreviewArguments(", 1)[1].split(
    "internal static IReadOnlyList<string> BuildAudioArguments", 1)[0]
require("BuildAudioArgumentsCore(audio, mp4: true, resetTimestamps: true)" in preview_arguments,
        "AUDIO-08 Preview source audio must delegate to the same BuildAudioArgumentsCore")
require('else arguments.AddRange(["-map", "[aout]", "-c:a", "aac", "-b:a", "192k"]);' in preview_arguments,
        "AUDIO-08 Preview Voice output must map the shared [aout] mix")

audio_core = video_editor.split("private static IReadOnlyList<string> BuildAudioArgumentsCore", 1)[1].split(
    "public async Task<byte[]> GetPreviewFrameJpegAsync", 1)[0]
require('if (resetTimestamps) filters.Add("asetpts=PTS-STARTPTS");' in audio_core,
        "AUDIO-08 Preview may reset audio timestamps without changing Keep/Duck/Mute semantics")
require('if (audio.SourceMode == "mute") return ["-an"];' in audio_core,
        "AUDIO-08 shared source-audio core must keep Mute semantics")
require('if (audio.SourceMode == "duck") filters.Add("volume=" + audio.SourceGain.ToString("0.000", CultureInfo.InvariantCulture));' in audio_core,
        "AUDIO-08 shared source-audio core must keep Duck semantics")
require('var arguments = new List<string> { "-map", "0:a?" };' in audio_core,
        "AUDIO-08 shared source-audio core must keep source audio for Keep/Duck")

voice_core = video_editor.split("private static string BuildVoiceAudioFilter", 1)[1].split(
    "public static bool IsActiveAt", 1)[0]
require('if (audio.SourceMode == "mute") return voiceChain + ";[voicea]anull[aout]";' in voice_core,
        "AUDIO-08 shared Voice core must keep Mute+Voice as Voice-only")
require('if (audio.SourceMode == "duck") sourceFilters.Add("volume=" + audio.SourceGain.ToString("0.000", CultureInfo.InvariantCulture));' in voice_core,
        "AUDIO-08 shared Voice core must attenuate source only for Duck")
require("[sourcea][voicea]amix=inputs=2:duration=longest:dropout_transition=0:normalize=0[aout]" in voice_core,
        "AUDIO-08 shared Voice core must keep identical Keep/Duck mix semantics")

# Image/logo is a post-video stage. It may copy/re-encode the already-produced audio,
# but it must not invent a second gain/mix policy.
image_render = image_composer.split("public async Task<string> RenderAsync(", 1)[1].split(
    "private async Task ValidateAsync", 1)[0]
require('"-map", "[vout]", "-map", "0:a?"' in image_render,
        "AUDIO-08 image/logo stage must pass through the input audio stream when present")
require('if (copyAudio) args.AddRange(["-c:a", "copy"]);' in image_render
        and 'else args.AddRange(["-c:a", "aac", "-b:a", "192k"]);' in image_render,
        "AUDIO-08 image/logo stage may only choose copy/AAC compatibility")
require("volume=" not in image_render and "amix=" not in image_render,
        "AUDIO-08 image/logo stage must not re-attenuate or remix Editor audio")

print("PASS: AUDIO-08 processed Preview and Export share one audio policy")