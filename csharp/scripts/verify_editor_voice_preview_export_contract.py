#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages"
EDITOR = PAGES / "EditorPage.xaml.cs"
PLAYBACK = PAGES / "EditorPage.Playback.cs"
IMAGES = PAGES / "EditorPage.Images.cs"
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
images = read(IMAGES)
video_editor = read(VIDEO_EDITOR)

# VOICE-14 — Processed Preview and final Export share blur/source-audio/
# Vietnamese-voice render ownership. Preview intentionally allows draft/source
# fallback captions so edits are visible before translation is complete; Export
# remains stricter and may burn only completed Vietnamese subtitles.

completed_subtitle = editor.split("private EditorSubtitleBurn? CompletedSubtitleBurn()", 1)[1].split(
    "private EditorSubtitleBurn? PreviewSubtitleBurn()", 1
)[0]
require("_subtitleSource.Cues.All(x => !string.IsNullOrWhiteSpace(x.VietnameseText))" in completed_subtitle,
        "VOICE-14 export subtitle owner must require complete Vietnamese text")
require("new EditorSubtitleBurn(_subtitleSource.Cues, _subtitlePlacement, _cueSpeechTiming, KaraokeToggle.IsOn)" in completed_subtitle,
        "VOICE-14 export subtitle owner must carry the same placement/timing/karaoke state")

preview_subtitle = editor.split("private EditorSubtitleBurn? PreviewSubtitleBurn()", 1)[1].split(
    "private VideoEditRequest CurrentEditRequest(", 1
)[0]
require("cue.SourceText" in preview_subtitle
        and "string.IsNullOrWhiteSpace(cue.VietnameseText) ? cue.SourceText : cue.VietnameseText" in preview_subtitle,
        "VOICE-14 processed Preview must preserve draft/source fallback captions")

# Processed playback and prefetch intentionally use the Preview subtitle helper,
# while CurrentEditRequest still owns the shared blur/audio/voice state.
require(playback.count("_page.CurrentEditRequest(_page.PreviewSubtitleBurn())") == 2,
        "VOICE-14 playback and prefetch must both originate from the shared Preview request")
require("_page.CurrentEditRequest(_page.CompletedSubtitleBurn())" not in playback,
        "VOICE-14 processed Preview must not regress to completed-only subtitle gating")

load_segment = playback.split("private async Task LoadSegmentCoreAsync(", 1)[1].split(
    "private async Task ActivateSegmentAsync(", 1
)[0]
require("CreateEditorPreviewSegmentAsync(" in load_segment
        and "_page.CurrentEditRequest(_page.PreviewSubtitleBurn())" in load_segment,
        "VOICE-14 current processed segment must use the shared Preview request")

prefetch_owner = playback.split("private void StartNextSegmentPrefetch()", 1)[1].split(
    "private async Task PrefetchNextSegmentAsync", 1
)[0]
require("var request = _page.CurrentEditRequest(_page.PreviewSubtitleBurn());" in prefetch_owner,
        "VOICE-14 next processed segment must snapshot the same Preview request")

current_request = editor.split("private VideoEditRequest CurrentEditRequest(", 1)[1].split(
    "private async void Render_Click", 1
)[0]
for token in (
    "_document.Regions.ToArray(),",
    "subtitle,",
    "_audioSettings,",
    "_voiceTrack);",
):
    require(token in current_request, f"VOICE-14 shared request lost render state: {token}")
require("_monitorAudio" not in current_request,
        "VOICE-14 Player monitor volume/mute must not enter Preview/Export render state")

# Export remains completed-Vietsub only, but PROJECT-10 locks the output target by
# cloning CurrentEditRequest into a captured request before StartEditor. Both the
# direct export and image-base stage must preserve that shared request ownership.
render_project = images.split("private async Task RenderProjectAsync()", 1)[1].split(
    "private void RefreshImageControls()", 1
)[0]
require("var subtitle = CompletedSubtitleBurn();" in render_project,
        "VOICE-14 Export must retain the completed-Vietnamese subtitle owner")
require(render_project.count("var request = CurrentEditRequest(subtitle) with") == 2,
        "VOICE-14 both Export paths must originate from CurrentEditRequest before locking the output target")
require(render_project.count("_jobId = _application.StartEditor(request);") == 2,
        "VOICE-14 both Export paths must render the locked CurrentEditRequest snapshot")

# Full Export and sliced Preview share the same visual filter owner and ASS builder.
export_run = video_editor.split("public async Task<VideoEditResult> RunAsync(", 1)[1].split(
    "private async Task ValidateRenderedOutputAsync", 1
)[0]
preview_run = video_editor.split("public async Task<EditorPreviewSegment> CreatePreviewSegmentAsync(", 1)[1].split(
    "public async Task DeletePreviewSegmentAsync", 1
)[0]
require("BuildAss(request.Subtitle!, request.SourceWidth, request.SourceHeight)" in export_run,
        "VOICE-14 Export subtitle must be rendered by the shared BuildAss owner")
require("BuildFilter(request, subtitleAss)" in export_run,
        "VOICE-14 Export video effects must enter the shared filter core")
require("BuildAss(sliced.Subtitle!, previewWidth, previewHeight)" in preview_run,
        "VOICE-14 Preview subtitle must be rendered by the same BuildAss owner")
require("BuildFilterCore(sliced, subtitleAss, \"previewbase\", requireEdit: false)" in preview_run,
        "VOICE-14 Preview video effects must enter the same filter core after timeline slicing")

preview_slice = video_editor.split("internal static VideoEditRequest BuildPreviewSlice(", 1)[1].split(
    "internal static IReadOnlyList<string> BuildPreviewArguments", 1
)[0]
require("Audio =" not in preview_slice and "VoiceTrack =" not in preview_slice,
        "VOICE-14 preview slicing may not replace source-audio or Vietnamese-voice ownership")
require("Start = Math.Max(0, region.Start - sourceStart)" in preview_slice
        and "End = Math.Min(segmentDuration, region.End - sourceStart)" in preview_slice,
        "VOICE-14 Preview may only translate effect timing into the segment timeline")
require("Start = Math.Max(0, cue.Start - sourceStart)" in preview_slice
        and "End = Math.Min(segmentDuration, cue.End - sourceStart)" in preview_slice,
        "VOICE-14 Preview may only translate subtitle timing into the segment timeline")
require("MosaicScaleX = previewWidth / (double)request.SourceWidth" in preview_slice
        and "MosaicScaleY = previewHeight / (double)request.SourceHeight" in preview_slice,
        "VOICE-14 downscaled Preview must compensate mosaic strength for source geometry")

# Voice path: both outputs use one source/voice mix owner. Preview contributes only
# sourceStart so the Vietnamese master is seeked/delayed onto the sliced timeline.
require("BuildVoiceAudioFilter(audio, voice, 1, sourceStart: 0)" in export_run,
        "VOICE-14 Export must use the shared Vietnamese voice mix owner")
require("BuildVoiceAudioFilter(EditorProjectStore.NormalizeAudio(sliced.Audio), voice, 1, sourceStart)" in preview_run,
        "VOICE-14 Preview must use the same Vietnamese voice mix owner")

preview_args = video_editor.split("internal static IReadOnlyList<string> BuildPreviewArguments(", 1)[1].split(
    "internal static IReadOnlyList<string> BuildAudioArguments", 1
)[0]
require("BuildAudioArgumentsCore(audio, mp4: true, resetTimestamps: true)" in preview_args,
        "VOICE-14 no-voice Preview must share source-audio policy with Export")
require("var voiceSeek = Math.Max(0, sourceStart - voice.Start);" in preview_args,
        "VOICE-14 Preview may seek Vietnamese master only to align the preview window")

export_audio = video_editor.split(
    "internal static IReadOnlyList<string> BuildAudioArguments(EditorAudioSettings? settings, bool mp4)", 1
)[1].split("private static IReadOnlyList<string> BuildAudioArgumentsCore", 1)[0]
require("=> BuildAudioArgumentsCore(settings, mp4, resetTimestamps: false);" in export_audio,
        "VOICE-14 no-voice Export must delegate to the same source-audio policy core")

voice_core = video_editor.split("private static string BuildVoiceAudioFilter", 1)[1].split(
    "public static bool IsActiveAt", 1
)[0]
require("voice.Start - sourceStart" in voice_core
        and "voice.Gain" in voice_core
        and "[sourcea][voicea]amix=inputs=2:duration=longest:dropout_transition=0:normalize=0[aout]" in voice_core,
        "VOICE-14 shared mix owner must preserve timing, gain and Keep/Duck source mixing")
require('if (audio.SourceMode == "mute") return voiceChain + ";[voicea]anull[aout]"' in voice_core,
        "VOICE-14 shared mix owner must preserve Mute as Vietnamese voice only")

activate = playback.split("private async Task ActivateSegmentAsync(", 1)[1].split(
    "private void StartNextSegmentPrefetch", 1
)[0]
require("MediaSource.CreateFromStorageFile(file)" in activate,
        "VOICE-14 Player must play the rendered processed segment, not raw source media")

# Encoding/performance differences are allowed; semantic audio/voice/effect state is not.
require('"-preset", "medium", "-crf", "18"' in export_run,
        "VOICE-14 fixture expects final Export quality settings to remain independent")
require('"-preset", "ultrafast", "-crf", "23"' in preview_args,
        "VOICE-14 fixture expects Preview to retain its faster encoder")

# Tiny policy fixture: incomplete Vietsub is visible in Preview via source fallback,
# but it is intentionally not exportable until every Vietnamese cue is complete.
def completed_subtitle(vietnamese: list[str]) -> bool:
    return bool(vietnamese) and all(value.strip() for value in vietnamese)


def preview_caption(source: str, vietnamese: str) -> str:
    return vietnamese if vietnamese.strip() else source


require(completed_subtitle(["Xin chào", "Đạo hữu"]) is True,
        "VOICE-14 fixture: complete Vietsub must remain exportable")
require(preview_caption("你好", "Xin chào") == "Xin chào",
        "VOICE-14 fixture: completed cue must preview Vietnamese text")
require(completed_subtitle(["Xin chào", ""]) is False,
        "VOICE-14 fixture: incomplete Vietsub must remain blocked from Export")
require(preview_caption("道友", "") == "道友",
        "VOICE-14 fixture: untranslated cue must remain visible in Preview through source fallback")
require(completed_subtitle(["  "]) is False,
        "VOICE-14 fixture: whitespace-only Vietsub is not exportable")
require(preview_caption("原文", "  ") == "原文",
        "VOICE-14 fixture: whitespace-only draft must fall back to source text in Preview")

print("PASS: VOICE-14 Preview/Export share render state with intentional draft-caption policy")
