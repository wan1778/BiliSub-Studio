#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
EDITOR = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml.cs"
PLAYBACK = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.Playback.cs"
PROJECT_STORE = ROOT / "csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs"
SUBTITLE_FINGERPRINT = ROOT / "csharp/src/BiliSubStudio.Core/Editor/EditorSubtitleSourceFingerprint.cs"


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
project_store = read(PROJECT_STORE)
subtitle_fingerprint = read(SUBTITLE_FINGERPRINT)

# VOICE-15 — reopening a project may restore only a previously completed local
# Vietnamese voice whose owning subtitle, Whisper analysis, TTS result manifest,
# pinned engine/profiles and master track are still valid. Reopen must reuse that
# track directly; it must never regenerate TTS just to preview/export the project.

open_video = editor.split("private async Task OpenVideoAsync()", 1)[1].split(
    "private async Task SaveCurrentSourceStateForSwitchAsync", 1
)[0]
require("candidateProject = await _application.LoadEditorProjectAsync" in open_video,
        "VOICE-15 reopen must load/normalize the persisted Editor project first")
require("else await RestoreSubtitleAsync(_project.Subtitle);" in open_video,
        "VOICE-15 reopen must restore the validated subtitle owner")
require("await RestoreSpeechAndVoiceAsync();" in open_video,
        "VOICE-15 reopen must restore speech/voice state")
require(open_video.index("await RestoreSpeechAndVoiceAsync();")
        < open_video.index("await _playback.PrepareAsync();"),
        "VOICE-15 voice must be restored before processed Preview is prepared")

load = project_store.split("public async Task<EditorProject> LoadOrCreateAsync(", 1)[1].split(
    "private static EditorProject CreateFreshProject", 1
)[0]
require("var subtitle = NormalizeSubtitle(loaded.Subtitle);" in load,
        "VOICE-15 project load must validate subtitle ownership before TTS")
require("Speech = NormalizeSpeech(loaded.Speech)," in load,
        "VOICE-15 project load must validate persisted Whisper analysis")
require("Tts = subtitle is null ? null : NormalizeTts(loaded.Tts)," in load,
        "VOICE-15 TTS must not survive a missing/changed subtitle")

subtitle = project_store.split("private static EditorSubtitleProject? NormalizeSubtitle(", 1)[1].split(
    "public static EditorAudioSettings NormalizeAudio", 1
)[0]
require("EditorSubtitleDocument.SourceFingerprintMatchesCurrent(" in subtitle
        and "path, subtitle.SourceSize, subtitle.SourceLastWriteUtcTicks, subtitle.SourceSha256" in subtitle,
        "VOICE-15 subtitle reopen must delegate to the canonical SRT fingerprint owner")
require("before.Length != expectedSize" in subtitle_fingerprint
        and "before.LastWriteTimeUtc.Ticks != expectedLastWriteUtcTicks" in subtitle_fingerprint
        and "SHA256.HashData(stream)" in subtitle_fingerprint
        and "after.Length == expectedSize" in subtitle_fingerprint
        and "after.LastWriteTimeUtc.Ticks == expectedLastWriteUtcTicks" in subtitle_fingerprint
        and "string.Equals(sha256, expectedSha256, StringComparison.OrdinalIgnoreCase)" in subtitle_fingerprint,
        "VOICE-15 canonical SRT fingerprint must validate size/timestamp/SHA before restore")

speech = project_store.split("private static EditorSpeechProject? NormalizeSpeech(", 1)[1].split(
    "private static EditorTtsProject? NormalizeTts", 1
)[0]
require("!File.Exists(analysisPath)" in speech
        and "!FileShaMatches(analysisPath, speech.AnalysisSha256)" in speech,
        "VOICE-15 Whisper reopen validity must include analysis existence and SHA")

tts = project_store.split("private static EditorTtsProject? NormalizeTts(", 1)[1].split(
    "private static IReadOnlyDictionary<string, string> NormalizeVoiceOverrides", 1
)[0]
for token in (
    "CurrentTtsEngine",
    "LocalTtsInstaller.EngineVersion",
    "LocalTtsInstaller.Voice",
):
    require(token in tts, f"VOICE-15 TTS reopen lost pinned runtime/profile check: {token}")
require("!File.Exists(manifest)" in tts
        and "!FileShaMatches(manifest, tts.ManifestSha256)" in tts,
        "VOICE-15 TTS result manifest must exist and match persisted SHA")
require("!File.Exists(trackPath)" in tts,
        "VOICE-15 generated master track must still exist on reopen")
require("new FileInfo(trackPath).Length <= 64" in tts,
        "VOICE-15 truncated/empty master track must not be restored as valid")
require("VoiceTrack = track with { Path = trackPath, Gain = Math.Clamp(track.Gain, 0, 4) }" in tts,
        "VOICE-15 validated normalized master must stay in reopened project state")

restore = editor.split("private async Task RestoreSpeechAndVoiceAsync()", 1)[1].split(
    "private async Task RefreshSpeechTimingForSubtitleAsync", 1
)[0]
require("_cueSpeechTiming = [];" in restore and "_voiceTrack = null;" in restore,
        "VOICE-15 reopen must clear runtime owners before validating persisted state")
require("_project?.Speech is not { Status: \"complete\" } speech" in restore,
        "VOICE-15 a voice is not restorable when its Whisper timing owner is unavailable")
require("await RefreshSpeechTimingForSubtitleAsync();" in restore,
        "VOICE-15 valid Whisper analysis must be SHA-loaded/mapped before voice promotion")
require("_project = _project with { Speech = null, Tts = null };" in restore,
        "VOICE-15 failed Whisper verification must invalidate persisted TTS ownership")
require("_project.Tts is { Status: \"complete\" } tts && File.Exists(tts.VoiceTrack.Path)" in restore,
        "VOICE-15 only a complete normalized TTS project may become runtime voice")
require("_voiceTrack = tts.VoiceTrack;" in restore,
        "VOICE-15 valid reopened TTS must repopulate the runtime voice owner")
require("Preview/Export dùng cùng track" in restore,
        "VOICE-15 UI must report that the restored track is the shared Preview/Export owner")
require("StartEditorTts" not in restore and "Generate" not in restore,
        "VOICE-15 reopen must reuse the completed master without regenerating TTS")

current_request = editor.split("private VideoEditRequest CurrentEditRequest(", 1)[1].split(
    "private async void Render_Click", 1
)[0]
require("_voiceTrack);" in current_request,
        "VOICE-15 restored runtime voice must flow into the shared edit request")

preview_load = playback.split("private async Task LoadSegmentCoreAsync(", 1)[1].split(
    "private async Task ActivateSegmentAsync", 1
)[0]
require("_page.CurrentEditRequest(_page.PreviewSubtitleBurn())" in preview_load,
        "VOICE-15 processed Preview must consume the restored shared request")

# Small validity fixture mirroring reopen policy. "speech_ok" is deliberately
# required: a master without its verified timing owner is not considered a valid
# restorable project voice, even if an audio file happens to remain on disk.
def restorable_voice(
    subtitle_ok: bool,
    speech_ok: bool,
    manifest_ok: bool,
    engine_ok: bool,
    profile_ok: bool,
    track_exists: bool,
    track_size: int,
) -> bool:
    return (
        subtitle_ok
        and speech_ok
        and manifest_ok
        and engine_ok
        and profile_ok
        and track_exists
        and track_size > 64
    )


require(restorable_voice(True, True, True, True, True, True, 4096),
        "VOICE-15 fixture: a fully valid persisted voice must restore")
require(not restorable_voice(False, True, True, True, True, True, 4096),
        "VOICE-15 fixture: changed/missing subtitle must reject old voice")
require(not restorable_voice(True, False, True, True, True, True, 4096),
        "VOICE-15 fixture: missing/invalid Whisper timing must reject voice restore")
require(not restorable_voice(True, True, False, True, True, True, 4096),
        "VOICE-15 fixture: bad TTS manifest SHA must reject voice restore")
require(not restorable_voice(True, True, True, False, True, True, 4096),
        "VOICE-15 fixture: changed TTS engine/version must reject old voice")
require(not restorable_voice(True, True, True, True, False, True, 4096),
        "VOICE-15 fixture: changed pinned voice profile must reject old voice")
require(not restorable_voice(True, True, True, True, True, False, 0),
        "VOICE-15 fixture: missing master must reject voice restore")
require(not restorable_voice(True, True, True, True, True, True, 32),
        "VOICE-15 fixture: truncated master must reject voice restore")

print("PASS: VOICE-15 reopened project restores only a valid Vietnamese voice track")
