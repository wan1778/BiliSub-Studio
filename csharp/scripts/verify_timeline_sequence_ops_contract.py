#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
OPS = ROOT / "csharp/src/BiliSubStudio.Core/Timeline/TimelineSequenceOps.cs"
SEQ = ROOT / "csharp/src/BiliSubStudio.Core/Timeline/TimelineSequence.cs"
EDITOR_STORE = ROOT / "csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs"


def require(condition: bool, message: str) -> None:
    if not condition:
        print("FAIL:", message, file=sys.stderr)
        raise SystemExit(1)


ops = OPS.read_text(encoding="utf-8")
seq = SEQ.read_text(encoding="utf-8")
editor_store = EDITOR_STORE.read_text(encoding="utf-8")

for name in ("Add(", "Reorder(", "Trim(", "Split(", "Delete("):
    require(f"public static IReadOnlyList<TimelineClip> {name}" in ops,
            f"G6 operation missing: {name}")
for token in (
    "ReflowContiguous(ordered)",
    "TimelineSequence.ValidateSequential(appended)",
    "SequenceToSourceUs(",
    "Id = TimelineClip.NewId()",
    "SourceInUs = sourceCutUs",
    "class TimelineCommandHistory",
):
    require(token in ops, f"G6 lost token: {token}")

# Split must cut source time through the playback-rate mapping, and undo
# must restore whole sets from immutable snapshots.
require("TimelineTime.SequenceToSourceUs(\n            cutSequenceUs, clip.TimelineStartUs, clip.SourceInUs, clip.PlaybackRate);" in ops,
        "G6 split must map the cut through the rate-aware time model")
require("_undo.Add(before);" in ops and "_redo.Add(current);" in ops
        and "_undo.Add(current);" in ops,
        "G6 history must swap whole snapshot sets on undo/redo")
require("if (_undo.Count > MaxEntries) _undo.RemoveAt(0);" in ops,
        "G6 history must stay bounded")
require("_redo.Clear();" in ops, "G6 new commit must clear the redo branch")

# Reflow must preserve the caller's list order (reorder semantics), not
# re-sort by stale start times.
require("public static IReadOnlyList<TimelineClip> ReflowContiguous(IReadOnlyList<TimelineClip> clips)" in seq,
        "G6 reflow signature drifted")
reflow = seq.split("public static IReadOnlyList<TimelineClip> ReflowContiguous", 1)[1].split("public void Validate", 1)[0]
require("OrderBy" not in reflow, "G6 reflow must not re-sort by stale start times")

# G6 is pure domain; no IO, no classic coupling.
for token in ("File.", "Directory.", "ProcessRunner", "ffmpeg", "new EditorPage",
              "EditorProjectStore.", "VideoEditorService."):
    require(token not in ops, f"G6 must stay a pure domain op: {token}")
require("Timeline" not in editor_store, "classic schema-7 store must not reference Timeline")

print("PASS: G6 multi-clip ops keep hard cuts, rate-aware split and atomic bounded undo")
