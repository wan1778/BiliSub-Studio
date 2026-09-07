#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
CORE = ROOT / "csharp/src/BiliSubStudio.Core/Timeline"
AUDIO = CORE / "TimelineAudio.cs"
ITEMS = CORE / "TimelineItems.cs"
PROJECT = CORE / "TimelineProjectV2.cs"
STORE = CORE / "TimelineProjectStoreV2.cs"
COMPILER = CORE / "TimelineRenderPlan.cs"
PARITY = CORE / "TimelineOneClipParity.cs"
DECISIONS = ROOT / "docs/editor-timeline/DECISIONS.md"
EDITOR_STORE = ROOT / "csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs"


def require(condition: bool, message: str) -> None:
    if not condition:
        print("FAIL:", message, file=sys.stderr)
        raise SystemExit(1)


audio = AUDIO.read_text(encoding="utf-8")
items = ITEMS.read_text(encoding="utf-8")
project = PROJECT.read_text(encoding="utf-8")
store = STORE.read_text(encoding="utf-8")
compiler = COMPILER.read_text(encoding="utf-8")
parity = PARITY.read_text(encoding="utf-8")
decisions = DECISIONS.read_text(encoding="utf-8")
editor_store = EDITOR_STORE.read_text(encoding="utf-8")

for token in (
    "TimelineSubtitleCue",
    "TimelineVoiceCue",
    "SubtitleCueId",
    "CueRevisionSha256",
    "TimelineAudioAsset",
    "SourceStartFrame",
    "FrameCount",
    "TimelineSourceAudioState",
    "TimelineMusicTrack",
    "TimelineAudioEditState",
    "TimelineAudioDependencyOps",
    "TimelineAudioEditHistory",
):
    require(token in audio, f"G8 audio ownership token missing: {token}")

require("public const int OutputSampleRate = 48_000;" in audio,
        "G8 output audio must normalize to 48 kHz")
require("MaximumOutputDriftUs" in audio and "/ frameRate / 2" in audio,
        "G8 lost DEC-019 sample/half-frame drift policy")
require("voice.SubtitleCueId" in audio and "voice.CueRevisionSha256" in audio,
        "G8 voice must validate against subtitle cue identity and revision")
require("G8 MVP chỉ hỗ trợ một music track" in audio,
        "G8 must enforce DEC-018 one-music limit")
for token in ("UpdateSubtitleCue(", "Reorder(", "Trim(", "Split(", "Delete(", "ReconcileVoices("):
    require(token in audio, f"G8 dependency operation missing: {token}")
require("TimelineVoiceBoundaryPolicy.Reject" in audio
        and "RemoveAffectedVoice" in audio,
        "G8 spoken-boundary edits must require an explicit voice policy")
require("SplitItemsWithOwnership(" in items and "RightItemIdsByOriginalId" in items,
        "G8 subtitle split must retain deterministic right-owner mapping")

for token in (
    "public const int Schema = 4;",
    "IReadOnlyList<TimelineSubtitleCue> SubtitleCues",
    "IReadOnlyList<TimelineAudioAsset> AudioAssets",
    "IReadOnlyList<TimelineSourceAudioState> SourceAudio",
    "IReadOnlyList<TimelineVoiceCue> VoiceCues",
    "TimelineMusicTrack? Music",
):
    require(token in project, f"G8 schema-4 project field missing: {token}")
require("payload.Schema is not 2 and not 3" in store
        and "TimelineSourceAudioState.Defaults(clips)" in store,
        "G8 store must explicitly upgrade schema 2/3 source-audio defaults")
require("schema cũ không được chứa audio state G8" in store,
        "G8 store must reject mixed old-schema audio fields")

for token in (
    "TimelineResolvedSourceAudio",
    "TimelineResolvedSubtitleCue",
    "TimelineResolvedVoiceCue",
    "TimelineResolvedMusic",
    "TimelineResolvedAudioPlan",
    "project.SourceAudio",
    "project.VoiceCues",
    "project.Music",
    "MaximumOutputDriftUs(project.Sequence.OutputFrameRate)",
    "IsVerified: false",
):
    require(token in compiler, f"G8 compiler audio token missing: {token}")
require("MicrosecondsToFramesFloor(availableUs" in compiler,
        "G8 compiler must clamp global music frames to sequence duration")
require("PlaybackRate - 1" in parity and "SequenceStartUs != 0" in parity,
        "classic parity must reject unsupported speed and leading gaps")

require("DEC-018" in decisions and "DEC-019" in decisions,
        "G8 product/audio synchronization decisions were not recorded")
for body, name in ((audio, "audio"), (project, "project"), (compiler, "compiler")):
    for token in ("EditorPage", "EditorProjectStore.", "VideoEditorService.",
                  "File.", "Directory.", "ProcessRunner", "ffmpeg"):
        require(token not in body, f"G8 {name} must stay isolated from classic/IO: {token}")
require("Timeline" not in editor_store,
        "classic schema-7 store must not reference Timeline")

print("PASS: G8 cue-linked voice, clip source audio, one global music and DEC-019 sync plan")
