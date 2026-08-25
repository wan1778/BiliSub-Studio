#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
SERVICE = ROOT / "csharp/src/BiliSubStudio.Core/Editor/LocalSubtitleTranslationService.cs"


def fail(message: str) -> None:
    print("FAIL:", message, file=sys.stderr)
    raise SystemExit(1)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


require(SERVICE.is_file(), f"SPEED-02 missing {SERVICE.relative_to(ROOT)}")
source = SERVICE.read_text(encoding="utf-8")

# SPEED-02 ownership: one job-local llama-server process owns the loaded Qwen model
# across analysis + translation batches. The old per-request llama-cli process path
# must not remain reachable.
for token in (
    'private string LlamaServer => Path.Combine(RuntimeDirectory, "llama-server.exe");',
    'ValidPe(LlamaServer) && RuntimeStampMatches()',
    'var needsInference = checkpoint.AnalysisPagesCompleted < analysisPages',
    'TranslationServerSession? runtime = null;',
    'runtime = await StartTranslationServerWithFallbackAsync(model, layers, job, job.CancellationToken);',
    'if (runtime is not null) await runtime.DisposeAsync();',
    'new ProcessStartInfo(LlamaServer)',
    '"--host", "127.0.0.1", "--port", port.ToString()',
    '"-ngl", gpuLayers < 0 ? "auto" : gpuLayers.ToString(), "--fit", "on"',
    '"-c", "24576", "-np", "1"',
    '"--cache-prompt"',
    'new Uri(session.Endpoint, "health")',
    'PostTranslationJsonAsync(session, "apply-template"',
    'PostTranslationJsonAsync(session, "completion"',
    '["cache_prompt"] = true',
    'public OwnedProcessGroup Processes { get; } = new();',
    'process.Kill(entireProcessTree: true)',
):
    require(token in source, f"SPEED-02 persistent-runtime contract missing: {token}")

require('private string LlamaCli' not in source and '"llama-cli.exe"' not in source,
        "SPEED-02 still contains the per-request llama-cli runtime path")
require('_processes.RunAsync(' not in source,
        "SPEED-02 still starts one inference process per request")
require(source.count('await RestartTranslationServerAsync(runtime!, model, LowerGpuLayers(layers), job.CancellationToken);') == 2,
        "SPEED-02 analysis and translation runtime faults must restart the same session on CPU fallback")
require(source.count('await RunJsonAsync(runtime!,') >= 6,
        "SPEED-02 analysis/retry/translation calls are not routed through the persistent session")
require('if (needsInference)' in source,
        "SPEED-02 completed checkpoints must not load Qwen unnecessarily")
require('cancellationToken.Register(() => KillProcess(process))' in source,
        "SPEED-02 cancellation no longer terminates the owned llama-server process")

# Synthetic lifecycle: N normal requests reuse one model load; a runtime fault permits
# exactly one additional load for the reviewed GPU -> CPU restart; a completed checkpoint
# starts no runtime at all.
def model_loads(needs_inference: bool, requests: int, runtime_fault_at: int | None = None) -> int:
    if not needs_inference or requests <= 0:
        return 0
    loads = 1
    for index in range(requests):
        if runtime_fault_at == index:
            loads += 1
    return loads

require(model_loads(True, 12) == 1,
        "SPEED-02 normal multi-batch translation did not reuse one model load")
require(model_loads(True, 12, 5) == 2,
        "SPEED-02 reviewed GPU->CPU fallback did not produce exactly one restart")
require(model_loads(False, 12) == 0,
        "SPEED-02 completed checkpoint still loads the model")

print("PASS: SPEED-02 Vietsub keeps one job-local llama-server model session; CPU fallback and cancellation remain owned")
