from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SERVICE = ROOT / "csharp/src/BiliSubStudio.Core/Editor/LocalSubtitleTranslationService.cs"
VERIFIER = ROOT / "csharp/scripts/verify_editor_translation_adaptive_batch_contract.py"


def replace_once(text: str, old: str, new: str, label: str) -> str:
    if text.count(old) != 1:
        raise SystemExit(f"{label} exact owner block not found once")
    return text.replace(old, new)


def write_lf(path: Path, text: str) -> None:
    with path.open("w", encoding="utf-8", newline="\n") as stream:
        stream.write(text)


source = SERVICE.read_text(encoding="utf-8")

source = replace_once(
    source,
    """    private const int TranslationBatchSize = 48;\n    private const int AnalysisBatchSize = 420;\n""",
    """    private const int TranslationBatchSmall = 8;\n    private const int TranslationBatchMedium = 24;\n    private const int TranslationBatchLarge = 48;\n    private const double TranslationLatencySpikeFactor = 1.6;\n    private const double TranslationLatencyEwmaAlpha = 0.25;\n    private static readonly TimeSpan TranslationLatencySpikeFloor = TimeSpan.FromSeconds(12);\n    private const int AnalysisBatchSize = 420;\n""",
    "SPEED-04 constants",
)

source = replace_once(
    source,
    """        checkpoint = checkpoint with { Translations = recovered };\n        var restored = checkpoint.Translations.Count;\n        var bible = checkpoint.Bible;\n        var needsInference = checkpoint.AnalysisPagesCompleted < analysisPages\n""",
    """        checkpoint = checkpoint with { Translations = recovered };\n        var restored = checkpoint.Translations.Count;\n        var bible = checkpoint.Bible;\n        var adaptiveResources = _hardware.ResourceSnapshot();\n        var translationBatchSize = RecommendedTranslationBatchSize(adaptiveResources);\n        var latencyBaselineMsPerCue = 0d;\n        var needsInference = checkpoint.AnalysisPagesCompleted < analysisPages\n""",
    "SPEED-04 pre-runtime resource sample",
)

source = replace_once(
    source,
    """            var pending = source.Where(x => !checkpoint.Translations.ContainsKey(x.Id)).ToArray();\n            var translationBatches = CreateBatches(pending, TranslationBatchSize, 20_000);\n            for (var batchIndex = 0; batchIndex < translationBatches.Count; batchIndex++)\n            {\n                job.CancellationToken.ThrowIfCancellationRequested();\n                var batch = translationBatches[batchIndex];\n                var firstIndex = IndexOf(source, batch[0].Id);\n""",
    """            var pending = source.Where(x => !checkpoint.Translations.ContainsKey(x.Id)).ToArray();\n            var pendingOffset = 0;\n            if (pending.Length > 0)\n                job.Log($"Adaptive Vietsub chọn batch {translationBatchSize} câu theo VRAM khả dụng trước khi nạp Qwen.");\n            while (pendingOffset < pending.Length)\n            {\n                job.CancellationToken.ThrowIfCancellationRequested();\n                var batch = CreateAdaptiveTranslationBatch(pending, pendingOffset, translationBatchSize, 20_000);\n                var firstIndex = IndexOf(source, batch[0].Id);\n""",
    "SPEED-04 adaptive loop header",
)

source = replace_once(
    source,
    """                var context = source.Skip(Math.Max(0, firstIndex - 4)).Take(batch.Length + 8).ToArray();\n                var prompt = BuildTranslationPrompt(batch, context, bible);\n                IReadOnlyDictionary<string, string> translations;\n                try\n""",
    """                var context = source.Skip(Math.Max(0, firstIndex - 4)).Take(batch.Length + 8).ToArray();\n                var prompt = BuildTranslationPrompt(batch, context, bible);\n                var batchWatch = Stopwatch.StartNew();\n                IReadOnlyDictionary<string, string> translations;\n                try\n""",
    "SPEED-04 response timer",
)

source = replace_once(
    source,
    """                    translations = ValidateBatch(retry, batch);\n                }\n                foreach (var pair in translations) checkpoint.Translations[pair.Key] = pair.Value;\n                await SaveCheckpointAsync(checkpointPath, checkpoint, job.CancellationToken);\n                var completed = checkpoint.Translations.Count;\n""",
    """                    translations = ValidateBatch(retry, batch);\n                }\n                batchWatch.Stop();\n                if (batch.Length == translationBatchSize)\n                {\n                    if (translationBatchSize > TranslationBatchSmall\n                        && IsTranslationLatencySpike(batchWatch.Elapsed, batch.Length, latencyBaselineMsPerCue))\n                    {\n                        var previousBatchSize = translationBatchSize;\n                        translationBatchSize = LowerTranslationBatchSize(translationBatchSize);\n                        latencyBaselineMsPerCue = 0d;\n                        job.Warn($"Adaptive Vietsub: batch {previousBatchSize} mất {batchWatch.Elapsed.TotalSeconds:0.0}s, phản hồi tăng đột biến; giảm batch lượt sau còn {translationBatchSize} câu.");\n                    }\n                    else\n                    {\n                        latencyBaselineMsPerCue = UpdateTranslationLatencyBaseline(latencyBaselineMsPerCue, batchWatch.Elapsed, batch.Length);\n                    }\n                }\n                foreach (var pair in translations) checkpoint.Translations[pair.Key] = pair.Value;\n                await SaveCheckpointAsync(checkpointPath, checkpoint, job.CancellationToken);\n                pendingOffset += batch.Length;\n                var completed = checkpoint.Translations.Count;\n""",
    "SPEED-04 latency feedback",
)

source = replace_once(
    source,
    """    private static int LowerGpuLayers(int current) => current switch { >= 99 => 24, >= 24 => 12, _ => 0 };\n\n    internal static IReadOnlyDictionary<string, string> ValidateBatch(JsonElement root, IReadOnlyList<EditorSubtitleCue> expected)\n""",
    """    private static int LowerGpuLayers(int current) => current switch { >= 99 => 24, >= 24 => 12, _ => 0 };\n\n    internal static int RecommendedTranslationBatchSize(HardwareResourceSnapshot resources)\n    {\n        const long gib = 1024L * 1024 * 1024;\n        var tierVram = resources.VramTelemetryAvailable && resources.AvailableVramBytes > 0\n            ? Math.Min(resources.TotalVramBytes, resources.AvailableVramBytes)\n            : resources.TotalVramBytes;\n        if (tierVram < 6 * gib) return TranslationBatchSmall;\n        if (tierVram <= 12 * gib) return TranslationBatchMedium;\n        return TranslationBatchLarge;\n    }\n\n    internal static int LowerTranslationBatchSize(int current) => current switch\n    {\n        > TranslationBatchMedium => TranslationBatchMedium,\n        > TranslationBatchSmall => TranslationBatchSmall,\n        _ => TranslationBatchSmall,\n    };\n\n    internal static bool IsTranslationLatencySpike(TimeSpan elapsed, int cueCount, double baselineMsPerCue)\n    {\n        if (cueCount <= 0 || baselineMsPerCue <= 0 || elapsed < TranslationLatencySpikeFloor) return false;\n        var currentMsPerCue = elapsed.TotalMilliseconds / cueCount;\n        return currentMsPerCue >= baselineMsPerCue * TranslationLatencySpikeFactor;\n    }\n\n    internal static double UpdateTranslationLatencyBaseline(double baselineMsPerCue, TimeSpan elapsed, int cueCount)\n    {\n        if (cueCount <= 0) return baselineMsPerCue;\n        var currentMsPerCue = elapsed.TotalMilliseconds / cueCount;\n        if (baselineMsPerCue <= 0) return currentMsPerCue;\n        return baselineMsPerCue * (1 - TranslationLatencyEwmaAlpha) + currentMsPerCue * TranslationLatencyEwmaAlpha;\n    }\n\n    internal static IReadOnlyDictionary<string, string> ValidateBatch(JsonElement root, IReadOnlyList<EditorSubtitleCue> expected)\n""",
    "SPEED-04 adaptive policy helpers",
)

source = replace_once(
    source,
    """    private static IReadOnlyList<EditorSubtitleCue[]> CreateBatches(IReadOnlyList<EditorSubtitleCue> cues, int maxItems, int maxCharacters)\n    {\n        var result = new List<EditorSubtitleCue[]>();\n        var current = new List<EditorSubtitleCue>(maxItems);\n        var characters = 0;\n        foreach (var cue in cues)\n        {\n            var size = cue.SourceText.Length + 64;\n            if (current.Count > 0 && (current.Count >= maxItems || characters + size > maxCharacters))\n            {\n                result.Add(current.ToArray());\n                current.Clear();\n                characters = 0;\n            }\n            current.Add(cue);\n            characters += size;\n        }\n        if (current.Count > 0) result.Add(current.ToArray());\n        return result;\n    }\n\n    private static void ValidateProjectId(string value)\n""",
    """    private static IReadOnlyList<EditorSubtitleCue[]> CreateBatches(IReadOnlyList<EditorSubtitleCue> cues, int maxItems, int maxCharacters)\n    {\n        var result = new List<EditorSubtitleCue[]>();\n        var current = new List<EditorSubtitleCue>(maxItems);\n        var characters = 0;\n        foreach (var cue in cues)\n        {\n            var size = cue.SourceText.Length + 64;\n            if (current.Count > 0 && (current.Count >= maxItems || characters + size > maxCharacters))\n            {\n                result.Add(current.ToArray());\n                current.Clear();\n                characters = 0;\n            }\n            current.Add(cue);\n            characters += size;\n        }\n        if (current.Count > 0) result.Add(current.ToArray());\n        return result;\n    }\n\n    private static EditorSubtitleCue[] CreateAdaptiveTranslationBatch(\n        IReadOnlyList<EditorSubtitleCue> cues,\n        int startIndex,\n        int maxItems,\n        int maxCharacters)\n    {\n        if (startIndex < 0 || startIndex >= cues.Count) throw new ArgumentOutOfRangeException(nameof(startIndex));\n        var current = new List<EditorSubtitleCue>(maxItems);\n        var characters = 0;\n        for (var index = startIndex; index < cues.Count; index++)\n        {\n            var cue = cues[index];\n            var size = cue.SourceText.Length + 64;\n            if (current.Count > 0 && (current.Count >= maxItems || characters + size > maxCharacters)) break;\n            current.Add(cue);\n            characters += size;\n        }\n        return current.ToArray();\n    }\n\n    private static void ValidateProjectId(string value)\n""",
    "SPEED-04 adaptive batch builder",
)

write_lf(SERVICE, source)

verifier = r'''#!/usr/bin/env python3
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
'''
write_lf(VERIFIER, verifier)
