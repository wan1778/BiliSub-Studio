#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
EDITOR_MAIN = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml.cs"
APPLICATION = ROOT / "csharp/src/BiliSubStudio.Core/Application/BiliSubApplication.cs"
APP_JOB = ROOT / "csharp/src/BiliSubStudio.Core/Jobs/AppJob.cs"
JOB_MANAGER = ROOT / "csharp/src/BiliSubStudio.Core/Jobs/JobManager.cs"
LOCAL_ASR = ROOT / "csharp/src/BiliSubStudio.Core/Editor/LocalAsrService.cs"
ASR_INSTALLER = ROOT / "csharp/src/BiliSubStudio.Core/Editor/LocalAsrInstaller.cs"


def fail(message: str) -> None:
    print(f"FAIL: {message}", file=sys.stderr)
    raise SystemExit(1)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


editor_main = read(EDITOR_MAIN)
application = read(APPLICATION)
app_job = read(APP_JOB)
job_manager = read(JOB_MANAGER)
local_asr = read(LOCAL_ASR)
installer = read(ASR_INSTALLER)

# VOICE-03 — Restart after cancel.
# A cancelled ASR run must release its live ownership only after cleanup, then a fresh
# editor-asr job may start. Durable ASR/model checkpoints may be reused only when they
# still match the exact source/model identity.

create_handler = editor_main.split("private async Task<bool> EnsureVoiceTimingAsync()", 1)[1].split(
    "private async Task PollAsrJobAsync()", 1)[0]
require("if (_asrJobId is not null || _project is null || _media is null || string.IsNullOrWhiteSpace(_path)) return false;" in create_handler,
        "VOICE-03 restart must be blocked only while a live ASR owner still exists or source state is missing")
require("_asrJobId = _application.StartEditorAsr(new EditorAsrRequest(_project.Id, _path, _media.Duration));" in create_handler,
        "VOICE-03 every restart must create a fresh application ASR job from the current exact project/source")
require("VoiceProgress.Value = 0;" in create_handler,
        "VOICE-03 restart must reset visible progress for the new job")
require("VoiceStatusText.Text = status;" in create_handler,
        "VOICE-03 restart must replace stale cancel text with the automatic timing start state")

poll = editor_main.split("private async Task PollAsrJobAsync()", 1)[1].split(
    "private async void GenerateTts_Click", 1)[0]
require("if (snapshot.Done)" in poll and "_asrJobId = null;" in poll and "RefreshEditorActions();" in poll,
        "VOICE-03 terminal cancel must release ASR ownership and immediately refresh controls")
require(poll.index("if (snapshot.Done)") < poll.index("_asrJobId = null;") < poll.index("RefreshEditorActions();", poll.index("_asrJobId = null;")),
        "VOICE-03 ASR ownership must clear only after terminal cleanup, before restart controls refresh")
require("snapshot.Result is EditorAsrResult result" in poll,
        "VOICE-03 cancelled runs must not promote partial state before restart")

refresh = editor_main.split("private void RefreshEditorActions()", 1)[1].split(
    "private static string FormatClock", 1)[0]
require("var idle = !EditorBusy;" in refresh and "var editable = idle && hasMedia && !_playback.IsPreviewMode;" in refresh,
        "VOICE-03 restart availability must derive from current live ownership")
require("GenerateTtsButton.IsEnabled = editable && !_subtitleManualDirty && subtitleReady;" in refresh,
        "VOICE-03 Create voice must re-enable when cancel cleanup releases EditorBusy")
require("CancelVoiceButton.IsEnabled = _asrJobId is not null || _ttsJobId is not null;" in refresh,
        "VOICE-03 cancel control must disappear after the cancelled ASR owner is released")

busy_decl = "private bool EditorBusy => _jobId is not null || _translationJobId is not null || _asrJobId is not null || _ttsJobId is not null || _playback.IsRendering;"
require(busy_decl in editor_main,
        "VOICE-03 EditorBusy must depend on live job ids, not historical cancelled jobs")

start_asr = application.split("public string StartEditorAsr(EditorAsrRequest request)", 1)[1].split(
    "public string StartEditorTts", 1)[0]
require("if (Jobs.HasActiveJobs) throw new InvalidOperationException" in start_asr,
        "VOICE-03 application restart must reject only jobs that are still active")
require('Jobs.Create("editor-asr", cleanupAwareCancel: true)' in start_asr,
        "VOICE-03 each restart must create a new cleanup-aware editor-asr job")
require("return job.Id;" in start_asr,
        "VOICE-03 fresh restart job id must return to the UI owner")

require("public bool HasActiveJobs => _jobs.Values.Any(x => !x.Snapshot().Done);" in job_manager,
        "VOICE-03 completed cancellation must no longer count as an active job")
create_core = job_manager.split("public AppJob Create(", 1)[1].split(
    "public bool TryGet", 1)[0]
require('Guid.NewGuid():N' in create_core,
        "VOICE-03 restarted ASR must receive a fresh unique job id instead of reusing the cancelled id")

cancel_complete = app_job.split("public void CancelComplete", 1)[1].split(
    "public Task RequestPauseAsync", 1)[0]
require('_status = "cancelled";' in cancel_complete and "_done = true;" in cancel_complete,
        "VOICE-03 restart must become eligible only after cleanup reaches terminal cancelled state")
require("_completion.TrySetResult(true);" in cancel_complete,
        "VOICE-03 cancellation completion must release cleanup waiters before a restart can proceed")

run_job = application.split("private static async Task RunJobAsync", 1)[1]
require("catch (OperationCanceledException)" in run_job
        and "if (!job.Snapshot().Done) job.CancelComplete();" in run_job,
        "VOICE-03 cancelled ASR action must unwind into terminal cancellation")

transcribe = local_asr.split("public async Task<EditorAsrResult> TranscribeAsync(", 1)[1].split(
    "private async Task<AsrSelection> SelectRuntimeAsync", 1)[0]
require('var checkpoint = await LoadCheckpointAsync(checkpointPath, hybrid ? key + ":hybrid-word-seam-v1" : key, job.CancellationToken, job.Warn);' in transcribe,
        "VOICE-03 restart must load durable ASR checkpoint before new transcription")
require("var resumeStart = checkpoint.Cues.Count == 0 ? 0 : Math.Max(0, checkpoint.Cues[^1].End - 1.5);" in transcribe,
        "VOICE-03 checkpoint restart must overlap 1.5 seconds to avoid clipping the resume boundary")
require("var retained = checkpoint.Cues.Where(x => x.End <= resumeStart + .05).ToList();" in transcribe,
        "VOICE-03 restart must discard only the overlap tail that will be retranscribed")
require("Cues = retained" in transcribe and "Complete = false" in transcribe,
        "VOICE-03 resumed checkpoint must return to an in-progress canonical state")
require("await ExtractAudioAsync(ffmpeg, source.FullName, audio, resumeStart, null, processes, job.CancellationToken);" in transcribe,
        "VOICE-03 restarted transcription must extract from the recovered resume frontier")
require("WorkerArguments(runtime, audio, selection, resumeStart, probe: false)" in transcribe,
        "VOICE-03 worker timestamps must keep the original video offset after restart")
require("await SaveCheckpointAsync(checkpointPath, checkpoint, CancellationToken.None, job.Warn);" in transcribe,
        "VOICE-03 fully received segments must remain durable even if cancellation arrives immediately afterwards")

checkpoint_loader = local_asr.split("private async Task<AsrCheckpoint> LoadCheckpointAsync", 1)[1].split(
    "private static bool ValidCue", 1)[0]
require("loaded.Schema != CheckpointSchema" in checkpoint_loader
        and "loaded.Key != key" in checkpoint_loader
        and "loaded.ModelRevision != LocalAsrInstaller.ModelRevision" in checkpoint_loader,
        "VOICE-03 restart must reject stale checkpoint schema/source/model identities")
require("return AsrCheckpoint.New(key);" in checkpoint_loader,
        "VOICE-03 invalid/stale checkpoints must restart clean instead of poisoning a new run")

require("EditorSpeechAnalysisDocument.SourceKey(" in local_asr
        and "source.Length" in local_asr
        and "source.LastWriteTimeUtc.Ticks" in local_asr
        and "LocalAsrInstaller.ModelRevision" in local_asr,
        "VOICE-03 checkpoint identity must stay bound to exact source bytes metadata, duration and model revision")

prepare = installer.split("public async Task<LocalAsrRuntime> PrepareAsync", 1)[1].split(
    "private async Task<string> EnsureWorkerAsync", 1)[0]
require("if (!Status.RuntimeReady)" in prepare
        and "if (Directory.Exists(RuntimeRoot)) Directory.Delete(RuntimeRoot, recursive: true);" in prepare,
        "VOICE-03 cancelled/incomplete ASR runtime must be rebuilt from a clean runtime root")
require("finally { _gate.Release(); }" in prepare,
        "VOICE-03 cancelling runtime preparation must release the installer gate for the next Start")

download = installer.split("private async Task DownloadVerifiedAsync(", 1)[1].split(
    "private bool FileMatchesStamp", 1)[0]
require('var partial = destination + ".partial";' in download,
        "VOICE-03 model download restart must use a durable partial file")
require("var existing = File.Exists(partial) ? new FileInfo(partial).Length : 0;" in download,
        "VOICE-03 model restart must detect already downloaded partial bytes")
require("request.Headers.Range = new RangeHeaderValue(existing, null);" in download,
        "VOICE-03 model restart should resume a valid partial download with HTTP Range")
require("if (existing > 0 && response.StatusCode != HttpStatusCode.PartialContent)" in download
        and "TryDelete(partial);" in download,
        "VOICE-03 servers that cannot resume must force a safe clean model redownload")
require("if (!string.Equals(actual, expectedSha, StringComparison.Ordinal))" in download,
        "VOICE-03 resumed model bytes must still pass pinned SHA-256 before becoming usable")

print("PASS: VOICE-03 restart after Whisper cancel contract")
