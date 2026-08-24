#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
WORKER = ROOT / "internal/asr/worker.py"
LOCAL_ASR = ROOT / "csharp/src/BiliSubStudio.Core/Editor/LocalAsrService.cs"
SPEECH = ROOT / "csharp/src/BiliSubStudio.Core/Editor/EditorSpeechAnalysis.cs"
TTS = ROOT / "csharp/src/BiliSubStudio.Core/Editor/LocalTtsService.cs"
PROJECT = ROOT / "csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs"
EDITOR = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml.cs"
XAML = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml"


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
tts = read(TTS)
project = read(PROJECT)
editor = read(EDITOR)
xaml = read(XAML)

# VOICE-06 — Male/Female advisory classification.
# This must stay a local F0-based routing hint, never speaker identity/diarization or
# a biological-gender claim. Uncertain remains an explicit safe state; TTS may still
# pick a binary voice with review=true so the workflow never silently pretends certainty.

classifier = worker.split("def estimate_voice_class(", 1)[1].split("\ndef main()", 1)[0]
require('"""Return male_like/female_like/uncertain using a lightweight language-neutral F0 heuristic.' in classifier,
        "VOICE-06 classifier must remain an advisory F0 heuristic")
require("never a biological-gender claim" in classifier,
        "VOICE-06 must not represent pitch routing as biological gender inference")
require('return "uncertain", 0.0, 0.0' in classifier,
        "VOICE-06 short/unvoiced audio must fail safe to uncertain")
require("frame_size = max(256, int(sample_rate * 0.05))" in classifier,
        "VOICE-06 classifier must keep the reviewed 50 ms F0 frame")
require("hop = max(128, int(sample_rate * 0.025))" in classifier,
        "VOICE-06 classifier must keep the reviewed 25 ms hop")
require("sample_rate / 320.0" in classifier and "sample_rate / 70.0" in classifier,
        "VOICE-06 F0 search band must remain 70-320 Hz")
require("if rms < 0.012:" in classifier,
        "VOICE-06 silence/low-energy frames must not vote on voice class")
require("if strength < 0.28:" in classifier,
        "VOICE-06 weak autocorrelation peaks must not vote on voice class")
require("median_pitch = float(np.median" in classifier,
        "VOICE-06 segment routing must use robust median pitch")
require("voiced_ratio = len(pitches) / max(1, attempted)" in classifier,
        "VOICE-06 confidence must account for the amount of voiced evidence")
require("if median_pitch <= 155.0:" in classifier and 'label = "male_like"' in classifier,
        "VOICE-06 reviewed male-like boundary must remain <=155 Hz")
require("elif median_pitch >= 185.0:" in classifier and 'label = "female_like"' in classifier,
        "VOICE-06 reviewed female-like boundary must remain >=185 Hz")
require('label = "uncertain"' in classifier,
        "VOICE-06 155-185 Hz overlap band must remain uncertain")
require('if label == "uncertain":' in classifier and "confidence = min(confidence, 0.59)" in classifier,
        "VOICE-06 uncertain evidence must stay below the automatic 0.60 routing threshold")

main = worker.split("def main()", 1)[1]
require("samples, sample_rate = load_pcm16_mono(audio)" in main,
        "VOICE-06 voice class must use the exact local mono PCM extracted for Whisper")
require("voice_class, voice_confidence, median_pitch = estimate_voice_class(samples, sample_rate, local_start, local_end)" in main,
        "VOICE-06 classification must run on each Whisper segment's local audio window")
require('"voice_class": voice_class' in main
        and '"voice_confidence": voice_confidence' in main
        and '"median_pitch_hz": median_pitch' in main,
        "VOICE-06 worker segment events must carry class/confidence/pitch")

parse = local_asr.split("private static AsrCue ParseCue(JsonElement root)", 1)[1].split(
    "private static EditorSpeechSegment ToSpeechSegment", 1)[0]
require('NormalizeVoiceClass(GetString(root, "voice_class"))' in parse,
        "VOICE-06 backend must normalize worker voice classes")
require('GetDouble(root, "voice_confidence")' in parse and 'GetDouble(root, "median_pitch_hz")' in parse,
        "VOICE-06 backend must parse confidence and pitch")
normalize = local_asr.split(
    "private static AsrCue? NormalizeCue(AsrCue cue, IReadOnlyList<AsrCue> existing)", 1
)[1].split("private static AsrCue ParseCue", 1)[0]
require("VoiceClass = EditorSpeechAnalysisDocument.NormalizeVoiceClass(cue.VoiceClass)" in normalize,
        "VOICE-06 normalized checkpoints must keep only canonical classes")
require("VoiceConfidence = Math.Clamp" in normalize and "MedianPitchHz =" in normalize,
        "VOICE-06 normalized checkpoints must sanitize confidence/pitch")
to_speech = local_asr.split("private static EditorSpeechSegment ToSpeechSegment(AsrCue cue)", 1)[1].split(
    "private static EditorSubtitleCue ToSubtitleCue", 1)[0]
require("cue.VoiceClass" in to_speech and "cue.VoiceConfidence" in to_speech and "cue.MedianPitchHz" in to_speech,
        "VOICE-06 persisted speech analysis must retain class/confidence/pitch")

require('case "male_like": male += weight; break;' in speech
        and 'case "female_like": female += weight; break;' in speech,
        "VOICE-06 cue mapping must aggregate male/female evidence by subtitle overlap")
require("var runner = Math.Min(male, female) + uncertain * .35;" in speech,
        "VOICE-06 ambiguous evidence must participate in the cue-level guard")
require('var label = top <= 0 || top < runner * 1.25 ? "uncertain"' in speech,
        "VOICE-06 conflicting cue-level evidence must fall back to uncertain")
require('if (label == "uncertain") confidence = Math.Min(confidence, .59);' in speech,
        "VOICE-06 cue-level uncertain state must remain below auto-route threshold")
require('"male" or "male_like" => "male_like"' in speech
        and '"female" or "female_like" => "female_like"' in speech,
        "VOICE-06 persisted aliases must normalize deterministically")
require('_ => "uncertain"' in speech,
        "VOICE-06 unknown labels must fail closed to uncertain")

select_voice = tts.split(
    "private static (string Voice, bool Review) SelectVoice(", 1
)[1].split("private static string CacheKey", 1)[0]
require('if (normalized is "male" or "female") return (normalized, false);' in select_voice,
        "VOICE-06 manual per-cue override must win over automatic routing")
require('"male_like" when timing.VoiceConfidence >= .60 => ("male", false)' in select_voice,
        "VOICE-06 confident male-like cues must route to male voice")
require('"female_like" when timing.VoiceConfidence >= .60 => ("female", false)' in select_voice,
        "VOICE-06 confident female-like cues must route to female voice")
require('_ => (timing.MedianPitchHz is > 0 and < 170 ? "male" : "female", true)' in select_voice,
        "VOICE-06 uncertain/low-confidence cues must still choose one binary voice but require review")

require('VoiceOverrides = NormalizeVoiceOverrides(loaded.VoiceOverrides)' in project
        and 'VoiceOverrides = NormalizeVoiceOverrides(project.VoiceOverrides)' in project,
        "VOICE-06 manual voice overrides must survive project reopen/save normalization")
override_normalizer = project.split(
    "private static IReadOnlyDictionary<string, string> NormalizeVoiceOverrides", 1
)[1]
require('voice is not ("male" or "female")' in override_normalizer,
        "VOICE-06 project persistence must reject non-binary manual overrides")

require('SelectionChanged="CurrentCueVoice_SelectionChanged"' in xaml,
        "VOICE-06 current-cue override must keep one UI event owner")
require('Tag="auto" Content="Tự động Nam/Nữ"' in xaml
        and 'Tag="male" Content="Ép voice Nam"' in xaml
        and 'Tag="female" Content="Ép voice Nữ"' in xaml,
        "VOICE-06 UI must expose auto/male/female routing only")

handler = editor.split(
    "private void CurrentCueVoice_SelectionChanged(", 1
)[1].split("private void UpdateCurrentCueVoiceUi()", 1)[0]
require('if (value is "male" or "female") overrides[cue.Id] = value;' in handler,
        "VOICE-06 UI must persist only explicit male/female overrides")
require("else overrides.Remove(cue.Id);" in handler,
        "VOICE-06 returning to auto must remove the manual override")
require("_voiceTrack = null;" in handler and "_project = _project with { VoiceOverrides = overrides, Tts = null };" in handler,
        "VOICE-06 changing a voice override must invalidate stale generated TTS")
require("QueueProjectSave();" in handler,
        "VOICE-06 per-cue voice override must persist with the project")

# Guard against accidental scope expansion into identity clustering or stem separation.
joined = "\n".join((worker, local_asr, speech))
for forbidden in ("pyannote", "diarization", "speaker_embedding", "demucs"):
    require(forbidden not in joined.lower(),
            f"VOICE-06 must not introduce {forbidden} into the ASR classification path")

# Policy fixtures: these test the reviewed class/routing thresholds without requiring
# numpy or an installed Whisper runtime in the repository verification environment.
def classify_pitch(pitch: float) -> str:
    if pitch <= 155.0:
        return "male_like"
    if pitch >= 185.0:
        return "female_like"
    return "uncertain"


def route(label: str, confidence: float, pitch: float) -> tuple[str, bool]:
    if label == "male_like" and confidence >= .60:
        return "male", False
    if label == "female_like" and confidence >= .60:
        return "female", False
    return ("male" if 0 < pitch < 170 else "female"), True


require(classify_pitch(120) == "male_like", "VOICE-06 fixture must classify 120 Hz as male-like")
require(classify_pitch(220) == "female_like", "VOICE-06 fixture must classify 220 Hz as female-like")
require(classify_pitch(170) == "uncertain", "VOICE-06 fixture must keep overlap pitch uncertain")
require(route("male_like", .80, 120) == ("male", False),
        "VOICE-06 confident male-like fixture must auto-route without review")
require(route("female_like", .80, 220) == ("female", False),
        "VOICE-06 confident female-like fixture must auto-route without review")
require(route("uncertain", .59, 160) == ("male", True),
        "VOICE-06 uncertain lower-pitch fixture must route male with review")
require(route("uncertain", .59, 180) == ("female", True),
        "VOICE-06 uncertain higher-pitch fixture must route female with review")

print("PASS: VOICE-06 local male/female classification contract")
