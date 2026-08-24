#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
WORKER = ROOT / "internal/asr/worker.py"
LOCAL_ASR = ROOT / "csharp/src/BiliSubStudio.Core/Editor/LocalAsrService.cs"
SPEECH = ROOT / "csharp/src/BiliSubStudio.Core/Editor/EditorSpeechAnalysis.cs"
APPLICATION = ROOT / "csharp/src/BiliSubStudio.Core/Application/BiliSubApplication.cs"
EDITOR_MAIN = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml.cs"
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
local_asr = read(LOCAL_ASR)
speech = read(SPEECH)
application = read(APPLICATION)
editor_main = read(EDITOR_MAIN)
video_editor = read(VIDEO_EDITOR)
contracts = read(CONTRACTS)

# VOICE-04 — Word timing.
# Lock the complete local Whisper word-timestamp path from worker emission through
# validated persisted speech analysis, cue mapping and Preview/Export karaoke timing.

require("word_timestamps=True" in worker,
        "VOICE-04 Whisper worker must request word-level timestamps")
require("for word in segment.words or []:" in worker,
        "VOICE-04 worker must tolerate segments without word timestamps")
require('if not value or word.start is None or word.end is None:' in worker,
        "VOICE-04 worker must reject incomplete word timing entries")
require("word_start = args.offset + max(0.0, float(word.start))" in worker,
        "VOICE-04 worker word start must retain the original video offset")
require("word_end = args.offset + max(float(word.start) + 0.01, float(word.end))" in worker,
        "VOICE-04 worker word end must be positive and retain the original video offset")
require('"probability": float(word.probability or 0.0)' in worker,
        "VOICE-04 worker must preserve Whisper word confidence")
require('"words": words' in worker,
        "VOICE-04 segment events must carry word timing payloads")
require('word_count += len(words)' in worker,
        "VOICE-04 worker completion statistics must count emitted words")

parse_cue = local_asr.split("private static AsrCue ParseCue(JsonElement root)", 1)[1].split(
    "private static EditorSpeechSegment ToSpeechSegment", 1)[0]
require('root.TryGetProperty("words", out var list)' in parse_cue,
        "VOICE-04 ASR parser must read the worker words array")
require('var start = GetDouble(word, "start");' in parse_cue
        and 'var end = GetDouble(word, "end");' in parse_cue,
        "VOICE-04 ASR parser must read each word start/end")
require("end <= start" in parse_cue,
        "VOICE-04 ASR parser must reject non-positive word ranges")
require("Math.Clamp(wordProbability, 0, 1)" in parse_cue,
        "VOICE-04 ASR parser must clamp word probability")
require("words.Add(new AsrWord(start, end, text" in parse_cue,
        "VOICE-04 parsed word timing must enter the ASR cue owner")

normalize = local_asr.split(
    "private static AsrCue? NormalizeCue(AsrCue cue, IReadOnlyList<AsrCue> existing)", 1
)[1].split("private static AsrCue ParseCue", 1)[0]
require(".Where(x => x.End > start && x.Start < end)" in normalize,
        "VOICE-04 normalization must keep only words overlapping the normalized segment")
require("Start = Math.Max(start, x.Start)" in normalize
        and "End = Math.Min(end, x.End)" in normalize,
        "VOICE-04 normalization must clamp word timing to segment boundaries")
require(".Where(x => x.End > x.Start + .005)" in normalize,
        "VOICE-04 normalization must reject collapsed word ranges")
require(".OrderBy(x => x.Start)" in normalize,
        "VOICE-04 normalized word timing must remain chronological")

transcribe = local_asr.split("public async Task<EditorAsrResult> TranscribeAsync(", 1)[1].split(
    "private async Task<AsrSelection> SelectRuntimeAsync", 1)[0]
require("await SaveCheckpointAsync(checkpointPath, checkpoint, CancellationToken.None);" in transcribe,
        "VOICE-04 completed segment word timing must be checkpointed durably")
require("checkpoint.Cues.Select(ToSpeechSegment).ToArray()" in transcribe,
        "VOICE-04 final speech analysis must be built from checkpointed ASR cues")
require("var wordCount = analysis.Segments.Sum(x => x.Words.Count);" in transcribe,
        "VOICE-04 final word count must come from persisted speech segments")

to_speech = local_asr.split("private static EditorSpeechSegment ToSpeechSegment(AsrCue cue)", 1)[1].split(
    "private static EditorSubtitleCue ToSubtitleCue", 1)[0]
require("cue.Words.Select(x => new EditorWordTiming(x.Text, x.Start, x.End, x.Probability)).ToArray()" in to_speech,
        "VOICE-04 ASR words must map losslessly into EditorWordTiming")

require("public sealed record EditorWordTiming(string Text, double Start, double End, double Probability);" in speech,
        "VOICE-04 persisted speech model must own text/start/end/probability per word")
validate = speech.split("private static void Validate(EditorSpeechAnalysis analysis)", 1)[1].split(
    "private static double Midpoint", 1)[0]
require("segment.Words is null || segment.Words.Count > 1_000" in validate,
        "VOICE-04 persisted analysis must bound words per segment")
require("word.Start < 0 || word.End <= word.Start" in validate,
        "VOICE-04 persisted analysis must reject invalid word time ranges")
require("word.Probability is < 0 or > 1.0001" in validate,
        "VOICE-04 persisted analysis must reject invalid word probability")

map_to_cues = speech.split(
    "public static IReadOnlyList<EditorCueSpeechTiming> MapToCues", 1
)[1].split("public static string SourceKey", 1)[0]
require(".SelectMany(segment => segment.Words)" in map_to_cues,
        "VOICE-04 cue timing must derive from persisted word timing")
require("Midpoint(word.Start, word.End) >= cue.Start - .08" in map_to_cues
        and "Midpoint(word.Start, word.End) <= cue.End + .08" in map_to_cues,
        "VOICE-04 cue mapping must use the reviewed ±80 ms midpoint tolerance")
require(".OrderBy(word => word.Start)" in map_to_cues,
        "VOICE-04 cue word timing must stay chronological")
require("words[0].Start" in map_to_cues and "words[^1].End" in map_to_cues,
        "VOICE-04 cue speech envelope must derive from first/last mapped words")
require("end - start >= PauseThresholdSeconds" in map_to_cues,
        "VOICE-04 pauses must derive from inter-word gaps")
require("public const double PauseThresholdSeconds = .18;" in speech,
        "VOICE-04 pause threshold must remain 180 ms")

load_timing = application.split(
    "public async Task<IReadOnlyList<EditorCueSpeechTiming>> LoadEditorCueSpeechTimingAsync(", 1
)[1].split("public Task<byte[]> GetEditorPreviewFrameJpegAsync", 1)[0]
require("EditorSpeechAnalysisDocument.LoadVerifiedAsync" in load_timing,
        "VOICE-04 cue timing must load only SHA-256 verified speech analysis")
require("EditorSpeechAnalysisDocument.MapToCues(analysis, cues)" in load_timing,
        "VOICE-04 application boundary must map verified analysis to current subtitle cues")

refresh_timing = editor_main.split(
    "private async Task RefreshSpeechTimingForSubtitleAsync()", 1
)[1].split("private async void ImportSubtitle_Click", 1)[0]
require("_cueSpeechTiming = await _application.LoadEditorCueSpeechTimingAsync" in refresh_timing,
        "VOICE-04 Editor must hydrate cue word timing from saved speech analysis")
require("speech.AnalysisPath, speech.AnalysisSha256, _subtitleSource.Cues" in refresh_timing,
        "VOICE-04 Editor must map exact saved analysis into current subtitle cues")

require("new EditorSubtitleBurn(_subtitleSource.Cues, _subtitlePlacement, _cueSpeechTiming, KaraokeToggle.IsOn)" in editor_main,
        "VOICE-04 Preview/Export subtitle burn must carry the current cue speech timing owner")

preview_slice = video_editor.split(
    "internal static VideoEditRequest BuildPreviewSlice(", 1
)[1].split("internal static IReadOnlyList<string> BuildPreviewArguments", 1)[0]
require("Words = timing.Words" in preview_slice,
        "VOICE-04 processed Preview must slice word timings")
require(".Where(word => word.End > sourceStart && word.Start < sourceEnd)" in preview_slice,
        "VOICE-04 Preview must retain only words overlapping its source window")
require("Start = Math.Max(0, word.Start - sourceStart)" in preview_slice
        and "End = Math.Min(segmentDuration, word.End - sourceStart)" in preview_slice,
        "VOICE-04 Preview must translate absolute word timing into segment-local timing")
require(".Where(word => word.End > word.Start)" in preview_slice,
        "VOICE-04 Preview must drop collapsed word timing after clipping")

build_ass = video_editor.split(
    "public static string BuildAss(EditorSubtitleBurn subtitle, int width, int height)", 1
)[1].split("internal static string BuildKaraokeText", 1)[0]
require("subtitle.SpeechTiming?.ToDictionary" in build_ass,
        "VOICE-04 ASS render must index cue speech timing")
require("timing.TryGetValue(cue.Id, out var rhythm)" in build_ass,
        "VOICE-04 ASS render must bind timing to the exact cue id")
require("BuildKaraokeText(cue.VietnameseText, rhythm)" in build_ass,
        "VOICE-04 karaoke render must consume mapped Whisper rhythm")

karaoke = video_editor.split("internal static string BuildKaraokeText", 1)[1].split(
    "private static int[] ResampleKaraokeDurations", 1)[0]
require("if (timing.Words.Count > 0)" in karaoke,
        "VOICE-04 karaoke timing must prefer real mapped Whisper words")
require("timing.Words[index + 1].Start" in karaoke,
        "VOICE-04 karaoke duration weights must use adjacent Whisper word starts")
require("ResampleKaraokeDurations(sourceUnits, tokens.Length, totalCs)" in karaoke,
        "VOICE-04 translated Vietnamese tokens must resample source word rhythm without assuming 1:1 words")

require('(\"Whisper word timing maps pauses and karaoke ASS\", EditorSpeechTimingKaraokeContractAsync)' in contracts,
        "VOICE-04 C# contract suite must keep the existing word timing/karaoke test registered")
csharp_contract = contracts.split("private static async Task EditorSpeechTimingKaraokeContractAsync()", 1)[1].split(
    "private static Task LocalTtsContractAsync()", 1)[0]
require('new EditorWordTiming(\"你\", 1.1, 1.6, .95)' in csharp_contract
        and 'new EditorWordTiming(\"好\", 2.0, 3.7, .93)' in csharp_contract,
        "VOICE-04 C# contract must retain concrete word timing fixtures")
require("Equal(2, timing[0].Words.Count);" in csharp_contract,
        "VOICE-04 C# contract must assert mapped word count")
require("Equal(1, timing[0].Pauses.Count);" in csharp_contract,
        "VOICE-04 C# contract must assert pause derivation")
require('ass.Contains(@\"{\\kf\"' in csharp_contract,
        "VOICE-04 C# contract must assert karaoke word highlight tags")

print("PASS: VOICE-04 Whisper word timing contract")
