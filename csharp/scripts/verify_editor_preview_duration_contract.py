#!/usr/bin/env python3
from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
SERVICE = ROOT / "csharp/src/BiliSubStudio.Core/Editor/VideoEditorService.cs"
TESTS = ROOT / "csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs"


def fail(message: str) -> None:
    print("FAIL:", message, file=sys.stderr)
    raise SystemExit(1)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


service = SERVICE.read_text(encoding="utf-8")
tests = TESTS.read_text(encoding="utf-8")

# CLEAN-06: the 12-second processed-preview window is one service policy, not a
# method-local magic value. Keep one named owner and make PreviewWindow consume it.
owner = "internal const double PreviewSegmentDurationSeconds = 12;"
require(service.count(owner) == 1,
        "CLEAN-06 must have exactly one named 12-second preview duration owner")
require("const double targetDuration = 12;" not in service,
        "CLEAN-06 legacy method-local 12-second magic value returned")

match = re.search(
    r"private static \(double Start, double Duration\) PreviewWindow\(double sourceDuration, double requestedStart\)\s*\{(?P<body>.*?)\n    \}",
    service,
    re.S,
)
require(match is not None, "CLEAN-06 PreviewWindow policy method is missing")
body = match.group("body")
require(body.count("PreviewSegmentDurationSeconds") == 2,
        "CLEAN-06 PreviewWindow must use the named duration for near-end shift and segment length")
require(re.search(r"\b12(?:\.0+)?\b", body) is None,
        "CLEAN-06 raw 12-second literal remains inside PreviewWindow")

# Existing boundary fixtures intentionally exercise values immediately below/at/above
# 12 seconds. They are test data, not runtime policy owners, and must remain coverage.
require('var durations = new[] { .04, 1, 11.9, 12, 12.1, 24, 24.5, 25, 299, 300, 3_600 };' in tests,
        "CLEAN-06 preview boundary fixture coverage drifted")
require("if (sourceDuration > 12.05)" in tests,
        "CLEAN-06 long-source boundary assertion drifted")

# Guard against blind replacement: unrelated 12 literals are valid different domains.
for marker in (
    "Math.Clamp(snapshot.LogicalProcessors - 2, 1, 12)",
    "if (resources.AvailableVramBytes >= 3 * gib) return 12;",
    "var percent = 12 + completed / (double)source.Count * 84;",
):
    # These live in other Editor source files; the verifier intentionally documents
    # that CLEAN-06 does not redefine them as seconds.
    combined = "\n".join(
        path.read_text(encoding="utf-8")
        for path in (ROOT / "csharp/src/BiliSubStudio.Core/Editor").glob("*.cs")
    )
    require(marker in combined, f"CLEAN-06 unrelated 12-domain marker unexpectedly changed: {marker}")

fixture = "private static double Window() { const double targetDuration = 12; return targetDuration; }"
require("const double targetDuration = 12;" in fixture,
        "CLEAN-06 negative fixture does not represent the old magic-duration pattern")

print("PASS: CLEAN-06 12-second preview policy has one named owner; unrelated 12 literals remain domain-specific")
