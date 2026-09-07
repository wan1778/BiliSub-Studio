#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
CORE = ROOT / "csharp/src/BiliSubStudio.Core/Timeline"
ITEMS = CORE / "TimelineItems.cs"
PROJECT = CORE / "TimelineProjectV2.cs"
STORE = CORE / "TimelineProjectStoreV2.cs"
COMPILER = CORE / "TimelineRenderPlan.cs"
PARITY = CORE / "TimelineOneClipParity.cs"
EDITOR_STORE = ROOT / "csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs"


def require(condition: bool, message: str) -> None:
    if not condition:
        print("FAIL:", message, file=sys.stderr)
        raise SystemExit(1)


items = ITEMS.read_text(encoding="utf-8")
project = PROJECT.read_text(encoding="utf-8")
store = STORE.read_text(encoding="utf-8")
compiler = COMPILER.read_text(encoding="utf-8")
parity = PARITY.read_text(encoding="utf-8")
editor_store = EDITOR_STORE.read_text(encoding="utf-8")

for token in (
    "TimelineItemKind",
    "TimelineSubtitleSplitPolicy",
    "IsSequenceGlobal",
    "RideClips(",
    "TrimItems(",
    "SplitItems(",
    "DeleteItemsOfClip(",
    "AttachSubtitleToClip(",
    "AttachSubtitleToSequence(",
    "class TimelineDependencyOps",
    "class TimelineEditHistory",
    "TimelineEditState Create(",
):
    require(token in items, f"G7 ownership token missing: {token}")

require("item.Kind == TimelineItemKind.Subtitle" in items
        and "TimelineSubtitleSplitPolicy.Reject" in items
        and "SplitAndClamp = 1" in items,
        "G7 must reject subtitle crossing without an explicit policy")
require("TimelineItemOps.SplitItems(" in items
        and "TimelineSequenceOps.Split(" in items,
        "G7 split must update clips and dependencies in one state operation")
require("TimelineItemOps.TrimItems(" in items
        and "TimelineSequenceOps.Trim(" in items,
        "G7 trim must update clips and dependencies in one state operation")
require("TimelineItemOps.DeleteItemsOfClip(" in items
        and "TimelineSequenceOps.Delete(" in items,
        "G7 delete must update clips and dependencies in one state operation")
require("List<TimelineEditState> _undo" in items
        and "List<TimelineEditState> _redo" in items,
        "G7 history must snapshot clips and items atomically")

require("public const int Schema =" in project
        and "IReadOnlyList<TimelineItem> Items" in project,
        "G7 project must retain persisted items in the current schema")
require("project.Items" in store and "payload.Items" in store,
        "G7 store lost item persistence")
require("payload.Schema == 2" in store,
        "G7 store must safely upgrade persisted schema-2 projects")
require("TimelineResolvedItem" in compiler and "project.Items" in compiler
        and "clip.TimelineStartUs + item.ClipStartUs" in compiler,
        "G7 compiler must resolve clip-local items into sequence time")
require("plan.Clips.Count != 1" in parity and "plan.Items.Count != 0" in parity,
        "classic parity must reject unsupported timeline items")

for body, name in ((items, "items"), (project, "project"), (compiler, "compiler")):
    for token in ("new EditorPage", "EditorProjectStore.", "VideoEditorService.",
                  "File.", "Directory.", "ProcessRunner", "ffmpeg"):
        require(token not in body, f"G7 {name} must stay isolated from classic/IO: {token}")
require("Timeline" not in editor_store,
        "classic schema-7 store must not reference Timeline")

print("PASS: G7 clip-local/global ownership, atomic dependency history and compiler resolution")
