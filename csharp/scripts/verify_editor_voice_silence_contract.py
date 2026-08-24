#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
WORKER = ROOT / "internal/asr/worker.py"
SPEECH = ROOT / "csharp/src/BiliSubStudio.Core/Editor/EditorSpeechAnalysis.cs"
VIDEO_EDITOR = ROOT / "csharp/src/BiliSubStudio.Core/Editor/VideoEditorService.cs"
CONTRACTS = ROOT / "csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs"


def fail(message: str) -> None:
    print(f"FAIL: {message}", file=sys.stderr)
    raise SystemExit(1)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


worker = read(WORKER)
speech = read(SPEECH)
video_editor = read(VIDEO_EDITOR)
contracts = read(CONTRACTS)

# VOICE-05 — Silence / pauses.
# Silence is derived from reviewed Whisper word timing: cue-edge silence comes from
# the first/last mapped words, while internal pauses are chronological word gaps.
# Preview slicing must recompute silence metadata after clipping to a local window.

require("vad_filter=True" in worker,
        "VOICE-05 Whisper worker must keep VAD enabled for stable speech boundaries")
require('vad_parameters={"min_silence_duration_ms": 250, "speech_pad_ms": 120}' in worker,
        "VOICE-05 Whisper VAD parameters must remain explicit and reviewed")

require("public sealed record EditorPauseTiming(double Start, double End)" in speech,
        "VOICE-05 pause timing must keep an explicit start/end owner")
require("public double Duration => Math.Max(0, End - Start);" in speech,
        "VOICE-05 pause duration must never become negative")
require("double LeadingSilence," in speech and "double TrailingSilence," in speech,
        "VOICE-05 cue timing must retain edge-silence metadata")
require("public const double PauseThresholdSeconds = .18;" in speech,
        "VOICE-05 reviewed internal-pause threshold must remain 180 ms")

mapping = speech.split(
    "public static IReadOnlyList<EditorCueSpeechTiming> MapToCues", 1
)[1].split("public static string SourceKey", 1)[0]
require(".SelectMany(segment => segment.Words)" in mapping,
        "VOICE-05 silence must derive from actual mapped Whisper words")
require(".OrderBy(word => word.Start)" in mapping and ".ThenBy(word => word.End)" in mapping,
        "VOICE-05 word gaps must be evaluated in deterministic chronological order")
require("var speechStart = words.Length > 0 ? Math.Clamp(words[0].Start, cue.Start, cue.End) : cue.Start;" in mapping,
        "VOICE-05 leading silence must anchor to the first mapped word")
require("var speechEnd = words.Length > 0 ? Math.Clamp(words[^1].End, speechStart, cue.End) : cue.End;" in mapping,
        "VOICE-05 trailing envelope must clamp safely even for an edge word inside the ±80 ms cue tolerance")
require("if (speechEnd <= speechStart + .01) { speechStart = cue.Start; speechEnd = cue.End; }" in mapping,
        "VOICE-05 collapsed edge mappings must fall back to the cue envelope instead of calling Math.Clamp with min > max")
require("var start = Math.Max(speechStart, words[index - 1].End);" in mapping,
        "VOICE-05 pause start must follow the previous word end")
require("var end = Math.Min(speechEnd, words[index].Start);" in mapping,
        "VOICE-05 pause end must stop at the next word start")
require("if (end - start >= PauseThresholdSeconds) pauses.Add(new EditorPauseTiming(start, end));" in mapping,
        "VOICE-05 only gaps at or above the reviewed threshold may become pauses")
require("Math.Max(0, speechStart - cue.Start)" in mapping,
        "VOICE-05 leading silence must be derived from the mapped speech envelope")
require("Math.Max(0, cue.End - speechEnd)" in mapping,
        "VOICE-05 trailing silence must be derived from the mapped speech envelope")

preview = video_editor.split(
    "internal static VideoEditRequest BuildPreviewSlice(", 1
)[1].split("internal static IReadOnlyList<string> BuildPreviewArguments", 1)[0]
require("LeadingSilence = Math.Max(0, Math.Max(0, timing.SpeechStart - sourceStart) - Math.Max(0, timing.CueStart - sourceStart))" in preview,
        "VOICE-05 Preview must recompute leading silence after clipping to sourceStart")
require("TrailingSilence = Math.Max(0, Math.Min(segmentDuration, timing.CueEnd - sourceStart) - Math.Min(segmentDuration, timing.SpeechEnd - sourceStart))" in preview,
        "VOICE-05 Preview must recompute trailing silence after clipping to the preview end")
require("Pauses = timing.Pauses" in preview,
        "VOICE-05 Preview must retain explicit internal pauses")
require(".Where(pause => pause.End > sourceStart && pause.Start < sourceEnd)" in preview,
        "VOICE-05 Preview must keep only pauses overlapping its source window")
require("Start = Math.Max(0, pause.Start - sourceStart)" in preview
        and "End = Math.Min(segmentDuration, pause.End - sourceStart)" in preview,
        "VOICE-05 Preview must translate pause timestamps into segment-local time")
require(".Where(pause => pause.End > pause.Start)" in preview,
        "VOICE-05 Preview must drop pauses collapsed by clipping")

require('(\"Whisper word timing maps pauses and karaoke ASS\", EditorSpeechTimingKaraokeContractAsync)' in contracts,
        "VOICE-05 core contract suite must keep the existing pause mapping test registered")
contract = contracts.split("private static async Task EditorSpeechTimingKaraokeContractAsync()", 1)[1].split(
    "private static Task LocalTtsContractAsync()", 1)[0]
require("Equal(1, timing[0].Pauses.Count);" in contract,
        "VOICE-05 core contract must assert an actual detected pause")
require("Equal(.4d, Math.Round(timing[0].Pauses[0].Duration, 1));" in contract,
        "VOICE-05 core contract must preserve the concrete 400 ms pause fixture")

# Numerical fixtures mirror only the reviewed formulas above. They make boundary
# regressions obvious even when the local WinUI/.NET toolchain is unavailable.
def map_silence(cue_start: float, cue_end: float, words: list[tuple[float, float]]):
    ordered = sorted(words)
    speech_start = max(cue_start, min(ordered[0][0], cue_end)) if ordered else cue_start
    speech_end = max(speech_start, min(ordered[-1][1], cue_end)) if ordered else cue_end
    if speech_end <= speech_start + .01:
        speech_start, speech_end = cue_start, cue_end
    pauses: list[tuple[float, float]] = []
    for previous, current in zip(ordered, ordered[1:]):
        start = max(speech_start, previous[1])
        end = min(speech_end, current[0])
        if end - start >= .18:
            pauses.append((start, end))
    return (
        speech_start,
        speech_end,
        max(0.0, speech_start - cue_start),
        max(0.0, cue_end - speech_end),
        pauses,
    )

normal = map_silence(1.0, 4.0, [(1.2, 1.6), (2.0, 2.3), (2.4, 3.8)])
require(abs(normal[2] - .2) < 1e-9 and abs(normal[3] - .2) < 1e-9,
        "VOICE-05 fixture lost cue-edge silence")
require(normal[4] == [(1.6, 2.0)],
        "VOICE-05 fixture must keep 400 ms gap and ignore the 100 ms gap")

edge = map_silence(1.0, 2.0, [(2.01, 2.03)])
require(edge[:4] == (1.0, 2.0, 0.0, 0.0),
        "VOICE-05 edge word beyond cue end must fall back safely instead of crashing")

source_start = 31.0
segment_duration = 2.0
cue_start, cue_end = 30.0, 34.0
speech_start, speech_end = 30.2, 33.8
local_cue_start = max(0.0, cue_start - source_start)
local_cue_end = min(segment_duration, cue_end - source_start)
local_speech_start = max(0.0, speech_start - source_start)
local_speech_end = min(segment_duration, speech_end - source_start)
leading = max(0.0, local_speech_start - local_cue_start)
trailing = max(0.0, local_cue_end - local_speech_end)
require((leading, trailing) == (0.0, 0.0),
        "VOICE-05 Preview clipping must zero edge silence that lies outside the preview window")

print("PASS: VOICE-05 Whisper silence/pause contract")
