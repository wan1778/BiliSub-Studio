#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
CORE = ROOT / "csharp/src/BiliSubStudio.Core/Timeline"


def require(condition: bool, message: str) -> None:
    if not condition:
        print("FAIL:", message, file=sys.stderr)
        raise SystemExit(1)


def read(name: str) -> str:
    return (CORE / name).read_text(encoding="utf-8")


for name in (
    "TimelineTime.cs",
    "TimelineMediaAsset.cs",
    "TimelineClip.cs",
    "TimelineTrack.cs",
    "TimelineSequence.cs",
):
    require((CORE / name).exists(), f"G3 domain file missing: {name}")

time = read("TimelineTime.cs")
for token in (
    "MicrosecondsPerSecond",
    "public static long FromSeconds(double seconds)",
    "public static double ToSeconds(long microseconds)",
    "public static long SnapToFrameGrid(long microseconds, double frameRate)",
    "public static long ClipDurationUs(",
    "public static long SequenceToSourceUs(",
    "public static long SourceToSequenceUs(",
    "MidpointRounding.AwayFromZero",
    "CultureInfo.InvariantCulture",
):
    require(token in time, f"G3 time model lost token: {token}")
require("DateTime.Now" not in time, "G3 time model must not read wall-clock time")
require("float " not in time, "G3 persisted time must not use float")

asset = read("TimelineMediaAsset.cs")
require("long DurationUs" in asset and "double FrameRate" in asset, "G3 asset lost microsecond/FPS fields")
require("static TimelineMediaAsset Create(" in asset, "G3 asset factory missing")

clip = read("TimelineClip.cs")
require("TimelineStartUs" in clip and "SourceInUs" in clip and "SourceOutUs" in clip
        and "double PlaybackRate" in clip, "G3 clip lost time ownership fields")
require("ValidateAgainst(TimelineMediaAsset asset)" in clip, "G3 clip lost asset-range validation")
require("TimelineEndUs" in clip, "G3 clip lost end identity")

track = read("TimelineTrack.cs")
for token in ("TimelineTrackType.Img", "TimelineTrackType.Sub", "TimelineTrackType.Fx",
              "TimelineTrackType.Video", "TimelineTrackType.SourceAudio",
              "TimelineTrackType.Voice", "TimelineTrackType.Music"):
    require(token in track, f"G3 canonical track order lost: {token}")
require("IsCanonicalOrder" in track and "CreateDefaultTracks" in track,
        "G3 track order helpers missing")

sequence = read("TimelineSequence.cs")
for token in (
    "ValidateSequential(",
    "ReflowContiguous(",
    "TotalDurationUs",
    "OutputFrameRate",
    "giao nhau; MVP",
):
    require(token in sequence, f"G3 sequence lost token: {token}")

project = read("TimelineProjectV2.cs")
require("public const int Schema = 2;" in project, "G3 project schema must be 2")
require("IReadOnlyList<TimelineMediaAsset> Assets" in project
        and "TimelineSequence? Sequence" in project, "G3 project lost assets/sequence")

store = read("TimelineProjectStoreV2.cs")
require("payload.Assets" in store and "payload.Sequence" in store,
        "G3 store lost assets/sequence persistence")

compiler = read("TimelineRenderPlan.cs")
require("TimelineResolvedClip" in compiler and "TotalDurationUs" in compiler,
        "G3 compiler lost resolved clip segments")

for name in ("TimelineTime.cs", "TimelineMediaAsset.cs", "TimelineClip.cs",
             "TimelineTrack.cs", "TimelineSequence.cs", "TimelineProjectV2.cs",
             "TimelineProjectStoreV2.cs", "TimelineRenderPlan.cs"):
    body = read(name)
    for token in ("new EditorPage", "EditorPage.", "new EditorProjectStore", "EditorProjectStore.",
                  "new VideoEditorService", "VideoEditorService.", "Paths.Data", "_editorProjects"):
        require(token not in body, f"G3 domain must not touch classic state ({name}): {token}")

print("PASS: G3 Timeline domain keeps microsecond time, tracks, clips and isolated persistence")
