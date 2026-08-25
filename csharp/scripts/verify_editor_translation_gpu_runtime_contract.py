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


require(SERVICE.is_file(), f"SPEED-01 missing {SERVICE.relative_to(ROOT)}")
source = SERVICE.read_text(encoding="utf-8")

# SPEED-01 ownership: the exact bundled Vulkan runtime owns accelerator discovery
# and VRAM fitting. Missing NVIDIA/NVML telemetry must never force the primary
# Qwen invocation to -ngl 0.
for token in (
    'RuntimeVersion = "b10566"',
    'llama-b10566-bin-win-vulkan-x64.zip',
    'ModelName = "Qwen3-8B Q4_K_M"',
    'TranslationSkillBundle.Load(_skillPath, requireBuiltInHash: true)',
    'internal const int RuntimeAutoGpuLayers = -1;',
    'var layers = RuntimeAutoGpuLayers;',
    '"-ngl", gpuLayers < 0 ? "auto" : gpuLayers.ToString(), "--fit", "on"',
):
    require(token in source, f"SPEED-01 runtime-owned GPU offload contract missing: {token}")

require('var layers = RecommendedGpuLayers(_hardware.ResourceSnapshot());' not in source,
        "SPEED-01 translation still lets NVML telemetry force the primary Qwen GPU layer count")
require(source.count("LowerGpuLayers(layers)") == 2,
        "SPEED-01 analysis and translation runtime failures must retain the reviewed CPU fallback")
require('private static int LowerGpuLayers(int current) => current switch { >= 99 => 24, >= 24 => 12, _ => 0 };' in source,
        "SPEED-01 CPU fallback no longer maps the runtime-auto sentinel to -ngl 0")
require(source.count("thử lại bằng CPU an toàn") == 2,
        "SPEED-01 runtime fallback status must tell the user it is falling back to CPU")

# Synthetic command-argument equivalence for the sentinel policy.
def ngl_argument(value: int) -> str:
    return "auto" if value < 0 else str(value)

require(ngl_argument(-1) == "auto",
        "SPEED-01 runtime-auto sentinel did not map to llama.cpp -ngl auto")
require(ngl_argument(0) == "0",
        "SPEED-01 explicit CPU fallback did not map to llama.cpp -ngl 0")
require(ngl_argument(24) == "24",
        "SPEED-01 numeric compatibility mapping drifted")

print("PASS: SPEED-01 llama.cpp Vulkan owns primary GPU offload; CPU fallback remains explicit")
