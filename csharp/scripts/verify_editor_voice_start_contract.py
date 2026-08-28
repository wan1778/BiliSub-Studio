#!/usr/bin/env python3
from __future__ import annotations

import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages"
EDITOR_XAML = PAGES / "EditorPage.xaml"
EDITOR_MAIN = PAGES / "EditorPage.xaml.cs"
APPLICATION = ROOT / "csharp/src/BiliSubStudio.Core/Application/BiliSubApplication.cs"
LOCAL_ASR = ROOT / "csharp/src/BiliSubStudio.Core/Editor/LocalAsrService.cs"
ASR_INSTALLER = ROOT / "csharp/src/BiliSubStudio.Core/Editor/LocalAsrInstaller.cs"
ASR_WORKER = ROOT / "internal/asr/worker.py"
APP_PROJECT = ROOT / "csharp/src/BiliSubStudio.App/BiliSubStudio.App.csproj"
EVENT_MAP = ROOT / "docs/engineering/EDITOR_EVENT_MAP.md"
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
        if element.attrib.get(f"{{{XAML_NS}}}Name") == name:
            return {key.rsplit("}", 1)[-1]: value for key, value in element.attrib.items()}
    fail(f"missing XAML control {name}")
    raise AssertionError("unreachable")


editor_xaml = read(EDITOR_XAML)
editor_main = read(EDITOR_MAIN)
application = read(APPLICATION)
local_asr = read(LOCAL_ASR)
installer = read(ASR_INSTALLER)
worker = read(ASR_WORKER)
app_project = read(APP_PROJECT)
event_map = read(EVENT_MAP)

# VOICE-01 — One user action creates voice. Whisper timing is an internal,
# cleanup-aware prerequisite only when the valid timing cache is absent.
generate_button = named_xaml_control("GenerateTtsButton")
require(generate_button.get("Click") == "GenerateTts_Click",
        "VOICE-01 voice creation must have exactly one XAML Click owner")
require(generate_button.get("IsEnabled") in ("False", "false"),
        "VOICE-01 voice creation must default disabled before subtitle/video state is ready")
voice_model = named_xaml_control("VoiceModelBox")
require(voice_model.get("SelectedIndex") == "0",
        "VOICE-01 must show the default supported local reading model")
require("ngoc_huyen" in editor_xaml and "Ngọc Huyền" in editor_xaml,
        "VOICE-01 must identify Ngọc Huyền as the supported local reading model")
require("CreateAsrButton" not in editor_xaml and "CreateAsr_Click" not in editor_main,
        "VOICE-01 must not expose a separate manual analysis action")
require("| `CreateAsrButton` |" not in event_map
        and "GenerateTts_Click\n  -> EnsureVoiceTimingAsync" in event_map,
        "VOICE-01 event map must describe the single voice entry point")

ensure_timing = editor_main.split("private async Task<bool> EnsureVoiceTimingAsync()", 1)[1].split(
    "private async Task PollAsrJobAsync()", 1)[0]
require('if (_project?.Speech is { Status: "complete" }) return true;' in ensure_timing,
        "VOICE-01 valid cached timing must be reused without a second analysis")
require("if (_asrJobId is not null || _project is null || _media is null || string.IsNullOrWhiteSpace(_path)) return false;" in ensure_timing,
        "VOICE-01 automatic timing must reject overlapping or incomplete editor state")
require("_asrJobId = _application.StartEditorAsr(new EditorAsrRequest(_project.Id, _path, _media.Duration));" in ensure_timing,
        "VOICE-01 automatic timing must pass project id, exact source path and media duration to ASR")
require("VoiceProgress.Value = 0;" in ensure_timing and "await PollAsrJobAsync();" in ensure_timing,
        "VOICE-01 automatic timing must reset progress and wait for its owned job")
require(ensure_timing.index("_asrJobId = _application.StartEditorAsr")
        < ensure_timing.index("RefreshEditorActions();")
        < ensure_timing.index("await PollAsrJobAsync();"),
        "VOICE-01 timing job id must lock controls before polling")

generate_handler = editor_main.split("private async void GenerateTts_Click", 1)[1].split(
    "private async Task PollTtsJobAsync()", 1)[0]
require('var selectedVoice = SelectedVoiceModel();' in generate_handler
        and 'if (string.IsNullOrWhiteSpace(selectedVoice))' in generate_handler,
        "VOICE-01 must reject a missing reading model; Core validates the pinned registry")
require("if (!await EnsureVoiceTimingAsync())" in generate_handler,
        "VOICE-01 the create-voice entry point must automatically acquire timing")
require("_ttsJobId = _application.StartEditorTts(new EditorTtsRequest(" in generate_handler,
        "VOICE-01 the same entry point must start TTS after timing is ready")
require(generate_handler.index("await EnsureVoiceTimingAsync()")
        < generate_handler.index("_ttsJobId = _application.StartEditorTts"),
        "VOICE-01 must never start TTS before automatic timing completes")

refresh_actions = editor_main.split("private void RefreshEditorActions()", 1)[1].split(
    "private static string FormatClock", 1)[0]
require("var editable = idle && hasMedia && !_playback.IsPreviewMode;" in refresh_actions,
        "VOICE-01 create-voice availability must require an idle editable media source")
require("VoiceModelBox.IsEnabled = idle && !_playback.IsPreviewMode;" in refresh_actions,
        "VOICE-01 voice selection and sample must not require video")
require("GenerateTtsButton.IsEnabled = voiceBlockReason is null;" in refresh_actions
        and "EditorVietnameseSubtitleWorkflow.VoiceBlockReason" in refresh_actions,
        "VOICE-01 create-voice must use the tested Vietnamese SRT readiness policy")
require("CancelVoiceButton.IsEnabled = _asrJobId is not null || _ttsJobId is not null;" in refresh_actions,
        "VOICE-01 automatic timing must expose the Voice cancel path")

busy_decl = "private bool EditorBusy => _jobId is not null || _asrJobId is not null || _ttsJobId is not null || _playback.IsRendering;"
require(busy_decl in editor_main,
        "VOICE-01 active ASR job must participate in the single Editor busy owner")

start_asr = application.split("public string StartEditorAsr(EditorAsrRequest request)", 1)[1].split(
    "public string StartEditorTts", 1)[0]
require("if (Jobs.HasActiveJobs) throw new InvalidOperationException" in start_asr,
        "VOICE-01 application boundary must reject overlapping jobs before creating ASR")
require('var job = Jobs.Create("editor-asr", cleanupAwareCancel: true);' in start_asr,
        "VOICE-01 Whisper must run as one cleanup-aware editor-asr job")
require("_ = RunJobAsync(job, async () =>" in start_asr,
        "VOICE-01 application start must dispatch ASR asynchronously instead of blocking the UI")
require("var result = await _asr.TranscribeAsync(job, request);" in start_asr,
        "VOICE-01 editor-asr job must delegate to the local ASR service")
require('job.Finish(null, $"Whisper timing hoàn tất: {result.SegmentCount} đoạn / {result.WordCount} từ.", result);' in start_asr,
        "VOICE-01 successful ASR job must finish with its result")
require("return job.Id;" in start_asr,
        "VOICE-01 application start must return the job id to the UI owner")

transcribe = local_asr.split("public async Task<EditorAsrResult> TranscribeAsync(", 1)[1].split(
    "private async Task<AsrSelection> SelectRuntimeAsync", 1)[0]
require("ValidateRequest(request);" in transcribe
        and transcribe.index("ValidateRequest(request);") < transcribe.index("_installer.PrepareAsync"),
        "VOICE-01 ASR request must be validated before runtime/model preparation")
require("var runtime = await _installer.PrepareAsync(job, 18);" in transcribe,
        "VOICE-01 ASR start must prepare the pinned local runtime/model")
require("var ffmpeg = await _tools.EnsureFfmpegAsync(job.CancellationToken);" in transcribe,
        "VOICE-01 ASR start must resolve local FFmpeg for audio extraction")
require("await using var processes = new OwnedProcessGroup();" in transcribe,
        "VOICE-01 ASR processes must be owned for cleanup-aware cancellation")
require('job.Set("asr-probe-audio", 19,' in transcribe
        and "var selection = await SelectRuntimeAsync(runtime, probeAudio, probeDuration, processes, job);" in transcribe,
        "VOICE-01 real audio benchmark must run before full transcription")

validation = local_asr.split("private static void ValidateRequest(EditorAsrRequest request)", 1)[1].split(
    "private static string SrtTime", 1)[0]
require("Project ID Whisper không hợp lệ." in validation,
        "VOICE-01 backend must validate project identity")
require("Video nguồn Whisper không tồn tại." in validation,
        "VOICE-01 backend must reject missing/empty source video")
require("Thời lượng video không hợp lệ." in validation,
        "VOICE-01 backend must reject non-positive/invalid duration")

require('internal const string ModelRepository = "Systran/faster-whisper-small";' in installer,
        "VOICE-01 local Whisper model repository must remain explicitly pinned")
require('internal const string ModelRevision = "536b0662742c02347bc0e980a01041f333bce120";' in installer,
        "VOICE-01 local Whisper model revision must remain pinned")
require('["HF_HUB_OFFLINE"] = "1"' in installer and '["TRANSFORMERS_OFFLINE"] = "1"' in installer,
        "VOICE-01 prepared ASR runtime must force offline inference")
require('Link="Assets\\ASR\\worker.py"' in app_project
        and 'CopyToOutputDirectory="PreserveNewest"' in app_project,
        "VOICE-01 reviewed ASR worker must be packaged with the Windows app")

require("local_files_only=True" in worker,
        "VOICE-01 Whisper worker must load only the prepared local model")
require('language="zh"' in worker and 'task="transcribe"' in worker,
        "VOICE-01 Whisper start must target Chinese transcription")
require('choices=("cpu", "cuda")' in worker,
        "VOICE-01 worker execution devices must remain local CPU/CUDA only")
require("emit({\"event\": \"ready\"" in worker,
        "VOICE-01 worker must emit ready after the local model is initialized")
require("ensure_ascii=False" in worker,
        "ASR-UTF8-01 worker must preserve Chinese Unicode in its JSON output")

worker_arguments = local_asr.split("private static string[] WorkerArguments", 1)[1].split(
    "private async Task ExtractAudioAsync", 1)[0]
require('"-I", "-X", "utf8", runtime.Worker,' in worker_arguments,
        "ASR-UTF8-01 must force UTF-8 stdout while retaining isolated Python mode")

print("PASS: VOICE-01 Start Whisper/ASR contract")
