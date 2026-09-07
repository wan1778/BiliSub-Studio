#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
MIGRATOR = ROOT / "csharp/src/BiliSubStudio.Core/Timeline/TimelineMigrator.cs"
STORE = ROOT / "csharp/src/BiliSubStudio.Core/Timeline/TimelineProjectStoreV2.cs"
EDITOR_STORE = ROOT / "csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs"


def require(condition: bool, message: str) -> None:
    if not condition:
        print("FAIL:", message, file=sys.stderr)
        raise SystemExit(1)


migrator = MIGRATOR.read_text(encoding="utf-8")
store = STORE.read_text(encoding="utf-8")
editor_store = EDITOR_STORE.read_text(encoding="utf-8")

require(migrator, "G4 migrator missing")
for token in (
    "MigrateSchema7(",
    "source.Schema != 7",
    "TimelineProjectV2.NewId()",
    "TimelineSequence.Create([clip], [asset]",
    "PlaybackRate: 1.0",
    "SourceInUs: 0",
    "HasAudio: hasAudio",
    "RegionsFlattened",
    "SubtitleDropped",
    "VoiceDropped",
    "project.Validate()",
):
    require(token in migrator, f"G4 migrator lost token: {token}")

# The migration must not perform IO on schema-7 files or media files.
for token in ("File.", "Directory.", "new FileStream", "ProcessRunner", "ffmpeg"):
    require(token not in migrator, f"G4 migrator must stay pure in-memory: {token}")
require("EditorProjectStore" not in migrator and "VideoEditorService" not in migrator,
        "G4 migrator must not bind classic persistence/render types")

# The classic schema-7 store must stay untouched by this gate.
baseline_markers = (
    "public const int CurrentSchema = 7;",
    "public async Task<EditorProject> LoadOrCreateAsync(",
    "public async Task SaveAsync(",
)
for token in baseline_markers:
    require(token in editor_store, f"classic schema-7 store changed unexpectedly: {token}")
require("Timeline" not in editor_store, "classic Editor store must not reference Timeline")

# The V2 store remains the only writer for migrated copies.
require("WriteStoredFileAsync" in store and "File.Move(temporary, path, overwrite: true);" in store,
        "V2 store lost atomic write path")

print("PASS: G4 migration is a pure schema-7 to one-clip V2 copy with no in-place writes")
