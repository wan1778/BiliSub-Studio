#!/usr/bin/env python3
from pathlib import Path
import re, sys

ROOT = Path(__file__).resolve().parents[1]
main = (ROOT / "cmd/bilisub/main.go").read_text(encoding="utf-8")
native_files = sorted((ROOT / "internal/nativeui").glob("*.go"))
native = "\n".join(p.read_text(encoding="utf-8") for p in native_files if not p.name.endswith("_test.go"))
player = (ROOT / "internal/nativeplayer/player.go").read_text(encoding="utf-8")
errors=[]

for forbidden in ["internal/api", "net/http", "net.Listen", "127.0.0.1", "Launch("]:
    if forbidden in main:
        errors.append(f"cmd/bilisub crosses native production boundary via {forbidden!r}")
for required in ["application.New", "nativeui.Run", "proc.EnableContainment"]:
    if required not in main:
        errors.append(f"cmd/bilisub missing production boundary marker {required}")

for forbidden in ["os/exec", "exec.Command", "ocr.Scanner{", "video.Service{", "videoedit.Service{", "tools.New(", "jobs.New("]:
    if forbidden in native:
        errors.append(f"nativeui owns forbidden service/process behavior {forbidden!r}")
for required in ["w.app.StartOCRScan", "w.app.StartVideo", "w.app.StartSubtitle", "w.app.StartEditor", "w.app.PrepareShutdown"]:
    if required not in native:
        errors.append(f"nativeui missing application call {required}")

if "exec.CommandContext" not in player or "proc.Hide" not in player:
    errors.append("nativeplayer does not visibly own/hide app FFmpeg preview processes")

if errors:
    print("APPLICATION BOUNDARY AUDIT: FAIL")
    for e in errors:
        print(" -", e)
    sys.exit(1)
print("APPLICATION BOUNDARY AUDIT: PASS")
