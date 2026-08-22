#!/usr/bin/env python3
from pathlib import Path
import re, sys

ROOT = Path(__file__).resolve().parents[1]
errors=[]

def read(rel):
    return (ROOT / rel).read_text(encoding="utf-8", errors="ignore")

main = read("cmd/bilisub/main.go")
procw = read("internal/proc/proc_windows.go")
tools = read("internal/tools/manager.go")
ocr_install = read("internal/ocr/install.go")
ocr_manager = read("internal/ocr/manager.go")

for forbidden in ["exec.LookPath", "LookPath(", "os.Getenv(\"PATH\")", "nvidia-smi", "powershell.exe", "pwsh.exe"]:
    corpus = "\n".join([main, tools, ocr_install, ocr_manager, read("internal/ocr/gpu_probe_windows.go")])
    if forbidden in corpus:
        errors.append(f"production dependency marker forbidden: {forbidden}")

for marker in ["ownedExecutable(m.Root, \"ffmpeg.exe\")", "ownedExecutable(m.Root, \"ffprobe.exe\")", "ownedExecutable(m.Root, \"yt-dlp.exe\")", "filepath.EvalSymlinks"]:
    if marker not in tools:
        errors.append(f"app-owned tools contract missing {marker}")
for marker in ["filepath.Join(runtimeRoot, \"venv\", \"Scripts\", \"python.exe\")", "inst.Python"]:
    if marker not in (ocr_install + "\n" + ocr_manager):
        errors.append(f"private OCR runtime contract missing {marker}")

for marker in ["jobObjectLimitKillClose", "AssignProcessToJobObject", "proc.EnableContainment"]:
    corpus = procw + "\n" + main
    if marker not in corpus:
        errors.append(f"process containment marker missing {marker}")
for marker in ["proc.Breakaway(exec.Command", "--apply-self-update"]:
    if marker not in main:
        errors.append(f"updater breakaway marker missing {marker}")

# Normal production helper spawns (legacy internal/api excluded by design) must
# be wrapped in proc.Hide; the self-updater is the sole proc.Breakaway spawn.
roots = [ROOT / "cmd", ROOT / "internal"]
for base in roots:
    for path in base.rglob("*.go"):
        rel = path.relative_to(ROOT).as_posix()
        if path.name.endswith("_test.go") or rel.startswith("internal/api/"):
            continue
        text = path.read_text(encoding="utf-8", errors="ignore")
        for m in re.finditer(r"exec\.Command(?:Context)?\(", text):
            start=max(0, m.start()-80); end=min(len(text), m.start()+220)
            window=text[start:end]
            if "proc.Hide(" not in window and "proc.Breakaway(" not in window:
                errors.append(f"{rel}: helper spawn is not visibly wrapped by proc.Hide/proc.Breakaway")

if errors:
    print("DEPENDENCY/PROCESS AUDIT: FAIL")
    for e in errors:
        print(" -", e)
    sys.exit(1)
print("DEPENDENCY/PROCESS AUDIT: PASS")
