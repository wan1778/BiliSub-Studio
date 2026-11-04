#!/usr/bin/env python3
from pathlib import Path
ROOT = Path(__file__).resolve().parents[2]
def read(path): return (ROOT / path).read_text(encoding="utf-8")
def require(condition, message):
    if not condition: raise SystemExit("FAIL: " + message)

service = read("csharp/src/BiliSubStudio.Core/Editor/LocalTtsService.cs")
worker = read("internal/vieneu/worker.py")
editor = read("csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml.cs")
application = read("csharp/src/BiliSubStudio.Core/Application/BiliSubApplication.cs")
job = read("csharp/src/BiliSubStudio.Core/Jobs/AppJob.cs")
runner = read("csharp/src/BiliSubStudio.Core/Processes/ProcessRunner.cs")
cancel = editor.split("private void CancelVoice_Click(", 1)[1].split("private static readonly TimeSpan StuckCancellingLimit", 1)[0]
require("_application.CancelJob(job);" in cancel and "_ttsJobId = null" not in cancel,
        "cancel must retain ownership until terminal cleanup")
poll = editor.split("private async Task PollTtsJobAsync()", 1)[1].split("private void CancelVoice_Click", 1)[0]
# Ownership release must live inside the terminal Done finally-block; the missing-job
# catch and the watchdog detach path are outside that terminal ownership rule.
done_block = poll.split("if (snapshot.Done)", 1)[1]
finally_block = done_block.split("finally", 1)[1]
require("_ttsJobId = null;" in finally_block,
        "UI must not clear live job before cleanup finishes")
# EDIT-CANCEL-OWNERSHIP: watchdog detach moves the id into an observed cleanup set.
# Source switching stays available, while new render/ASR/TTS jobs remain blocked.
watchdog = poll.split("CheckStuckCancelling(", 1)[1]
require("DetachEditorJob(jobId);" in watchdog,
        "watchdog detach must move the job id into the cleanup-detached ownership set")
require("var sourceRevision = _editorSourceRevision;" in poll and
        "string.Equals(_ttsJobId, jobId, StringComparison.Ordinal)" in poll and
        "sourceRevision == _editorSourceRevision" in poll,
        "TTS poll must not publish an old job into a newly selected source")
observer = editor.split("private async Task ObserveDetachedEditorJobAsync", 1)[1].split(
    "private void PruneDetachedEditorJobs", 1)[0]
require("GetSnapshot(jobId).Done" in observer and
        "_cleanupDetachedJobIds.Remove(jobId);" in observer and
        "RefreshEditorActions();" in observer,
        "detached ownership must observe terminal cleanup and refresh the UI")
actions = editor.split("private void RefreshEditorActions()", 1)[1].split(
    "private static string FormatClock", 1)[0]
require("var cleanupPending = _cleanupDetachedJobIds.Count > 0;" in actions and
        "editable && !cleanupPending" in actions and
        "!idle || cleanupPending" in actions,
        "detached cleanup must block new jobs without blocking source switching")
require("PruneDetachedEditorJobs();" in editor.split("private void RefreshEditorActions()", 1)[1].split(
    "private static string FormatClock", 1)[0],
        "detached ownership must be pruned against live job state on every refresh")
require("snapshot.Result is EditorTtsResult result" in poll, "cancel may not promote partial master")
for marker in ('Jobs.Create("editor-tts", cleanupAwareCancel: true)', "catch (OperationCanceledException)",
               "if (!job.Snapshot().Done) job.CancelComplete();"):
    require(marker in application, "cleanup-aware job contract missing " + marker)
require('_status = waitsForCleanup ? "cancelling" : "cancelled";' in job, "cancellation must remain nonterminal")
for marker in ("cancellationToken.Register(() => Kill(process))", "process.Kill(entireProcessTree: true)",
               "await ReapAsync(process, stderrTask);"):
    require(marker in runner, "child tree cleanup missing " + marker)
for marker in ("await using var processes = new OwnedProcessGroup();", "await processes.StopAsync();",
               "Directory.Delete(runRoot, recursive: true)", "if (!accepted) { File.Delete(resultPath); File.Delete(masterPath); }"):
    require(marker in service, "run-owned cleanup missing " + marker)
require(service.index("await processes.StopAsync();") < service.index("Directory.Delete(runRoot"),
        "delete temp audio only after processes stop")
require('var runId = Guid.NewGuid().ToString("N");' in service and
        '$"result-{runId}.json"' in service and '$"voice-master-{runId}.flac"' in service,
        "master and result must be unique per run")
require('temporary = run_root / "voice-master.flac"' in worker and "temporary.replace(destination)" in worker,
        "master must promote atomically from the owned temp directory")
require("atomic_json(result_path, result)" in worker, "result must commit atomically")
require("shutil.rmtree" not in worker, "worker may not delete previous completed caches/masters")
print("PASS: TTS cancel keeps previous master and cleans only the stopped run")
