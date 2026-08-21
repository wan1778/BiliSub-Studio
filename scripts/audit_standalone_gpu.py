#!/usr/bin/env python3
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
PRODUCTION = [ROOT / "cmd", ROOT / "internal"]
FORBIDDEN = (
    "nvidia-smi",
    "powershell.exe",
    "pwsh.exe",
)

hits = []
for base in PRODUCTION:
    for path in base.rglob("*"):
        if not path.is_file() or path.suffix.lower() not in {".go", ".py"}:
            continue
        if path.name.endswith("_test.go"):
            continue
        text = path.read_text(encoding="utf-8", errors="ignore").lower()
        for token in FORBIDDEN:
            if token in text:
                hits.append(f"{path.relative_to(ROOT)}: forbidden external GPU/runtime CLI marker {token!r}")

if hits:
    print("STANDALONE GPU AUDIT: FAIL")
    print("\n".join(hits))
    sys.exit(1)

windows_probe = ROOT / "internal" / "ocr" / "gpu_probe_windows.go"
probe = windows_probe.read_text(encoding="utf-8", errors="ignore") if windows_probe.exists() else ""
required = ["nvcuda.dll", "nvml.dll", "cuDriverGetVersion", "nvmlDeviceGetMemoryInfo", "nvmlDeviceGetUtilizationRates"]
missing = [item for item in required if item not in probe]
if missing:
    print("STANDALONE GPU AUDIT: FAIL")
    print("native NVIDIA probe missing:", ", ".join(missing))
    sys.exit(1)

print("STANDALONE GPU AUDIT: PASS")
