#!/usr/bin/env python3
from __future__ import annotations

import re
import sys
from collections import Counter
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
PAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages"
PARTIALS = sorted(PAGES.glob("EditorPage*.cs"))

EMPTY_CATCH_RE = re.compile(r"catch(?:\s*\([^{}]*\))?\s*\{\s*\}", re.S)
METHOD_RE = re.compile(
    r"(?m)^\s*(?:public|private|protected|internal)\s+"
    r"(?:(?:static|async|sealed|virtual|override|new)\s+)*"
    r"(?:[A-Za-z_][\w?.<>\[\],]*|\([^\n)]+\))\s+"
    r"([A-Za-z_]\w*)\s*\("
)

# CLEAN-11 keeps only empty catches whose suppression is intentional.  The key
# is (file, owning method); the value locks both the count and the reason.
EXPECTED_INTENTIONAL = {
    ("EditorPage.xaml.cs", "EditorPage_Unloaded"): (
        1,
        "tab unload performs an extra best-effort image-sidecar flush; strict app-close persistence has its own failure gate",
    ),
    ("EditorPage.xaml.cs", "OpenVideo_Click"): (
        1,
        "user/source-selection cancellation is a no-op and must not overwrite the current project status",
    ),
    ("EditorPage.xaml.cs", "ImportSubtitle_Click"): (
        1,
        "subtitle picker/import cancellation is a no-op and preserves the current subtitle state",
    ),
    ("EditorPage.xaml.cs", "RefreshPreviewAsync"): (
        1,
        "frame refresh cancellation means a newer preview revision superseded this request",
    ),
    ("EditorPage.xaml.cs", "RefreshEditorActions"): (
        1,
        "AI readiness probing fails closed: aiReady remains false and Translate stays disabled",
    ),
    ("EditorPage.Images.cs", "LoadBitmapAndRefreshAsync"): (
        1,
        "lazy bitmap refresh is best-effort; authoritative image/project state is not mutated by this background paint path",
    ),
    ("EditorPage.Images.cs", "SaveImageSidecarAsync"): (
        1,
        "temporary-file cleanup must not mask the primary sidecar save result or exception",
    ),
    ("EditorPage.Images.cs", "RenderProjectAsync"): (
        1,
        "intermediate ImageBase deletion is cleanup and must not replace the render result/error",
    ),
    ("EditorPage.Playback.cs", "SeekAsync"): (
        1,
        "latest-wins preview seeking cancels superseded seek work normally",
    ),
    ("EditorPage.Playback.cs", "PrefetchNextSegmentAsync"): (
        3,
        "prefetch is speculative: cancellation/render failure/cleanup failure must not break foreground playback",
    ),
    ("EditorPage.Playback.cs", "CancelPreviewWorkAsync"): (
        1,
        "teardown must continue after an already-cancelled speculative prefetch task",
    ),
    ("EditorPage.Playback.cs", "ContinueAfterSegmentAsync"): (
        1,
        "segment continuation may be superseded by a newer playback revision/cancellation",
    ),
    ("EditorPage.SubtitleCueEditing.cs", "SyncSubtitleCueEditorAsync"): (
        1,
        "re-reading original SRT only enriches the manual-override baseline; in-memory/current cue state remains usable without it",
    ),
    ("EditorPage.SubtitleCueEditing.cs", "RetranslateSelectedCueAsync"): (
        1,
        "single-cue translation output is temporary cleanup after the translated cue has already been merged",
    ),
    ("EditorPage.SubtitleCueEditing.cs", "RewriteVietnameseSrtAsync"): (
        1,
        "temporary SRT cleanup must not mask the primary write/move result or exception",
    ),
    ("EditorPage.ParityFixes.cs", "RebuildEditorCompositePreviewAsync"): (
        1,
        "debounced auto-composite cancellation means a newer refresh owns the preview",
    ),
    ("EditorPage.VoiceArtifacts.cs", "ExitPreviewAfterVoiceArtifactLossAsync"): (
        1,
        "playback shutdown can be superseded by a newer transition while voice-artifact reconciliation continues",
    ),
}


def fail(message: str) -> None:
    print("FAIL:", message, file=sys.stderr)
    raise SystemExit(1)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


def owner_method(source: str, offset: int) -> str | None:
    owner = None
    for match in METHOD_RE.finditer(source, 0, offset):
        owner = match.group(1)
    return owner


require(PARTIALS, "CLEAN-11 EditorPage partials not found")

actual: Counter[tuple[str, str]] = Counter()
for path in PARTIALS:
    source = path.read_text(encoding="utf-8")
    for match in EMPTY_CATCH_RE.finditer(source):
        method = owner_method(source, match.start())
        require(method is not None, f"CLEAN-11 cannot assign empty catch owner in {path.name}")
        actual[(path.name, method)] += 1

expected_counts = Counter({key: count for key, (count, _) in EXPECTED_INTENTIONAL.items()})
unexpected = actual - expected_counts
missing = expected_counts - actual
require(
    not unexpected,
    "CLEAN-11 unreviewed empty catch found: "
    + ", ".join(f"{file}:{method} x{count}" for (file, method), count in sorted(unexpected.items())),
)
require(
    not missing,
    "CLEAN-11 intentional empty-catch inventory drifted: "
    + ", ".join(f"{file}:{method} x{count}" for (file, method), count in sorted(missing.items())),
)
require(sum(actual.values()) == 19, f"CLEAN-11 expected 19 reviewed empty catches, found {sum(actual.values())}")

images = (PAGES / "EditorPage.Images.cs").read_text(encoding="utf-8")
require(
    "try { File.Delete(path); } catch { }" not in images,
    "CLEAN-11 image sidecar delete failure is being swallowed again",
)
require(
    "if (_imageOverlays.Count == 0)\n        {\n            File.Delete(path);\n            return;\n        }" in images,
    "CLEAN-11 zero-image sidecar deletion must propagate persistence failure",
)

# Negative fixture: a new helper with a naked empty catch must be outside the
# reviewed ownership map and therefore rejected by this gate.
fixture = "private void Helper() { try { Work(); } catch { } }"
fixture_match = EMPTY_CATCH_RE.search(fixture)
require(fixture_match is not None, "CLEAN-11 negative fixture no longer contains an empty catch")
require(("Fixture.cs", "Helper") not in EXPECTED_INTENTIONAL, "CLEAN-11 negative fixture was accidentally whitelisted")

print("PASS: CLEAN-11 empty catches reviewed · 19 intentional suppressions locked · image sidecar delete failure propagates")
