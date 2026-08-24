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
LOCAL_TTS = ROOT / "csharp/src/BiliSubStudio.Core/Editor/LocalTtsService.cs"
PROCESS_RUNNER = ROOT / "csharp/src/BiliSubStudio.Core/Processes/ProcessRunner.cs"
OWNED_GROUP = ROOT / "csharp/src/BiliSubStudio.Core/Processes/OwnedProcessGroup.cs"
WORKER = ROOT / "internal/tts/worker.py"
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


editor = read(EDITOR_MAIN)
application = read(APPLICATION)
app_job = read(APP_JOB)
job_manager = read(JOB_MANAGER)
local_tts = read(LOCAL_TTS)
process_runner = read(PROCESS_RUNNER)
owned_group = read(OWNED_GROUP)
worker = read(WORKER)
event_map = read(EVENT_MAP)

# VOICE-08 — Cancel TTS.
# Cancellation must keep a single UI/job owner, remain non-terminal until child
# cleanup finishes, never promote a cancelled run, kill/reap the Python/FFmpeg tree,
# and never corrupt an already-valid master track while a replacement is being built.

cancel_button = named_xaml_control("CancelVoiceButton")
require(cancel_button.get("Click") == "CancelVoice_Click",
        "VOICE-08 CancelVoiceButton must keep one XAML owner")
require(editor.count("private void CancelVoice_Click(") == 1,
        "VOICE-08 cancel handler must have exactly one implementation")
require("| `CancelVoiceButton` | XAML | `CancelVoice_Click` | 1 |" in event_map,
        "VOICE-08 event map must keep one CancelVoiceButton binding")

cancel = editor.split("private void CancelVoice_Click(", 1)[1].split(
    "private void Karaoke_Toggled", 1
)[0]
require("var job = _ttsJobId ?? _asrJobId;" in cancel,
        "VOICE-08 cancel must target TTS first when TTS owns Voice")
require("if (job is null) return;" in cancel,
        "VOICE-08 cancel must be a no-op without an active Voice job")
require("_application.CancelJob(job);" in cancel,
        "VOICE-08 UI cancel must delegate to application job ownership")
require("Đang dừng TTS local và thu hồi process" in cancel,
        "VOICE-08 TTS cancel must expose cleanup state")
for forbidden in ("_ttsJobId = null", "_voiceTrack =", "_project =", "Tts ="):
    require(forbidden not in cancel,
            f"VOICE-08 click must not eagerly mutate terminal TTS state: {forbidden}")

refresh = editor.split("private void RefreshEditorActions()", 1)[1].split(
    "private static string FormatClock", 1
)[0]
require("CancelVoiceButton.IsEnabled = _asrJobId is not null || _ttsJobId is not null;" in refresh,
        "VOICE-08 cancel must remain available while TTS cleanup owns its job id")
require("private bool EditorBusy =>" in editor and "_ttsJobId is not null" in editor.split("private bool EditorBusy =>", 1)[1].split(";", 1)[0],
        "VOICE-08 EditorBusy must stay true while TTS cleanup is still live")

poll = editor.split("private async Task PollTtsJobAsync()", 1)[1].split(
    "private void CancelVoice_Click", 1
)[0]
require("VoiceStatusText.Text = snapshot.Message;" in poll,
        "VOICE-08 polling must surface cancelling/cancelled job status")
require("if (snapshot.Done)" in poll,
        "VOICE-08 UI must wait for terminal job state")
require("snapshot.Result is EditorTtsResult result" in poll,
        "VOICE-08 project promotion must require a successful EditorTtsResult")
require("_ttsJobId = null;" in poll,
        "VOICE-08 TTS ownership must release from terminal polling")
require(poll.index("if (snapshot.Done)") < poll.index("_ttsJobId = null;"),
        "VOICE-08 TTS job id must not clear before cleanup reports Done")

start = application.split("public string StartEditorTts(EditorTtsRequest request)", 1)[1].split(
    "public async Task<IReadOnlyList<EditorCueSpeechTiming>>", 1
)[0]
require('Jobs.Create("editor-tts", cleanupAwareCancel: true)' in start,
        "VOICE-08 editor-tts must remain cleanup-aware")
require("await _tts.GenerateAsync(job, request);" in start,
        "VOICE-08 application owner must await LocalTtsService before terminal state")

require("public void CancelJob(string id) => Jobs.Cancel(id);" in application,
        "VOICE-08 application cancel boundary must delegate to JobManager")
require("job.Cancel();" in job_manager,
        "VOICE-08 JobManager.Cancel must signal the exact AppJob")

cancel_core = app_job.split("public void Cancel()", 1)[1].split(
    "public void CancelComplete", 1
)[0]
require("var waitsForCleanup = PauseSupported || CleanupAwareCancel;" in cancel_core,
        "VOICE-08 cleanup-aware cancel must enter a non-terminal phase")
require('_status = waitsForCleanup ? "cancelling" : "cancelled";' in cancel_core,
        "VOICE-08 TTS cancel must expose cancelling before cancelled")
require("_done = !waitsForCleanup;" in cancel_core,
        "VOICE-08 cleanup-aware cancel must not become Done at click time")

cancel_complete = app_job.split("public void CancelComplete", 1)[1].split(
    "public Task RequestPauseAsync", 1
)[0]
require('_status = "cancelled";' in cancel_complete and "_done = true;" in cancel_complete,
        "VOICE-08 cleanup completion must own terminal cancelled state")
require("_completion.TrySetResult(true);" in cancel_complete,
        "VOICE-08 terminal cancel must release waiters")

run_job = application.split("private static async Task RunJobAsync", 1)[1]
require("catch (OperationCanceledException)" in run_job
        and "if (!job.Snapshot().Done) job.CancelComplete();" in run_job,
        "VOICE-08 CancelComplete must happen only after the TTS action unwinds")

generate = local_tts.split("public async Task<EditorTtsResult> GenerateAsync(", 1)[1].split(
    "internal static IReadOnlyList<TtsRhythmGroup> BuildRhythmGroups", 1
)[0]
require("await using var processes = new OwnedProcessGroup();" in generate,
        "VOICE-08 TTS worker must live in an owned process group")
require("job.CancellationToken" in generate,
        "VOICE-08 TTS must propagate AppJob cancellation into the worker")
require("_processes.RunStreamingAsync(" in generate
        and "job.CancellationToken" in generate,
        "VOICE-08 worker process must be cancellation-aware")
require("finally" in generate and "await processes.StopAsync()" in generate,
        "VOICE-08 TTS finally must stop owned child processes before unwinding")

require("cancellationToken.Register(() => Kill(process))" in process_runner,
        "VOICE-08 cancellation must immediately kill the active child")
require("process.Kill(entireProcessTree: true)" in process_runner,
        "VOICE-08 ProcessRunner must kill Python and nested FFmpeg descendants")
require("finally" in process_runner and "await ReapAsync(process, stderrTask);" in process_runner,
        "VOICE-08 ProcessRunner must reap the child after kill")

stop_group = owned_group.split("public async Task StopAsync()", 1)[1].split(
    "private void RemoveExited", 1
)[0]
require("foreach (var pair in active) Kill(pair.Value);" in stop_group,
        "VOICE-08 owned group must kill every tracked child")
require("if (_processes.IsEmpty) return;" in stop_group,
        "VOICE-08 owned group must wait until tracked children are gone")
require("process.Kill(entireProcessTree: true)" in owned_group,
        "VOICE-08 owned group must kill nested process trees")

# The completed master must be transaction-like. A cancelled replacement run may
# leave only a temp FLAC; the project-visible master is replaced only after FFmpeg
# has returned success and the temp file has passed a size check.
require('master_flac = output_root / "voice-master.flac"' in worker,
        "VOICE-08 worker must keep one canonical master path")
require('master_flac_temp = output_root / ("voice-master.flac.tmp-" + os.urandom(6).hex())' in worker,
        "VOICE-08 replacement master must render to a unique temp file")
require('str(master_flac_temp)' in worker,
        "VOICE-08 FFmpeg must write the replacement to the temp master")
require("not master_flac_temp.is_file()" in worker
        and "master_flac_temp.stat().st_size <= 64" in worker,
        "VOICE-08 temp master must be validated before commit")
require("master_flac_temp.replace(master_flac)" in worker,
        "VOICE-08 validated replacement must atomically replace the canonical master")
require(worker.index("master_flac_temp.replace(master_flac)") > worker.index("compressed.returncode != 0"),
        "VOICE-08 master replacement must occur only after FFmpeg success validation")

direct_command = '"-compression_level", "5", str(master_flac)]'
require(direct_command not in worker,
        "VOICE-08 FFmpeg must never truncate the canonical master directly")

require('temp_path = output_root / ("result.json.tmp-" + os.urandom(6).hex())' in worker
        and "temp_path.replace(result_path)" in worker,
        "VOICE-08 result metadata must remain atomic as well")

print("PASS: VOICE-08 Cancel TTS cleanup and master-safety contract")
