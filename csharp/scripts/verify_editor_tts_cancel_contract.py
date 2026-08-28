#!/usr/bin/env python3
from pathlib import Path
ROOT = Path(__file__).resolve().parents[2]
def read(path): return (ROOT / path).read_text(encoding="utf-8")
def require(condition, message):
    if not condition: raise SystemExit("FAIL: " + message)

service = read("csharp/src/BiliSubStudio.Core/Editor/LocalTtsService.cs")
worker = read("internal/tts/worker.py")
editor = read("csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml.cs")
application = read("csharp/src/BiliSubStudio.Core/Application/BiliSubApplication.cs")
job = read("csharp/src/BiliSubStudio.Core/Jobs/AppJob.cs")
runner = read("csharp/src/BiliSubStudio.Core/Processes/ProcessRunner.cs")
cancel = editor.split("private void CancelVoice_Click(", 1)[1].split("private void Karaoke_Toggled", 1)[0]
require("_application.CancelJob(job);" in cancel and "_ttsJobId = null" not in cancel,
        "cancel must retain ownership until terminal cleanup")
poll = editor.split("private async Task PollTtsJobAsync()", 1)[1].split("private void CancelVoice_Click", 1)[0]
require(poll.index("if (snapshot.Done)") < poll.index("_ttsJobId = null;"),
        "UI must not clear live job before cleanup finishes")
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
