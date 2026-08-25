#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
XAML = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml"
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


xaml = read(XAML)
editor = read(EDITOR)
playback = read(PLAYBACK)
video_editor = read(VIDEO_EDITOR)

# VOICE-12 — Preview must play the validated Vietnamese master track.
# The TTS result, shared edit request, processed preview renderer and MediaPlayer
# must remain one path; the preview slice may move time but may not drop the track.
require(xaml.count('x:Name="PlayerPlayPauseButton"') == 1
        and 'x:Name="PlayerPlayPauseButton" Click="PlayerPlayPause_Click"' in xaml,
        "VOICE-12 preview transport must keep one Play/Pause event owner")

tts_poll = editor.split("private async Task PollTtsJobAsync()", 1)[1].split(
    "private void CancelVoice_Click(", 1
)[0]
require("snapshot.Result is EditorTtsResult result" in tts_poll,
        "VOICE-12 Preview may only promote a successful TTS result")
require("_voiceTrack = result.VoiceTrack;" in tts_poll,
        "VOICE-12 completed Vietnamese master must become the Preview owner")
require("Tts = new EditorTtsProject(" in tts_poll
        and "await SaveProjectNowAsync();" in tts_poll,
        "VOICE-12 Preview owner must be persisted with the completed TTS project state")
require("QueuePreviewRefresh();" in tts_poll,
        "VOICE-12 completed TTS must refresh the editor before playback")

current_request = editor.split("private VideoEditRequest CurrentEditRequest(", 1)[1].split(
    "private async void Render_Click", 1
)[0]
require("_audioSettings," in current_request and "_voiceTrack);" in current_request,
        "VOICE-12 shared Preview request must carry the source audio policy and Vietnamese track")

load_segment = playback.split("private async Task LoadSegmentCoreAsync(", 1)[1].split(
    "private async Task ActivateSegmentAsync(", 1
)[0]
require("_page.CurrentEditRequest(_page.PreviewSubtitleBurn())" in load_segment
        and "CreateEditorPreviewSegmentAsync" in load_segment,
        "VOICE-12 processed playback must render from the current shared Preview request")
require("ActivateSegmentAsync(" in load_segment,
        "VOICE-12 processed playback must activate the rendered segment in the player")

prefetch = playback.split("private async Task PrefetchNextSegmentAsync(", 1)[1].split(
    "private async Task DiscardPrefetchedSegmentAsync", 1
)[0]
require("CreateEditorPreviewSegmentAsync(\n                        request, nextStart, cancellationToken)" in prefetch,
        "VOICE-12 preview prefetch must use the same request carrying the Vietnamese track")

preview_run = video_editor.split("public async Task<EditorPreviewSegment> CreatePreviewSegmentAsync(", 1)[1].split(
    "public async Task DeletePreviewSegmentAsync", 1
)[0]
require("var sliced = BuildPreviewSlice(request, sourceStart, segmentDuration, previewWidth, previewHeight);" in preview_run,
        "VOICE-12 Preview must create the time-sliced request through the shared owner")
require("var voice = NormalizeVoiceTrack(sliced.VoiceTrack, requireFile: true);" in preview_run,
        "VOICE-12 Preview must validate the generated Vietnamese master before rendering")
require("BuildVoiceAudioFilter(EditorProjectStore.NormalizeAudio(sliced.Audio), voice, 1, sourceStart)" in preview_run,
        "VOICE-12 Preview must route the validated track through the voice audio graph")
require("BuildPreviewArguments(input, temporary, graph, sliced.Audio, sourceStart, segmentDuration, voice)" in preview_run,
        "VOICE-12 Preview must forward the validated track into FFmpeg arguments")

preview_slice = video_editor.split("internal static VideoEditRequest BuildPreviewSlice(", 1)[1].split(
    "internal static IReadOnlyList<string> BuildPreviewArguments", 1
)[0]
require("VoiceTrack =" not in preview_slice and "Audio =" not in preview_slice,
        "VOICE-12 preview slicing may not replace the shared VoiceTrack or Audio state")

preview_arguments = video_editor.split("internal static IReadOnlyList<string> BuildPreviewArguments(", 1)[1].split(
    "internal static IReadOnlyList<string> BuildAudioArguments", 1
)[0]
require("var voice = NormalizeVoiceTrack(voiceTrack, requireFile: false);" in preview_arguments,
        "VOICE-12 Preview argument construction must retain the validated track owner")
require("var voiceSeek = Math.Max(0, sourceStart - voice.Start);" in preview_arguments
        and '"-ss", voiceSeek.ToString("0.000", CultureInfo.InvariantCulture), "-i", voice.Path' in preview_arguments,
        "VOICE-12 Preview must seek the voice master to the same source timeline")
require('else arguments.AddRange(["-map", "[aout]", "-c:a", "aac", "-b:a", "192k"]);' in preview_arguments,
        "VOICE-12 Preview must map the Vietnamese/mixed audio output instead of source audio directly")

voice_core = video_editor.split("private static string BuildVoiceAudioFilter", 1)[1].split(
    "public static bool IsActiveAt", 1
)[0]
require("[{voiceInputIndex}:a]" in voice_core and "asetpts=PTS-STARTPTS" in voice_core,
        "VOICE-12 voice graph must consume the separate Vietnamese audio input")
require("voice.Start - sourceStart" in voice_core and "adelay=" in voice_core,
        "VOICE-12 voice graph must preserve the master track timing in each preview segment")
require("voice.Gain" in voice_core,
        "VOICE-12 voice graph must preserve the validated track gain")
require('if (audio.SourceMode == "mute") return voiceChain + ";[voicea]anull[aout]"' in voice_core,
        "VOICE-12 Preview with muted source must remain voice-only")
require("[sourcea][voicea]amix=inputs=2:duration=longest:dropout_transition=0:normalize=0[aout]" in voice_core,
        "VOICE-12 Preview with source audio must mix the Vietnamese track through the shared graph")

activate = playback.split("private async Task ActivateSegmentAsync(", 1)[1].split(
    "private void StartNextSegmentPrefetch", 1
)[0]
require("MediaSource.CreateFromStorageFile(file)" in activate
        and "player.PlaybackSession.Position" in activate,
        "VOICE-12 Preview must activate and seek the rendered segment before playback")

print("PASS: VOICE-12 Vietnamese master track reaches processed Preview")
