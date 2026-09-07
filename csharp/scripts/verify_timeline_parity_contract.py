#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
PARITY = ROOT / "csharp/src/BiliSubStudio.Core/Timeline/TimelineOneClipParity.cs"
SERVICE = ROOT / "csharp/src/BiliSubStudio.Core/Timeline/TimelinePreviewExportService.cs"
EDITOR = ROOT / "csharp/src/BiliSubStudio.Core/Editor/VideoEditorService.cs"


def require(condition: bool, message: str) -> None:
    if not condition:
        print("FAIL:", message, file=sys.stderr)
        raise SystemExit(1)


parity = PARITY.read_text(encoding="utf-8")
service = SERVICE.read_text(encoding="utf-8")
editor = EDITOR.read_text(encoding="utf-8")

# Parity mapping produces the classic request shape.
for token in (
    "IsOneClip(",
    "ToVideoEditRequest(",
    "new VideoEditRequest(",
    "new EditorTrimRange(",
    "TimelineTime.ToSeconds(clip.SourceInUs)",
    "TimelineTime.ToSeconds(clip.SourceOutUs)",
    "EditorAudioSettings.Default",
):
    require(token in parity, f"G5 parity lost token: {token}")
require("Regions: []" in parity and "Subtitle: null" in parity and "VoiceTrack: null" in parity,
        "G5 parity must not invent classic edits")

# G5 compatibility service delegates to the classic pipeline; it must not
# build its own FFmpeg graph or claim ownership of the G9 V2 proxy root.
require("VideoEditorService _editor" in service
        and "_editor.CreatePreviewSegmentAsync" in service
        and "_editor.RunAsync" in service,
        "G5 service must delegate to VideoEditorService")
for token in ("ffmpeg", "ProcessRunner", "File.Move", "File.WriteAll", "new FileStream"):
    require(token not in service, f"G5 service must not duplicate render IO: {token}")
require("G9 owns real V2 proxy lifecycle separately" in service
        and "TimelineProjectStoreV2.TestRootFolderName" not in service,
        "G5 adapter must not overclaim ownership of the G9 V2 proxy root")

# The classic pipeline remains the single FFmpeg graph owner.
require("BuildConfiguredRenderArguments(" in editor
        and "public async Task<EditorPreviewSegment> CreatePreviewSegmentAsync(" in editor,
        "classic render owners drifted")

print("PASS: G5 one-clip parity maps V2 onto the single classic render pipeline")
