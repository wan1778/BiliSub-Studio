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
APP_JOB = ROOT / "csharp/src/BiliSubStudio.Core/Jobs/AppJob.cs"
JOB_MANAGER = ROOT / "csharp/src/BiliSubStudio.Core/Jobs/JobManager.cs"
LOCAL_ASR = ROOT / "csharp/src/BiliSubStudio.Core/Editor/LocalAsrService.cs"
PROCESS_RUNNER = ROOT / "csharp/src/BiliSubStudio.Core/Processes/ProcessRunner.cs"
OWNED_GROUP = ROOT / "csharp/src/BiliSubStudio.Core/Processes/OwnedProcessGroup.cs"
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


editor_main = read(EDITOR_MAIN)
application = read(APPLICATION)
app_job = read(APP_JOB)
job_manager = read(JOB_MANAGER)
local_asr = read(LOCAL_ASR)
process_runner = read(PROCESS_RUNNER)
owned_group = read(OWNED_GROUP)
event_map = read(EVENT_MAP)

# VOICE-02 — Cancel Whisper.
# Cancellation has one UI owner, keeps the Editor busy while cleanup is in flight,
# kills/reaps the exact Python/FFmpeg process trees, preserves durable checkpoint
# work, and never promotes a cancelled run into completed Speech/TTS project state.

cancel_button = named_xaml_control("CancelVoiceButton")
require(cancel_button.get("Click") == "CancelVoice_Click",
        "VOICE-02 Voice cancel button must keep one reviewed Click handler")
require(editor_main.count("private void CancelVoice_Click(") == 1,
        "VOICE-02 cancel handler must have exactly one implementation")
require("| `CancelVoiceButton` | XAML | `CancelVoice_Click` | 1 |" in event_map,
        "VOICE-02 event map must keep exactly one CancelVoiceButton binding")

cancel_handler = editor_main.split("private void CancelVoice_Click(", 1)[1].split(
    "private void Karaoke_Toggled", 1)[0]
require("var job = _ttsJobId ?? _asrJobId;" in cancel_handler,
        "VOICE-02 cancel must target the active Voice job owner")
require("if (job is null) return;" in cancel_handler,
        "VOICE-02 cancel must be a no-op when no Voice job is active")
require("_application.CancelJob(job);" in cancel_handler,
        "VOICE-02 UI cancel must delegate to the application job owner")
require("Đang dừng Whisper" in cancel_handler and "checkpoint" in cancel_handler,
        "VOICE-02 ASR cancel status must explain cleanup/checkpoint behavior")
for forbidden in ("_asrJobId = null", "_project =", "_voiceTrack =", "Speech =", "Tts ="):
    require(forbidden not in cancel_handler,
            f"VOICE-02 click handler must not eagerly mutate terminal ASR state: {forbidden}")

refresh = editor_main.split("private void RefreshEditorActions()", 1)[1].split(
    "private static string FormatClock", 1)[0]
require("CancelVoiceButton.IsEnabled = _asrJobId is not null || _ttsJobId is not null;" in refresh,
        "VOICE-02 cancel must remain available while ASR cleanup owns its job id")

poll = editor_main.split("private async Task PollAsrJobAsync()", 1)[1].split(
    "private async void GenerateTts_Click", 1)[0]
require("AsrStatusText.Text = snapshot.Message;" in poll,
        "VOICE-02 polling must surface cancelling/cancelled job messages")
require("if (snapshot.Done)" in poll,
        "VOICE-02 UI must wait for terminal job state before releasing ASR ownership")
done_block = poll.split("if (snapshot.Done)", 1)[1]
require("snapshot.Result is EditorAsrResult result" in done_block,
        "VOICE-02 project promotion must require a real successful ASR result")
require("_asrJobId = null;" in done_block,
        "VOICE-02 ASR job id must be released only from the terminal branch")
require(poll.index("if (snapshot.Done)") < poll.index("_asrJobId = null;"),
        "VOICE-02 ASR ownership must not clear before cleanup reports Done")

start_asr = application.split("public string StartEditorAsr(EditorAsrRequest request)", 1)[1].split(
    "public string StartEditorTts", 1)[0]
require('Jobs.Create("editor-asr", cleanupAwareCancel: true)' in start_asr,
        "VOICE-02 editor-asr jobs must remain cleanup-aware")

require("public void CancelJob(string id) => Jobs.Cancel(id);" in application,
        "VOICE-02 application cancel boundary must delegate to JobManager")
require("job.Cancel();" in job_manager,
        "VOICE-02 JobManager.Cancel must signal the exact AppJob")

cancel_core = app_job.split("public void Cancel()", 1)[1].split(
    "public void CancelComplete", 1)[0]
require("var waitsForCleanup = PauseSupported || CleanupAwareCancel;" in cancel_core,
        "VOICE-02 cleanup-aware cancellation must enter a non-terminal cancelling phase")
require('_status = waitsForCleanup ? "cancelling" : "cancelled";' in cancel_core,
        "VOICE-02 cleanup-aware ASR must expose cancelling before cancelled")
require("_done = !waitsForCleanup;" in cancel_core,
        "VOICE-02 cleanup-aware ASR must not become Done at button click time")

cancel_complete = app_job.split("public void CancelComplete", 1)[1].split(
    "public Task RequestPauseAsync", 1)[0]
require('_status = "cancelled";' in cancel_complete and "_done = true;" in cancel_complete,
        "VOICE-02 cleanup completion must be the terminal cancelled transition")
require("_completion.TrySetResult(true);" in cancel_complete,
        "VOICE-02 terminal cancellation must release completion waiters")

run_job = application.split("private static async Task RunJobAsync", 1)[1]
require("catch (OperationCanceledException)" in run_job
        and "if (!job.Snapshot().Done) job.CancelComplete();" in run_job,
        "VOICE-02 RunJobAsync must mark cancelled only after the ASR action unwinds")

transcribe = local_asr.split("public async Task<EditorAsrResult> TranscribeAsync(", 1)[1].split(
    "private async Task<AsrSelection> SelectRuntimeAsync", 1)[0]
require("await using var processes = new OwnedProcessGroup();" in transcribe,
        "VOICE-02 Whisper/FFmpeg children must share one owned process group")
require("job.CancellationToken" in transcribe,
        "VOICE-02 ASR backend must propagate the AppJob cancellation token")
require("await SaveCheckpointAsync(checkpointPath, checkpoint, CancellationToken.None);" in transcribe,
        "VOICE-02 fully received ASR segments must persist atomically before later cancellation")
require("finally" in transcribe
        and "try { await processes.StopAsync(); } catch { }" in transcribe
        and "TryDeleteDirectory(operationRoot);" in transcribe,
        "VOICE-02 ASR finally must reap children and delete only the temporary operation root")
require('Path.Combine(_paths.Data, "Projects", "ASR", request.ProjectId + ".json")' in transcribe,
        "VOICE-02 durable checkpoint must live outside the temporary operation root")

require("cancellationToken.Register(() => Kill(process))" in process_runner,
        "VOICE-02 process runner must react immediately to cancellation")
require("process.Kill(entireProcessTree: true)" in process_runner,
        "VOICE-02 ProcessRunner cancellation must kill the whole child process tree")
require("finally" in process_runner and "await ReapAsync(process, stderrTask);" in process_runner,
        "VOICE-02 ProcessRunner must reap child output/process state after cancellation")

stop_group = owned_group.split("public async Task StopAsync()", 1)[1].split(
    "private void RemoveExited", 1)[0]
require("foreach (var pair in active) Kill(pair.Value);" in stop_group,
        "VOICE-02 owned process cleanup must kill every tracked child")
require("if (_processes.IsEmpty) return;" in stop_group,
        "VOICE-02 cleanup must wait until the owned process set is empty")
require("process.Kill(entireProcessTree: true)" in owned_group,
        "VOICE-02 OwnedProcessGroup must kill nested Python/FFmpeg descendants")

print("PASS: VOICE-02 Cancel Whisper cleanup contract")
