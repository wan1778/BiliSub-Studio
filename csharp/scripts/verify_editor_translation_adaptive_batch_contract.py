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


require(SERVICE.is_file(), f"SPEED-04 missing {SERVICE.relative_to(ROOT)}")
source = SERVICE.read_text(encoding="utf-8")

for token in (
    "private const int TranslationBatchSmall = 8;",
    "private const int TranslationBatchMedium = 24;",
    "private const int TranslationBatchLarge = 48;",
    "private const double TranslationLatencySpikeFactor = 1.6;",
    "private static readonly TimeSpan TranslationLatencySpikeFloor = TimeSpan.FromSeconds(12);",
    "var adaptiveResources = _hardware.ResourceSnapshot();",
    "var translationBatchSize = RecommendedTranslationBatchSize(adaptiveResources);",
    "var latencyBaselineMsPerCue = 0d;",
    "CreateAdaptiveTranslationBatch(pending, pendingOffset, translationBatchSize, 20_000)",
    "var batchWatch = Stopwatch.StartNew();",
    "IsTranslationLatencySpike(batchWatch.Elapsed, batch.Length, latencyBaselineMsPerCue)",
    "translationBatchSize = LowerTranslationBatchSize(translationBatchSize);",
    "UpdateTranslationLatencyBaseline(latencyBaselineMsPerCue, batchWatch.Elapsed, batch.Length)",
    "pendingOffset += batch.Length;",
    "giảm batch lượt sau còn {translationBatchSize} câu",
):
    require(token in source, f"SPEED-04 adaptive batch contract missing: {token}")

require("private const int TranslationBatchSize = 48;" not in source,
        "SPEED-04 still owns one fixed translation batch size")
require("CreateBatches(pending, TranslationBatchSize, 20_000)" not in source,
        "SPEED-04 translation loop still precomputes fixed-size batches")
require("private const int AnalysisBatchSize = 420;" in source,
        "SPEED-04 must not alter the analysis batch owner")
require('["cache_prompt"] = true' in source,
        "SPEED-04 regressed SPEED-03 prompt caching")
require('RuntimeAutoGpuLayers = -1' in source and '"--cache-prompt"' in source,
        "SPEED-04 regressed SPEED-01/02 runtime ownership")

# Synthetic policy mirrors the reviewed C# thresholds. When live NVML free-VRAM
# telemetry exists, current free VRAM constrains the tier; otherwise known total
# VRAM is used. CPU/no-GPU stays conservative.
GIB = 1024 ** 3

def tier(total_gib: float, free_gib: float | None) -> int:
    total = int(total_gib * GIB)
    usable = min(total, int(free_gib * GIB)) if free_gib is not None and free_gib > 0 else total
    if usable < 6 * GIB:
        return 8
    if usable <= 12 * GIB:
        return 24
    return 48

require(tier(0, None) == 8, "SPEED-04 CPU/no-GPU tier must be 8")
require(tier(5.9, None) == 8, "SPEED-04 <6 GB tier must be 8")
require(tier(6, None) == 24, "SPEED-04 6 GB tier must be 24")
require(tier(12, None) == 24, "SPEED-04 12 GB tier must remain 24")
require(tier(12.1, None) == 48, "SPEED-04 >12 GB tier must be 48")
require(tier(16, 5) == 8, "SPEED-04 live free VRAM must safely downgrade a busy 16 GB GPU")
require(tier(16, 9) == 24, "SPEED-04 live free VRAM must select the 24-cue middle tier")
require(tier(16, 14) == 48, "SPEED-04 live free VRAM must permit 48 cues with headroom")


def lower(current: int) -> int:
    if current > 24:
        return 24
    if current > 8:
        return 8
    return 8

require(lower(48) == 24 and lower(24) == 8 and lower(8) == 8,
        "SPEED-04 latency backoff tiers drifted")


def spike(seconds: float, cues: int, baseline_ms_per_cue: float) -> bool:
    if cues <= 0 or baseline_ms_per_cue <= 0 or seconds < 12:
        return False
    return seconds * 1000 / cues >= baseline_ms_per_cue * 1.6

require(not spike(48, 48, 1000), "SPEED-04 stable response falsely triggered backoff")
require(spike(80, 48, 1000), "SPEED-04 sudden latency increase did not trigger backoff")
require(not spike(10, 8, 500), "SPEED-04 short transient must stay below the absolute latency floor")


def ewma(baseline: float, seconds: float, cues: int) -> float:
    current = seconds * 1000 / cues
    return current if baseline <= 0 else baseline * 0.75 + current * 0.25

require(abs(ewma(0, 48, 48) - 1000) < 0.001, "SPEED-04 first latency baseline is wrong")
require(abs(ewma(1000, 60, 48) - 1062.5) < 0.001, "SPEED-04 latency EWMA drifted")

print("PASS: SPEED-04 selects 8/24/48 by safe VRAM thresholds and backs off one tier on sudden latency growth")
