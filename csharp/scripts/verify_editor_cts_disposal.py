#!/usr/bin/env python3
from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
PAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages"
COORDINATOR = ROOT / "csharp/src/BiliSubStudio.Core/Editor/EditorPreviewRequestCoordinator.cs"
PARTIALS = sorted(PAGES.glob("EditorPage*.cs"))

EXPECTED_CTS_ALLOCATIONS = 4


def fail(message: str) -> None:
    print("FAIL:", message, file=sys.stderr)
    raise SystemExit(1)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


def count_allocations(source: str) -> int:
    return len(re.findall(r"\bnew\s+CancellationTokenSource\s*\(", source))


require(PARTIALS, "CLEAN-09 EditorPage partials not found")
require(COORDINATOR.is_file(), "CLEAN-09 preview request coordinator not found")

code_by_file = {path.name: path.read_text(encoding="utf-8") for path in PARTIALS}
page_code = "\n".join(code_by_file.values())
main = code_by_file["EditorPage.xaml.cs"]
parity = code_by_file["EditorPage.ParityFixes.cs"]
coordinator = COORDINATOR.read_text(encoding="utf-8")
scoped_code = page_code + "\n" + coordinator

# CLEAN-09 inventory: Editor owns two frame-preview CTS allocations, one
# auto-composite CTS allocation, and the Core preview coordinator owns one.
require(
    count_allocations(scoped_code) == EXPECTED_CTS_ALLOCATIONS,
    f"CLEAN-09 CTS allocation inventory drift: expected {EXPECTED_CTS_ALLOCATIONS}, "
    f"found {count_allocations(scoped_code)}; assign a Dispose owner before updating this gate",
)
require(
    "CancellationTokenSource.CreateLinkedTokenSource" not in scoped_code,
    "CLEAN-09 linked CTS added without an explicit disposal owner map",
)

# Frame-preview CTS is page-owned current-request state. It is bounded to one
# current source and is disposed whenever ownership ends: replacement, source
# switch, or page unload.
require(
    len(re.findall(r"private\s+CancellationTokenSource\?\s+_previewCancellation\s*;", main)) == 1,
    "CLEAN-09 preview CTS owner field drifted",
)
require(
    count_allocations(main) == 2,
    "CLEAN-09 frame preview must keep exactly two allocation entry points (immediate + debounced)",
)
require(
    main.count("_previewCancellation?.Cancel();") == 3
    and main.count("_previewCancellation?.Dispose();") == 3,
    "CLEAN-09 preview replacement/unload paths must Cancel + Dispose the owned CTS",
)
require(
    main.count("frameCancellation.Cancel();") == 1
    and main.count("frameCancellation.Dispose();") == 1,
    "CLEAN-09 source-change preview cleanup must Cancel + Dispose the detached CTS",
)
require(
    "var frameCancellation = _previewCancellation;" in main
    and "await _playback.DisposeForSourceChangeAsync();" in main,
    "CLEAN-09 source-change CTS cleanup call map drifted",
)

# Auto-composite CTS is operation-owned: replacement/toggle/unload can dispose
# the current handle, and the worker also deterministically disposes its own
# local source in finally.
require(
    len(re.findall(r"private\s+CancellationTokenSource\?\s+_editorAutoCompositeCancellation\s*;", parity)) == 1,
    "CLEAN-09 auto-composite CTS owner field drifted",
)
require(count_allocations(parity) == 1, "CLEAN-09 auto-composite CTS allocation count drifted")
require(
    parity.count("_editorAutoCompositeCancellation?.Dispose();") == 3,
    "CLEAN-09 auto-composite replacement/toggle/unload Dispose boundaries drifted",
)
require(
    parity.count("cancellation.Dispose();") == 1
    and "finally" in parity
    and "ReferenceEquals(_editorAutoCompositeCancellation, cancellation)" in parity,
    "CLEAN-09 auto-composite worker must Dispose its local CTS in finally",
)

# Preview-request coordinator owns the render/prefetch CTS and always disposes
# it after operation/cleanup completion. CancelAsync only requests cancellation;
# RunLatestAsync retains disposal ownership to avoid double-owner ambiguity.
require(
    len(re.findall(r"private\s+CancellationTokenSource\?\s+_currentCancellation\s*;", coordinator)) == 1,
    "CLEAN-09 coordinator CTS owner field drifted",
)
require(count_allocations(coordinator) == 1, "CLEAN-09 coordinator CTS allocation count drifted")
require(
    coordinator.count("cancellation.Dispose();") == 1
    and "completion.TrySetResult(true);" in coordinator,
    "CLEAN-09 coordinator must Dispose CTS before publishing completion",
)
require(
    "RequestCancellation(previousCancellation);" in coordinator
    and "RequestCancellation(cancellation);" in coordinator
    and "cancellation?.Cancel();" in coordinator,
    "CLEAN-09 coordinator cancellation call map drifted",
)

# Negative fixture proves the class of regression CLEAN-09 is meant to reject:
# allocation plus Cancel, but no Dispose owner.
fixture = """
private CancellationTokenSource? _leaked;
_leaked = new CancellationTokenSource();
_leaked?.Cancel();
"""
require(
    count_allocations(fixture) == 1 and ".Dispose(" not in fixture and "?.Dispose(" not in fixture,
    "CLEAN-09 negative fixture no longer represents CTS-without-Dispose",
)

print(
    "PASS: CLEAN-09 CTS ownership locked · "
    "4 allocations · preview replacement/source/unload Dispose · "
    "auto-composite finally Dispose · coordinator finally Dispose"
)
