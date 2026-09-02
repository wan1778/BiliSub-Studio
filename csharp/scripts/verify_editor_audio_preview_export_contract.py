#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
PAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages"
EDITOR = (PAGES / "EditorPage.xaml.cs").read_text(encoding="utf-8")
PLAYBACK = (PAGES / "EditorPage.Playback.cs").read_text(encoding="utf-8")
IMAGES = (PAGES / "EditorPage.Images.cs").read_text(encoding="utf-8")
SERVICE = (ROOT / "csharp/src/BiliSubStudio.Core/Editor/VideoEditorService.cs").read_text(encoding="utf-8")
COMPOSER = (ROOT / "csharp/src/BiliSubStudio.App/Services/EditorImageOverlayComposer.cs").read_text(encoding="utf-8")


def require(condition: bool, message: str) -> None:
    if not condition:
        print("FAIL:", message, file=sys.stderr)
        raise SystemExit(1)


# One request owner supplies identical persisted source gain and voice state.
request = EDITOR.split("private VideoEditRequest CurrentEditRequest(", 1)[1].split(
    "private async void Render_Click", 1
)[0]
require("_audioSettings," in request and "_voiceTrack" in request,
        "AUDIO-SHARED CurrentEditRequest lost audio or voice state")

preview = PLAYBACK.split("private async Task LoadSegmentCoreAsync(", 1)[1].split(
    "private async Task ActivateSegmentAsync(", 1
)[0]
require("_page.CurrentEditRequest(_page.PreviewSubtitleBurn())" in preview,
        "AUDIO-SHARED processed Preview does not originate from CurrentEditRequest")

export = IMAGES.split("private async Task RenderProjectAsync()", 1)[1].split(
    "private void RefreshImageControls()", 1
)[0]
require("var request = CurrentEditRequest(subtitle) with" in export
        and "_application.StartEditor(request)" in export,
        "AUDIO-SHARED final Export does not originate from CurrentEditRequest")

# Preview slicing may move timelines and dimensions, but must preserve Audio/Voice.
slice_policy = SERVICE.split("internal static VideoEditRequest BuildPreviewSlice(", 1)[1].split(
    "internal static IReadOnlyList<string> BuildPreviewArguments", 1
)[0]
require("Audio =" not in slice_policy and "VoiceTrack =" not in slice_policy,
        "AUDIO-SHARED preview slicing replaced source gain or voice state")

run = SERVICE.split("public async Task<VideoEditResult> RunAsync(", 1)[1].split(
    "private async Task ValidateRenderedOutputAsync", 1
)[0]
require("var audio = EditorProjectStore.NormalizeAudio(renderRequest.Audio);" in run,
        "AUDIO-SHARED Export does not normalize sliced request audio")
require("var voice = NormalizeVoiceTrack(renderRequest.VoiceTrack, requireFile: true);" in run,
        "AUDIO-SHARED Export lost voice track")
require("BuildRenderArguments(input, temporary, graph, audio, trim, voice, mp4)" in run,
        "AUDIO-SHARED Export bypassed common FFmpeg argument policy")

preview_run = SERVICE.split("public async Task<EditorPreviewSegment> CreatePreviewSegmentAsync(", 1)[1].split(
    "public async Task DeletePreviewSegmentAsync", 1
)[0]
require("BuildVoiceAudioFilter(EditorProjectStore.NormalizeAudio(sliced.Audio), voice, 1, sourceStart)" in preview_run,
        "AUDIO-SHARED Preview voice mix bypassed normalized source gain")
require("BuildPreviewArguments(input, temporary, graph, sliced.Audio, sourceStart, segmentDuration, voice)" in preview_run,
        "AUDIO-SHARED Preview arguments lost request audio")

preview_args = SERVICE.split("internal static IReadOnlyList<string> BuildPreviewArguments(", 1)[1].split(
    "internal static IReadOnlyList<string> BuildRenderArguments", 1
)[0]
render_args = SERVICE.split("internal static IReadOnlyList<string> BuildRenderArguments(", 1)[1].split(
    "internal static IReadOnlyList<string> BuildAudioArguments", 1
)[0]
require("BuildAudioArgumentsCore(audio, mp4: true, resetTimestamps: true)" in preview_args,
        "AUDIO-SHARED Preview does not use source gain core")
require("BuildAudioArgumentsCore(audio, mp4, resetTimestamps: true)" in render_args,
        "AUDIO-SHARED Export does not use source gain core")
require("BuildVoiceAudioFilter(audio, voice, 1, trim.Start)" in render_args,
        "AUDIO-SHARED Export voice mix does not use source gain")

audio_core = SERVICE.split("private static IReadOnlyList<string> BuildAudioArgumentsCore", 1)[1].split(
    "public async Task<byte[]> GetPreviewFrameJpegAsync", 1
)[0]
voice_core = SERVICE.split("private static string BuildVoiceAudioFilter", 1)[1].split(
    "public static bool IsActiveAt", 1
)[0]
require('if (audio.SourceMode == "mute") return ["-an"];' in audio_core
        and 'volume=" + audio.SourceGain.ToString("0.000"' in audio_core,
        "AUDIO-SHARED source-only FFmpeg gain policy lost")
require('if (audio.SourceMode == "mute") return voiceChain + ";[voicea]anull[aout]";' in voice_core
        and 'volume=" + audio.SourceGain.ToString("0.000"' in voice_core,
        "AUDIO-SHARED voice mix gain policy lost")

# Image/logo is post-processing and must not apply source gain twice.
image_render = COMPOSER.split("public async Task<string> RenderAsync(", 1)[1].split(
    "private async Task ValidateAsync", 1
)[0]
require('"-map", "[vout]", "-map", "0:a?"' in image_render,
        "AUDIO-SHARED image stage lost rendered audio")
require("volume=" not in image_render and "amix=" not in image_render,
        "AUDIO-SHARED image stage applies source gain a second time")

print("PASS: AUDIO-SHARED processed Preview and final Export consume one direct source-gain owner")
