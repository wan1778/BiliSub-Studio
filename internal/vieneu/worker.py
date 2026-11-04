from __future__ import annotations

import argparse
import hashlib
import importlib.metadata
import json
import math
import os
import re
import subprocess
import sys
import unicodedata
import wave
from pathlib import Path

ENGINE = "vieneu-tts"
ENGINE_VERSION = "vieneu-v3-turbo-1"
# Backbone pin: the HF revision was verified to download without a token. The SDK
# manages its own cache; the worker only checks the revision and that the backbone file exists, with the full hash pinned by the installer.
MODEL_REVISION = "61b85e3d937fbbacb387714180e8182823512523"
MODEL_SHA256 = "61b85e3d937fbbacb387714180e8182823512523"
TIMING_ALGORITHM = "whole-cue-vieneu-natural-first-v1"
VOICE_REVISION = MODEL_REVISION + ":" + TIMING_ALGORITHM
VOICE_NAME = "Hải Đăng"
SAMPLE_RATE = 22050
VIENEU_SAMPLE_RATE = 48000
# VieNeu has no native speed control like Piper: it only reads at its natural
# pace. A window that is too loose is padded, too tight is condensed / atempoed /
# grouped (all engine-agnostic). Since base == length == 1.0, every validator bound
# (scale .30-1.20, preferred band) keeps its meaning with no separate branch.
FIT_METHOD = "vieneu-natural"
# Natural batch synth (30/09, RTX 3050 4GB): an 8-cue single-read profile spends
# 98% of its time in sequential GPU synth (RTF 0.255). Measured on 16 sentences
# (~55s of audio): batch8 RTF 0.071 peak 3251MB, batch16 RTF 0.061 peak 3283MB,
# batch32 RTF 0.065 peak 3283MB (saturated, no further gain). Fixed at 16: the
# fastest option while peak still stays ~800MB below the 4GB ceiling. Above that the read falls back to single, never killing the job.
BATCH_SYNTH_SIZE = 16
MAX_SYNTHESIS_ATTEMPTS = 12
MAX_GROUP_SYNTHESIS_ATTEMPTS = 12
MAX_TEMPO_ATTEMPTS = 6
# Audible speed is measured from Piper's own native-rate PCM. length_scale is
# not treated as a linear speed multiplier. A natural-rate cue is accepted
# immediately whenever it fits its SRT window. Only an actually overlong cue is
# synthesized again toward the smallest measured speed that can fit. If Piper
# saturates, bounded pitch-preserving atempo fits the complete waveform. Speech
# cutting and pitch-shifting filters are never permitted.
MIN_RATE_SCALE = .30
MAX_RATE_SCALE = 1.20
MIN_ACTUAL_SPEED = 1.0
MAX_ACTUAL_SPEED = 100.0
# Pace rule (01/10 + micro-band 02/10, user approved 1.8x then 2.0x; VieNeu
# only): never exceed 1.5x on every path (single, group, fallback), EXCEPT micro
# cues (window <1.1s) which are allowed 2.0x because their prosody carrier already
# starts at ~1.0s and no seed can fit it (the “Vâng” case needing 1.97x); wide cues
# stretch at most 1.4x of duration (pace never below ~0.71x). HARD_MAX_SPEED applies
# on top of the need (input/target); the factor written to the record can reach
# ~1.5005 due to FIT_HEADROOM + TEMPO_RETRY_MARGIN, so the record ceiling is 1.51
# (micro: 3.01 correspondingly). The C# mirror follows the window exactly, with the reason commented.
HARD_MAX_SPEED = 1.5
HARD_MAX_FACTOR = 1.51
# Micro-band (02/10 2.0x; user raised the demo threshold to 2.5x on 07/10 and to
# 3.0x on 25/10): micro cues (window <1.1s, usually 1-3 words) carry a fixed
# prosody carrier (~1.0s for a single word like “Cảm”) so the real need is
# 1.5-3.0x and no seed can manage it. atempo on a clip under 1s is almost
# transparent, hence the separate 3.0x ceiling (record 3.01 under the same FIT+T
# margin). Every other cue keeps 1.5x. Always review (the tempo path). The C# mirror follows the window exactly.
MICRO_MAX_SPEED = 3.0
MICRO_MAX_FACTOR = 3.01
MICRO_WINDOW_FRAMES = round(1.1 * SAMPLE_RATE)
# Overflow (user duyet): a single cue that is too long may read the WHOLE
# sentence at the window speed cap and spill into following silence. The
# absolute ceiling is 3.0s, but the real per-cue budget is the free gap to the
# next cue's start (see overflow_budget_frames): spilling into the next cue's
# window is the repeat-reading bug (tail of cue A + head of cue B).
# Only single cues (never sentence-group/sample). Always review + full
# spoken_text for traceability. C# mirrors the placement rule.
OVERFLOW_MAX_SECONDS = 3.0
OVERFLOW_MAX_FRAMES = int(OVERFLOW_MAX_SECONDS * SAMPLE_RATE)
# Group placement lag ceiling (07/10; user REQUIRES the voice to keep reading when
# the subtitle appears): a sentence-group member may place its voice later than the
# subtitle time, but not by even one frame -- either raise atempo within the ceiling
# (keeping group speed uniform, no C# change needed) or raise so the layer above
# splits into singles (a single gets widen/later gap + review, placed at the right
# time). Earlier than the subtitle is still fine (the voice is already reading when the subtitle appears). Never edit the text, never exceed the pace ceiling.
GROUP_MAX_LAG_SECONDS = 0.0
GROUP_MAX_LAG_FRAMES = int(GROUP_MAX_LAG_SECONDS * SAMPLE_RATE)


def micro_allowed(target_frames) -> bool:
    try:
        return 0 < int(target_frames) < MICRO_WINDOW_FRAMES
    except (TypeError, ValueError):
        return False


def cap_speed_for(target_frames: int) -> float:
    return MICRO_MAX_SPEED if micro_allowed(target_frames) else HARD_MAX_SPEED


def cap_factor_for(target_frames: int) -> float:
    return MICRO_MAX_FACTOR if micro_allowed(target_frames) else HARD_MAX_FACTOR
# Seeded-retry (02/10): the raw output landing on exactly 1.440s across independent
# runs means the model truncated mid-way at max-length (broken prosody). A raw
# output hitting this threshold is never accepted as a voice, even if the trimmed version fits.
SEED_TRUNC_SUSPECT_FRAMES = round(1.435 * SAMPLE_RATE)
SLOW_MIN_SPEED = 1.0 / 1.4
SLOW_MIN_FACTOR = 1.0 / 1.4
FIT_HEADROOM = .995
TEMPO_RETRY_MARGIN = 1.001
# Smallest representable speedup: just above natural speed so a passthrough
# attempt still records tempo_factor > 1 (required by result validation).
_MIN_TEMPO_FACTOR = 1.000001
MIN_GROUP_RATE_SCALE = MIN_RATE_SCALE
MAX_GROUP_ACTUAL_SPEED = MAX_ACTUAL_SPEED
# A sentence group may share only a continuously visible subtitle interval.
# Gaps up to a breath beat are still one passage: rapid dialogue cues separated
# by 0.1-0.25s pool their time instead of each fragment being squeezed alone.
# Must mirror LocalTtsService.MaxSentenceGroupGapFrames exactly (frames
# @22050 Hz); 5512 == 0.25s, the same magnitude as the overlap bound below.
MAX_GROUP_GAP_FRAMES = 5512
MAX_GROUP_DURATION_FRAMES = 300 * SAMPLE_RATE
MAX_GROUP_CUES = 512
# Lead budget (17/11, user-approved bounded "read early"): a cue with no room on
# either side used to lose the tail of its sentence, because neither the pace
# ceiling nor the trailing gap could cover it. Reading up to 1.0s EARLY into the
# previous cue's silence keeps every word without touching the pace ceiling.
# Mirrored by EditorVoiceCuePlanner + LocalTtsService.ReadResultAsync exactly.
MAX_LEAD_SECONDS = 1.0
MAX_LEAD_FRAMES = round(MAX_LEAD_SECONDS * SAMPLE_RATE)
SILENCE_AMPLITUDE = 32
SILENCE_GUARD_FRAMES = round(.05 * SAMPLE_RATE)
# P1 (02/10): the model layers ~0.02-0.07s of absolute silence before every short cue.
LEADING_SILENCE_GUARD_FRAMES = round(.005 * SAMPLE_RATE)
MIN_PREFERRED_RATE_SCALE = .70
MAX_PREFERRED_RATE_SCALE = 1.0
# Pilot package set (exact versions, pinned by the installer ahead of the C# match).
PACKAGES = {"torch": "2.8.0+cu128", "torchaudio": "2.8.0+cu128", "transformers": "4.57.6",
            "vieneu": "3.8.3", "soxr": "1.1.0", "vietnormalizer": None, "numpy": None}
MIN_SPEECH_FRAMES = round(.10 * SAMPLE_RATE)
MIN_SPEECH_FRAMES_PER_UNIT = round(.07 * SAMPLE_RATE)
# Windows: child FFmpeg must never flash a console window (thousands of atempo
# calls per video). Zero elsewhere (creationflags is ignored off Windows).
_NO_WINDOW = getattr(subprocess, "CREATE_NO_WINDOW", 0)

# Vietnamese-first pronunciation overrides for the third-party dictionary.
# These words are ordinary Vietnamese vocabulary that the packaged dictionary
# mistranscribes (long -> lon, song -> xong...): drop the entry so the word
# keeps its own spelling. Engineered overrides live here (not in the pip
# package) so a runtime reinstall can never wipe them.
TTS_DICTIONARY_DROPS = ("long", "song", "sen", "dan", "dao", "kia")
TTS_DICTIONARY_FIXES = {
    "gout": "gút",
    "coin": "cô-in",
    # NOTE: fix values must be plain Vietnamese syllables only. Anything the
    # detector deems non-Vietnamese (for example a lone "ch") gets chewed by
    # the transliterator AFTER replacement (tớt-ch -> tớt-kh), so "touch"
    # keeps just "tớt" instead of a fancier but unstable spelling.
    "touch": "tớt",
    "sup": "xúp",
    "okay": "ô-kê",
    "ok": "ô-kê",
}
_SINGLE_UPPER_LETTER_PATTERN = re.compile(r"\b([A-Z])\b")


def emit(payload: dict) -> None:
    print(json.dumps(payload, ensure_ascii=False, separators=(",", ":")), flush=True)


def normalize_vietnamese_text(normalizer, text: str) -> str:
    if not isinstance(text, str):
        raise ValueError("Vietnamese TTS text must be a string")
    # Keep Vietnamese vowels and tone marks in canonical composed form before
    # and after number/abbreviation normalization. This prevents equivalent
    # decomposed input (for example u + dot-below) from taking a different
    # phonemization/cache path.
    source = unicodedata.normalize("NFC", text).strip()
    # Spell out isolated uppercase single letters with Vietnamese letter names
    # (R -> e-rờ, X -> ích). The packaged normalizer only spells codes of 2+
    # characters, so a lone letter would otherwise reach Piper unhandled and be
    # guessed, often English-style. Lowercase singletons (a, e, o...) can be
    # real Vietnamese words, so they are left alone.
    from vietnormalizer.normalizer import LETTER_NAMES
    source = _SINGLE_UPPER_LETTER_PATTERN.sub(
        lambda match: LETTER_NAMES[match.group(1)], source)
    normalized = unicodedata.normalize("NFC", normalizer.normalize(source)).strip()
    if not normalized:
        raise ValueError("Vietnamese normalization returned empty text")
    return normalized


def apply_tts_dictionary_overrides(normalizer) -> None:
    """Apply the Vietnamese-first overrides (see TTS_DICTIONARY_* above)."""
    for dropped in TTS_DICTIONARY_DROPS:
        normalizer.replacements.pop(dropped, None)
    normalizer.replacements.update(TTS_DICTIONARY_FIXES)


def minimum_speech_frames(text: str) -> int:
    """Reject clicks/truncated tails that cannot plausibly contain the text.

    This deliberately uses a very permissive ceiling of roughly 14 Vietnamese
    syllables per second. It is a corruption guard, not a speaking-rate control.
    """
    units = sum(1 for token in re.findall(r"\w+", text, flags=re.UNICODE)
                if any(character.isalnum() for character in token))
    if units <= 0:
        raise ValueError("Vietnamese TTS text has no speakable units")
    return max(MIN_SPEECH_FRAMES, units * MIN_SPEECH_FRAMES_PER_UNIT)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def atomic_json(path: Path, value: dict) -> None:
    temporary = path.with_name(path.name + ".tmp-" + os.urandom(6).hex())
    try:
        with temporary.open("x", encoding="utf-8") as stream:
            json.dump(value, stream, ensure_ascii=False, indent=2, allow_nan=False)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        temporary.replace(path)
    finally:
        temporary.unlink(missing_ok=True)


def read_wav(path: Path):
    import numpy as np
    with wave.open(str(path), "rb") as source:
        if (source.getnchannels(), source.getsampwidth(), source.getframerate()) != (1, 2, SAMPLE_RATE):
            raise ValueError("VieNeu clip must be mono PCM16 at 22050 Hz")
        frames = source.getnframes()
        data = source.readframes(frames)
    if frames <= 0 or len(data) != frames * 2:
        raise ValueError("VieNeu clip is empty or truncated")
    samples = np.frombuffer(data, dtype="<i2").astype(np.float32) / 32768.0
    if not np.isfinite(samples).all() or float(np.max(np.abs(samples))) < 0.0001:
        raise ValueError("VieNeu clip is silent or invalid")
    # This is a signal-integrity check, not proof of intelligible speech or voice quality.
    return samples


def cache_identity(cue: dict, text: str, worker_sha: str) -> str:
    value = [VOICE_REVISION, PACKAGES, worker_sha, cue["id"], cue["cue_start"], cue["cue_end"],
             cue["voice_start"], cue["voice_end"], cue["timing_source"], text]
    return hashlib.sha256(json.dumps(value, ensure_ascii=False, sort_keys=True).encode("utf-8")).hexdigest()


def cue_window(cue: dict):
    start = round(cue["voice_start"] * SAMPLE_RATE)
    frames = round(cue["voice_end"] * SAMPLE_RATE) - start
    if start < 0 or frames <= 0:
        raise ValueError("Source speech window has no PCM samples")
    return start, frames


def overflow_budget_frames(cues: list[dict], index: int, active_cue: dict, total_frames: int) -> int:
    """Free frames between this cue's own window and the next cue's start.

    Overflow may only consume real silence. Extending into the next cue's
    window makes the listener hear the tail of cue A followed by the head of
    cue B, which is the reported repeat-reading bug. The last cue may use the
    remaining source duration.
    """
    start, frames = cue_window(active_cue)
    next_start = (round(cues[index + 1]["cue_start"] * SAMPLE_RATE)
                  if index + 1 < len(cues) else total_frames)
    return max(0, next_start - (start + frames))


def clip_overlap_pairs(clips: list[dict]) -> list[dict]:
    """Every overlapping clip pair, with cue ids, frames and milliseconds.

    ``unexpected`` is true when the right clip starts at or after the left
    cue's own visible window end, i.e. the overlap cannot be explained by
    overlapping source subtitles and must be a placement bug.
    """
    ordered = sorted(clips, key=lambda clip: (clip["start_sample"], clip.get("id", "")))
    active: list[dict] = []
    overlaps: list[dict] = []
    for clip in ordered:
        active = [item for item in active
                  if item["start_sample"] + item["frames"] > clip["start_sample"]]
        for item in active:
            overlap = item["start_sample"] + item["frames"] - clip["start_sample"]
            window_end = item.get("window_end_sample", item["start_sample"] + item["frames"])
            overlaps.append({"left": item.get("id", "?"), "right": clip.get("id", "?"),
                             "frames": overlap, "seconds": overlap / SAMPLE_RATE,
                             "unexpected": clip["start_sample"] >= window_end})
        active.append(clip)
    return overlaps


def padding_budget(target_frames: int) -> int:
    # A short remainder is considered a precise fit. Larger trailing silence is
    # still safe because every model-produced PCM sample is retained, but the
    # cue is marked for review instead of rejecting valid speech.
    return max(1, min(round(.04 * SAMPLE_RATE), target_frames // 50))


def needs_rate_review(length_scale: float, base_length_scale: float) -> bool:
    return not MIN_PREFERRED_RATE_SCALE <= length_scale / base_length_scale <= MAX_PREFERRED_RATE_SCALE


def single_needs_sentence_group(record: dict) -> bool:
    """A single-cue fit that only fits by speaking unnaturally fast must yield
    to sentence-group synthesis when the cue belongs to a touching group.

    Mirrors needs_rate_review on the slow side only: a too-SLOW single is never
    improved by grouping (padding already handles it), but a too-FAST single
    (relative scale below the preferred band, or pitch-safe atempo fallback)
    reads naturally once it shares the group's wider window. Missing or
    unparsable rate metadata fails open to the old behavior.
    """
    try:
        base = float(record.get("base_length_scale", 0))
        scale = float(record.get("length_scale", 0))
    except (TypeError, ValueError):
        return False
    if base > 0 and math.isfinite(scale):
        relative = scale / base
        if math.isfinite(relative) and relative < MIN_PREFERRED_RATE_SCALE:
            return True
    return str(record.get("fit_method", "")) == ATEMPO_METHOD


PATHOLOGICAL_NEED_RATIO = 3.0
FAST_SINGLE_NEED_RATIO = 1.43
GROUP_DRIFT_WARN_SECONDS = 0.5
EXCLAMATION_TERMINALS = ("!", "！", "?", "？")


def speakable_units(text: str) -> int:
    return sum(1 for token in re.findall(r"\w+", text, flags=re.UNICODE)
               if any(character.isalnum() for character in token))


def is_exclamation_exempt(text: str) -> bool:
    """Short shouted lines read fast naturally ("Đại Tông Sư!"). Reporting-only
    exemption: never blocks synthesis, only keeps them off the pathological and
    isolated-fast watch lists (which target mistimed data, not shouts)."""
    stripped = text.strip() if isinstance(text, str) else ""
    return bool(stripped) and stripped[-1] in EXCLAMATION_TERMINALS and speakable_units(stripped) <= 4


def need_ratio(native_reference_frames, window_frames) -> float:
    """Authored-window shortfall: natural speech frames divided by authored
    window frames. >1 means the window is too short; >3 means timing data bug.
    Unparsable input fails open to 0 (never listed)."""
    try:
        native = float(native_reference_frames)
        window = float(window_frames)
    except (TypeError, ValueError):
        return 0.0
    if not math.isfinite(native) or not math.isfinite(window) or window <= 0 or native <= 0:
        return 0.0
    return native / window


def report_group_rate(group_indices, group_entries, cues, texts, tally):
    """Classify this group's voicing outcome for the operator watch lists.

    Pure bookkeeping over already-fitted records: never synthesizes, never
    raises, never changes output. Reads:
    - pathological: non-exempt member whose authored window holds less than a
      third of its natural speech (timing data bug, not fast dialogue);
    - isolated-fast: ungrouped single needing >1.43x (manual SRT fix candidate);
    - drift: group-path window borrowing beyond 0.5s per boundary.
    Grouped non-pathological members stay silent: sharing time is the design,
    and their individual ratios are stale after borrowing.
    """
    drift = 0.0
    try:
        for position, entry in enumerate(group_entries):
            index = group_indices[position]
            active, record = entry[0], entry[3]
            original = cues[index]
            text = texts[index]
            if record.get("timing_source") == "sentence-group":
                start_drift = abs(float(active["voice_start"]) - float(original["voice_start"]))
                end_drift = abs(float(active["voice_end"]) - float(original["voice_end"]))
                drift = max(drift, start_drift, end_drift)
                continue
            span = round(float(original["cue_end"]) * SAMPLE_RATE) - round(float(original["cue_start"]) * SAMPLE_RATE)
            ratio = need_ratio(record.get("native_reference_frames", 0), span)
            if ratio <= 0 or is_exclamation_exempt(text):
                continue
            cue_id = original.get("id", f"cue-{index + 1}")
            if ratio > PATHOLOGICAL_NEED_RATIO:
                tally["pathological"].append({"id": cue_id, "need_ratio": round(ratio, 2),
                                              "absorbed": len(group_indices) > 1})
            elif len(group_indices) == 1 and ratio > FAST_SINGLE_NEED_RATIO:
                tally["isolated_fast"].append({"id": cue_id, "need_ratio": round(ratio, 2)})
    except (TypeError, ValueError, KeyError, IndexError):
        return 0.0
    if drift > 0:
        tally["max_drift_s"] = max(tally.get("max_drift_s", 0.0), drift)
        if drift > GROUP_DRIFT_WARN_SECONDS:
            first, last = group_indices[0], group_indices[-1]
            try:
                emit({"event": "notice", "code": "boundary-drift",
                      "message": (f"Nhóm cue {first + 1}..{last + 1} mượn cửa sổ tới "
                                  f"{drift:.3f}s để vừa lời — kiểm tra biên timecode nếu voice lệch hình.")})
            except Exception:
                pass
    return drift


def native_record_valid(record: dict, target_frames: int, natural_sample: bool,
                        minimum_generated_frames: int = 1) -> bool:
    if record.get("fit_method") == "silence-fallback":
        return (record.get("status") == "review"
                and type(record.get("source_frames")) is int and record["source_frames"] == 0
                and type(record.get("generated_frames")) is int and record["generated_frames"] == 0
                and type(record.get("trimmed_silence_frames")) is int and record["trimmed_silence_frames"] == 0
                and type(record.get("padding_frames")) is int and record["padding_frames"] == target_frames
                and type(record.get("frames")) is int and record["frames"] == target_frames
                and type(record.get("target_frames")) is int and record["target_frames"] == target_frames
                and type(record.get("synthesis_attempts")) is int
                and 1 <= record["synthesis_attempts"] <= MAX_GROUP_SYNTHESIS_ATTEMPTS
                and not natural_sample)
    base, scale = record["base_length_scale"], record["length_scale"]
    source, generated = record["source_frames"], record["generated_frames"]
    trimmed, padding = record["trimmed_silence_frames"], record["padding_frames"]
    attempts = record["synthesis_attempts"]
    maximum_attempts = (MAX_GROUP_SYNTHESIS_ATTEMPTS
                        if record.get("timing_source") == "sentence-group" else MAX_SYNTHESIS_ATTEMPTS)
    fit_method = record["fit_method"]
    tempo_fallback = fit_method == ATEMPO_METHOD
    minimum_rate = MIN_GROUP_RATE_SCALE if record.get("timing_source") == "sentence-group" else MIN_RATE_SCALE
    if (fit_method not in (FIT_METHOD, ATEMPO_METHOD) or not math.isfinite(base) or base <= 0
            or not math.isfinite(scale) or not minimum_rate <= scale / base <= MAX_RATE_SCALE
            or type(source) is not int or source <= 0 or type(generated) is not int
            or generated <= 0
            or type(trimmed) is not int or not 0 <= trimmed < source
            or type(padding) is not int
            or not 0 <= padding < target_frames or generated + padding != record["frames"]
            or not natural_sample and record["frames"] != target_frames
            and not (type(record.get("overflow_frames")) is int
                     and 0 < record["overflow_frames"] <= OVERFLOW_MAX_FRAMES
                     and record["frames"] == target_frames + record["overflow_frames"])
            or type(attempts) is not int or not 1 <= attempts <= maximum_attempts):
        return False
    native_reference = record.get("native_reference_frames", 0)
    group_native = record.get("group_native_frames", 0)
    actual_speed = record.get("actual_speed_factor", 0)
    # Pace rule (01/10 + micro-band 02/10 2.0x, user 07/10 raised the demo threshold to
    # 2.5x): the tempo record (fast or slow) must sit in [SLOW_MIN_SPEED, 1.51] normally,
    # or [.., 2.51] when the window is <1.1s; ordinary records still keep [1, 100] as
    # before. The C# mirror follows the window exactly.
    cap_factor = cap_factor_for(target_frames)
    if tempo_fallback:
        actual_bounds = SLOW_MIN_SPEED <= actual_speed <= cap_factor
    else:
        actual_bounds = MIN_ACTUAL_SPEED <= actual_speed <= MAX_ACTUAL_SPEED
    if (type(native_reference) is not int or native_reference <= 0
            or not isinstance(actual_speed, (int, float)) or not math.isfinite(actual_speed)
            or not actual_bounds):
        return False
    if record.get("timing_source") == "sentence-group":
        if type(group_native) is not int or group_native != native_reference:
            return False
    elif group_native != 0 or abs(native_reference / generated - actual_speed) > 1e-9:
        return False
    tempo_input = record.get("tempo_input_frames", 0)
    tempo_factor = record.get("tempo_factor", 0)
    tempo_attempts = record.get("tempo_attempts", 0)
    if tempo_fallback:
        slow = isinstance(tempo_factor, (int, float)) and tempo_factor < 1
        if (type(tempo_input) is not int or tempo_input < minimum_generated_frames
                or tempo_input != source - trimmed
                or (tempo_input <= generated if not slow else tempo_input >= generated)
                or not isinstance(tempo_factor, (int, float)) or not math.isfinite(tempo_factor)
                or not (SLOW_MIN_FACTOR <= tempo_factor <= cap_factor)
                or abs(tempo_input / generated - tempo_factor) > 1e-9
                or type(tempo_attempts) is not int or not 1 <= tempo_attempts <= MAX_TEMPO_ATTEMPTS):
            return False
    elif (generated < minimum_generated_frames or source - trimmed != generated
          or tempo_input != 0 or tempo_factor != 0 or tempo_attempts != 0):
        return False
    if attempts == 1:
        if scale != base:
            return False
        # 30/09 (VieNeu, no scale loop): atempo on attempt 1 is normal (probe at natural
        # pace then atempo, no scale iteration like Piper) so native != generated on a
        # tempo record is honest, not a lie. The native==generated condition applies only
        # to the natural (non-tempo) attempt.
        if (fit_method != ATEMPO_METHOD and record.get("timing_source") != "sentence-group"
                and native_reference != generated):
            return False
    if natural_sample and record.get("timing_source") != "sample":
        return False
    needs_review = (tempo_fallback or record.get("timing_source") == "sentence-group"
                    or needs_rate_review(scale, base) or trimmed > 0
                    or padding > padding_budget(target_frames)
                    # 28/09 (M11): condensed text is always review even when it fits at natural
                    # pace -- otherwise a cache retry self-rejects and C# disagrees on status.
                    or "spoken_text" in record)
    return record["status"] == ("review" if needs_review else "fit")


def _overflow_likely_severe(text: str, target_frames: int) -> bool:
    """Uoc luong nhanh bang cong thuc pace hieu chuan (0.35s/don vi + 0.2s):
    chi dang probe overflow khi tu nhien uoc tinh vuot 1.8x khung (vung
    truncate nang). Cue vua tran vua duoc cuu bang atempo vua khung nhu cu."""
    try:
        estimate = (speakable_units(text) * 0.35 + 0.2) * SAMPLE_RATE
    except ValueError:
        return False
    return estimate > max(1, target_frames) * 1.8


def try_spill_into_gap(ffmpeg: Path, fitted_source: Path, run_root: Path, key: str,
                       cue_id: str, timing_source: str, target_frames: int,
                       budget_frames: int, base: float, text: str,
                       source_frames: int, trimmed_silence: int, natural: int,
                       raw_frames: int, native_reference_frames: int,
                       cap: float, attempt: int, minimum_generated: int):
    """Read the WHOLE sentence into the real silence after the cue (F1 17/11).

    Reached from fit_cue when the sentence needs more room than its own window
    allows at the permitted pace, but the next cue has not started yet. The
    record mirrors the proven try_overflow_voice shape (atempo + overflow_frames
    + spoken_text + review) so the C# validator accepts it unchanged. Unlike the
    escalate path this reuses the native render already measured in Phase 0, so
    it costs no extra GPU synthesis. Returns the record dict, or None when even
    the permitted pace cannot fit inside target + gap.
    """
    budget = min(OVERFLOW_MAX_FRAMES, max(0, int(budget_frames)))
    if budget < 500:
        return None
    # Keep the historical 500-frame safety margin so atempo rounding can never
    # step past the gap boundary (mirrors try_overflow_voice).
    usable = max(1, target_frames + budget - 500)
    if natural <= 0 or natural / usable > cap:
        return None
    # Slowest pace the extended window still allows: a spilled read at 1.1x
    # sounds far better than the same read pinned to the ceiling.
    aim = natural / usable
    if aim < 1.0:
        aim = 1.0
    if aim > cap - 0.02:
        aim = max(1.0, cap - 0.02)
    out_frames = max(1, math.ceil(natural / aim))
    if out_frames > usable:
        out_frames = usable
    if out_frames <= target_frames:
        return None
    # fit_cue's own convention: the fitted clip always lands at key + "-fit.wav",
    # and resolve_individual hashes/replaces exactly that path.
    final_path = run_root / (key + "-fit.wav")
    try:
        generated = apply_atempo(ffmpeg, fitted_source, final_path, natural / max(1, out_frames))
    except Exception:
        return None
    if generated <= target_frames or generated > usable:
        return None
    actual_speed = natural / generated
    if not (MIN_ACTUAL_SPEED <= actual_speed <= cap):
        return None
    record = {"raw_duration": raw_frames / SAMPLE_RATE,
              "fitted_duration": generated / SAMPLE_RATE,
              "frames": generated, "target_frames": target_frames,
              "timing_source": timing_source,
              "fit_method": ATEMPO_METHOD, "base_length_scale": base,
              "length_scale": base, "source_frames": source_frames,
              "generated_frames": generated,
              "trimmed_silence_frames": trimmed_silence, "padding_frames": 0,
              "native_reference_frames": native_reference_frames,
              "group_native_frames": 0, "actual_speed_factor": actual_speed,
              "tempo_input_frames": natural, "tempo_factor": natural / generated,
              "tempo_attempts": 1, "synthesis_attempts": max(1, attempt),
              "status": "review", "spoken_text": text,
              "overflow_frames": generated - target_frames}
    if not native_record_valid(record, target_frames, timing_source == "sample",
                               minimum_generated):
        return None
    emit({"event": "notice", "code": "spill-into-gap",
          "message": (f"Cue {cue_id} khong vua khung o nhip cho phep: doc NGUYEN CAU "
                      f"x{actual_speed:.2f} tran {(generated - target_frames) / SAMPLE_RATE:.2f}s "
                      f"vao khoang lang that sau cau + co review.")})
    return record


def try_overflow_voice(index, cue_id, total, active_cue, text, target_frames, voice,
                       base_scale, run_root, clip_root, worker_sha, ffmpeg,
                       max_overflow_frames, on_attempt):
    """Read the WHOLE sentence at the window speed cap and use real silence.

    Returns an entry (active, key, path, record, cache_hit, calls) like
    escalate, or None so the caller falls back to truncation. Only single cues
    (not sentence-group/sample). ``max_overflow_frames`` is the measured free
    gap to the next cue; records are never allowed to exceed it. Every synth
    attempt is counted through on_attempt so C# accounting matches; the record
    follows finish_tempo shape with overflow_frames + spoken_text."""
    if active_cue.get("timing_source") in ("sentence-group", "sample"):
        return None
    budget = min(OVERFLOW_MAX_FRAMES, max(0, int(max_overflow_frames)))
    # Less than ~23ms of real silence is not worth a different read.
    if budget < 500:
        return None
    cap = cap_speed_for(target_frames)
    try:
        minimum_generated = minimum_speech_frames(text)
    except ValueError:
        return None
    natural_sample = active_cue["timing_source"] == "sample"
    overflow_key = cache_identity(active_cue, "overflow:" + text, worker_sha)
    cached_path = clip_root / (overflow_key + ".wav")
    try:
        sidecar = cached_path.with_suffix(".json")
        if cached_path.is_file() and sidecar.is_file():
            hit_record = json.loads(sidecar.read_text(encoding="utf-8"))
            if (hit_record.get("key") == overflow_key
                    and hit_record.get("sha256") == sha256(cached_path)
                    and type(hit_record.get("overflow_frames")) is int
                    and 0 < hit_record["overflow_frames"] <= budget
                    and native_record_valid(hit_record, target_frames, natural_sample,
                                            minimum_generated)):
                return (active_cue, overflow_key, cached_path, hit_record, True,
                        hit_record["synthesis_attempts"])
    except (OSError, ValueError, KeyError, TypeError):
        pass
    probe_path = run_root / (overflow_key + "-probe.wav")
    probe_trim_path = run_root / (overflow_key + "-probe-trim.wav")
    on_attempt(1, base_scale)
    synthesize_cue(voice, text, probe_path, base_scale)
    probe_source = len(read_wav(probe_path))
    probe_trimmed = trim_trailing_silence(probe_path, probe_trim_path, SILENCE_GUARD_FRAMES)
    natural = probe_source - probe_trimmed
    if natural < minimum_generated or natural <= 0:
        return None
    units = speakable_units(text)
    trimmed_probe = probe_trim_path if probe_trimmed else probe_path
    if not text_self_repeats(text) and speech_repeats(trimmed_probe, units if units <= 8 else None):
        return None
    max_frames = target_frames + budget
    # If even max-speed speech cannot fit inside the real gap, there is no
    # overflow path; let the caller truncate the text instead of overlapping.
    if natural / max(1, max_frames) > cap:
        return None
    aim_factor = max(cap - 0.02, 1.0)
    out_frames = max(1, math.ceil(natural / aim_factor))
    # Keep the historical 500-frame safety margin so atempo rounding can never
    # step past the gap boundary.
    out_frames = min(out_frames, max_frames - 500)
    if out_frames <= target_frames:
        return None
    final_path = run_root / (overflow_key + "-fit.wav")
    generated = apply_atempo(ffmpeg, trimmed_probe, final_path, natural / max(1, out_frames))
    if generated <= target_frames or generated > max_frames:
        return None
    actual_speed = natural / generated
    if not (MIN_ACTUAL_SPEED <= actual_speed <= cap):
        return None
    record = {"raw_duration": probe_source / SAMPLE_RATE,
              "fitted_duration": generated / SAMPLE_RATE,
              "frames": generated, "target_frames": target_frames,
              "timing_source": active_cue["timing_source"],
              "fit_method": ATEMPO_METHOD, "base_length_scale": base_scale,
              "length_scale": base_scale, "source_frames": probe_source,
              "generated_frames": generated,
              "trimmed_silence_frames": probe_trimmed, "padding_frames": 0,
              "native_reference_frames": natural, "group_native_frames": 0,
              "actual_speed_factor": actual_speed,
              "tempo_input_frames": natural, "tempo_factor": natural / generated,
              "tempo_attempts": 1, "synthesis_attempts": 1, "status": "review",
              "spoken_text": text, "overflow_frames": generated - target_frames}
    if not native_record_valid(record, target_frames, natural_sample, minimum_generated):
        return None
    record.update({"key": overflow_key, "sha256": sha256(final_path)})
    final_path.replace(cached_path)
    atomic_json(cached_path.with_suffix(".json"), record)
    emit({"event": "notice", "code": "overflow-voice",
          "message": (f"Cue {index + 1} ({cue_id}) qua dai vat ly: doc nguyen cau "
                      f"x{actual_speed:.2f} tran khung {(generated - target_frames) / SAMPLE_RATE:.2f}s "
                      f"trong khe trong that + co review.")})
    return (active_cue, overflow_key, cached_path, record, False, 1)


def escalate_truncated_voice(index, cue_id, total, active_cue, text, target_frames, voice,
                             base_scale, run_root, clip_root, worker_sha, ffmpeg,
                             max_overflow_frames, on_attempt):
    """Cue Việt đọc đơn vẫn không vừa: cắt ngắn dần rồi fit chuẩn. Luôn voice
    + spoken_text là đoạn thực đọc + cờ review. Chỉ raise khi 1 từ cũng không
    vừa (vật lý).
    Triệt để phương sai (02/10): đo rồi fit_cue tổng hợp LẠI là sai kiến trúc
    với synth diffusion — lần đo và lần fit là 2 byte khác nhau (lệch tới
    2.5x ở cue ngắn), tìm kiếm trên số đo chỉ là tối ưu nhiễu. Cue ngắn (≤8
    đơn vị) duyệt prefix dài-trước + seed định trước, CHỐT byte thắng rồi đặt
    sẵn vào candidate để fit_cue tái dùng (preloaded) thay vì tổng hợp lại:
    đo ≡ fit, không còn lệch. Cue dài giữ binary-search cũ (phương sai nhỏ,
    đo lại ổn)."""
    words = text.split()
    if not words:
        raise RuntimeError("cue rỗng, không có chữ để đọc")
    try:
        esc_units = speakable_units(text)
    except ValueError:
        esc_units = 0
    # F1 (17/11): the whole sentence always gets first refusal of the real
    # silence after the cue, before any word is cut. fit_cue already tried the
    # cheap spill (reusing its native render); this is the last-resort read, so
    # the >8-unit severity gate of the old order is gone - a short cue with a
    # real gap deserves its full sentence just as much as a long one.
    if active_cue.get("timing_source") not in ("sentence-group", "sample"):
        overflow_entry = try_overflow_voice(
            index, cue_id, total, active_cue, text, target_frames, voice,
            base_scale, run_root, clip_root, worker_sha, ffmpeg,
            max_overflow_frames, on_attempt)
        if overflow_entry is not None:
            return overflow_entry
    trunc_probe = run_root / (cache_identity(active_cue, text, worker_sha) + "-trunc-probe.wav")
    trunc_trimmed = run_root / (cache_identity(active_cue, text, worker_sha) + "-trunc-trim.wav")

    def _measure(prefix):
        synthesize_cue(voice, prefix, trunc_probe, base_scale)
        source = len(read_wav(trunc_probe))
        trimmed = trim_trailing_silence(trunc_probe, trunc_trimmed, SILENCE_GUARD_FRAMES)
        return source - trimmed

    preloaded = None
    if speakable_units(text) <= 8:
        hit = _seeded_prefix_search(
            index, cue_id, total, voice, words, active_cue, text, target_frames,
            run_root, worker_sha, base_scale)
        if hit is None:
            # Overflow (whole sentence into the real gap) was already attempted at
            # the top of this function, so a miss here means the cue has no usable
            # silence at all: a physically impossible window the app must report
            # for an SRT timecode fix instead of guessing.
            raise RuntimeError(
                "khong vua khung o nhip cho phep va khong con khoang lang that sau cau")
            raise RuntimeError("truncate cũng bó tay: không prefix/seed nào vừa khung ở pace cho phép")
        prefix, trunc_key = hit
        preloaded = {str(run_root / (trunc_key + "-native.wav")): True}
    else:
        # Long cues: variance is small, so measure-once + refit (as before) stays correct.
        # Bounded by the pace ceiling of the window (micro-band when the window is <1.1s).
        search_bound = max(1, math.floor(target_frames * cap_speed_for(target_frames) / 1.15))
        found = longest_fitting_prefix(_measure, words, search_bound, minimum_speech_frames)
        if found is None:
            first_word = words[0]
            first_frames = _measure(first_word)
            if first_frames is not None and first_frames > 0:
                found = (first_word, first_frames)
            else:
                raise RuntimeError("1 từ cũng không vừa timecode")
        prefix, _ = found
        trunc_key = cache_identity(active_cue, "trunc:" + prefix, worker_sha)
    trunc_cached = clip_root / (trunc_key + ".wav")
    trunc_path, trunc_record = fit_cue(
        voice, prefix, target_frames, run_root, trunc_key,
        active_cue["timing_source"], ffmpeg, on_attempt, cue_id=cue_id,
        preloaded=preloaded)
    trunc_record = repair_group_native(trunc_record)
    if prefix != text:
        trunc_record.setdefault("spoken_text", prefix)
        trunc_record["status"] = "review"
    if not native_record_valid(trunc_record, target_frames,
                               active_cue["timing_source"] == "sample",
                               minimum_speech_frames(prefix)):
        raise RuntimeError("cắt ngắn vẫn không đạt validation")
    trunc_record.update({"key": trunc_key, "sha256": sha256(trunc_path)})
    trunc_path.replace(trunc_cached)
    atomic_json(trunc_cached.with_suffix(".json"), trunc_record)
    emit({"event": "notice", "code": "truncate-voice",
          "message": (f"Cue {index + 1} ({cue_id}) quá dài vật lý: đọc {len(prefix.split())}/{len(words)} từ "
                      f"vừa khung + cờ review. SRT hiển thị giữ nguyên.")})
    return (active_cue, trunc_key, trunc_cached, trunc_record, False,
            trunc_record["synthesis_attempts"])


def repair_group_native(record: dict) -> dict:
    """Đọc đơn lại cue nhóm bằng 1 synth duy nhất: tiling nhóm không chẻ
    speech của nó nên group-native bằng native theo cấu tạo. Pure, test được."""
    if record.get("timing_source") == "sentence-group":
        return dict(record, group_native_frames=record["native_reference_frames"])
    return record


def vietnamese_word_ratio(text: str) -> float:
    """Tỉ lệ chữ Việt trong cue (01/10, luật L3): đa số ngoại văn (<0.5) là
    lỗi dữ liệu để user sửa SRT. Dùng detector chính quy của vietnormalizer
    (đúng cả chữ Việt không dấu như "di"/"thoi", chữ đã Việt-hóa như "ô-kê");
    số/ký hiệu trơ trụi tính là ngoại văn. Pure, test được không cần model."""
    from vietnormalizer.detector import is_vietnamese_word
    units = [token for token in re.findall(r"\w+", text or "", flags=re.UNICODE)
             if any(character.isalnum() for character in token)]
    if not units:
        return 0.0
    return sum(1 for unit in units if is_vietnamese_word(unit)) / len(units)


def _seeded_prefix_search(index: int, cue_id: str, total: int, voice, words: list[str],
                          active_cue: dict, text: str,
                          target_frames: int, run_root: Path, worker_sha: str,
                          base_scale: float, seeds_full: int = 6, seeds_short: int = 3):
    """Duyệt prefix dài-trước, mỗi prefix ≤N seed định trước, chốt byte thắng.

    Trả (prefix, trunc_key) và ĐÃ đặt byte thắng vào candidate mà fit_cue sẽ
    tái dùng (run_root/trunc_key-native.wav) — caller truyền preloaded để
    fit không tổng hợp lại. Chấp nhận prefix khi trimmed/target ≤ trần pace,
    raw dưới ngưỡng truncate, đủ minimum speech. Seed sha từ trunc_key nên
    chạy lại nào cũng cùng dãy (cache/sha an toàn). Trả None khi không chốt
    được. Tiến trình báo qua event attempt TRỰC TIẾP (không qua on_attempt
    đếm calls): số lần đọc seed-thử KHÔNG được tính vào synthesis_calls, nếu
    không record attempts lệch khỏi calls và C# từ chối cả job (vụ 28 cue
    calls 13-35 vs attempts 1).
    """
    try:
        import torch
    except Exception:
        return None
    trim_tmp = run_root / (cache_identity(active_cue, text, worker_sha) + "-esc-trim.wav")
    tries = 0
    for end in range(len(words), 0, -1):
        # Seed budget scales with prefix length: a long rendering needs to land a rare
        # short draw, so give it many; a short rendering fits on any draw, so give it few.
        # The worst-case total is capped (~10+7x4) yet short prefixes always get their turn
        # (case #351: the old total cap cut off the prefix ‘Không thể nào!’ at the tail).
        budget = seeds_full if end == len(words) else seeds_short
        prefix = " ".join(words[:end])
        try:
            prefix_min = minimum_speech_frames(prefix)
        except ValueError:
            continue
        trunc_key = cache_identity(active_cue, "trunc:" + prefix, worker_sha)
        candidate = run_root / (trunc_key + "-native.wav")
        capped_streak = 0
        for seed_pos in range(max(1, budget)):
            seed = int(hashlib.sha256(
                f"{trunc_key}:esc:{seed_pos}".encode("utf-8")).hexdigest(), 16) % (2 ** 31)
            tries += 1
            try:
                emit({"event": "attempt", "index": index + 1, "total": total, "id": cue_id,
                      "attempt": tries, "max_attempts": MAX_SYNTHESIS_ATTEMPTS,
                      "timing_source": active_cue.get("timing_source", ""),
                      "length_scale": base_scale})
            except Exception:
                pass
            try:
                torch.manual_seed(seed)
                synthesize_cue(voice, prefix, candidate, base_scale)
                source = len(read_wav(candidate))
                trimmed = trim_trailing_silence(candidate, trim_tmp, SILENCE_GUARD_FRAMES)
                kept = source - trimmed
            except Exception:
                continue
            # Cost squeeze (04/10): once the model enters inflation mode (raw hitting the
            # ceiling) further seeds are useless -- evidence: “Đừng quan tâm” hit the ceiling
            # 16/16 draws. Only count it here; the veto/cut below still runs normally for the
            # current draw, breaking at the end of the loop.
            if source >= SEED_TRUNC_SUSPECT_FRAMES:
                capped_streak += 1
            else:
                capped_streak = 0
            # Content veto/cut is OFF when the text self-repeats (identical halves are correct)
            # audio). The nuclear rule only inspects renders >=0.6s, so a short prefix
            # (‘Không’ 0.36s) is never killed by mistake.
            prefix_repeats = (not text_self_repeats(prefix)
                              and speech_repeats(trim_tmp, speakable_units(prefix)))
            if (kept >= prefix_min and kept > 0
                    and source < SEED_TRUNC_SUSPECT_FRAMES
                    and kept / max(1, target_frames) <= cap_speed_for(target_frames)
                    and not prefix_repeats):
                return prefix, trunc_key
            if (kept >= prefix_min and kept > 0 and prefix_repeats):
                # Doubled render: cut the first instance and overwrite the candidate so fit_cue
                # reuses exactly the clean render (preloaded).
                try:
                    esc_cut = run_root / (trunc_key + f"-esc{seed_pos}-cut.wav")
                    cut_first_instance(trim_tmp, esc_cut, prefix_min)
                    cut_source = len(read_wav(esc_cut))
                    cut_trimmed = trim_trailing_silence(esc_cut, trim_tmp, SILENCE_GUARD_FRAMES)
                    cut_kept = cut_source - cut_trimmed
                except Exception:
                    continue
                if (cut_kept >= prefix_min and cut_kept > 0
                        and cut_kept / max(1, target_frames) <= cap_speed_for(target_frames)):
                    import shutil
                    shutil.copyfile(trim_tmp, candidate)
                    return prefix, trunc_key
            # 4 consecutive inflated draws -> this prefix is hopeless, move to a shorter one
            # (do not burn more GPU; short prefixes fit on any draw).
            if capped_streak >= 4:
                break
    return None


def longest_fitting_prefix(measure_frames, words: list[str], target_frames: int, minimum_frames):
    """Binary search tiền tố dài nhất vừa khung (số từ tăng thì frames tăng).
    measure_frames(prefix) -> frames hoặc None. Trả (prefix, frames) hoặc None.
    Pure (measure inject được), test được không cần model."""
    best = None
    lo, hi = 1, len(words)
    while lo <= hi:
        mid = (lo + hi) // 2
        prefix = " ".join(words[:mid])
        try:
            frames = measure_frames(prefix)
            if frames is None or minimum_frames(prefix) > frames:
                frames = None
        except Exception:
            frames = None
        if frames is not None and frames <= target_frames:
            best = (prefix, frames)
            lo = mid + 1
        else:
            hi = mid - 1
    return best


def load_clip(path: Path, key: str, cue: dict, minimum_generated_frames: int = 1):
    # natural_sample is used in both branches (silence-fallback and ordinary native), so
    # it is initialised before the branch split: without that the native branch hits
    # UnboundLocalError when revalidating a cache hit (the editor-tts job died at exactly this spot 08:56 on 27/09).
    natural_sample = cue.get("timing_source") == "sample"
    try:
        record = json.loads(path.with_suffix(".json").read_text(encoding="utf-8"))
        if record["key"] != key or record["sha256"] != sha256(path):
            return None
        if record.get("fit_method") == "silence-fallback":
            # Exact-length silence has no speech for read_wav to integrity-check
            # (it rejects silent PCM); verify the container shape instead.
            try:
                with wave.open(str(path), "rb") as source:
                    silent_ok = ((source.getnchannels(), source.getsampwidth(), source.getframerate())
                                 == (1, 2, SAMPLE_RATE) and source.getnframes() == record["frames"])
            except wave.Error:
                return None
            _, target_frames = cue_window(cue)
            if (silent_ok and record.get("status") == "review"
                    and record["frames"] == target_frames and record["target_frames"] == target_frames
                    and record["timing_source"] == cue["timing_source"]
                    and native_record_valid(record, target_frames, natural_sample, minimum_generated_frames)):
                return record
            return None
        samples = read_wav(path)
        duration = len(samples) / SAMPLE_RATE
        _, target_frames = cue_window(cue)
        if not math.isfinite(record["raw_duration"]) or record["raw_duration"] <= 0:
            return None
        if (record["frames"] != len(samples) or record["target_frames"] != target_frames
                or record["timing_source"] != cue["timing_source"] or len(samples) > target_frames
                or len(samples) != target_frames
                or not math.isfinite(record["fitted_duration"]) or abs(record["fitted_duration"] - duration) > 1e-9
                or not native_record_valid(record, target_frames, natural_sample, minimum_generated_frames)):
            return None
        return record
    except (OSError, ValueError, KeyError, TypeError, wave.Error):
        return None


ATEMPO_METHOD = "vieneu-atempo"


def strip_emotion_tags(text: str) -> str:
    # VieNeu speaks emotion tags aloud ([cười] is a real laugh mid-sentence!). Only
    # VieNeu's documented control tags are stripped; other brackets ([nhạc nền],
    # [Phần 1]...) are kept and read as ordinary text.
    return re.sub(r"\[(cười|thở dài|hắng giọng)\]", "", text)


def fold_slug(text: str) -> str:
    """Gập label SDK thành slug ASCII (khớp TtsVoiceCatalog.FoldSlug C#):
    NFD, bỏ dấu tổ hợp, thường, space/gạch nối thành gạch dưới. Pure."""
    import unicodedata
    out = []
    for character in (text or "").strip().replace("Đ", "D").replace("đ", "d"):
        decomposed = unicodedata.normalize("NFD", character)
        for part in decomposed:
            if unicodedata.category(part) == "Mn":
                continue
            out.append("_" if part in (" ", "-") else part.lower())
    return "".join(out)


def resolve_preset_label(available: list, requested: str) -> str:
    """Khớp voice id C# (vieneu_<slug> hoặc label gốc, không phân biệt hoa/thường).
    Pure, test được không cần model."""
    want = (requested or "").strip()
    slug = want[7:] if want.lower().startswith("vieneu_") else want
    for label in available:
        if isinstance(label, str) and fold_slug(label) == fold_slug(slug):
            return label
    for label in available:
        if isinstance(label, str) and label.strip().lower() == want.lower():
            return label
    raise ValueError(f"VieNeu preset voice không có: {requested}")


class VieneuVoice:
    # Shim so the fit machinery (written for Piper) runs without touching call-sites:
    # config.length_scale is always 1.0 (VieNeu's only natural pace).
    def __init__(self, engine, label: str):
        import types
        self.engine = engine
        self.label = label
        self.config = types.SimpleNamespace(length_scale=1.0, sample_rate=VIENEU_SAMPLE_RATE)


def synthesize_cue(voice, text: str, temporary: Path, length_scale: float = 1.0) -> None:
    import numpy as np
    import soxr
    # length_scale keeps its signature so call-sites stay untouched; VieNeu has only a
    # natural pace, so this parameter is deliberately ignored.
    audio = voice.engine.infer(text, voice=voice.label, apply_watermark=False)
    samples = np.asarray(audio, dtype=np.float32).reshape(-1)
    if samples.size == 0:
        raise ValueError("VieNeu returned no audio")
    resampled = soxr.resample(samples, VIENEU_SAMPLE_RATE, SAMPLE_RATE)
    pcm = (np.clip(resampled, -1.0, 1.0) * 32767).astype(np.int16)
    with wave.open(str(temporary), "wb") as output:
        output.setnchannels(1)
        output.setsampwidth(2)
        output.setframerate(SAMPLE_RATE)
        output.writeframes(pcm.tobytes())


def synthesize_cue_batch(voice, jobs: list, batch_size: int = BATCH_SYNTH_SIZE) -> None:
    """Tổng hợp hàng loạt probe tự nhiên bằng 1 infer_batch mỗi chunk.

    jobs: [(text, temporary Path)]. Tham số SDK y hệt synthesize_cue
    (voice label + apply_watermark=False, còn lại mặc định) nên phân phối
    audio tương đương đọc đơn; record nào cũng tự đo lại nên cache vẫn đúng.
    Lỗi bất kỳ (kể cả OOM) -> raise để caller rớt về đọc đơn từng cue.
    """
    import numpy as np
    import soxr
    size = max(1, int(batch_size))
    for start in range(0, len(jobs), size):
        chunk = jobs[start:start + size]
        audios = voice.engine.infer_batch(
            [text for text, _ in chunk], voice=voice.label, apply_watermark=False,
            batch_size=size)
        if len(audios) != len(chunk):
            raise RuntimeError("VieNeu batch trả thiếu audio")
        for (text, temporary), audio in zip(chunk, audios):
            samples = np.asarray(audio, dtype=np.float32).reshape(-1)
            if samples.size == 0:
                raise ValueError("VieNeu returned no audio")
            resampled = soxr.resample(samples, VIENEU_SAMPLE_RATE, SAMPLE_RATE)
            pcm = (np.clip(resampled, -1.0, 1.0) * 32767).astype(np.int16)
            with wave.open(str(temporary), "wb") as output:
                output.setnchannels(1)
                output.setsampwidth(2)
                output.setframerate(SAMPLE_RATE)
                output.writeframes(pcm.tobytes())


def preload_probes(voice, cues, texts, run_root: Path, clip_root: Path,
                   worker_sha: str, emit, items: list,
                   batch_size: int = BATCH_SYNTH_SIZE) -> dict:
    """Batch probe tự nhiên cho cue chưa cache (30/09, cứng 10/10).

    items: [(global_index, cue, text)]. Chỉ probe candidate chính của đọc
    đơn, bỏ qua cue đã có sidecar+wav trong clip cache (retry nóng tốn 0
    synth). Trả {str(candidate run path): True} để fit_cue tái dùng đúng 1
    lần probe đầu; chunk nào lỗi (OOM/text lạ) thì CHỈ chunk đó rớt về đọc
    đơn — các chunk khác giữ nguyên (trước đây 1 chunk lỗi là cả pre-pass
    hàng nghìn cue về đơn hết mà không để lại dấu vết).
    Bucket theo độ dài chữ (ngắn đi chung) để câu ngắn khỏi chờ câu dài
    trong cùng batch (batch-and-wait) và giảm đỉnh VRAM. Mỗi chunk xong bắn
    event attempt để thanh tiến trình chạy trong lúc pre-pass (không là UI
    đứng yên dễ tưởng treo). Module-level để unit-test được (không còn là
    closure trong main).
    """
    jobs = []
    seen: dict = {}
    total = len(cues)
    for position, cue, text in items:
        try:
            key = cache_identity(cue, text, worker_sha)
        except (KeyError, TypeError, ValueError):
            continue
        if key in seen:
            seen[key].append(position)
            continue
        seen[key] = [position]
        if (clip_root / (key + ".wav")).is_file() and (clip_root / (key + ".json")).is_file():
            continue
        jobs.append((position, text, run_root / (key + "-native.wav")))
    if not jobs:
        return {}
    # Stable bucketing by length (and original position so the batch order repeats
    # identically every run): even chunks -> less wasted padding + lower VRAM peak than
    # chunks mixing long and short.
    jobs.sort(key=lambda job: (len(job[1]), job[0]))
    preloaded: dict = {}
    size = max(1, int(batch_size))
    for start in range(0, len(jobs), size):
        chunk = jobs[start:start + size]
        try:
            synthesize_cue_batch(voice, [(text, path) for _, text, path in chunk], size)
        except Exception as error:
            # Only this chunk re-reads singly; the half-written wav of a failed chunk never
            # enters the dict, so fit_cue overwrites it cleanly and it is never reused by mistake.
            emit({"event": "notice", "code": "batch-probe-chunk-fallback",
                  "message": (f"Pre-batch {start // size + 1} ({len(chunk)} cue) lỗi "
                              f"({str(error)[:120]}); các cue này đọc đơn, chunk khác không ảnh hưởng.")})
            continue
        for position, _, path in chunk:
            preloaded[str(path)] = True
            emit({"event": "attempt", "index": position + 1, "total": total,
                  "id": cues[position]["id"], "attempt": 1, "max_attempts": 1,
                  "timing_source": "batch-probe", "length_scale": 1.0})
    return preloaded


def next_length_scale(scale: float, frames: int, aim_frames: int, base: float,
                      lower: float | None, upper: float | None) -> float:
    if (not math.isfinite(scale) or scale <= 0 or not math.isfinite(base) or base <= 0
            or frames <= 0 or aim_frames <= 0):
        raise ValueError("Invalid model-native duration measurement")
    # Unlike playback tempo, a SMALLER length_scale asks Piper to speak faster.
    corrected = scale * aim_frames / frames
    if lower is not None and upper is not None and lower < upper and not lower < corrected < upper:
        corrected = (lower + upper) / 2
    return max(base * MIN_RATE_SCALE, min(base * MAX_RATE_SCALE, corrected))


def pad_exact_clip(source_path: Path, output_path: Path, target_frames: int):
    # Copy all model-produced PCM unchanged and fill only the unused tail with
    # silence. This is not stretching, resampling or cutting spoken audio.
    with wave.open(str(source_path), "rb") as source:
        frames = source.getnframes()
        data = source.readframes(frames)
        if not 0 <= target_frames - frames or frames <= 0 or len(data) != frames * 2:
            raise ValueError("Refusing to cut or alter model-produced speech")
        with wave.open(str(output_path), "wb") as output:
            output.setparams(source.getparams())
            output.writeframes(data)
            output.writeframes(b"\0\0" * (target_frames - frames))


def trim_trailing_silence_to_fit(source_path: Path, output_path: Path, target_frames: int) -> int:
    # Piper may append sentence-final silence after punctuation. Remove it only
    # when every sample beyond the target is below -60 dBFS and the target keeps
    # an additional 50 ms guard after the final active sample.
    import numpy as np
    with wave.open(str(source_path), "rb") as source:
        frames = source.getnframes()
        data = source.readframes(frames)
        params = source.getparams()
    if frames <= target_frames or target_frames <= 0 or len(data) != frames * 2:
        return 0
    samples = np.frombuffer(data, dtype="<i2").astype(np.int32)
    active = np.flatnonzero(np.abs(samples) > SILENCE_AMPLITUDE)
    if len(active) == 0 or int(active[-1]) + 1 + SILENCE_GUARD_FRAMES > target_frames:
        return 0
    with wave.open(str(output_path), "wb") as output:
        output.setparams(params)
        output.writeframes(data[:target_frames * 2])
    return frames - target_frames


def trim_trailing_silence(source_path: Path, output_path: Path, guard_frames: int) -> int:
    # P1 (02/10): strip absolute silence from BOTH ends. The tail guard keeps its
    # parameter (protecting decay/reverb); the head guard is fixed at 5ms -- only samples
    # below -60dBFS are cut so the voice (even a soft attack) never touches it. Returns
    # the total number of removed samples; the output file is the speech intact minus
    # head/tail silence, so every validator frames equation (source-trimmed==generated) still holds.
    import numpy as np
    with wave.open(str(source_path), "rb") as source:
        frames = source.getnframes()
        data = source.readframes(frames)
        params = source.getparams()
    if frames <= 0 or len(data) != frames * 2:
        raise ValueError("VieNeu group clip is empty or truncated")
    samples = np.frombuffer(data, dtype="<i2").astype(np.int32)
    active = np.flatnonzero(np.abs(samples) > SILENCE_AMPLITUDE)
    if len(active) == 0:
        raise ValueError("VieNeu group clip has no active speech")
    if type(guard_frames) is not int or guard_frames < 0:
        raise ValueError("Invalid sentence-group silence guard")
    lead_cut = max(0, int(active[0]) - LEADING_SILENCE_GUARD_FRAMES)
    retained_end = min(frames, int(active[-1]) + 1 + guard_frames)
    # Do NOT return early when there is nothing to cut: the caller (the escalate
    # repeat-cut branch) uses the output file UNCONDITIONALLY -- returning early leaves
    # stale bytes from the previous trim and copyfile would copy the old audio into the
    # candidate, so fit measures wrong (case #93b45d9a: copied 1.587s instead of
    # 0.65s). Always write so the output equals the trimmed input; one extra file write is negligible.
    with wave.open(str(output_path), "wb") as output:
        output.setparams(params)
        output.writeframes(data[lead_cut * 2:retained_end * 2])
    return frames - (retained_end - lead_cut)


def atempo_filter(factor: float, floor: float = _MIN_TEMPO_FACTOR) -> str:
    # Default floor (2026-09-24): a factor at/below natural pace means "no speedup",
    # not an error. The stretch path passes floor=0.5 (the single-stage limit of ffmpeg;
    # valid stages are 0.5..100).
    if not math.isfinite(factor) or factor <= floor:
        # The fast path keeps its old meaning (passthrough ~1.0, no raise so the group retry
        # loop keeps converging); the slow path floors at 0.5.
        factor = _MIN_TEMPO_FACTOR if floor >= 1 else max(floor, 0.5)
    stages = []
    remaining = factor
    while remaining > 2:
        stages.append(2.0)
        remaining /= 2
    while 0 < remaining < 0.5:
        stages.append(0.5)
        remaining /= 0.5
    if (remaining > 1 + 1e-9) or (0 < remaining < 1 - 1e-9):
        stages.append(remaining)
    if not stages:
        stages.append(1.0)
    return ",".join(f"atempo={stage:.12g}" for stage in stages)


# Repeat cut (03/10): when every render is doubled, cut the first instance at the
# silence gap inside the file (verified: cutting “Vâng! Vâng!” at 0.36s yields an
# intact “Vâng!"). Cut only on a real gap (<-50dBFS, at least 60ms wide, within
# 15%-85% of the file), snapped to a zero crossing within 3ms plus a 64-sample fade
# to prevent clicking. Raise when the conditions are not met so the caller tries something else.
def cut_first_instance(source_path, output_path, minimum_frames: int) -> int:
    import numpy as np
    with wave.open(str(source_path), "rb") as source:
        frames = source.getnframes()
        sample_rate = source.getframerate()
        data = source.readframes(frames)
        params = source.getparams()
    if frames <= 0 or len(data) != frames * 2 or sample_rate <= 0:
        raise ValueError("repeat-cut input rỗng")
    samples = np.frombuffer(data, dtype="<i2").astype(np.float32)
    if float(np.max(np.abs(samples))) <= 0:
        raise ValueError("repeat-cut input câm")
    cell = max(1, sample_rate // 100)
    nb = len(samples) // cell
    if nb < 10:
        raise ValueError("repeat-cut input quá ngắn")
    env = np.abs(samples[:nb * cell].reshape(nb, cell)).mean(axis=1)
    thresh = float(np.max(env)) * 10 ** (-50 / 20)
    need = max(1, int(0.06 * sample_rate / cell))
    best_len, best_at = 0, -1
    run = 0
    for i, v in enumerate(env):
        if v < thresh:
            run += 1
            start_frac = (i - run + 1) * cell / frames
            end_frac = (i + 1) * cell / frames
            if run >= need and start_frac > 0.15 and end_frac < 0.85 and run > best_len:
                best_len, best_at = run, i - run + 1
        else:
            run = 0
    if best_at < 0:
        raise ValueError("repeat-cut không thấy khe im giữa file")
    cut_sample = best_at * cell
    span = max(1, int(0.003 * sample_rate))
    lo = max(1, cut_sample - span)
    hi = min(len(samples) - 1, cut_sample + span)
    seg = samples[lo:hi + 1]
    cross = np.flatnonzero(seg[1:] * seg[:-1] <= 0)
    if len(cross):
        cut_sample = lo + 1 + int(cross[len(cross) // 2])
    fade = min(64, cut_sample)
    if fade > 1:
        samples[cut_sample - fade:cut_sample] *= np.linspace(1.0, 0.0, fade, dtype=np.float32)
    if cut_sample < minimum_frames:
        raise ValueError("repeat-cut miếng đầu quá ngắn")
    with wave.open(str(output_path), "wb") as output:
        output.setparams(params)
        output.writeframes(samples[:cut_sample].astype(np.int16).tobytes())
    return cut_sample


# Repeat-read guard (03/10): the model often doubles short sentences (“Vâng! Vâng!”,
# "Vao di! Vao di!" -- confirmed by replaying the audio to Whisper). Two detection
# rules: (1) autocorrelation envelope peak >=0.30 at a lag of about half the
# duration (clear doubling); (2) the nuclear rule: an energy peak above the word
# count plus a far gap >=0.35s (also catches differently-prosodied doubling that ACF
# misses; the “Vâng! Vâng!” case had an ACF peak of only 0.10). Calibrated on real
# samples: a single sentence never satisfies both. Audio <0.4s is not evaluated (noisy ACF). numpy-only, ~1ms.
REP_PEAK_THRESHOLD = 0.30
REP_MIN_DURATION_SECONDS = 0.6
REP_FAR_GAP_SECONDS = 0.35


def text_self_repeats(text: str) -> bool:
    """Chữ TỰ lặp ("không thể nào! không thể nào!", "ây da ×3", "Vâng! Vâng!"):
    audio giống nhau ở 2 nửa là ĐÚNG (đọc đủ chữ), không phải model nhân đôi.
    Tắt veto/cắt nội dung cho các cue này — chỉ còn luật dài-bất-thường và
    trần pace xử lý. So sánh token thường (kệ hoa/dấu câu). Pure, test được."""
    units = [token.lower() for token in re.findall(r"\w+", text or "", flags=re.UNICODE)
             if any(character.isalnum() for character in token)]
    return len(units) > 1 and len(set(units)) < len(units)


def speech_repeats(path, max_words: int | None = None) -> bool:
    """Audio có bị nhân đôi không (ACF envelope + luật hạt nhân).

    Chỉ xét audio ≥0.6s: nhân đôi thật cần ≥2 instance, mỗi instance không
    thể dưới ~0.3s — bản ngắn hơn không thể là lặp có nghĩa, veto sẽ chỉ
    giết oan bản sạch (vụ prefix 'Không thể' 0.49s bị veto oan). Hiệu chuẩn
    03/10: câu đơn ACF peak ≤0.17, nhân đôi rõ 0.43-0.80; luật hạt nhân
    (đỉnh vượt số chữ + khe xa ≥0.35s) bắt thêm nhân đôi khác ngữ điệu.
    max_words=None thì chỉ dùng ACF.
    """
    import numpy as np
    with wave.open(str(path), "rb") as source:
        frames = source.getnframes()
        sample_rate = source.getframerate()
        data = source.readframes(frames)
    if frames <= 0 or len(data) != frames * 2 or sample_rate <= 0:
        return False
    duration = frames / sample_rate
    if duration < REP_MIN_DURATION_SECONDS:
        return False
    samples = np.abs(np.frombuffer(data, dtype="<i2").astype(np.float32))
    peak = float(np.max(samples))
    if peak <= 0:
        return False
    if max_words is not None and max_words > 0:
        fine = max(1, sample_rate // 100)
        nb = len(samples) // fine
        if nb >= 8:
            env = samples[:nb * fine].reshape(nb, fine).mean(axis=1) / (peak + 1e-9)
            loc = [i for i in range(nb) if env[i] >= 0.25
                   and (i == 0 or env[i] >= env[i - 1]) and (i == nb - 1 or env[i] >= env[i + 1])]
            merged: list[int] = []
            for i in loc:
                if merged and (i - merged[-1]) * fine / sample_rate < 0.12:
                    if env[i] > env[merged[-1]]:
                        merged[-1] = i
                else:
                    merged.append(i)
            times = [i * fine / sample_rate for i in merged]
            far = sum(1 for a, b in zip(times, times[1:]) if b - a >= REP_FAR_GAP_SECONDS)
            if len(merged) > max_words and far >= 1:
                return True
    envelope_size = max(1, sample_rate // 62)
    count = len(samples) // envelope_size
    if count < 12:
        return False
    envelope = samples[:count * envelope_size].reshape(count, envelope_size).mean(axis=1)
    envelope = envelope / (float(np.max(envelope)) + 1e-9)
    centered = envelope - float(envelope.mean())
    denom = float((centered ** 2).sum())
    if denom <= 1e-9:
        return False
    full = np.correlate(centered, centered, mode="full")[count - 1:] / denom
    low = max(2, int(0.30 * duration * sample_rate / envelope_size))
    high = min(len(full) - 1, int(0.52 * duration * sample_rate / envelope_size))
    if high <= low:
        return False
    return bool(float(np.max(full[low:high + 1])) >= REP_PEAK_THRESHOLD)


# U1 (03/10, speed): in-process atempo via torchaudio's phase vocoder instead of
# spawning ffmpeg.exe every time (a Windows spawn costs ~0.1-0.3s, times tens of
# thousands of calls for 16k cues). Same floor/stage semantics as atempo_filter;
# the caller re-measures the output length as before, so the retry/validation loops
# are unchanged. Any error falls back to FFmpeg (the old path is kept for A/B comparison).
_TEMPO_N_FFT = 1024
_TEMPO_HOP = 256


def _phase_vocoder_rate(factor: float, floor: float) -> float:
    if not math.isfinite(factor) or factor <= floor:
        factor = _MIN_TEMPO_FACTOR if floor >= 1 else max(floor, 0.5)
    return factor


def apply_tempo_inprocess(source_path: Path, output_path: Path, factor: float,
                          floor: float = _MIN_TEMPO_FACTOR) -> int:
    import numpy as np
    import torch
    from torchaudio.functional import phase_vocoder
    rate = _phase_vocoder_rate(factor, floor)
    with wave.open(str(source_path), "rb") as source:
        frames = source.getnframes()
        data = source.readframes(frames)
        params = source.getparams()
    if frames <= 0 or len(data) != frames * 2:
        raise ValueError("Tempo input rỗng hoặc hỏng")
    samples = np.frombuffer(data, dtype="<i2").astype(np.float32) / 32768.0
    waveform = torch.from_numpy(samples)
    window = torch.hann_window(_TEMPO_N_FFT)
    spec = torch.stft(waveform, n_fft=_TEMPO_N_FFT, hop_length=_TEMPO_HOP,
                      win_length=_TEMPO_N_FFT, window=window, center=True,
                      return_complex=True)
    n_freq = _TEMPO_N_FFT // 2 + 1
    phase_advance = torch.linspace(0, math.pi * _TEMPO_HOP, n_freq).unsqueeze(1)
    stretched = phase_vocoder(spec, rate=rate, phase_advance=phase_advance)
    target_len = max(1, int(round(frames / rate)))
    rebuilt = torch.istft(stretched, n_fft=_TEMPO_N_FFT, hop_length=_TEMPO_HOP,
                          win_length=_TEMPO_N_FFT, window=window, center=True,
                          length=target_len)
    pcm = np.clip(np.rint(rebuilt.numpy() * 32767), -32768, 32767).astype(np.int16)
    temporary = output_path.with_name(output_path.name + ".tmp.wav")
    temporary.unlink(missing_ok=True)
    with wave.open(str(temporary), "wb") as output:
        output.setparams(params)
        output.writeframes(pcm.tobytes())
    out_frames = len(read_wav(temporary))
    temporary.replace(output_path)
    return out_frames


def apply_atempo(ffmpeg: Path, source_path: Path, output_path: Path, factor: float,
                 floor: float = _MIN_TEMPO_FACTOR) -> int:
    try:
        return apply_tempo_inprocess(source_path, output_path, factor, floor)
    except Exception:
        pass
    temporary = output_path.with_name(output_path.name + ".tmp.wav")
    temporary.unlink(missing_ok=True)
    completed = subprocess.run(
        [str(ffmpeg), "-hide_banner", "-loglevel", "error", "-y", "-i", str(source_path),
          "-af", atempo_filter(factor, floor), "-ac", "1", "-ar", str(SAMPLE_RATE),
          "-c:a", "pcm_s16le", str(temporary)],
        capture_output=True, text=True, encoding="utf-8", errors="replace",
        creationflags=_NO_WINDOW)
    if completed.returncode != 0 or not temporary.is_file():
        temporary.unlink(missing_ok=True)
        raise RuntimeError("FFmpeg atempo failed: " + completed.stderr.strip()[-1000:])
    frames = len(read_wav(temporary))
    temporary.replace(output_path)
    return frames


def tempo_fit_clip(ffmpeg: Path, source_path: Path, final_path: Path, target_frames: int,
                   native_reference_frames: int, run_root: Path, key: str,
                   max_speed: float = HARD_MAX_SPEED):
    input_frames = len(read_wav(source_path))
    # Pace ceiling (02/10): 1.5x normally, 2.0x for the micro-band when the window is
    # <1.1s. A need above the ceiling raises immediately, saving synth/GPU time (the caller
    # falls through to condense/truncate along the no-silence chain).
    cap_speed = max_speed if max_speed != HARD_MAX_SPEED else cap_speed_for(target_frames)
    cap_factor = (MICRO_MAX_FACTOR if cap_speed == MICRO_MAX_SPEED
                  else HARD_MAX_FACTOR if cap_speed == HARD_MAX_SPEED else cap_speed + 0.01)
    if input_frames / max(1, target_frames) > cap_speed:
        raise RuntimeError(
            f"Cue cần tăng tốc {input_frames / max(1, target_frames):.2f}x vượt trần {cap_speed}x")
    desired_frames = max(1, math.floor(target_frames * FIT_HEADROOM))
    factor = max(_MIN_TEMPO_FACTOR, input_frames / desired_frames * TEMPO_RETRY_MARGIN)
    for attempt in range(1, MAX_TEMPO_ATTEMPTS + 1):
        if factor > cap_factor:
            raise RuntimeError(
                f"atempo chạm trần {cap_speed}x mà vẫn chưa vừa khung")
        tempo_path = run_root / f"{key}-tempo-{attempt}.wav"
        generated = apply_atempo(ffmpeg, source_path, tempo_path, factor)
        actual_speed = native_reference_frames / generated
        if generated <= target_frames and MIN_ACTUAL_SPEED <= actual_speed <= MAX_ACTUAL_SPEED:
            if generated < target_frames:
                pad_exact_clip(tempo_path, final_path, target_frames)
                output_path = final_path
            else:
                output_path = tempo_path
            return output_path, input_frames, generated, input_frames / generated, attempt, actual_speed
        factor *= max(generated / target_frames,
                      MIN_ACTUAL_SPEED / max(actual_speed, 1e-9)) * TEMPO_RETRY_MARGIN
    raise RuntimeError("FFmpeg atempo không thể khớp đủ nguyên câu vào timecode")


def tempo_stretch_clip(ffmpeg: Path, source_path: Path, final_path: Path, target_frames: int,
                       native_reference_frames: int, run_root: Path, key: str):
    """Kéo dài cue rộng cho đầy khung (pace chậm, giữ cao độ, tối đa 1.4x
    thời lượng). Vượt trần (pace phải dưới ~0.71x) thì raise để caller pad im
    như cũ. Shape record y hệt atempo nhanh (actual/tempo <1), review luôn.
    """
    input_frames = len(read_wav(source_path))
    if input_frames <= 0:
        raise ValueError("Không có PCM để stretch")
    if input_frames / max(1, target_frames) < SLOW_MIN_FACTOR:
        raise RuntimeError(
            f"Cue cần đọc chậm {target_frames / max(1, input_frames):.2f}x vượt trần 1.4x thời lượng")
    factor = min(1.0 - 1e-9, input_frames / max(1, target_frames) / TEMPO_RETRY_MARGIN)
    for attempt in range(1, MAX_TEMPO_ATTEMPTS + 1):
        if factor < SLOW_MIN_FACTOR:
            raise RuntimeError("stretch chạm trần 1.4x thời lượng mà vẫn chưa đầy khung")
        tempo_path = run_root / f"{key}-stretch-{attempt}.wav"
        generated = apply_atempo(ffmpeg, source_path, tempo_path, factor, floor=0.5)
        actual_speed = native_reference_frames / generated
        if generated <= target_frames and SLOW_MIN_SPEED <= actual_speed <= 1.0:
            if generated < target_frames:
                pad_exact_clip(tempo_path, final_path, target_frames)
                output_path = final_path
            else:
                output_path = tempo_path
            return output_path, input_frames, generated, input_frames / generated, attempt, actual_speed
        # Not yet filling the window while pace is still above the slow ceiling -> stretch
        # further (the factor shrinks each time); once pace reaches the ceiling, raise on the next iteration.
        factor = max(factor / (TEMPO_RETRY_MARGIN * max(generated / max(target_frames, 1), 1e-9)
                               if generated < target_frames else 1.0), 0.0)
        if factor <= 0 or not math.isfinite(factor):
            raise RuntimeError("stretch không hội tụ")
    raise RuntimeError("FFmpeg stretch không thể đầy khung ở pace cho phép")


def sentence_groups(cues: list[dict], texts: list[str]) -> list[list[int]]:
    if len(cues) != len(texts):
        raise ValueError("TTS sentence grouping input mismatch")
    groups, current = [], []
    for index, cue in enumerate(cues):
        if current:
            previous = cues[current[-1]]
            gap_frames = round(cue["cue_start"] * SAMPLE_RATE) - round(previous["cue_end"] * SAMPLE_RATE)
            group_start = round(cues[current[0]]["cue_start"] * SAMPLE_RATE)
            candidate_end = round(cue["cue_end"] * SAMPLE_RATE)
            if (previous["timing_source"] == "sample" or cue["timing_source"] == "sample"
                    or gap_frames < -round(.25 * SAMPLE_RATE) or gap_frames > MAX_GROUP_GAP_FRAMES
                    or len(current) >= MAX_GROUP_CUES
                    or candidate_end - group_start > MAX_GROUP_DURATION_FRAMES):
                groups.append(current)
                current = []
        current.append(index)
    if current:
        groups.append(current)
    return groups


def group_plan_identity(cues: list[dict], texts: list[str], worker_sha: str) -> str:
    value = [VOICE_REVISION, PACKAGES, worker_sha,
             [[cue["id"], cue["cue_start"], cue["cue_end"], text] for cue, text in zip(cues, texts)]]
    return hashlib.sha256(json.dumps(value, ensure_ascii=False, sort_keys=True).encode("utf-8")).hexdigest()


def load_group_plan(path: Path, key: str, cues: list[dict]) -> list[dict] | None:
    try:
        plan = json.loads(path.read_text(encoding="utf-8"))
        if plan.get("schema") != 1 or plan.get("key") != key or plan.get("timing_source") != "sentence-group":
            return None
        windows = plan["windows"]
        if len(windows) != len(cues):
            return None
        cursor = round(cues[0]["cue_start"] * SAMPLE_RATE)
        group_end = round(cues[-1]["cue_end"] * SAMPLE_RATE)
        result = []
        for cue, window in zip(cues, windows):
            if (window["id"] != cue["id"] or window["start_sample"] != cursor
                    or type(window["target_frames"]) is not int or window["target_frames"] <= 0):
                return None
            active = dict(cue)
            active["voice_start"] = cursor / SAMPLE_RATE
            cursor += window["target_frames"]
            active["voice_end"] = cursor / SAMPLE_RATE
            active["timing_source"] = "sentence-group"
            result.append(active)
        return result if cursor == group_end else None
    except (OSError, ValueError, KeyError, TypeError):
        return None


def group_windows(cue_starts: list[int], cue_ends: list[int], retained: list[int]):
    """Minimal tiling where no cue starts before its own subtitle appears.

    voice_start[0] sits exactly on the group start; every later cue starts at
    max(previous speech end, its own cue_start), so gaps stay silent and a
    voice never reads ahead of its subtitle. Padding absorbs the slack, which
    is why the tiling is fully determined by the retained speech lengths.
    Returns the voice-start samples, or None when even this tightest packing
    overflows the group end (caller must speed up or fall back to silence).
    """
    starts = []
    cursor = cue_starts[0]
    for index in range(len(retained)):
        if cursor < cue_starts[index]:
            cursor = cue_starts[index]
        starts.append(cursor)
        cursor += retained[index]
    if cursor > cue_ends[-1]:
        return None
    return starts


def max_tiling_lag(cue_starts: list[int], starts) -> int:
    """Tre dat muon nhat cua thanh vien nhom so voi gio sub (frames).

    Cascade don forward don no tu cac thanh vien truoc; vuot
    GROUP_MAX_LAG_FRAMES la tang tren phai tang atempo (trong tran) hoac
    raise tach don — khong bao gio nhan tiling tre nhu cu (vụ 07/10: tre
    4s). starts None (khong vua) tra so rat lon de caller tang toc/raise.
    """
    try:
        if not starts:
            return 10 ** 18
        worst = 0
        for want, got in zip(cue_starts, starts):
            if got is None or want is None:
                return 10 ** 18
            late = int(got) - int(want)
            if late > worst:
                worst = late
        return worst
    except (TypeError, ValueError, IndexError):
        return 10 ** 18


def _tiling_growth(cue_starts: list[int], cue_ends: list[int], retained: list[int]) -> float:
    """Speedup needed so the tightest cascade packing fits the group end.

    Returns 1.0 when the tiling already fits. Replicates the group_windows
    cascade to measure the overflow, then asks the speech *after the last
    silence gap* to absorb it: earlier speech is shielded by gaps (shrinking
    it never moves the tail), so sizing by the grand total undershoots and
    creeps 1% per attempt past the attempt budget. Capped per attempt; the
    retry loop re-measures every round.
    """
    total = sum(retained)
    if total <= 0:
        return 1.0
    cursor = cue_starts[0]
    tail = 0
    for index in range(len(retained)):
        if cursor < cue_starts[index]:
            cursor = cue_starts[index]
            tail = 0
        tail += retained[index]
        cursor += retained[index]
    overflow = cursor - cue_ends[-1]
    if overflow <= 0 or tail <= 0:
        return 1.0
    room = tail - overflow
    if room > 0:
        return min(tail / room, 4.0)
    room_total = total - overflow
    if room_total > 0:
        return min(total / room_total, 4.0)
    return 2.0


def synthesize_sentence_group(voice, cues: list[dict], texts: list[str], run_root: Path,
                              clip_root: Path, worker_sha: str, plan_path: Path, plan_key: str,
                              ffmpeg: Path, on_attempt):
    base = float(voice.config.length_scale)
    if not math.isfinite(base) or base <= 0:
        raise ValueError("Invalid length_scale in verified model config")
    group_start = round(cues[0]["cue_start"] * SAMPLE_RATE)
    group_end = round(cues[-1]["cue_end"] * SAMPLE_RATE)
    group_frames = group_end - group_start
    if group_frames <= 0:
        raise ValueError("Sentence group has no source time")
    cue_starts = [round(cue["cue_start"] * SAMPLE_RATE) for cue in cues]
    cue_ends = [round(cue["cue_end"] * SAMPLE_RATE) for cue in cues]
    scale = base
    raw_frames = [0] * len(cues)
    selected = None
    native_retained_frames = 0
    last_total_retained = 0
    # VieNeu: a single pass at natural pace only (no scale loop -- re-measuring at the
    # same pace only burns synth). If it does not fit, drop to atempo below as before.
    attempt = 1
    measurements = []
    # Natural batch probe for the whole group (30/09): N independent synths merged into
    # ceil(N/8) infer_batch calls. A batch error (including OOM) falls back to reading each cue singly, as before.
    raw_paths = [run_root / f"group-{plan_key}-{local_index}-native.wav"
                 for local_index in range(len(cues))]
    try:
        synthesize_cue_batch(voice, [(text, raw_path) for text, raw_path in zip(texts, raw_paths)])
        batched = True
    except Exception:
        batched = False
    for local_index, (cue, text) in enumerate(zip(cues, texts)):
        on_attempt(local_index, attempt, scale)
        raw_path = raw_paths[local_index]
        trimmed_path = run_root / f"group-{plan_key}-{local_index}-trimmed.wav"
        if not batched:
            synthesize_cue(voice, text, raw_path, scale)
        source_frames = len(read_wav(raw_path))
        raw_frames[local_index] = source_frames
        guard = SILENCE_GUARD_FRAMES if local_index == len(cues) - 1 else round(.01 * SAMPLE_RATE)
        trimmed = trim_trailing_silence(raw_path, trimmed_path, guard)
        retained = source_frames - trimmed
        # Repeat-read guard (03/10): a short member that gets doubled is cut down to its
        # first instance (cheap, certain); only when cutting fails is it re-read singly with
        # one seed. Self-repeating text is skipped (identical halves are correct audio).
        # FIX 05/10 (the C# line 495 case: 8 sentence-group cues whose file was longer than
        # the reported figures): if re-trim after the cut returns 0, trimmed_path holds the
        # CUT render (raw is still the old uncut audio) -- trimmed_path must be used and
        # falling back to raw_path is forbidden. cut_ok=True means the candidate lives in
        # trimmed_path regardless of whether trimmed is 0 or not (only when cut_ok is
        # False, i.e. nothing was cut or a re-synth overwrote raw, may the code fall back
        # to raw according to trimmed).
        cut_ok = False
        if speakable_units(text) <= 8 and not text_self_repeats(text) \
                and speech_repeats(trimmed_path if trimmed else raw_path,
                                   speakable_units(text)):
            cut_ok = False
            try:
                cut_path = run_root / f"group-{plan_key}-{local_index}-cut.wav"
                cut_first_instance(trimmed_path if trimmed else raw_path, cut_path,
                                   minimum_speech_frames(text))
                cut_source = len(read_wav(cut_path))
                cut_trimmed = trim_trailing_silence(cut_path, trimmed_path, guard)
                source_frames, trimmed, retained = cut_source, cut_trimmed, cut_source - cut_trimmed
                raw_frames[local_index] = source_frames
                cut_ok = True
            except Exception:
                cut_ok = False
            if not cut_ok:
                try:
                    import torch
                    torch.manual_seed(int(hashlib.sha256(
                        f"{plan_key}:m{local_index}".encode("utf-8")).hexdigest(), 16) % (2 ** 31))
                    synthesize_cue(voice, text, raw_path, scale)
                    source_frames = len(read_wav(raw_path))
                    raw_frames[local_index] = source_frames
                    trimmed = trim_trailing_silence(raw_path, trimmed_path, guard)
                    retained = source_frames - trimmed
                except Exception:
                    pass
        if retained < minimum_speech_frames(text):
            raise RuntimeError(
                f"VieNeu returned only {retained / SAMPLE_RATE:.3f}s of active speech for a non-empty cue")
        measurements.append({"source_path": trimmed_path if (trimmed or cut_ok) else raw_path,
                             "source_frames": source_frames, "trimmed": trimmed, "retained": retained})
    total_retained = sum(item["retained"] for item in measurements)
    native_retained_frames = total_retained
    actual_speed = 1.0
    last_total_retained = total_retained
    retained_list = [item["retained"] for item in measurements]
    tiling = group_windows(cue_starts, cue_ends, retained_list)
    if (total_retained <= group_frames
            and MIN_ACTUAL_SPEED <= actual_speed <= MAX_ACTUAL_SPEED
            and tiling is not None
            and max_tiling_lag(cue_starts, tiling) <= GROUP_MAX_LAG_FRAMES):
        selected = (attempt, scale, measurements, actual_speed)
    tempo_used = False
    if selected is None:
        desired_frames = max(1, math.floor(group_frames * FIT_HEADROOM))
        # Group pace ceiling (02/10): the same micro-band applies to the group's total window
        # (a micro group <0.8s also gets 2.0x) -- a need above the ceiling raises immediately
        # (falling back to per-cue voice/truncate, never silence).
        group_cap_speed = cap_speed_for(group_frames)
        group_cap_factor = cap_factor_for(group_frames)
        if last_total_retained / max(1, group_frames) > group_cap_speed:
            raise RuntimeError(
                f"Nhóm cue cần tăng tốc {last_total_retained / max(1, group_frames):.2f}x "
                f"vượt trần {group_cap_speed}x")
        tempo_factor = max(_MIN_TEMPO_FACTOR,
                           last_total_retained / desired_frames * TEMPO_RETRY_MARGIN)
        for tempo_attempt in range(1, MAX_TEMPO_ATTEMPTS + 1):
            if tempo_factor > group_cap_factor:
                raise RuntimeError(
                    f"atempo nhóm chạm trần {group_cap_speed}x mà vẫn chưa vừa khung")
            transformed = []
            for local_index, measurement in enumerate(measurements):
                tempo_path = run_root / f"group-{plan_key}-{local_index}-tempo-{tempo_attempt}.wav"
                generated = apply_atempo(ffmpeg, measurement["source_path"], tempo_path, tempo_factor)
                transformed.append(measurement | {
                    "source_path": tempo_path,
                    "retained": generated,
                    "tempo_input": measurement["retained"],
                    "tempo_factor": measurement["retained"] / generated,
                    "tempo_attempts": tempo_attempt,
                })
            transformed_total = sum(item["retained"] for item in transformed)
            actual_speed = native_retained_frames / transformed_total
            retained_list = [item["retained"] for item in transformed]
            tiling = group_windows(cue_starts, cue_ends, retained_list)
            lag_frames = max_tiling_lag(cue_starts, tiling)
            if (transformed_total <= group_frames and MIN_ACTUAL_SPEED <= actual_speed <= MAX_ACTUAL_SPEED
                    and tiling is not None and lag_frames <= GROUP_MAX_LAG_FRAMES):
                selected = (attempt, scale, transformed, actual_speed)
                tempo_used = True
                break
            # Direction-aware retry (2026-09-24): the old update shrank the
            # factor whenever the speech total fit, even when per-cue tiling
            # overflowed across the group's internal gaps. That spiralled the
            # factor below 1.0, atempo_filter raised ValueError, and the whole
            # group fell back to silence. Overflow (total or tiling) or a
            # sub-natural crawl must GROW the factor; ease back only when the
            # tiling already fits but the speech is merely too fast. The floor
            # keeps every attempt a valid speedup, never a slowdown.
            # 07/10: placement lag (MANDATORY at 0) is also a reason to GROW the factor like
            # overflow -- speed up the whole group uniformly (keeping it consistent) until it fits,
            # and raise at the ceiling so the layer above splits into singles (a single can be placed at the right time).
            if transformed_total > group_frames or tiling is None or actual_speed < MIN_ACTUAL_SPEED or lag_frames > GROUP_MAX_LAG_FRAMES:
                growth = max(transformed_total / max(group_frames, 1),
                             MIN_ACTUAL_SPEED / max(actual_speed, 1e-9),
                             _tiling_growth(cue_starts, cue_ends, retained_list),
                             1.0 + 1e-3)
                tempo_factor = max(_MIN_TEMPO_FACTOR,
                                   tempo_factor * growth * TEMPO_RETRY_MARGIN)
            else:
                tempo_factor = max(_MIN_TEMPO_FACTOR,
                                   tempo_factor
                                   * max(transformed_total / max(group_frames, 1),
                                         MIN_ACTUAL_SPEED / max(actual_speed, 1e-9))
                                   * TEMPO_RETRY_MARGIN)
    if selected is None:
        raise RuntimeError(
            f"Nhóm câu {cues[0]['id']}..{cues[-1]['id']} không thể nén đủ nguyên lời vào timecode")

    attempt, scale, measurements, actual_speed = selected
    retained = [item["retained"] for item in measurements]
    starts = group_windows(cue_starts, cue_ends, retained)
    if starts is None:
        raise RuntimeError(
            f"Nhóm cue {cues[0]['id']}..{cues[-1]['id']} không giữ được giờ sub khi xếp voice")
    # Safety net 07/10: every path reaching here must pass the lag filter
    # (natural/atempo with the latency ceiling) -- any deviation raises immediately so the
    # layer above splits into singles placed at the right time; a late record must never leak past the validator.
    lag_frames = max_tiling_lag(cue_starts, starts)
    if lag_frames > GROUP_MAX_LAG_FRAMES:
        raise RuntimeError(
            f"Nhom cue {cues[0]['id']}..{cues[-1]['id']} tre "
            f"{lag_frames / SAMPLE_RATE:.2f}s qua tran {GROUP_MAX_LAG_SECONDS:.1f}s: "
            f"tach don doc dung gio + review")
    entries, windows = [], []
    for index, (cue, text, measurement) in enumerate(zip(cues, texts, measurements)):
        voice_start = starts[index]
        # Fail-open guard: placement must never read ahead of the subtitle.
        # group_windows guarantees this; a violation means the tiling drifted
        # and the group must fall back to silence, never misplace a voice.
        if voice_start < cue_starts[index]:
            raise RuntimeError(
                f"Nhóm cue {cues[0]['id']}..{cues[-1]['id']} đặt voice trước giờ sub")
        window_end = group_end if index == len(cues) - 1 else starts[index + 1]
        target_frames = window_end - voice_start
        padding = target_frames - measurement["retained"]
        if target_frames <= 0 or padding < 0:
            raise RuntimeError("Sentence group could not allocate every complete speech clip")
        active = dict(cue)
        active["voice_start"] = voice_start / SAMPLE_RATE
        active["voice_end"] = window_end / SAMPLE_RATE
        active["timing_source"] = "sentence-group"
        key = cache_identity(active, text, worker_sha)
        cached_path = clip_root / (key + ".wav")
        final_path = measurement["source_path"]
        if padding:
            padded = run_root / (key + "-group-fit.wav")
            pad_exact_clip(final_path, padded, target_frames)
            final_path = padded
        # 28/09, the 2-hour run that failed on 1 cue: the atempo flag belongs to the WHOLE
        # group, but a member that was not really sped up (factor ~1, output == input) yet
        # reports atempo is a lying record -> C# rejects all 17611 cues. Only a truly
        # sped-up member may report atempo.
        member_tempo = (tempo_used and measurement.get("tempo_factor", 0) > 1
                        and measurement.get("tempo_input", 0) > measurement["retained"])
        record = {"raw_duration": raw_frames[len(entries)] / SAMPLE_RATE,
                  "fitted_duration": target_frames / SAMPLE_RATE, "frames": target_frames,
                  "target_frames": target_frames, "timing_source": "sentence-group",
                  "fit_method": ATEMPO_METHOD if member_tempo else FIT_METHOD,
                  "base_length_scale": base, "length_scale": scale,
                  "source_frames": measurement["source_frames"], "generated_frames": measurement["retained"],
                  "trimmed_silence_frames": measurement["trimmed"], "padding_frames": padding,
                  "native_reference_frames": native_retained_frames,
                  "group_native_frames": native_retained_frames, "actual_speed_factor": actual_speed,
                  "tempo_input_frames": measurement.get("tempo_input", 0) if member_tempo else 0,
                  "tempo_factor": measurement.get("tempo_factor", 0) if member_tempo else 0,
                  "tempo_attempts": measurement.get("tempo_attempts", 0) if member_tempo else 0,
                  "synthesis_attempts": attempt, "status": "review", "key": key,
                  "sha256": sha256(final_path)}
        final_path.replace(cached_path)
        # FIX 05/10: a defence net -- the cache file MUST be exactly target_frames long (the
        # C# line 495 case: reported 8418 but the file was 31752 because of a raw/trimmed
        # mix-up after the repeat cut). Any mismatch raises immediately with the id so the
        # layer above can isolate that cue (P2); a lying record must never reach the
        # validator and bring down a 9k-cue job.
        cached_frames = len(read_wav(cached_path))
        if cached_frames != target_frames:
            raise RuntimeError(
                f"Group clip {cue['id']} file dài {cached_frames} != target {target_frames}")
        atomic_json(cached_path.with_suffix(".json"), record)
        windows.append({"id": cue["id"], "start_sample": voice_start,
                        "target_frames": target_frames})
        entries.append((active, key, cached_path, record, False, attempt))
    if window_end != group_end:
        raise RuntimeError("Sentence group allocation did not preserve its source boundary")
    atomic_json(plan_path, {"schema": 1, "key": plan_key, "timing_source": "sentence-group",
                            "windows": windows})
    return entries


# Condense the spoken text for tight cues (user 27/09: never read faster than the
# wind). Accumulating layers, pure logic + deterministic (testable without the model):
# V1 cleans repeats/whitespace -> V2 drops a sentence-opening vocative -> V3 drops a
# sentence-ending particle -> V4 drops intensifiers. Each layer is added only when the
# text truly gets shorter and still reads. The displayed SRT is never touched; the spoken render is recorded in spoken_text + a review flag.
_FILLER_LEAD = ("này", "kia", "à", "ơi", "nào")
_FILLER_TRAIL = ("nhé", "nha", "nhỉ", "đó", "chứ", "à", "ơi")
_INTENSIFIERS = ("rất", "quá", "lắm", "hết sức", "quá đỗi")
_INTENSIFIER_PHRASES = ("vô cùng", "cực kỳ")


def _strip_punct(token: str) -> str:
    return token.strip(",.!?;:\"'\"“”‘’…-")


def _drop_intensifiers(tokens: list[str]) -> list[str]:
    """Bỏ từ nhấn mạnh đơn + cụm 2 chữ (vô cùng, cực kỳ)."""
    kept: list[str] = []
    index = 0
    while index < len(tokens):
        pair = (_strip_punct(tokens[index]).lower()
                + " " + _strip_punct(tokens[index + 1]).lower()) \
            if index + 1 < len(tokens) else ""
        if pair in _INTENSIFIER_PHRASES:
            index += 2
            continue
        if _strip_punct(tokens[index]).lower() in _INTENSIFIERS:
            index += 1
            continue
        kept.append(tokens[index])
        index += 1
    return kept


def condense_variants(text: str) -> list[tuple[str, str]]:
    """Các bản rút gọn (variant, note), ngắn dần, không gồm bản gốc."""
    out: list[tuple[str, str]] = []

    def push(tokens: list[str], note: str) -> list[str]:
        variant = " ".join(tokens)
        if (variant and variant != text
                and not any(variant == seen for seen, _ in out)
                and any(character.isalnum() for character in variant)):
            out.append((variant, note))
        return variant.split()

    tokens = text.split()
    if not tokens:
        return out
    # Order 07/10 (user complaint: the voice differed from the subtitle because
    # emotionally repeated words were lost): particles/filler go FIRST (V2/V3/V4, the
    # least damaging), merging repeated words (V1) comes LAST -- only when the light
    # variants do not fit do we touch repeated words. The caller takes the first variant
    # that fits, so this order keeps “chậm thôi, chậm thôi” intact whenever dropping “Này”/“quá” is enough to fit.
    current = tokens
    # V2: drop a sentence-opening vocative when enough words remain.
    if len(current) > 3 and _strip_punct(current[0]).lower() in _FILLER_LEAD:
        current = push(current[1:], "bỏ tiếng gọi đầu câu")
    # V3: drop a sentence-ending particle when enough words remain.
    if len(current) > 3 and _strip_punct(current[-1]).lower() in _FILLER_TRAIL:
        current = push(current[:-1], "bỏ lơi cuối câu")
    # V4: drop intensifiers (keep the clause, soften the tone).
    lightened = _drop_intensifiers(current)
    if len(lightened) != len(current):
        push(lightened, "bỏ từ nhấn mạnh")
        current = lightened
    # V1 LAST: collapse consecutive repeated words (keep one) + extra whitespace.
    deduped = [token for index, token in enumerate(current)
               if index == 0 or token.lower() != current[index - 1].lower()]
    if deduped != current:
        push(deduped, "gộp từ lặp")
    return out


def punct_swap_variants(text: str) -> list:
    """Bản text đổi dấu câu cuối để phá vòng lặp attractor của model
    (kinh nghiệm cộng đồng XTTS: câu kẹt lặp mọi seed thường thoát khi
    đổi `.`/`。` thành `!`/`！` — chỉ đổi ngữ điệu, không đổi chữ).
    Mở rộng V2: cue ngắn hay kết bằng `!`/`?` (đúng kiểu hay nhân đôi)
    thì đổi NGƯỢC về `.` cho hiền + gập `!!...`/`...` (hiệu chuẩn 12/10:
    bản `!` attractor ở mọi seed/param). Trả [] khi không có dấu đổi được.
    Pure, test được."""
    stripped = (text or "").rstrip()
    if not stripped:
        return []
    out = []
    multi = (("...", "."), ("…", "."), ("!!", "!"), ("？？", "？"))
    for old, new in multi:
        if stripped.endswith(old):
            collapsed = stripped[: -len(old)] + new
            if collapsed != stripped:
                out.append(collapsed)
    if out:
        return out
    pairs = ((".", "!"), ("。", "！"), ("!", "."), ("！", "。"), ("?", "."), ("？", "。"))
    for old, new in pairs:
        if stripped.endswith(old):
            swapped = stripped[: -len(old)] + new
            if swapped != stripped and swapped not in out:
                out.append(swapped)
    return out


def final_pick_repeats(fitted_path, text: str = "") -> bool:
    """Kiểm cuối trước khi ship (11/10, lưới V2 12/10): bản định ship có lặp không.
    Ủy quyền cho cửa thống nhất draw_repeats (ACF + hạt nhân có số chữ +
    dài-bất-thường + 2 cụm clip ngắn). Chữ tự lặp thì bỏ qua (audio giống
    nhau là đúng). Không bao giờ raise (file lạ -> False, fail-open).
    Pure-ish, test được."""
    try:
        return bool(draw_repeats(fitted_path, text))
    except Exception:
        return False


def too_long_for_text(frames: int, text: str) -> bool:
    """Bản đọc dài bất thường so với chữ (V2): cue ≤8 đơn vị mà ACTIVE
    speech vượt units*0.35+0.2s. Đếm speech thật (mẫu ≥-44dBFS), không đếm
    im lặng — file chưa trim/kê đầu im không đánh lừa. Hiệu chuẩn 12/10
    trên 144 draws Ngọc Huyền: 65/90 draws cue ngắn trip (kể cả bản ACF
    mù), 0/36 draws câu control và 0/36 câu dài trip. frames = số mẫu
    active (không phải độ dài file)."""
    units = speakable_units(text or "")
    return 0 < units <= 8 and frames > (units * 0.35 + 0.2) * SAMPLE_RATE


def speech_bursts(path, min_gap_seconds: float = 0.18, threshold: float = 0.25) -> int:
    """Đếm cụm năng lượng (onset) cách nhau bởi im lặng thật: cue ngắn 1-3
    chữ mà ≥2 cụm là nhân đôi — bắt được cả bản dưới 0.6s mà ACF/hạt nhân
    mù (vụ "Sao cơ?" 0.8s mọi param ACF 0/18). Onset chỉ tính khi vừa cách
    onset trước ≥min_gap, vừa đứng sau ≥0.12s im liên tục (khỏi đếm nhầm
    nhấp nhô trong 1 cụm). numpy-only, ~ms. Lỗi -> 0 (không biết)."""
    import numpy as np
    try:
        with wave.open(str(path), "rb") as source:
            frames = source.getnframes()
            rate = source.getframerate()
            data = source.readframes(frames)
        if frames <= 0 or len(data) != frames * 2 or rate <= 0:
            return 0
        samples = np.abs(np.frombuffer(data, dtype="<i2").astype(np.float32))
        peak = float(np.max(samples))
        if peak <= 0:
            return 0
        width = max(1, rate // 100)
        nb = len(samples) // width
        if nb < 2:
            return 0
        env = samples[:nb * width].reshape(nb, width).mean(axis=1) / peak
        active = env >= threshold
        gap_bins = max(1, int(round(min_gap_seconds * rate / width)))
        quiet_bins = max(1, int(round(0.12 * rate / width)))
        bursts = 0
        last_on = -10 * gap_bins
        quiet_run = 10 * quiet_bins
        for i in range(nb):
            if active[i]:
                if (i == 0 or not active[i - 1]) and i - last_on >= gap_bins and quiet_run >= quiet_bins:
                    bursts += 1
                    last_on = i
                quiet_run = 0
            else:
                quiet_run += 1
        return bursts
    except Exception:
        return 0


def draw_repeats(path, text: str = "") -> bool:
    """Cửa thống nhất V2: bản vẽ có lặp không (OR 4 lớp).
    ACF + luật hạt nhân (truyền số chữ, bắt nhân đôi khác ngữ điệu) +
    dài-bất-thường theo chữ (bắt cả bản bị bóp dưới 0.6s) + 2 cụm năng
    lượng cho clip ngắn (bắt bản ACF mù). Chữ tự lặp -> False. Fail-open."""
    try:
        if text_self_repeats(text):
            return False
        units = speakable_units(text or "")
        # Long sentences (>8 units): keep the V1 behaviour unchanged (ACF only). The
        # word-count nuclear rule + tripwire + 2-burst rule are calibrated only for short
        # cues; on long sentences they flag whole batches falsely (measured 12/10: 36/36
        # clean control sentences flagged when nuclear is called with max_words).
        if 0 < units <= 8:
            try:
                if speech_repeats(path, units):
                    return True
            except Exception:
                pass
        else:
            try:
                if speech_repeats(path):
                    return True
            except Exception:
                pass
        try:
            with wave.open(str(path), "rb") as source:
                frames = source.getnframes()
                rate = source.getframerate()
                data = source.readframes(frames)
        except Exception:
            return False
        if frames <= 0 or rate <= 0 or len(data) != frames * 2:
            return False
        import numpy as np
        active = int((np.abs(np.frombuffer(data, dtype="<i2").astype(np.float32)) >= 300).sum())
        if too_long_for_text(active, text):
            return True
        duration = frames / rate
        if duration < 0.6 and 0 < units <= 3 and speech_bursts(path) >= 2:
            return True
    except Exception:
        return False
    return False


def fit_cue(voice, text: str, target_frames: int, run_root: Path, key: str,
            timing_source: str, ffmpeg: Path, on_attempt, cue_id: str = "",
            preloaded: dict | None = None, spill_budget_frames: int | None = None):
    if target_frames <= 0:
        raise ValueError("Source speech duration must contain at least one sample")
    base = float(voice.config.length_scale)
    if not math.isfinite(base) or base <= 0:
        raise ValueError("Invalid length_scale in verified model config")
    scale = base
    raw_frames = 0
    native_reference_frames = 0
    candidate = run_root / (key + "-native.wav")
    final_path = run_root / (key + "-fit.wav")
    minimum_generated = minimum_speech_frames(text)
    attempt = 0
    probe_reused = False
    # A ship-suspect case where every attempt repeats (no clean render left to pick):
    # ship anyway to avoid silence (rule L1) but set a flag -> spoken_text +
    # status review so the user can find it and relisten. Set only at the Phase-3 ship
    # point (the natural path needs the flag; the tempo path is review already). Do not
    # set it when merely discarding a draw (avoids a flood of false reviews).
    repeat_suspect = False

    def measure(active_text, active_scale):
        nonlocal attempt, probe_reused
        attempt += 1
        on_attempt(attempt, active_scale)
        # The batch probe from the pre-pass (only the very first time, the original text,
        # and the already-batched file): a condensed variant differs from the text so it can
        # never be reused by mistake. Any mismatch falls back to the single read, as before.
        if (not probe_reused and active_text == text and preloaded is not None
                and str(candidate) in preloaded and candidate.is_file()):
            probe_reused = True
        else:
            synthesize_cue(voice, active_text, candidate, active_scale)
        return measure_file(candidate)

    measure_seq = [0]

    def measure_file(path):
        # EVERY measurement gets its OWN trim file (monotonic sequence number): if the same
        # input is re-synthesised into different content (a variant reusing the shared
        # candidate, a reseed), a later measurement would still overwrite the previous tuple
        # -- a skewed record (tempo_in != source-trimmed) that fails validation for no
        # reason, or even the wrong voice bytes (case #93b45d9a: a 1.587s variant overwrote
        # the Phase-0 1.4s bytes). The temp file name never enters the record or key, so cache/sha are absolutely safe.
        measure_seq[0] += 1
        own_trim = Path(path).with_name(f"{Path(path).stem}-trim-{measure_seq[0]:03d}.wav")
        measured_source = len(read_wav(path))
        measured_trim = trim_trailing_silence(path, own_trim, SILENCE_GUARD_FRAMES)
        measured_frames = measured_source - measured_trim
        return measured_source, measured_trim, measured_frames, (
            own_trim if measured_trim else path)

    def punct_redraw(base_text):
        """Đọc lại tối đa len(punct_swap_variants) lượt với dấu câu đổi để phá
        attractor lặp khi mọi seed thường đều lặp (kinh nghiệm cộng đồng: đổi
        `.`/`。` thành `!`/`！` phá được vòng lặp). Trả (fitted_path, measured,
        swapped) sạch ĐẦU TIÊN vừa khung, hoặc None. Mọi lượt đều đếm attempt
        (giữ invariant phase_calls==attempts) và tôn trần MAX — không cháy giờ,
        không vỡ validator attempts≤12."""
        nonlocal attempt
        for swapped in punct_swap_variants(base_text):
            if attempt >= MAX_SYNTHESIS_ATTEMPTS:
                break
            try:
                import torch
            except Exception:
                return None
            seed = int(hashlib.sha256(f"{key}:punctswap:{swapped}".encode("utf-8")).hexdigest(), 16) % (2 ** 31)
            punct_path = run_root / (key + "-punct.wav")
            attempt += 1
            on_attempt(attempt, scale)
            try:
                torch.manual_seed(seed)
                synthesize_cue(voice, swapped, punct_path, scale)
                punct_measured = measure_file(punct_path)
            except Exception:
                continue
            p_source, p_trim, p_frames, p_fitted = punct_measured
            if p_frames < minimum_generated or p_frames <= 0 or p_frames > target_frames:
                continue
            if final_pick_repeats(p_fitted, swapped):
                continue
            emit({"event": "notice", "code": "punct-reseed",
                  "message": (f"Cue {cue_id} mọi seed thường đều lặp: đổi dấu câu "
                              f"phá attractor, bản sạch vừa khung + cờ review.")})
            return p_fitted, punct_measured, swapped
        return None

    def finish(active_text, note, measured, current_scale, raw, native, actual):
        measured_source, measured_trim, measured_frames, measured_fitted = measured
        condensed = active_text != text
        padding = target_frames - measured_frames
        output_path = measured_fitted
        if padding:
            pad_exact_clip(measured_fitted, final_path, target_frames)
            output_path = final_path
        output_frames = len(read_wav(output_path))
        if output_frames != measured_frames + padding:
            raise RuntimeError("Voice duration did not match the native sample count")
        record = {"raw_duration": raw / SAMPLE_RATE, "fitted_duration": output_frames / SAMPLE_RATE,
                  "frames": output_frames, "target_frames": target_frames, "timing_source": timing_source,
                  "fit_method": FIT_METHOD, "base_length_scale": base, "length_scale": current_scale,
                  "source_frames": measured_source, "generated_frames": measured_frames,
                  "trimmed_silence_frames": measured_trim, "padding_frames": padding,
                  "native_reference_frames": native,
                  "group_native_frames": 0, "actual_speed_factor": actual,
                  "synthesis_attempts": attempt,
                  "status": "review" if (condensed or needs_rate_review(current_scale, base)
                  or measured_trim or padding > padding_budget(target_frames)
                  or repeat_suspect) else "fit"}
        if condensed:
            record["spoken_text"] = active_text
        if repeat_suspect and "spoken_text" not in record:
            # The spoken text matches the display but the audio is repeat-suspect: insert the text
            # so C# (and native_record_valid) catch the review and the user can find it to
            # relisten. The displayed SRT never uses this field.
            record["spoken_text"] = active_text
            emit({"event": "notice", "code": "condense-fit",
                  "message": (f"Cue {cue_id} quá chật: rút gọn ({note}) đọc nhịp tự nhiên + cờ review. "
                              f"SRT hiển thị giữ nguyên.")})
        return output_path, record

    def finish_tempo_or_natural(tempo_out, tempo_in, gen, factor, tempo_tries, speed,
                                raw, native, src, trim, m_text, m_note):
        # Degenerate passthrough (the floor-clamped factor is exactly 1.0, in==gen -- seen
        # when the cue fits exactly and the vocoder emits canonical bytes, so the validator
        # equation `tempo_input > generated` fails): the audio IS the natural render, so
        # record it as natural to stay honest. Review stays as-is (trim/pad already qualify
        # for review). The validator is untouched (both the worker and C# accept natural+pad+review).
        if factor == 1.0 and tempo_in == gen:
            return finish(m_text, m_note, (src, trim, gen, tempo_out),
                          scale, raw, native, speed)
        return finish_tempo(tempo_out, tempo_in, gen, factor, tempo_tries, speed,
                            raw, native, src, trim)

    def finish_tempo(tempo_out, tempo_in, gen, factor, tempo_tries, speed,
                     raw, native, src, trim):
        # The atempo record mirrors the old branch exactly (the C#/native_record_valid
        # validator only reads the record shape, not phase order): a whole-sentence fast read
        # is always review, keeps pitch, and never cuts words.
        out_frames = len(read_wav(tempo_out))
        padding = out_frames - gen
        record = {"raw_duration": raw / SAMPLE_RATE, "fitted_duration": out_frames / SAMPLE_RATE,
                  "frames": out_frames, "target_frames": target_frames, "timing_source": timing_source,
                  "fit_method": ATEMPO_METHOD, "base_length_scale": base, "length_scale": scale,
                  "source_frames": src, "generated_frames": gen,
                  "trimmed_silence_frames": trim, "padding_frames": padding,
                  "native_reference_frames": native,
                  "group_native_frames": 0, "actual_speed_factor": speed,
                  "tempo_input_frames": tempo_in, "tempo_factor": factor,
                  "tempo_attempts": tempo_tries,
                  "synthesis_attempts": attempt, "status": "review"}
        return tempo_out, record

    # Phase 0: probe the original text at the base pace (attempt 1, measured exactly like the old loop).
    source_frames, trimmed_silence, frames, fitted_source = measure(text, scale)
    base_measured = (source_frames, trimmed_silence, frames, fitted_source)
    raw_frames, native_reference_frames = source_frames, frames
    actual_speed = 1.0
    if frames < minimum_generated:
        raise RuntimeError(
            f"VieNeu returned only {frames / SAMPLE_RATE:.3f}s of active speech for a non-empty cue")
    # Repeat-read guard (03/10): a single-read render that fits the window but is doubled
    # (“Vâng! Vâng!”) is NOT accepted -- force a reseed to find a clean render. Two
    # signals: a content veto (self-correlation + nuclear rule), or an abnormal length
    # for the text (calibrated threshold: a correct single word reads ~0.3-0.5s; a render
    # >=0.55s is suspect -- either genuinely slow reading, or already doubled). Short cues
    # only (partial doubling of long renders needs word-splitting, done in a later phase).
    short_units = speakable_units(text)
    too_long_for_text = (short_units <= 8
                         and frames > (short_units * 0.35 + 0.2) * SAMPLE_RATE)
    primary_repeats = (short_units <= 8 and not text_self_repeats(text)
                         and speech_repeats(fitted_source, short_units))
    primary_suspect = primary_repeats or too_long_for_text
    # Final ACF-leaning check (11/10): if the primary passes the vetoes above but ACF
    # still sees a repeat (proven on a real repeat-shipped render), treat it as suspect
    # too -- funnel it into the seed loop for a clean render instead of shipping directly. Cheap (~ms), fail-open.
    if not primary_suspect and not text_self_repeats(text) \
            and final_pick_repeats(fitted_source, text):
        primary_suspect = True
    if primary_suspect:
        emit({"event": "notice", "code": "repeat-retry",
              "message": (f"Cue {cue_id} bản đọc nghi nhân đôi/dài bất thường, "
                          f"thử lại seed khác tìm bản sạch + cờ review.")})
    if frames <= target_frames and MIN_ACTUAL_SPEED <= actual_speed <= MAX_ACTUAL_SPEED \
            and not primary_suspect:
        # Phase 0b (01/10, stretch): a wide cue where silence padding exceeds the review
        # budget -> read the whole sentence slower to fill the window (at most 1.4x the
        # duration, keeping pitch) instead of 0.5s of speech + 1s of silence. Above the ceiling, pad as before. Always review.
        if frames < target_frames and (target_frames - frames) > padding_budget(target_frames):
            stretch_need = target_frames / max(1, frames)
            if stretch_need <= 1.4:
                try:
                    tempo_out, tempo_in, gen, factor, tempo_tries, speed = tempo_stretch_clip(
                        ffmpeg, fitted_source, final_path, target_frames,
                        native_reference_frames, run_root, key)
                    emit({"event": "notice", "code": "stretch-fit",
                          "message": (f"Cue {cue_id} rộng (gấp {stretch_need:.2f}): "
                                      f"đọc chậm nguyên câu x{1 / factor:.2f} giữ cao độ + cờ review.")})
                    return finish_tempo(tempo_out, tempo_in, gen, factor, tempo_tries, speed,
                                        raw_frames, native_reference_frames, source_frames, trimmed_silence)
                except RuntimeError:
                    pass
        return finish(text, None, (source_frames, trimmed_silence, frames, fitted_source),
                      scale, raw_frames, native_reference_frames, actual_speed)
    # Phase 1 (30/09, atempo-first): a moderately tight cue -> read the WHOLE SENTENCE
    # faster with pitch-preserving atempo first; condense the text only when the pace
    # ceiling is exceeded. The ceiling reuses the FAST_SINGLE_NEED_RATIO threshold from
    # the watch list (a single needing >1.43x is a manual-SRT-fix candidate): below the
    # ceiling a fast pace still sounds natural (4.80 x 1.43 ~ 6.9 words/s, covering up to the p90 of the daily window).
    pace_need = frames / max(1, math.floor(target_frames * FIT_HEADROOM))
    if pace_need <= FAST_SINGLE_NEED_RATIO and not primary_suspect:
        try:
            tempo_out, tempo_in, gen, factor, tempo_tries, speed = tempo_fit_clip(
                ffmpeg, fitted_source, final_path, target_frames, native_reference_frames, run_root, key)
            if factor <= FAST_SINGLE_NEED_RATIO * 1.05:
                emit({"event": "notice", "code": "pace-fit",
                      "message": (f"Cue {cue_id} chật vừa phải (gấp {pace_need:.2f}): "
                                  f"đọc nhanh nguyên câu x{factor:.2f} giữ cao độ + cờ review.")})
                return finish_tempo_or_natural(tempo_out, tempo_in, gen, factor, tempo_tries, speed,
                                    raw_frames, native_reference_frames, source_frames, trimmed_silence,
                                    text, None)
        except RuntimeError:
            pass
    # Phase 1.5 (02/10, seeded-retry): diffusion synth has ENORMOUS variance on short
    # cues -- the same “Vào đi.” synthesised 5 times gave raw 0.80s / 1.44s x4
    # (trimmed 0.58s / 1.27-1.44s). torch.manual_seed locks the output (same seed ->
    # bit-identical, verified 02/10), so when the whole sentence exceeds the pace-first
    # ceiling and the text is short, retry with at most 4 pre-derived seeds (a sha over
    # the key, so every rerun yields the same seed sequence -> byte-identical,
    # cache/sha safe), keeping the SHORTEST intact render and then atempo as in Phase 1.
    # On a miss, drop to Phase 2 as before. The 1.5x ceiling is untouched (L2), the text
    # is untouched, no wrong condensation. Short cues only (at most 8 units): long cues
    # have small variance, a retry cannot save them and only burns synth; the primary stays unseeded to preserve the old cache.
    if ((pace_need > FAST_SINGLE_NEED_RATIO or primary_suspect)
            and speakable_units(text) <= 8 and frames >= minimum_generated):
        try:
            import torch
            _torch_ok = True
        except Exception:
            _torch_ok = False
        if _torch_ok:
            seed_best = None
            cut_best = None
            capped_streak = 0
            # The far seed family goes FIRST (V2): once the primary is suspect, near seeds tend to
            # share the same attractor -- one far-family draw right at the start usually escapes
            # early, saving attempts. The salt is entirely different from the near family, so the seed sequence stays deterministic (cache/sha safe).
            for seed_pos in [16] + list(range(16)):
                if attempt >= MAX_SYNTHESIS_ATTEMPTS:
                    break
                salt = "reseed:far" if seed_pos == 16 else f"reseed:{seed_pos}"
                seed = int(hashlib.sha256(
                    f"{key}:{salt}".encode("utf-8")).hexdigest(), 16) % (2 ** 31)
                seed_path = run_root / f"{key}-seed{seed_pos}.wav"
                attempt += 1
                on_attempt(attempt, scale)
                try:
                    torch.manual_seed(seed)
                    synthesize_cue(voice, text, seed_path, scale)
                    seed_measured = measure_file(seed_path)
                except Exception:
                    continue
                seed_source, seed_trim, seed_frames, seed_fitted = seed_measured
                # Cost squeeze (04/10): consecutive draws hitting the ceiling means the model is
                # stuck in inflation mode with this text, so further attempts are useless (evidence
                # 16/16). Stop early and let condense/truncate handle the rest.
                if seed_source >= SEED_TRUNC_SUSPECT_FRAMES:
                    capped_streak += 1
                    if capped_streak >= 4:
                        break
                    continue
                capped_streak = 0
                if (seed_frames < minimum_generated or seed_frames <= 0):
                    continue
                if not text_self_repeats(text) and speech_repeats(seed_fitted, speakable_units(text)):
                    # A doubled render: try cutting the first instance (the silence gap in between). A
                    # clean render always beats a cut one (more natural). Self-repeating text is skipped
                    # (identical halves are correct audio).
                    try:
                        cut_path = run_root / f"{key}-seed{seed_pos}-cut.wav"
                        cut_first_instance(seed_fitted, cut_path, minimum_generated)
                        cut_measured = measure_file(cut_path)
                    except Exception:
                        continue
                    cut_source, cut_trim, cut_frames, cut_fitted = cut_measured
                    if cut_frames < minimum_generated or cut_frames <= 0:
                        continue
                    if cut_best is None or cut_frames < cut_best[0]:
                        cut_best = (cut_frames, cut_source, cut_trim, cut_fitted, seed_pos)
                    continue
                if seed_best is None or seed_frames < seed_best[0]:
                    seed_best = (seed_frames, seed_source, seed_trim, seed_fitted, seed_pos)
                # Lock in early ONLY when it fits the window AND passes the V2 gate (11/10 only
                # checked the fit, so a doubled-but-fitting render slipped through; 12/10 adds the
                # unified gate: ACF/nuclear/abnormal-length/2-burst).
                if seed_frames <= target_frames and not draw_repeats(seed_fitted, text):
                    break
            best_cut = False
            best_frames = None
            if seed_best is not None and seed_best[0] < frames:
                best_frames, best_source, best_trim, best_fitted, best_pos = seed_best
            elif cut_best is not None and cut_best[0] < frames:
                best_frames, best_source, best_trim, best_fitted, best_pos = cut_best
                best_cut = True
            if best_frames is not None:
                # Final check (11/10): if the winning render is still repeat-suspect, try a bounded
                # punct-swap before shipping; if it still repeats, ship (tempo is always review so the
                # user can find it, no extra flag needed on this branch).
                if not text_self_repeats(text) and final_pick_repeats(best_fitted, text):
                    punct = punct_redraw(text)
                    if punct is not None:
                        p_fitted, p_measured, _ = punct
                        p_source, p_trim, p_frames, _ = p_measured
                        best_fitted, best_frames = p_fitted, p_frames
                        best_source, best_trim = p_source, p_trim
                try:
                    tempo_out, tempo_in, gen, factor, tempo_tries, speed = tempo_fit_clip(
                        ffmpeg, best_fitted, final_path, target_frames, best_frames, run_root, key)
                    emit({"event": "notice", "code": "seed-retry",
                          "message": (f"Cue {cue_id} synth phương sai lớn (gấp {pace_need:.2f}): "
                                      f"thử lại seed định trước, lấy bản ngắn nhất vừa khung "
                                      f"x{factor:.2f} giữ cao độ + cờ review."
                                      + (" Bản nhân đôi đã cắt lấy instance đầu." if best_cut else ""))})
                    return finish_tempo_or_natural(tempo_out, tempo_in, gen, factor, tempo_tries, speed,
                                        best_source, best_frames, best_source, best_trim,
                                        text, None)
                except RuntimeError:
                    pass
    # F1 (17/11, quality bug heard in an exported video: some cues dropped the last
    # 2-3 words of the sentence): NEVER shorten the spoken text while the cue still
    # has real silence after it. Read the WHOLE sentence and spill into that gap
    # (bounded by the measured gap to the next cue, the mirror of C#'s overflow
    # budget), at the SLOWEST pace the extended window still allows - not the pace
    # ceiling - and always review. Only a cue with no gap at all falls through to
    # condensing/truncation below, which the app reports by id + timecode.
    if (spill_budget_frames is not None and spill_budget_frames > 0
            and timing_source not in ("sentence-group", "sample")
            and frames > target_frames):
        spilled = try_spill_into_gap(
            ffmpeg, fitted_source, run_root, key, cue_id, timing_source,
            target_frames, int(spill_budget_frames), base, text,
            source_frames, trimmed_silence, frames,
            raw_frames, native_reference_frames, cap_speed_for(target_frames),
            attempt, minimum_speech_frames(text))
        if spilled is not None:
            return run_root / (key + "-fit.wav"), spilled
    # Phase 2: an excessively tight cue (over the pace ceiling) -> condense the spoken
    # independent, so batch them once instead of reading each one singly (30/09): take
    # the first variant that fits and discard the rest. A batch error falls back to reading each one singly as before. The total synth count for the function never exceeds
    # MAX_SYNTHESIS_ATTEMPTS.
    short_text, short_note = text, None
    short_measured = None
    variants = []
    for variant, note in condense_variants(text):
        try:
            variant_min = minimum_speech_frames(variant)
        except ValueError:
            continue
        variants.append((variant, note, variant_min))
    batch_ok = False
    if variants:
        var_paths = [run_root / f"{key}-var{position}.wav" for position in range(len(variants))]
        try:
            synthesize_cue_batch(voice, [(variant, path) for (variant, _, _), path in zip(variants, var_paths)])
            batch_ok = all(path.is_file() for path in var_paths)
        except Exception:
            batch_ok = False
    for position, (variant, note, variant_min) in enumerate(variants):
        if attempt >= MAX_SYNTHESIS_ATTEMPTS:
            break
        attempt += 1
        on_attempt(attempt, base)
        try:
            if batch_ok:
                measured = measure_file(run_root / f"{key}-var{position}.wav")
            else:
                synthesize_cue(voice, variant, candidate, base)
                measured = measure_file(candidate)
        except Exception:
            continue
        variant_source, variant_trim, variant_frames, variant_fitted = measured
        if variant_frames < variant_min or variant_frames <= 0:
            continue
        if speakable_units(variant) <= 8 and not text_self_repeats(variant) \
                and speech_repeats(variant_fitted, speakable_units(variant)):
            continue
        # Final check (11/10): the vetoes above filter obvious doubling; the ACF-leaning
        # angle catches additional leaks (proven on a real repeat-shipped render) -- skip this
        # variant, try the remaining ones, and if none are left fall to Phase 3.
        if not text_self_repeats(variant) and final_pick_repeats(variant_fitted, variant):
            continue
        short_text, short_note = variant, note
        short_measured = measured
        if variant_frames <= target_frames:
            return finish(variant, note, measured, base,
                          variant_source, variant_frames, 1.0)
    # Phase 3 VieNeu: there is NO native speed dial -- a scale loop would measure the
    # same pace over and over, burning synth for nothing (each round returns the same
    # frame count). Drop the loop and go straight to atempo on the SHORTEST render
    # between Phase 0 and Phase 2 (worst case: short+fast with review). Take the truly
    # shortest by measurement (case #93b45d9a: a Phase 0 render that just fit was
    # discarded so a longer variant could be atempo'd, and it died for no reason). Keep the raw/native semantics of the old version's first round.
    if short_measured is not None and short_measured[2] < base_measured[2]:
        active_text, active_note, measured0 = short_text, short_note, short_measured
    else:
        active_text, active_note, measured0 = text, None, base_measured
    active_min = minimum_speech_frames(active_text)
    scale = base
    if measured0 is not None:
        source_frames, trimmed_silence, frames, fitted_source = measured0
    else:
        source_frames, trimmed_silence, frames, fitted_source = measure(active_text, scale)
    raw_frames, native_reference_frames = source_frames, frames
    actual_speed = native_reference_frames / frames
    # Phase-3 final check (11/10): if the render about to ship is repeat-suspect (it
    # slipped every veto above), try a bounded punct-swap; if it still repeats, ship +
    # review flag (finish() handles it). Never silence, never raise (raising here kills the whole job for no reason).
    if (not text_self_repeats(active_text) and fitted_source.is_file()
            and final_pick_repeats(fitted_source, active_text)):
        punct = punct_redraw(active_text)
        if punct is not None:
            p_fitted, p_measured, _ = punct
            p_source, p_trim, p_frames, _ = p_measured
            if p_frames >= active_min and p_frames > 0 and p_frames <= target_frames:
                fitted_source = p_fitted
                source_frames, trimmed_silence, frames = p_source, p_trim, p_frames
                raw_frames, native_reference_frames = p_source, p_frames
                actual_speed = 1.0
                emit({"event": "notice", "code": "punct-reseed",
                      "message": (f"Cue {cue_id} bản định ship nghi lặp: đổi dấu câu "
                                  f"phá attractor được bản sạch vừa khung + cờ review.")})
            else:
                repeat_suspect = True
        else:
            repeat_suspect = True
    if frames < active_min:
        raise RuntimeError(
            f"VieNeu returned only {frames / SAMPLE_RATE:.3f}s of active speech for a non-empty cue")
    if frames <= target_frames and MIN_ACTUAL_SPEED <= actual_speed <= MAX_ACTUAL_SPEED:
        return finish(active_text, active_note,
                      (source_frames, trimmed_silence, frames, fitted_source),
                      scale, raw_frames, native_reference_frames, actual_speed)
    try:
        output_path, tempo_input, generated, tempo_factor, tempo_attempts, actual_speed = tempo_fit_clip(
            ffmpeg, fitted_source, final_path, target_frames, native_reference_frames, run_root, key)
        tempo_out, record = finish_tempo_or_natural(output_path, tempo_input, generated, tempo_factor,
                                         tempo_attempts, actual_speed,
                                         raw_frames, native_reference_frames, source_frames, trimmed_silence,
                                         active_text, active_note)
        # Content honesty (01/10): atempo on a condensed render reports spoken_text just like
        # the condense branch (review is already there because of tempo).
        if active_text != text and not record.get("spoken_text"):
            record["spoken_text"] = active_text
        return tempo_out, record
    except RuntimeError as error:
        # Rule 01/10 (L1): silence is FORBIDDEN for every cue -- if atempo hits the ceiling
        # and condensing still does not fit, raise so the chain above (truncate/escalate)
        # handles it; never return a silent clip. The caller catches (ValueError, RuntimeError).
        raise RuntimeError(
            f"Cue {cue_id} không vừa khung ở pace cho phép "
            f"(tối đa {cap_speed_for(target_frames)}x): {error}") from error


def silence_entry(cue: dict, text: str, base: float, attempts: int,
                  run_root: Path, clip_root: Path, worker_sha: str):
    """Stability quarantine: exact-length silence for an unsynthesizable cue.

    Never aborts the run. The timeline stays intact (every sample is real
    silence of exactly the window length); status review forces operator
    attention and the cue is counted in review_count.
    """
    _, target_frames = cue_window(cue)
    key = cache_identity(cue, text, worker_sha)
    cached_path = clip_root / (key + ".wav")
    temporary = run_root / (key + "-silence.wav")
    with wave.open(str(temporary), "wb") as output:
        output.setnchannels(1)
        output.setsampwidth(2)
        output.setframerate(SAMPLE_RATE)
        output.writeframes(b"\x00\x00" * target_frames)
    record = {"raw_duration": target_frames / SAMPLE_RATE,
              "fitted_duration": target_frames / SAMPLE_RATE,
              "frames": target_frames, "target_frames": target_frames,
              "timing_source": cue["timing_source"],
              "fit_method": "silence-fallback",
              "base_length_scale": base, "length_scale": base,
              "source_frames": 0, "generated_frames": 0,
              "trimmed_silence_frames": 0, "padding_frames": target_frames,
              "native_reference_frames": 0,
              "group_native_frames": 0, "actual_speed_factor": 0.0,
              "tempo_input_frames": 0, "tempo_factor": 0.0,
              "tempo_attempts": 0,
              "synthesis_attempts": max(1, attempts), "status": "review",
              "key": key, "sha256": sha256(temporary)}
    temporary.replace(cached_path)
    atomic_json(cached_path.with_suffix(".json"), record)
    return cue, key, cached_path, record, False, max(1, attempts)


def srt_fallback_cue(cue: dict):
    if cue["timing_source"] != "whisper":
        return None
    if cue["voice_start"] == cue["cue_start"] and cue["voice_end"] == cue["cue_end"]:
        return None
    fallback = dict(cue)
    fallback["voice_start"] = cue["cue_start"]
    fallback["voice_end"] = cue["cue_end"]
    fallback["timing_source"] = "srt-fallback"
    return fallback


def validate_manifest(manifest: dict, voice: str) -> list[dict]:
    if (manifest.get("schema"), manifest.get("engine_version"), manifest.get("voice"), manifest.get("timing_algorithm")) != (
            2, ENGINE_VERSION, voice, TIMING_ALGORITHM):
        raise ValueError("TTS manifest identity mismatch")
    duration = manifest.get("duration")
    if not isinstance(duration, (int, float)) or not math.isfinite(duration) or duration <= 0:
        raise ValueError("TTS duration is invalid")
    cues = manifest.get("cues")
    if not isinstance(cues, list) or not 0 < len(cues) <= 100000:
        raise ValueError("TTS manifest has no valid cues")
    ids = set()
    for position, cue in enumerate(cues, start=1):
        start, end = cue["cue_start"], cue["cue_end"]
        if not math.isfinite(start) or not math.isfinite(end) or not 0 <= start < end <= duration:
            raise ValueError("TTS cue time is outside source duration")
        voice_start, voice_end = cue["voice_start"], cue["voice_end"]
        # A whisper window may sit anywhere inside the subtitle; the ONLY new
        # freedom is starting EARLY, bounded by the lead budget (F1 17/11).
        lead_seconds = start - voice_start
        if (not math.isfinite(voice_start) or not math.isfinite(voice_end)
                or not start - MAX_LEAD_SECONDS <= voice_start < voice_end <= end
                or lead_seconds > MAX_LEAD_SECONDS + 1e-6
                or cue["timing_source"] not in ("whisper", "srt-fallback", "sample")):
            raise ValueError("Missing or invalid source speech window")
        if cue["timing_source"] == "srt-fallback" and (voice_end != end or lead_seconds > MAX_LEAD_SECONDS + 1e-6):
            raise ValueError("SRT fallback must use the external cue window with at most the bounded lead")
        if cue["timing_source"] == "sample" and (len(cues) != 1 or cue["id"] != "voice-demo-cue"):
            raise ValueError("Natural sample mode is not valid for project subtitles")
        cue_window(cue)
        if not isinstance(cue["id"], str) or not cue["id"] or cue["id"] in ids:
            raise ValueError("TTS cue IDs must be unique")
        ids.add(cue["id"])
        if cue["voice"] != voice or not isinstance(cue.get("text"), str) or not cue["text"].strip():
            raise ValueError("TTS cue text or voice is invalid")
        # Preflight advisories: flag likely-unreadable cues early, never abort.
        text = cue["text"].strip()
        cue_dur = cue["cue_end"] - cue["cue_start"]
        if cue_dur < 0.15:
            emit({"event": "notice", "code": "tts-preflight", "message": f"Cue {cue['id']} (SRT #{position}) window {cue_dur:.3f}s < 0.15s; "
                f"timecode {cue['cue_start']:.3f}..{cue['cue_end']:.3f}; "
                f"text={text[:80]!r}. Sửa timecode SRT hoặc gộp cue."})
        has_vi_vowel = any(ch in "aeiouyàáạảãâầấậẩẫăằắặẳẵèéẹẻẽêềếệểễìíịỉĩòóọỏõôồốộổỗơờớợởỡùúụủũưừứựửữỳýỵỷỹđAEIOUYÀÁẠẢÃÂẦẤẬẨẪĂẰẮẶẲẴÈÉẸẺẼÊỀẾỆỂỄÌÍỊỈĨÒÓỌỎÕÔỒỐỘỔỖƠỜỚỢỞỠÙÚỤỦŨƯỪỨỰỬỮỲÝỴỶỸĐ" for ch in text)
        if not has_vi_vowel:
            emit({"event": "notice", "code": "tts-preflight", "message": f"Cue {cue['id']} (SRT #{position}) không có nguyên âm Việt; "
                f"timecode {cue['cue_start']:.3f}..{cue['cue_end']:.3f}; "
                f"text={text[:80]!r}. Xóa cue rác hoặc sửa chữ."})
        # Extremely dense text (>10 speakable units/sec) will force unnatural atempo.
        speakable = sum(1 for t in re.findall(r"\w+", text, flags=re.UNICODE) if any(c.isalnum() for c in t))
        if cue_dur > 0 and speakable / cue_dur > 10:
            emit({"event": "notice", "code": "tts-preflight", "message": f"Cue {cue['id']} (SRT #{position}) quá chật: {speakable} units/{cue_dur:.2f}s; "
                f"timecode {cue['cue_start']:.3f}..{cue['cue_end']:.3f}; "
                f"text={text[:80]!r}. Mở rộng timecode hoặc rút gọn chữ."})
    return cues


def build_master(ffmpeg: Path, clips: list[dict], duration: float, run_root: Path, destination: Path) -> None:
    import numpy as np
    total_samples = math.ceil(duration * SAMPLE_RATE)
    block_size = 30 * SAMPLE_RATE
    pending = sorted(clips, key=lambda clip: clip["start"])
    if any(clip["end_sample"] - clip["start_sample"] != clip["frames"]
           or not 0 <= clip["start_sample"] < clip["end_sample"] <= total_samples for clip in pending):
        raise ValueError("Master must include every fitted PCM sample within the source timeline")
    overlaps = clip_overlap_pairs(pending)
    unexpected = [item for item in overlaps if item["unexpected"]]
    if unexpected:
        first = unexpected[0]
        raise RuntimeError(
            "Voice placement overlaps after the gap cap: " + str(len(unexpected))
            + " pair(s); first " + first["left"] + " -> " + first["right"]
            + f" by {first['seconds']:.3f}s ({first['frames']} frames). "
            + "Refusing to mix overlapping cues (repeat-reading bug).")
    if overlaps:
        emit({"event": "notice", "code": "source-window-overlap",
              "message": (f"{len(overlaps)} source subtitle window(s) overlap; voice keeps the "
                          f"original timeline (first {overlaps[0]['left']} -> {overlaps[0]['right']} "
                          f"{overlaps[0]['seconds']:.3f}s).")})
    active = []
    cursor = 0
    temporary = run_root / "voice-master.flac"
    # Stream bounded PCM blocks to FLAC; never build a full-video WAV/RAM buffer.
    with (run_root / "ffmpeg-master.log").open("wb") as error_log:
        process = subprocess.Popen(
            [str(ffmpeg), "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
             "-f", "s16le", "-ar", str(SAMPLE_RATE), "-ac", "1", "-i", "pipe:0",
             "-c:a", "flac", "-compression_level", "5", str(temporary)],
            stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=error_log,
            creationflags=_NO_WINDOW)
        try:
            for block_start in range(0, total_samples, block_size):
                block_end = min(total_samples, block_start + block_size)
                active = [clip for clip in active if clip["end_sample"] > block_start]
                while cursor < len(pending) and pending[cursor]["start_sample"] < block_end:
                    active.append(pending[cursor])
                    cursor += 1
                mix = np.zeros(block_end - block_start, dtype=np.float32)
                for clip in active:
                    start = max(block_start, clip["start_sample"])
                    end = min(block_end, clip["end_sample"])
                    if end <= start:
                        continue
                    with wave.open(str(clip["path"]), "rb") as source:
                        source.setpos(start - clip["start_sample"])
                        data = source.readframes(end - start)
                    if len(data) != (end - start) * 2:
                        raise ValueError("Cached clip changed during master mix")
                    mix[start - block_start:end - block_start] += np.frombuffer(data, dtype="<i2") / 32768.0
                process.stdin.write((np.clip(mix, -1, 1) * 32767).astype("<i2").tobytes())
                emit({"event": "block", "index": block_end, "total": total_samples})
            process.stdin.close()
            if process.wait(timeout=60):
                raise RuntimeError("FFmpeg master encoding failed")
        finally:
            if process.stdin and not process.stdin.closed:
                process.stdin.close()
            if process.poll() is None:
                process.kill()
                process.wait(timeout=10)
    if not temporary.is_file() or temporary.stat().st_size <= 64:
        raise RuntimeError("VieNeu master FLAC is missing")
    temporary.replace(destination)


def fail_with_list(failures: list) -> None:
    """Single terminal for physically-impossible cues (P2).

    Never master with missing voices: L1 bans silence and C# rejects any
    result whose cue count differs from the manifest. Fail exactly once with
    the COMPLETE isolation list so the SRT can be fixed; every other cue is
    already voiced and stays in cache, so the rerun jumps straight ahead.
    """
    raise RuntimeError(
        "TTS voice done mostly but "
        + str(len(failures)) + " cue(s) physically impossible, no master to keep no-silence rule "
        + "(all other cues are voiced and cached - fix the list below then rerun): "
        + json.dumps(failures, ensure_ascii=False))


def main() -> int:
    # Isolated Python ignores environment encoding variables on Windows.
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser()
    for name in ("manifest", "model", "config", "ffmpeg", "output-root", "run-root", "result", "master"):
        parser.add_argument("--" + name, required=True)
    parser.add_argument("--voice", default=VOICE_NAME)
    options = parser.parse_args()
    if not options.voice or not options.voice.strip():
        raise ValueError("Invalid voice: empty VieNeu preset label")
    model_dir = Path(options.model).resolve()
    if not model_dir.is_dir():
        raise ValueError("VieNeu backbone directory is missing")
    for package, version in PACKAGES.items():
        if version is None:
            continue
        if importlib.metadata.version(package) != version:
            raise ValueError("VieNeu runtime package mismatch: " + package)
    manifest = json.loads(Path(options.manifest).read_text(encoding="utf-8"))
    cues = validate_manifest(manifest, options.voice)
    total_frames = math.ceil(manifest["duration"] * SAMPLE_RATE)
    output_root = Path(options.output_root).resolve()
    run_root = Path(options.run_root).resolve()
    result_path, master_path = Path(options.result).resolve(), Path(options.master).resolve()
    if any(not path.is_relative_to(output_root) or path == output_root for path in (run_root, result_path, master_path)):
        raise ValueError("TTS output escaped the project cache")
    run_root.mkdir(parents=True, exist_ok=True)
    worker_sha = sha256(Path(__file__))
    clip_root = output_root / "clips" / hashlib.sha256(VOICE_REVISION.encode()).hexdigest()[:16]
    clip_root.mkdir(parents=True, exist_ok=True)
    from vietnormalizer import VietnameseNormalizer
    normalizer = VietnameseNormalizer()
    apply_tts_dictionary_overrides(normalizer)
    texts = [strip_emotion_tags(normalize_vietnamese_text(normalizer, cue["text"])) for cue in cues]
    # Rule 01/10 (L3): a non-Vietnamese cue is a data error -- report once, listing ALL
    # cues (index/id/timecode/text) BEFORE spending GPU time, so the user can fix the SRT
    # and rerun. Silence is forbidden, reading nonsense anyway is forbidden.
    offenders = []
    for position, (cue, text) in enumerate(zip(cues, texts)):
        if vietnamese_word_ratio(text) < 0.5:
            offenders.append(
                f"SRT #{position + 1} ({cue['id']}, {cue['cue_start']:.3f}s→{cue['cue_end']:.3f}s): "
                f"{text[:80]!r}")
    if offenders:
        shown = "\n".join(offenders[:50])
        hidden = f"\n… và {len(offenders) - 50} cue nữa." if len(offenders) > 50 else ""
        raise ValueError(
            f"SRT có {len(offenders)} cue không phải tiếng Việt (model VieNeu không đọc được) — "
            f"sửa chữ rồi chạy lại:\n{shown}{hidden}")
    # GPU is mandatory (user decision): no CUDA means immediate failure, no quiet fallback to CPU.
    import torch
    if not torch.cuda.is_available():
        raise ValueError("VieNeu pilot requires CUDA GPU; none is available")
    from vieneu import Vieneu
    engine = Vieneu()
    available = [vid for _, vid in engine.list_preset_voices()]
    preset_label = resolve_preset_label(available, options.voice)
    # Load exactly once, fail closed on load failure, and reuse for all uncached cues.
    voice = VieneuVoice(engine, preset_label)
    base_scale = float(voice.config.length_scale)
    if not math.isfinite(base_scale) or base_scale <= 0:
        raise ValueError("Invalid length_scale in voice shim config")
    emit({"event": "ready", "cues": len(cues), "voice": options.voice, "model_loads": 1,
          "engine": ENGINE, "sample_rate": SAMPLE_RATE, "voice_revision": VOICE_REVISION})

    def emit_attempt(index: int, cue: dict, attempt: int, maximum: int, timing_source: str, scale: float):
        emit({"event": "attempt", "index": index + 1, "total": len(cues), "id": cue["id"],
              "attempt": attempt, "max_attempts": maximum,
              "timing_source": timing_source, "length_scale": scale})

    def resolve_individual(index: int, cue: dict, text: str, preload: dict | None = None):
        candidate_cues = [cue]
        fallback = srt_fallback_cue(cue)
        if fallback is not None:
            candidate_cues.append(fallback)
        candidates = []
        for candidate_cue in candidate_cues:
            candidate_key = cache_identity(candidate_cue, text, worker_sha)
            candidate_path = clip_root / (candidate_key + ".wav")
            candidates.append((candidate_cue, candidate_key, candidate_path))
        for candidate_cue, candidate_key, candidate_path in candidates:
            candidate_record = load_clip(
                candidate_path, candidate_key, candidate_cue, minimum_speech_frames(text))
            if candidate_record is not None:
                return candidate_cue, candidate_key, candidate_path, candidate_record, True, 0
        failures = []
        synthesis_calls = 0
        for candidate_cue, candidate_key, candidate_path in candidates:
            _, candidate_target_frames = cue_window(candidate_cue)
            phase_calls = 0

            def on_attempt(attempt, scale):
                nonlocal synthesis_calls, phase_calls
                synthesis_calls += 1
                phase_calls += 1
                emit_attempt(index, cue, attempt, MAX_SYNTHESIS_ATTEMPTS, candidate_cue["timing_source"], scale)
            try:
                final_path, candidate_record = fit_cue(
                    voice, text, candidate_target_frames, run_root, candidate_key,
                    candidate_cue["timing_source"], Path(options.ffmpeg), on_attempt,
                    cue_id=candidate_cue.get("id", ""), preloaded=preload,
                    spill_budget_frames=overflow_budget_frames(cues, index, candidate_cue, total_frames))
            except (ValueError, RuntimeError) as error:
                failures.append(f"{candidate_cue['timing_source']}: {error}")
                continue
            if phase_calls != candidate_record["synthesis_attempts"]:
                raise RuntimeError("TTS attempt accounting mismatch")
            candidate_record.update({"key": candidate_key, "sha256": sha256(final_path)})
            final_path.replace(candidate_path)
            atomic_json(candidate_path.with_suffix(".json"), candidate_record)
            return candidate_cue, candidate_key, candidate_path, candidate_record, False, synthesis_calls
        # Rule 01/10 (L1): silence is FORBIDDEN -- if every candidate fails, truncate (the
        # longest prefix that fits at pace <=1.5x, always voice + spoken_text + review); if
        # truncation also fails (a pathological window), raise fail-loud with the cue id.
        primary = candidates[0][0]
        _, primary_target = cue_window(primary)
        escalate_calls = 0

        def on_escalate(attempt, scale):
            nonlocal synthesis_calls, escalate_calls
            synthesis_calls += 1
            escalate_calls += 1
            emit_attempt(index, cue, attempt, MAX_SYNTHESIS_ATTEMPTS, primary["timing_source"], scale)

        try:
            entry = escalate_truncated_voice(
                index, cue["id"], len(cues), primary, text, primary_target,
                voice, base_scale, run_root, clip_root, worker_sha,
                Path(options.ffmpeg),
                overflow_budget_frames(cues, index, primary, total_frames), on_escalate)
            return (entry[0], entry[1], entry[2], entry[3], False, synthesis_calls)
        except (ValueError, RuntimeError) as error:
            raise RuntimeError(
                f"Cue {index + 1} ({cue['id']}) bất khả thi: {' | '.join(failures)}; "
                f"truncate cũng bó tay ({error})") from error

    # Global pre-pass for single cues (no group-plan path, so the preload is always
    # consumed; cache-hit cues were already skipped inside preload_probes).
    # NOTE: all_groups must be assigned BEFORE this call (an UnboundLocalError on 10/10
    # when a refactor placed the call above the assignment) -- see the wiring-pin test below.
    ordered_entries = []
    # P2 (02/10, failure isolation): a physically impossible cue must not kill the whole
    # job (cases 169/1095/3407: died on the first one -> rerun dies on the next,
    # endlessly). Record {n, id, text, window, reason} and keep running through the whole
    # project; at the end of the job, fail exactly once with the COMPLETE LIST (no partial
    # master, no silence per L1, no ceiling violation per L2). The clip cache means an
    # already-voiced cue never fails again, so each round the list only shrinks, never loops forever.
    failures: list = []

    def isolate_failure(index: int, reason: Exception | str) -> None:
        cue = cues[index]
        try:
            window = [round(float(cue["cue_start"]), 3), round(float(cue["cue_end"]), 3)]
        except (KeyError, TypeError, ValueError):
            window = []
        failures.append({"n": index + 1, "id": cue.get("id", f"cue-{index + 1}"),
                         "text": str(texts[index])[:80], "window": window,
                         "reason": str(reason)[:300]})
        try:
            emit({"event": "notice", "code": "isolated-failure",
                  "message": (f"Cue {index + 1} ({cue.get('id', '?')}) bất khả thi, đã cách ly "
                              f"({str(reason)[:120]}); chạy tiếp các cue còn lại...")})
        except Exception:
            pass

    rate_tally: dict = {"pathological": [], "isolated_fast": [], "max_drift_s": 0.0, "groups": 0}
    all_groups = sentence_groups(cues, texts)
    # Global pre-pass for single cues (no group-plan path, so the preload is always
    # consumed; cache-hit cues were already skipped inside preload_probes).
    singles_preload = preload_probes(
        voice, cues, texts, run_root, clip_root, worker_sha, emit,
        [(indices[0], cues[indices[0]], texts[indices[0]]) for indices in all_groups if len(indices) == 1])
    for group_indices in all_groups:
        group_cues = [cues[index] for index in group_indices]
        group_texts = [texts[index] for index in group_indices]
        plan_key = group_plan_identity(group_cues, group_texts, worker_sha)
        plan_path = clip_root / (plan_key + ".group.json")
        group_entries = []
        if len(group_indices) > 1:
            planned_cues = load_group_plan(plan_path, plan_key, group_cues)
            if planned_cues is not None:
                for active_cue, text in zip(planned_cues, group_texts):
                    key = cache_identity(active_cue, text, worker_sha)
                    cached_path = clip_root / (key + ".wav")
                    record = load_clip(
                        cached_path, key, active_cue, minimum_speech_frames(text))
                    if record is None:
                        group_entries = []
                        break
                    group_entries.append((active_cue, key, cached_path, record, True, 0))
        if not group_entries:
            individual_error: RuntimeError | None = None
            resolved: list | None = None
            # A multi-cue group missing its plan cache: batch-probe the members first, then read
            # each cue singly (the preload is only consumed when load_clip really fails).
            group_preload = (preload_probes(
                                 voice, cues, texts, run_root, clip_root, worker_sha, emit,
                                 [(group_indices[pos], group_cues[pos], group_texts[pos])
                                  for pos in range(len(group_indices))])
                             if len(group_indices) > 1 else singles_preload)
            try:
                resolved = [resolve_individual(index, cues[index], texts[index], group_preload)
                            for index in group_indices]
            except RuntimeError as error:
                if len(group_indices) == 1:
                    # P2: isolate instead of killing the job -- record it and keep running, reporting the
                    # complete list once at the end of the job.
                    only = group_indices[0]
                    isolate_failure(only, error)
                    continue
                else:
                    individual_error = error
                    resolved = []
            else:
                if len(group_indices) == 1 or not any(
                        entry[0] is cues[group_indices[position]]
                        and single_needs_sentence_group(entry[3])
                        for position, entry in enumerate(resolved)):
                    group_entries = resolved
                    resolved = []
            if not group_entries:
                if individual_error is None:
                    individual_error = RuntimeError(
                        "Nhóm cue "
                        + "..".join(str(group_indices[index] + 1) for index in (0, -1))
                        + " có cue đọc đơn quá nhanh (dưới nhịp tự nhiên) — chuyển sang sentence-group")

                def on_group_attempt(local_index, attempt, scale):
                    index = group_indices[local_index]
                    emit_attempt(index, cues[index], attempt, MAX_GROUP_SYNTHESIS_ATTEMPTS, "sentence-group", scale)
                try:
                    group_entries = synthesize_sentence_group(
                        voice, group_cues, group_texts, run_root, clip_root, worker_sha,
                        plan_path, plan_key, Path(options.ffmpeg), on_group_attempt)
                except (ValueError, RuntimeError) as group_error:
                    if resolved:
                        # The group does not compress to fit but each single sentence already has its own
                        # result (fast read/truncate + review flag): keep them instead of failing the whole
                        # group together. Each single entry has passed the same validation as single-read
                        # mode, so the timeline and review flags keep their meaning. (L1: a single entry is
                        # never silent -- resolve_individual already forbids silence.)
                        #
                        emit({"event": "notice", "code": "silence-fallback",
                              "message": (f"Nhóm cue {group_indices[0] + 1}..{group_indices[-1] + 1} "
                                          f"không nén vừa timecode ({group_error}); giữ lại voice từng câu, "
                                          f"cần nghe lại.")})
                        group_entries = resolved
                    else:
                        # L1: the group fails and no single results exist -> truncate each cue (always voice +
                        # review); if a cue also defeats truncation, fail loud with the id so the SRT can be
                        # fixed.
                        group_entries = []
                        group_ok = True
                        for pos in range(len(group_indices)):
                            gindex = group_indices[pos]
                            try:
                                _, gtarget = cue_window(group_cues[pos])
                            except ValueError as window_error:
                                # P2: an impossible timecode still only isolates that cue, and the rest of the group's
                                # tail is still attempted so the list is complete right away.
                                isolate_failure(gindex, window_error)
                                group_ok = False
                                continue

                            def on_group_truncate(attempt, scale, _idx=gindex):
                                emit_attempt(_idx, cues[_idx], attempt,
                                             MAX_SYNTHESIS_ATTEMPTS, "sentence-group", scale)

                            try:
                                gentry = escalate_truncated_voice(
                                    gindex, cues[gindex]["id"], len(cues), group_cues[pos], group_texts[pos],
                                    gtarget, voice, base_scale, run_root, clip_root, worker_sha,
                                    Path(options.ffmpeg), 0, on_group_truncate)
                            except (ValueError, RuntimeError) as trunc_error:
                                # P2: truncation cannot save a cue -> isolate it and still try the rest of the
                                # group's tail so the list is complete in a single run. Successful single entries are
                                # still discarded (the timeline basis must stay absolute); a rerun will
                                # re-synthesise them.
                                isolate_failure(gindex, trunc_error)
                                group_ok = False
                                continue
                            group_entries.append(
                                (gentry[0], gentry[1], gentry[2], gentry[3], False, gentry[5]))
                        if not group_ok:
                            continue
        report_group_rate(group_indices, group_entries, cues, texts, rate_tally)
        rate_tally["groups"] += 1
        ordered_entries.extend(group_entries)

    if failures:
        fail_with_list(failures)
    if len(ordered_entries) != len(cues):
        raise RuntimeError("TTS sentence grouping lost or duplicated cues")

    # Re-attaching to groups (P2b, 04/10): group-member records come from a joint read --
    # re-reading a single member in a later round keeps the group label but carries
    # single figures (its own native/speed), and C# requires the whole group to be
    # uniform (native/speed/placement), so the job died entirely even though every cue
    # was voiced (case df0b: dozens of groups). Scan before the assembly round, mirroring
    # exactly C# ValidateSentenceGroupWindows + the per-cue validation: any group that is
    # not uniform is re-synthesised WHOLE (uniform by construction); any group still
    # broken is isolated entirely and the run continues. Never patch group figures into a
    # single record (that is lying metadata). (The worker and C# group with the same rule over the same manifest, so the spans match.)
    dead: set[int] = set()

    def _group_entries_uniform(group_indices) -> bool:
        # Any record/window access may be broken (stale cache data) -- return False so the
        # caller re-synthesises or isolates, and never throw outwards.
        try:
            first_native = first_actual = None
            first_start = None
            group_native_total = 0
            generated_total = 0
            for order, pos in enumerate(group_indices):
                active_cue, _key, _path, record, _hit, _calls = ordered_entries[pos]
                if record.get("timing_source") != "sentence-group":
                    return False
                try:
                    _s, target_frames = cue_window(active_cue)
                    min_frames = minimum_speech_frames(texts[pos])
                except (ValueError, KeyError, TypeError):
                    return False
                if target_frames <= 0 or min_frames is None or not native_record_valid(
                        record, target_frames, False, min_frames):
                    return False
                if order == 0:
                    first_native = record["group_native_frames"]
                    first_actual = record["actual_speed_factor"]
                    try:
                        first_start = cue_window(active_cue)[0]
                    except (ValueError, KeyError, TypeError):
                        return False
                else:
                    try:
                        prev = ordered_entries[group_indices[order - 1]]
                        prev_start, _p = cue_window(prev[0])
                    except (ValueError, KeyError, TypeError):
                        return False
                    if _s != prev_start + prev[3]["target_frames"]:
                        return False
                if record["group_native_frames"] != first_native \
                        or record["actual_speed_factor"] != first_actual:
                    return False
                if not isinstance(record.get("generated_frames"), int) or record["generated_frames"] <= 0:
                    return False
                group_native_total = first_native
                generated_total += record["generated_frames"]
            if not generated_total or not isinstance(group_native_total, int) or group_native_total <= 0:
                return False
            measured = group_native_total / generated_total
            last_active = ordered_entries[group_indices[-1]][0]
            last_start, _l = cue_window(last_active)
            last_target = ordered_entries[group_indices[-1]][3]["target_frames"]
            group_start = round(cues[group_indices[0]]["cue_start"] * SAMPLE_RATE)
            group_end = round(cues[group_indices[-1]]["cue_end"] * SAMPLE_RATE)
            if group_end - group_start > MAX_GROUP_DURATION_FRAMES \
                    or first_start != group_start \
                    or last_start + last_target != group_end:
                return False
            span_frames = last_start + last_target - first_start
            cap = MICRO_MAX_SPEED if 0 < span_frames < MICRO_WINDOW_FRAMES else HARD_MAX_SPEED
            # Mirrors C# exactly (the low bound has -1e-9, the high bound strict): any group C#
            # would reject must be re-attached or isolated right here, never allowed to slip down to
            # validator.
            return abs(measured - first_actual) <= 1e-9 \
                and SLOW_MIN_SPEED - 1e-9 <= measured <= cap
        except (ValueError, KeyError, TypeError, IndexError, AttributeError):
            return False

    for group_indices in all_groups:
        if len(group_indices) <= 1:
            continue
        has_group_timing = any(
            ordered_entries[pos][3].get("timing_source") == "sentence-group" for pos in group_indices)
        if not has_group_timing:
            continue
        if _group_entries_uniform(group_indices):
            continue
        group_cues = [cues[pos] for pos in group_indices]
        group_texts = [texts[pos] for pos in group_indices]
        regroup_key = group_plan_identity(group_cues, group_texts, worker_sha)
        regroup_plan = clip_root / (regroup_key + ".group.json")

        def on_regroup_attempt(local_index, attempt, scale, _gi=group_indices):
            _index = _gi[local_index]
            emit_attempt(_index, cues[_index], attempt, MAX_GROUP_SYNTHESIS_ATTEMPTS,
                         "sentence-group", scale)

        fresh = None
        try:
            fresh = synthesize_sentence_group(
                voice, group_cues, group_texts, run_root, clip_root, worker_sha,
                regroup_plan, regroup_key, Path(options.ffmpeg), on_regroup_attempt)
        except (ValueError, RuntimeError) as regroup_error:
            regroup_error_message = str(regroup_error)
        else:
            regroup_error_message = ""
        replaced = False
        if fresh is not None:
            try:
                for (active_cue, key, cached_path, record, cache_hit, synthesis_calls), pos in zip(
                        fresh, group_indices):
                    ordered_entries[pos] = (active_cue, key, cached_path, record, cache_hit, synthesis_calls)
                replaced = len(fresh) == len(group_indices) and _group_entries_uniform(group_indices)
            except (ValueError, KeyError, TypeError, IndexError, AttributeError):
                replaced = False
        if replaced:
            emit({"event": "notice", "code": "regroup-voice",
                  "message": (f"Nhóm cue {group_indices[0] + 1}..{group_indices[-1] + 1} không đồng nhất, "
                              f"đã tổng hợp lại cả nhóm giữ voice + cờ review.")})
        else:
            # Layer 2: split the group out and read each cue singly (single timing means C# skips
            # the group check, leaving only per-cue validation). The later assembly round
            # (per-cue refit/escalate/isolation) handles the rest like an ordinary single cue.
            # Only a genuinely impossible cue makes it into the final list.
            if regroup_error_message:
                emit({"event": "notice", "code": "ungroup-voice",
                      "message": (f"Nhóm cue {group_indices[0] + 1}..{group_indices[-1] + 1} "
                                  f"không gắn lại được ({regroup_error_message[:120]}); "
                                  f"tách ra đọc đơn từng cue.")})
            for pos in group_indices:
                if pos in dead:
                    continue
                try:
                    single = resolve_individual(pos, cues[pos], texts[pos], None)
                except Exception as single_error:
                    # The broad catch at the group-splitting layer is deliberate: one odd cue must not
                    # kill a 16k-cue job; the reason is recorded in full in the final list.
                    dead.add(pos)
                    isolate_failure(pos, f"nhóm {group_indices[0] + 1}..{group_indices[-1] + 1} "
                                         f"tách đơn vẫn bó tay ({single_error})".replace("\n", " ")[:300])
                    continue
                ordered_entries[pos] = single

    clips, results = [], []
    for index, (cue, entry) in enumerate(zip(cues, ordered_entries)):
        if index in dead:
            continue
        active_cue, key, cached_path, record, cache_hit, synthesis_calls = entry
        start_sample, target_frames = cue_window(active_cue)
        gap_frames = target_frames + overflow_budget_frames(cues, index, active_cue, total_frames)
        # Per-cue fail-fast (28/09, the 2-hour run that failed because of 1 empty atempo
        # record). User rule 01/10 (L1+L3): SILENCE IS FORBIDDEN for every cue -- a broken
        # record is re-read singly, and if that fails, truncated (voice + spoken_text +
        # review). A non-Vietnamese or physically impossible cue is REPORTED so the SRT can be
        # fixed (preflight already scanned before the GPU stage; this is the defence net).
        # native_record_valid mirrors C# exactly, so it only blocks what C# blocks.
        try:
            min_frames = minimum_speech_frames(texts[index])
        except ValueError:
            min_frames = None
        speakable = min_frames is not None and vietnamese_word_ratio(texts[index]) >= 0.5
        invalid_record = min_frames is not None and not native_record_valid(
            record, target_frames, active_cue["timing_source"] == "sample", min_frames)
        if record["frames"] > gap_frames or invalid_record:
            if not speakable:
                # L3: a non-Vietnamese cue is a data error (preflight reports it before the GPU
                # stage; reaching here means it slipped the net) -- P2: isolate it + run on, report
                # the complete list at the end of the job, no silence.
                isolate_failure(index, "không phải tiếng Việt" if invalid_record
                                else "voice vượt khe tới cue kế và không thể đọc lại")
                continue
            else:
                if active_cue["timing_source"] == "sentence-group":
                    # Tripwire P2b: the pre-pass already re-attached or isolated every broken group, so
                    # execution should never reach here. Reading it singly would break group uniformity
                    # (its own native/speed) -> isolate instead of voicing arbitrarily.
                    if index not in dead:
                        dead.add(index)
                        isolate_failure(index, "bản nhóm lọt lưới validation, cách ly giữ đồng nhất")
                    continue
                refit_calls = 0

                def on_refit(attempt, scale, _idx=index):
                    nonlocal refit_calls
                    refit_calls += 1
                    emit_attempt(_idx, cue, attempt, MAX_SYNTHESIS_ATTEMPTS,
                                 active_cue["timing_source"], scale)

                try:
                    refit_key = cache_identity(active_cue, texts[index], worker_sha)
                    refit_cached = clip_root / (refit_key + ".wav")
                    refit_path, refit_record = fit_cue(
                        voice, texts[index], target_frames, run_root, refit_key,
                        active_cue["timing_source"], Path(options.ffmpeg), on_refit,
                        cue_id=cue["id"])
                    refit_record = repair_group_native(refit_record)
                    if not native_record_valid(refit_record, target_frames,
                                               active_cue["timing_source"] == "sample",
                                               min_frames):
                        raise RuntimeError("đọc đơn lại vẫn không đạt validation")
                    refit_record.update({"key": refit_key, "sha256": sha256(refit_path)})
                    refit_path.replace(refit_cached)
                    atomic_json(refit_cached.with_suffix(".json"), refit_record)
                    entry = (active_cue, refit_key, refit_cached, refit_record, False, refit_calls)
                    emit({"event": "notice", "code": "refix-voice",
                          "message": (f"Cue {index + 1} ({cue['id']}) record hỏng ({record.get('fit_method')}); "
                                      f"đã đọc đơn lại giữ voice + cờ review, cần nghe lại.")})
                except (ValueError, RuntimeError):
                    try:
                        entry = escalate_truncated_voice(
                            index, cue["id"], len(cues), active_cue, texts[index], target_frames,
                            voice, base_scale, run_root, clip_root, worker_sha,
                            Path(options.ffmpeg),
                            overflow_budget_frames(cues, index, active_cue, total_frames), on_refit)
                    except (ValueError, RuntimeError) as trunc_error:
                        # P2: truncation cannot save it -> isolate + run on (report the complete list at the
                        # end of the job), absolutely no silence.
                        isolate_failure(index, trunc_error)
                        continue
            active_cue, key, cached_path, record, cache_hit, synthesis_calls = entry
            start_sample, target_frames = cue_window(active_cue)
            gap_frames = target_frames + overflow_budget_frames(cues, index, active_cue, total_frames)
            if record["frames"] > gap_frames:
                isolate_failure(index, "đọc lại vẫn vượt khe tới cue kế")
                continue
        # The clip already has the required length. Place ALL its samples;
        # never hide a timing failure by truncating the clip at the SRT boundary.
        end_sample = start_sample + record["frames"]
        overflow = record.get("overflow_frames", 0)
        overflow_ok = (type(overflow) is int and 0 < overflow <= OVERFLOW_MAX_FRAMES
                       and record["frames"] == target_frames + overflow
                       and end_sample <= start_sample + gap_frames
                       and end_sample <= total_frames)
        if end_sample > start_sample + target_frames and not overflow_ok:
            # P2: isolate + run on, like every other per-cue error.
            isolate_failure(index, "voice vượt cửa sổ speech hoặc khe tới cue kế")
            continue
        clips.append({"id": cue["id"], "path": cached_path, "start": active_cue["voice_start"],
                      "frames": record["frames"], "start_sample": start_sample, "end_sample": end_sample,
                      "window_end_sample": round(cue["cue_end"] * SAMPLE_RATE)})
        output_status = ("review" if record["status"] != "fit"
                         or active_cue["timing_source"] in ("srt-fallback", "sentence-group") else "fit")
        results.append({"id": cue["id"], "voice": options.voice, "voice_review": output_status == "review",
                        "raw_duration": record["raw_duration"], "fitted_duration": record["fitted_duration"],
                        "status": output_status, "cache_hit": cache_hit,
                        "clip_path": str(cached_path), "clip_sha256": record["sha256"],
                        "clipped": False, "timing_source": active_cue["timing_source"], "target_frames": target_frames,
                        "frames": record["frames"], "clip_start_sample": start_sample, "clip_end_sample": end_sample,
                        "fit_method": record["fit_method"], "base_length_scale": record["base_length_scale"],
                        "length_scale": record["length_scale"], "source_frames": record["source_frames"],
                         "generated_frames": record["generated_frames"],
                         "trimmed_silence_frames": record["trimmed_silence_frames"],
                         "padding_frames": record["padding_frames"], "synthesis_attempts": record["synthesis_attempts"],
                         "native_reference_frames": record["native_reference_frames"],
                         "group_native_frames": record.get("group_native_frames", 0),
                         "actual_speed_factor": record.get("actual_speed_factor", 0),
                         "tempo_factor": record.get("tempo_factor", 0),
                        "tempo_input_frames": record.get("tempo_input_frames", 0),
                        "tempo_attempts": record.get("tempo_attempts", 0),
                         "spoken_text": record.get("spoken_text", ""),
                         "overflow_frames": record.get("overflow_frames", 0),
                         "synthesis_calls": synthesis_calls})
        emit({"event": "cue", "index": index + 1, "total": len(cues), "id": cue["id"],
              "status": output_status, "cache_hit": cache_hit,
              "timing_source": active_cue["timing_source"], "synthesis_calls": synthesis_calls})
    if failures:
        fail_with_list(failures)
    pathological = rate_tally["pathological"]
    isolated = rate_tally["isolated_fast"]
    worst = sorted(pathological, key=lambda item: item["need_ratio"], reverse=True)[:5]
    if not pathological and not isolated:
        summary = (f"Voice giữ nhịp tự nhiên toàn bộ {len(cues)} cue qua "
                   f"{rate_tally['groups']} nhóm (trôi biên tối đa "
                   f"{rate_tally['max_drift_s']:.3f}s).")
    else:
        parts = []
        if pathological:
            examples = ", ".join(f"{item['id']} (gấp {item['need_ratio']:.1f} lần"
                                 + (", đã gộp nhóm" if item["absorbed"] else ", đọc đơn")
                                 + ")" for item in worst)
            parts.append(f"{len(pathological)} cue mistimed cần sửa timecode SRT: {examples}")
        if isolated:
            parts.append(f"{len(isolated)} cue nhanh lẻ loi (đa phần cần gộp/sửa timing): "
                         + ", ".join(item["id"] for item in isolated[:5]))
        summary = ("Voice xong nhưng cần soát timing: " + "; ".join(parts)
                   + f". Trôi biên tối đa {rate_tally['max_drift_s']:.3f}s.")
    try:
        emit({"event": "notice", "code": "voice-rate-summary", "message": summary,
              "pathological_total": len(pathological), "isolated_fast_total": len(isolated),
              "max_drift_s": round(rate_tally["max_drift_s"], 3),
              "groups": rate_tally["groups"]})
    except Exception:
        pass
    build_master(Path(options.ffmpeg), clips, manifest["duration"], run_root, master_path)
    result = {"schema": 2, "engine": ENGINE, "engine_version": ENGINE_VERSION, "voice": options.voice,
              "voice_revision": VOICE_REVISION, "cues": results,
              "master": {"path": str(master_path), "start": 0.0, "duration": manifest["duration"],
                         "sha256": sha256(master_path)}, "sample_rate": SAMPLE_RATE,
              "review_count": sum(cue["status"] != "fit" for cue in results)}
    atomic_json(result_path, result)
    emit({"event": "complete", "result": str(result_path), "review_count": result["review_count"]})
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as error:
        print(f"TTS_WORKER_ERROR: {type(error).__name__}: {error}", file=sys.stderr, flush=True)
        raise
