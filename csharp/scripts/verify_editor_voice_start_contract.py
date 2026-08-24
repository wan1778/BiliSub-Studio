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

# VOICE-01 — Start Whisper/ASR. One click creates exactly one cleanup-aware ASR job,
# hands a validated source request to the local Whisper service, and immediately moves
# the Editor into its busy state without running transcription synchronously on the UI thread.
create_button = named_xaml_control("CreateAsrButton")
require(create_button.get("Click") == "CreateAsr_Click",
        "VOICE-01 Whisper start button must keep one reviewed Click handler")
require(create_button.get("IsEnabled") in ("False", "false"),
        "VOICE-01 Whisper start button must default disabled before a video/project is ready")
require(editor_main.count("private async void CreateAsr_Click(") == 1,
        "VOICE-01 Whisper start handler must have exactly one implementation")
require("| `CreateAsrButton` | XAML | `CreateAsr_Click` | 1 |" in event_map,
        "VOICE-01 event map must keep exactly one CreateAsrButton binding")

handler = editor_main.split("private async void CreateAsr_Click(", 1)[1].split(
    "private async Task PollAsrJobAsync()", 1)[0]
require("if (_asrJobId is not null || _project is null || _media is null || string.IsNullOrWhiteSpace(_path)) return;" in handler,
        "VOICE-01 UI start must reject double-start and missing project/media/source")
require("_asrJobId = _application.StartEditorAsr(new EditorAsrRequest(_project.Id, _path, _media.Duration));" in handler,
        "VOICE-01 UI start must pass project id, exact source path and media duration to ASR")
require("VoiceProgress.Value = 0;" in handler,
        "VOICE-01 starting Whisper must reset Voice progress before polling")
require("AsrStatusText.Text = \"Đang benchmark Whisper local rồi phân tích word timing, khoảng lặng và chất giọng Nam/Nữ.\";" in handler,
        "VOICE-01 starting Whisper must expose the reviewed local benchmark/start status")
require("VoiceStatusText.Text = AsrStatusText.Text;" in handler,
        "VOICE-01 Voice details must mirror the initial ASR start status")
require(handler.index("_asrJobId = _application.StartEditorAsr")
        < handler.index("RefreshEditorActions();")
        < handler.index("await PollAsrJobAsync();"),
        "VOICE-01 UI must own the returned job id before locking controls and polling")
require("catch (Exception error)" in handler
        and "_asrJobId = null;" in handler
        and "AsrStatusText.Text = error.Message;" in handler
        and handler.rfind("RefreshEditorActions();") > handler.index("catch (Exception error)"),
        "VOICE-01 synchronous start failure must release the UI job owner and refresh controls")

refresh_actions = editor_main.split("private void RefreshEditorActions()", 1)[1].split(
    "private static string FormatClock", 1)[0]
require("var editable = idle && hasMedia && !_playback.IsPreviewMode;" in refresh_actions,
        "VOICE-01 ASR start availability must require an idle editable media source")
require("CreateAsrButton.IsEnabled = editable;" in refresh_actions,
        "VOICE-01 ASR start button must follow Editor editability")
require("CancelVoiceButton.IsEnabled = _asrJobId is not null || _ttsJobId is not null;" in refresh_actions,
        "VOICE-01 acquiring an ASR job id must expose the Voice cancel path")

busy_decl = "private bool EditorBusy => _jobId is not null || _translationJobId is not null || _asrJobId is not null || _ttsJobId is not null || _playback.IsRendering;"
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

print("PASS: VOICE-01 Start Whisper/ASR contract")
