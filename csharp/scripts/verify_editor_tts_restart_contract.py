#!/usr/bin/env python3
from __future__ import annotations

import hashlib
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
EDITOR = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml.cs"
APPLICATION = ROOT / "csharp/src/BiliSubStudio.Core/Application/BiliSubApplication.cs"
APP_JOB = ROOT / "csharp/src/BiliSubStudio.Core/Jobs/AppJob.cs"
JOB_MANAGER = ROOT / "csharp/src/BiliSubStudio.Core/Jobs/JobManager.cs"
LOCAL_TTS = ROOT / "csharp/src/BiliSubStudio.Core/Editor/LocalTtsService.cs"
INSTALLER = ROOT / "csharp/src/BiliSubStudio.Core/Editor/LocalTtsInstaller.cs"
WORKER = ROOT / "internal/tts/worker.py"


def fail(message: str) -> None:
    print(f"FAIL: {message}", file=sys.stderr)
    raise SystemExit(1)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


editor = read(EDITOR)
application = read(APPLICATION)
app_job = read(APP_JOB)
job_manager = read(JOB_MANAGER)
local_tts = read(LOCAL_TTS)
installer = read(INSTALLER)
worker = read(WORKER)

# VOICE-09 — Restart TTS after cancel.
# Restart is allowed only after cleanup reaches terminal cancelled state. A fresh
# editor-tts job may reuse only fully committed clip caches with the same exact
# text/voice/timing/profile identity. Every run owns immutable master/result files,
# so a cancelled restart cannot mutate the last completed project-owned voice.

generate = editor.split("private async void GenerateTts_Click(", 1)[1].split(
    "private async Task PollTtsJobAsync()", 1
)[0]
require(
    "if (_ttsJobId is not null || _project is null || _media is null || _subtitleSource is null || string.IsNullOrWhiteSpace(_path)) return;" in generate,
    "VOICE-09 restart must remain blocked while a live TTS owner still exists",
)
require(
    "_ttsJobId = _application.StartEditorTts(new EditorTtsRequest(" in generate,
    "VOICE-09 every restart must create a fresh application TTS job",
)
require("VoiceProgress.Value = 0;" in generate,
        "VOICE-09 a fresh TTS restart must reset visible progress")
require("RefreshEditorActions();" in generate and "await PollTtsJobAsync();" in generate,
        "VOICE-09 restart must enter shared busy state and poll its new owner")
for forbidden in ("_voiceTrack = null", "Tts = null"):
    require(forbidden not in generate,
            f"VOICE-09 Start must preserve the last completed project voice until success: {forbidden}")

poll = editor.split("private async Task PollTtsJobAsync()", 1)[1].split(
    "private void CancelVoice_Click", 1
)[0]
require("if (snapshot.Done)" in poll and "_ttsJobId = null;" in poll,
        "VOICE-09 cancelled TTS ownership must release only from terminal polling")
require(poll.index("if (snapshot.Done)") < poll.index("_ttsJobId = null;"),
        "VOICE-09 UI must not permit restart before terminal cleanup")
require("snapshot.Result is EditorTtsResult result" in poll,
        "VOICE-09 cancelled runs must not promote partial TTS state")
require("_voiceTrack = result.VoiceTrack;" in poll,
        "VOICE-09 only a successful restarted run may replace the Editor voice track")
require("Tts = new EditorTtsProject(" in poll and "await SaveProjectNowAsync();" in poll,
        "VOICE-09 successful restart must persist the new TTS result")
require("QueuePreviewRefresh();" in poll,
        "VOICE-09 successful restart must refresh processed Preview")

cancel = editor.split("private void CancelVoice_Click(", 1)[1].split(
    "private void Karaoke_Toggled", 1
)[0]
for forbidden in ("_ttsJobId = null", "_voiceTrack =", "_project =", "Tts ="):
    require(forbidden not in cancel,
            f"VOICE-09 cancel must preserve restart ownership/project state: {forbidden}")

refresh = editor.split("private void RefreshEditorActions()", 1)[1].split(
    "private static string FormatClock", 1
)[0]
require("var idle = !EditorBusy;" in refresh
        and "var editable = idle && hasMedia && !_playback.IsPreviewMode;" in refresh,
        "VOICE-09 restart availability must derive from live Editor ownership")
require(
    'GenerateTtsButton.IsEnabled = editable && !_subtitleManualDirty && subtitleReady && _project?.Speech is { Status: "complete" };'
    in refresh,
    "VOICE-09 Generate TTS must re-enable after cancelled ownership is released",
)
require("CancelVoiceButton.IsEnabled = _asrJobId is not null || _ttsJobId is not null;" in refresh,
        "VOICE-09 cancel control must stop owning UI after terminal cleanup")

start = application.split("public string StartEditorTts(EditorTtsRequest request)", 1)[1].split(
    "public async Task<IReadOnlyList<EditorCueSpeechTiming>>", 1
)[0]
require("if (Jobs.HasActiveJobs)" in start,
        "VOICE-09 backend must reject restart while cleanup is still active")
require('Jobs.Create("editor-tts", cleanupAwareCancel: true)' in start,
        "VOICE-09 every restart must create a fresh cleanup-aware editor-tts job")
require("return job.Id;" in start,
        "VOICE-09 the fresh TTS job id must return to the UI owner")

require("public bool HasActiveJobs => _jobs.Values.Any(x => !x.Snapshot().Done);" in job_manager,
        "VOICE-09 terminal cancelled jobs must no longer count as active")
create = job_manager.split("public AppJob Create(", 1)[1].split("public bool TryGet", 1)[0]
require("Guid.NewGuid():N" in create,
        "VOICE-09 restarted TTS must receive a new unique job id")

cancel_complete = app_job.split("public void CancelComplete", 1)[1].split(
    "public Task RequestPauseAsync", 1
)[0]
require('_status = "cancelled";' in cancel_complete and "_done = true;" in cancel_complete,
        "VOICE-09 restart may become eligible only after terminal cancellation")
require("_completion.TrySetResult(true);" in cancel_complete,
        "VOICE-09 cancellation completion must release cleanup waiters")

prepare = installer.split("public async Task<LocalTtsRuntime> PrepareAsync", 1)[1].split(
    "private static IReadOnlyList<TtsModelFile> ModelFiles()", 1
)[0]
require("await _gate.WaitAsync(job.CancellationToken);" in prepare
        and "finally { _gate.Release(); }" in prepare,
        "VOICE-09 cancelled preparation must release the TTS installer gate")
require("if (!Status.RuntimeReady)" in prepare
        and "if (Directory.Exists(RuntimeRoot)) Directory.Delete(RuntimeRoot, recursive: true);" in prepare,
        "VOICE-09 an incomplete Piper runtime must rebuild cleanly on restart")

download = installer.split("private async Task DownloadVerifiedAsync(", 1)[1].split(
    "private static bool FileMatches", 1
)[0]
require('var partial = destination + ".partial";' in download,
        "VOICE-09 cancelled model download must retain a resumable partial file")
require("request.Headers.Range = new RangeHeaderValue(existing, null);" in download,
        "VOICE-09 restart should resume model bytes with HTTP Range")
require("if (existing > 0 && response.StatusCode != HttpStatusCode.PartialContent)" in download
        and "TryDelete(partial);" in download,
        "VOICE-09 unsafe/non-range resume must fall back to a clean download")
require("if (!string.Equals(actual, expectedSha, StringComparison.Ordinal))" in download,
        "VOICE-09 resumed model bytes must pass pinned SHA-256 before use")

cache_key = local_tts.split("private static string CacheKey(", 1)[1].split(
    "private async Task<TtsWorkerResult> ReadResultAsync", 1
)[0]
for marker in (
    "TimingAlgorithm",
    "LocalTtsInstaller.PiperVersion",
    "LocalTtsInstaller.VoiceRevision",
    "cueId",
    "groupIndex",
    "voice",
    "start:0.000",
    "end:0.000",
    "text",
):
    require(marker in cache_key, f"VOICE-09 cache identity lost {marker}")

generate_core = local_tts.split("public async Task<EditorTtsResult> GenerateAsync(", 1)[1].split(
    "internal static IReadOnlyList<TtsRhythmGroup> BuildRhythmGroups", 1
)[0]
require("var reportedResult = string.Empty;" in generate_core,
        "VOICE-09 worker must report the exact immutable result path for the restarted run")
require("!completed || string.IsNullOrWhiteSpace(reportedResult)" in generate_core,
        "VOICE-09 a restarted run without an explicit complete result must fail")
require("var resultPath = reportedResult;" in generate_core,
        "VOICE-09 service must promote the worker-reported versioned result path")

read_result = local_tts.split("private async Task<TtsWorkerResult> ReadResultAsync(", 1)[1].split(
    "private static void ValidateRequest", 1
)[0]
require("absolute.StartsWith(safeRoot, StringComparison.OrdinalIgnoreCase)" in read_result,
        "VOICE-09 dynamic result path must remain under the current project cache")
require("masterPath.StartsWith(safeRoot, StringComparison.OrdinalIgnoreCase)" in read_result,
        "VOICE-09 dynamic master path must remain under the current project cache")

fit_group = worker.split("def fit_group(", 1)[1].split("def ensure_profile_cache", 1)[0]
require("if cache_path.is_file() and cache_path.stat().st_size > 44:" in fit_group,
        "VOICE-09 restart must reuse only a committed non-empty cached clip")
require("final = wav_duration(cache_path)" in fit_group,
        "VOICE-09 cached clip reuse must still parse the WAV before accepting it")
require("candidate.replace(cache_path)" in fit_group and "baseline.replace(cache_path)" in fit_group,
        "VOICE-09 newly completed clips must commit by rename, not direct cache writes")

profile_cache = worker.split("def ensure_profile_cache(", 1)[1].split("def main()", 1)[0]
require("if current == VOICE_PROFILE_REVISION:" in profile_cache and "return" in profile_cache,
        "VOICE-09 same-profile restart must preserve compatible clip cache")
require('for name in ("clips", "blocks"):' in profile_cache,
        "VOICE-09 profile changes must invalidate old synthesis caches")

main = worker.split("def main()", 1)[1]
require("run_id = os.urandom(8).hex()" in main,
        "VOICE-09 every restarted worker must own a fresh output version id")
require('block_path = block_root / f"block-{block_index:04d}.wav"' in main
        and "write_wav_float(block_path, mix, sample_rate)" in main,
        "VOICE-09 restart must rebuild block mixes from current clip entries")
require('master_flac = output_root / f"voice-master-{run_id}.flac"' in main,
        "VOICE-09 restarted master must use an immutable per-run filename")
require('master_flac_temp = output_root / (master_flac.name + ".tmp-" + os.urandom(6).hex())' in main,
        "VOICE-09 every restarted master build must use a fresh temp output")
require("master_flac_temp.replace(master_flac)" in main,
        "VOICE-09 validated master must atomically commit only to its own run path")
require('result_path = output_root / f"result-{run_id}.json"' in main,
        "VOICE-09 restarted result manifest must use the same per-run version id")
require('temp_path = output_root / (result_path.name + ".tmp-" + os.urandom(6).hex())' in main
        and "temp_path.replace(result_path)" in main,
        "VOICE-09 restarted result metadata must commit atomically")
require('master_flac = output_root / "voice-master.flac"' not in main
        and 'result_path = output_root / "result.json"' not in main,
        "VOICE-09 restarted runs must not overwrite files owned by the previous completed project")

# Small lifecycle/cache fixtures mirroring the production contracts.
class Job:
    def __init__(self, ident: str) -> None:
        self.ident = ident
        self.done = False
        self.status = "running"

    def cancel(self) -> None:
        self.status = "cancelling"

    def complete_cancel(self) -> None:
        self.status = "cancelled"
        self.done = True


old = Job("editor-tts-old")
old.cancel()
require(not old.done and old.status == "cancelling",
        "VOICE-09 fixture: restart must stay blocked while cleanup is cancelling")
old.complete_cancel()
require(old.done and old.status == "cancelled",
        "VOICE-09 fixture: terminal cancel must release restart eligibility")
new = Job("editor-tts-new")
require(new.ident != old.ident,
        "VOICE-09 fixture: restarted job must own a distinct id")


def key(text: str, voice: str, start: float, end: float) -> str:
    payload = f"whisper-rhythm-v1|1.4.2|profile-v1|cue|0|{voice}|{start:.3f}|{end:.3f}|{text}"
    return hashlib.sha256(payload.encode()).hexdigest()


base = key("xin chào", "female", 1.0, 2.0)
require(base == key("xin chào", "female", 1.0, 2.0),
        "VOICE-09 fixture: unchanged restart inputs must reuse the same cache identity")
require(base != key("xin chào", "male", 1.0, 2.0)
        and base != key("xin chào bạn", "female", 1.0, 2.0)
        and base != key("xin chào", "female", 1.1, 2.0),
        "VOICE-09 fixture: voice/text/timing changes must invalidate cached clips")

old_master = "voice-master-old.flac"
restart_master = "voice-master-new.flac"
require(old_master != restart_master,
        "VOICE-09 fixture: a cancelled/restarted run must never share the old project master filename")

print("PASS: VOICE-09 restart TTS after cancel contract")
