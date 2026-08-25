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


require(SERVICE.is_file(), f"LIVE-SRT-01 missing {SERVICE.relative_to(ROOT)}")
source = SERVICE.read_text(encoding="utf-8")
start = source.index("public async Task<EditorTranslationResult> TranslateAsync")
end = source.index("internal static int RecommendedGpuLayers", start)
translate = source[start:end]

for token in (
    'internal const string TranslationPolicyKey = "direct-cue-v1";',
    "private const int DirectTranslationBatchSize = 1;",
    "const int analysisPages = 0;",
    "checkpoint = checkpoint with { Bible = string.Empty, AnalysisPagesCompleted = 0, Translations = recovered };",
    "var translationBatchSize = DirectTranslationBatchSize;",
    "var needsInference = source.Any(x => !checkpoint.Translations.ContainsKey(x.Id));",
    "CreateAdaptiveTranslationBatch(pending, pendingOffset, translationBatchSize, 20_000)",
    "source.Skip(Math.Max(0, firstIndex - 2)).Take(batch.Length + 4).ToArray()",
    "TranslationSchema, 1024, job.CancellationToken, job",
    "foreach (var pair in translations) checkpoint.Translations[pair.Key] = pair.Value;",
    "await SaveCheckpointAsync(checkpointPath, checkpoint, job.CancellationToken);",
    "pendingOffset += batch.Length;",
    'SetLive("translation-cue", overallProgress,',
    "Vietsub trực tiếp dùng 1 cue/lượt; checkpoint được lưu sau từng câu.",
):
    require(token in source, f"LIVE-SRT-01 direct cue contract missing: {token}")

require("var adaptiveResources = _hardware.ResourceSnapshot();" not in translate,
        "LIVE-SRT-01 runtime still selects multi-cue batches from VRAM")
require("RecommendedTranslationBatchSize(adaptiveResources)" not in translate,
        "LIVE-SRT-01 runtime still uses adaptive 8/24/48 batching")
require("checkpoint.AnalysisPagesCompleted < analysisPages\n            || source.Any" not in translate,
        "LIVE-SRT-01 still blocks first translated cue on whole-SRT analysis")
require("if (translationBatchSize > 1 && batch.Length == translationBatchSize)" in translate,
        "LIVE-SRT-01 legacy latency backoff must stay disabled for one-cue runtime")

save_pos = translate.index("await SaveCheckpointAsync(checkpointPath, checkpoint, job.CancellationToken);")
offset_pos = translate.index("pendingOffset += batch.Length;", save_pos)
live_pos = translate.index('SetLive("translation-cue", overallProgress,', offset_pos)
require(save_pos < offset_pos < live_pos,
        "LIVE-SRT-01 must persist each translated cue before publishing the progress change that wakes the Editor checkpoint reader")

# Keep the already-reviewed runtime/cache ownership intact; this task changes translation granularity only.
require('["cache_prompt"] = true' in source, "LIVE-SRT-01 regressed persistent prompt caching")
require('RuntimeAutoGpuLayers = -1' in source and '"--cache-prompt"' in source,
        "LIVE-SRT-01 regressed persistent llama-server ownership")

print("PASS: LIVE-SRT-01 translates exactly one cue per inference, checkpoints it before progress publication, and skips blocking whole-SRT analysis")
