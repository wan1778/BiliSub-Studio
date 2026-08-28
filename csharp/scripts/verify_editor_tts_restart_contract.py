#!/usr/bin/env python3
from pathlib import Path
ROOT = Path(__file__).resolve().parents[2]
def read(path): return (ROOT / path).read_text(encoding="utf-8")
def require(condition, message):
    if not condition: raise SystemExit("FAIL: " + message)
service = read("csharp/src/BiliSubStudio.Core/Editor/LocalTtsService.cs")
worker = read("internal/tts/worker.py")
installer = read("csharp/src/BiliSubStudio.Core/Editor/LocalTtsInstaller.cs")
editor = read("csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml.cs")
require("GenerateTtsButton.IsEnabled = voiceBlockReason is null;" in editor
        and "EditorVietnameseSubtitleWorkflow.VoiceBlockReason" in editor,
        "restart must re-enable after the active owner releases")
require("finally { _gate.Release(); }" in installer and "finally { _generationGate.Release(); }" in service,
        "cancellation must release installation and generation gates")
require('var partial = destination + ".partial";' in installer and "resume=True" in installer,
        "Drive download must retain resumable partial bytes")
require("MatchesAsync(partial, file" in installer, "resumed downloads must verify reviewed content hash")
identity = worker.split("def cache_identity(", 1)[1].split("def load_clip(", 1)[0]
for marker in ("VOICE_REVISION", "PACKAGES", "worker_sha", 'cue["id"]', 'cue["cue_start"]', 'cue["cue_end"]', "text"):
    require(marker in identity, "cache identity missing " + marker)
cache = worker.split("def load_clip(", 1)[1].split("def synthesize_cue(", 1)[0]
for marker in ('record["sha256"] != sha256(path)', "read_wav(path)", 'record["raw_duration"]', 'record["fitted_duration"]'):
    require(marker in cache, "cache validation missing " + marker)
require("final_path.replace(cached_path)" in worker and 'atomic_json(cached_path.with_suffix(".json"), record)' in worker,
        "only fully written clips and matching metadata may be reused")
require('cache_hit = record is not None' in worker, "worker must report real cache hits")
require("if record is None:" in worker and "synthesize_cue(voice, text, temporary)" in worker,
        "missing/corrupt clips must run real inference again")
require("File.Delete(masterPath)" in service and "if (!accepted)" in service,
        "failed restart may remove only its new unaccepted output")
print("PASS: TTS restart validates cached content and retains completed cues")
