#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
XAML = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml"
EDITOR = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml.cs"
APPLICATION = ROOT / "csharp/src/BiliSubStudio.Core/Application/BiliSubApplication.cs"
TTS = ROOT / "csharp/src/BiliSubStudio.Core/Editor/LocalTtsService.cs"
INSTALLER = ROOT / "csharp/src/BiliSubStudio.Core/Editor/LocalTtsInstaller.cs"
PROJECT = ROOT / "csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs"
VIDEO_EDITOR = ROOT / "csharp/src/BiliSubStudio.Core/Editor/VideoEditorService.cs"
WORKER = ROOT / "internal/tts/worker.py"
APP_PROJECT = ROOT / "csharp/src/BiliSubStudio.App/BiliSubStudio.App.csproj"
VOICE_CONTRACT = ROOT / "csharp/tests/BiliSubStudio.Core.ContractTests/EditorLicensedVoiceProfileContract.cs"


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
application = read(APPLICATION)
tts = read(TTS)
installer = read(INSTALLER)
project = read(PROJECT)
video_editor = read(VIDEO_EDITOR)
worker = read(WORKER)
app_project = read(APP_PROJECT)
voice_contract = read(VOICE_CONTRACT)

# VOICE-07 — Generate Vietnamese voice.
# Lock the single path from the Editor button through a local Piper/VAIS worker,
# timing fit, validated master track persistence, and Preview/Export handoff.

require(xaml.count('x:Name="GenerateTtsButton"') == 1
        and 'x:Name="GenerateTtsButton" Click="GenerateTts_Click"' in xaml,
        "VOICE-07 Generate TTS button must keep one XAML event owner")

generate = editor.split("private async void GenerateTts_Click(", 1)[1].split(
    "private async Task PollTtsJobAsync()", 1
)[0]
require("if (_ttsJobId is not null" in generate,
        "VOICE-07 UI must prevent a second TTS start while one is active")
require('_project.Speech is not { Status: "complete" } speech' in generate,
        "VOICE-07 generation must require completed Whisper speech timing")
require("_subtitleSource.Cues.Any(x => string.IsNullOrWhiteSpace(x.VietnameseText))" in generate,
        "VOICE-07 generation must require every cue to have Vietnamese text")
require("_ttsJobId = _application.StartEditorTts(new EditorTtsRequest(" in generate,
        "VOICE-07 UI must create the TTS job through the application owner")
for marker in (
    "_project.Id,",
    "_path,",
    "_media.Duration,",
    "_subtitleSource,",
    "speech.AnalysisPath,",
    "speech.AnalysisSha256,",
    "_project.VoiceOverrides",
):
    require(marker in generate, f"VOICE-07 TTS request lost {marker}")
require("await PollTtsJobAsync();" in generate,
        "VOICE-07 UI must poll the exact TTS job it started")

poll = editor.split("private async Task PollTtsJobAsync()", 1)[1].split(
    "private void CancelVoice_Click(", 1
)[0]
require("snapshot.Result is EditorTtsResult result" in poll,
        "VOICE-07 project state may only promote a real EditorTtsResult")
require("_voiceTrack = result.VoiceTrack;" in poll,
        "VOICE-07 completed master voice track must become the Editor owner")
require("Tts = new EditorTtsProject(" in poll,
        "VOICE-07 completed TTS metadata must persist in the project")
require("await SaveProjectNowAsync();" in poll,
        "VOICE-07 completed TTS state must be saved immediately")
require("QueuePreviewRefresh();" in poll,
        "VOICE-07 completed TTS must refresh processed Preview")

start = application.split("public string StartEditorTts(EditorTtsRequest request)", 1)[1].split(
    "public ", 1
)[0]
require("if (Jobs.HasActiveJobs)" in start,
        "VOICE-07 backend must reject overlapping global jobs")
require('Jobs.Create("editor-tts", cleanupAwareCancel: true)' in start,
        "VOICE-07 TTS job must remain cleanup-aware")
require("await _tts.GenerateAsync(job, request);" in start,
        "VOICE-07 application owner must delegate synthesis to LocalTtsService")
require("job.Finish(" in start and "result.ReviewCount" in start,
        "VOICE-07 backend must finish with the validated TTS result")

generate_core = tts.split("public async Task<EditorTtsResult> GenerateAsync(", 1)[1].split(
    "internal static IReadOnlyList<TtsRhythmGroup> BuildRhythmGroups", 1
)[0]
require("ValidateRequest(request);" in generate_core,
        "VOICE-07 must validate the request before synthesis")
require("EditorSpeechAnalysisDocument.LoadVerifiedAsync" in generate_core,
        "VOICE-07 must load SHA-256 verified Whisper timing")
require("EditorSpeechAnalysisDocument.SourceKey" in generate_core
        and "analysis.SourceKey" in generate_core,
        "VOICE-07 must reject Whisper timing from a different source")
require("EditorSpeechAnalysisDocument.MapToCues" in generate_core,
        "VOICE-07 must map verified Whisper rhythm to current subtitle cues")
require("VietnameseTtsTextNormalizer.Normalize(cue.VietnameseText)" in generate_core,
        "VOICE-07 must normalize Vietnamese text before synthesis")
require("SelectVoice(cue.Id, timing, request.VoiceOverrides)" in generate_core,
        "VOICE-07 must apply automatic/manual male-female routing")
require("BuildRhythmGroups(cue, timing, text, selection.Voice)" in generate_core,
        "VOICE-07 must split Vietnamese text around Whisper speech intervals")
require("await WriteAtomicJsonAsync(inputPath, manifest, job.CancellationToken);" in generate_core,
        "VOICE-07 TTS input manifest must be written atomically")
require("_processes.RunStreamingAsync(" in generate_core
        and '"-I", runtime.Worker' in generate_core
        and '"--manifest", inputPath' in generate_core
        and '"--ffmpeg", ffmpeg' in generate_core,
        "VOICE-07 must execute the packaged local worker with manifest and FFmpeg")
require("if (result.ExitCode != 0 || !ready || !completed)" in generate_core,
        "VOICE-07 must reject incomplete worker execution")
require("SamePath(reportedResult, resultPath)" in generate_core,
        "VOICE-07 worker cannot redirect result ownership outside expected cache")
require("ReadResultAsync(resultPath, outputRoot, request.Duration" in generate_core,
        "VOICE-07 must validate the worker result/master track before promotion")
require("new EditorVoiceTrack(parsedResult.Master.Path, parsedResult.Master.Start, parsedResult.Master.Duration)" in generate_core,
        "VOICE-07 result must expose the validated master track")

rhythm = tts.split("internal static IReadOnlyList<TtsRhythmGroup> BuildRhythmGroups", 1)[1].split(
    "private static (string Voice, bool Review) SelectVoice", 1
)[0]
require("var speechStart = Math.Clamp(timing.SpeechStart, cue.Start, cue.End);" in rhythm,
        "VOICE-07 speech start must clamp to the cue")
require("var speechEnd = Math.Clamp(timing.SpeechEnd, speechStart, cue.End);" in rhythm,
        "VOICE-07 speech end must use a safe clamp even for very short cues")
require("if (speechEnd <= speechStart + .01) { speechStart = cue.Start; speechEnd = cue.End; }" in rhythm,
        "VOICE-07 collapsed speech envelopes must fall back to the cue")
require("Math.Clamp(timing.SpeechEnd, speechStart + .08, cue.End)" not in rhythm,
        "VOICE-07 must not restore the min>max short-cue crash")
require("foreach (var pause in timing.Pauses.OrderBy(x => x.Start))" in rhythm,
        "VOICE-07 rhythm groups must retain Whisper pauses")
require("CacheKey(cue.Id, index, groupText, voice, interval.Start, interval.End)" in rhythm,
        "VOICE-07 clip cache identity must include cue/text/voice/timing")

validate = tts.split("private static void ValidateRequest(EditorTtsRequest request)", 1)[1].split(
    "private async Task WriteAtomicJsonAsync", 1
)[0]
require("request.Subtitle.Cues.Count == 0" in validate
        and "string.IsNullOrWhiteSpace(x.VietnameseText)" in validate,
        "VOICE-07 backend must reject missing Vietsub")
require("request.SpeechAnalysisSha256.Length != 64" in validate,
        "VOICE-07 backend must validate the Whisper analysis hash shape")

read_result = tts.split("private async Task<TtsWorkerResult> ReadResultAsync(", 1)[1].split(
    "private static void ValidateRequest", 1
)[0]
require("masterPath.StartsWith(safeRoot" in read_result,
        "VOICE-07 master track must remain inside the project TTS cache")
require("!File.Exists(masterPath)" in read_result and "new FileInfo(masterPath).Length <= 64" in read_result,
        "VOICE-07 master track must exist and be non-empty")
require("result.Master.Duration > duration + 5" in read_result,
        "VOICE-07 master duration must remain bounded against the source")

for marker in (
    'internal const string PiperVersion = "1.4.2";',
    'internal const string VoiceRepository = "rhasspy/piper-voices";',
    'internal const string BaseVoice = "vi_VN-vais1000-medium";',
    'internal const string MaleVoice = "vais1000-male-profile-v1";',
    'internal const string FemaleVoice = "vais1000-female-profile-v1";',
):
    require(marker in installer, f"VOICE-07 pinned local voice contract lost {marker}")
require('Path.Combine(AppContext.BaseDirectory, "Assets", "TTS", "worker.py")' in installer,
        "VOICE-07 installer must copy the packaged TTS worker")
require('["HF_HUB_OFFLINE"] = "1"' in installer and '["TRANSFORMERS_OFFLINE"] = "1"' in installer,
        "VOICE-07 inference runtime must remain offline")

require('internal\\tts\\worker.py" Link="Assets\\TTS\\worker.py"' in app_project,
        "VOICE-07 app package must include the local TTS worker")

for marker in (
    "from piper import PiperVoice",
    "MALE_PITCH_FACTOR = 0.84",
    "def fit_group(",
    "desired = max(0.86, min(1.16, target / raw))",
    "bounded = max(0.92, min(1.08, ratio))",
    'master_flac = output_root / "voice-master.flac"',
    '"engine": "piper-vais1000-profiles"',
    '"review_count": sum(1 for cue in cue_results if cue["status"] != "fit")',
    "temp_path.replace(result_path)",
):
    require(marker in worker, f"VOICE-07 local worker contract lost {marker}")
require("zero_chunk = bytes(sample_rate * 2)" in worker,
        "VOICE-07 master timeline must zero-fill silent regions between generated clips")

normalize_tts = project.split("private static EditorTtsProject? NormalizeTts", 1)[1].split(
    "private static IReadOnlyDictionary<string, string> NormalizeVoiceOverrides", 1
)[0]
require("CurrentTtsEngine" in normalize_tts
        and "LocalTtsInstaller.PiperVersion" in normalize_tts
        and "LocalTtsInstaller.MaleVoice" in normalize_tts
        and "LocalTtsInstaller.FemaleVoice" in normalize_tts,
        "VOICE-07 project reopen must reject stale engine/profile identities")
require("FileShaMatches(manifest, tts.ManifestSha256)" in normalize_tts
        and "!File.Exists(trackPath)" in normalize_tts,
        "VOICE-07 complete project TTS requires verified manifest and track file")

require("EditorVoiceTrack? VoiceTrack = null" in video_editor,
        "VOICE-07 VideoEditRequest must carry the generated voice track")
require("BuildVoiceAudioFilter" in video_editor,
        "VOICE-07 Preview/Export render core must consume EditorVoiceTrack")

require("class EditorLicensedVoiceProfileContract" in voice_contract,
        "VOICE-07 existing licensed voice profile C# contract must remain present")
require("VerifyVoiceProfileAsync" in voice_contract
        and "VerifyEditorProjectAsync" in voice_contract,
        "VOICE-07 existing C# contract must retain profile/rhythm and reopen validation")

# Numerical fixtures for the production short-cue envelope policy.
def safe_envelope(cue_start: float, cue_end: float, speech_start: float, speech_end: float):
    start = max(cue_start, min(speech_start, cue_end))
    end = max(start, min(speech_end, cue_end))
    if end <= start + .01:
        start, end = cue_start, cue_end
    return start, end


require(safe_envelope(1.0, 1.05, 1.0, 1.05) == (1.0, 1.05),
        "VOICE-07 50 ms cue must remain valid instead of crashing")
require(safe_envelope(1.0, 2.0, 2.0, 2.03) == (1.0, 2.0),
        "VOICE-07 speech collapsed at cue end must fall back to the cue envelope")
require(safe_envelope(1.0, 4.0, 1.2, 3.8) == (1.2, 3.8),
        "VOICE-07 normal speech envelope must remain unchanged")

# Guard against accidentally adding cloud TTS providers to generation.
lower_worker = worker.lower()
for forbidden in ("openai.audio", "elevenlabs", "azure.cognitiveservices", "google.cloud.texttospeech"):
    require(forbidden not in lower_worker,
            f"VOICE-07 must remain local; forbidden provider marker found: {forbidden}")

print("PASS: VOICE-07 local Vietnamese voice generation contract")
