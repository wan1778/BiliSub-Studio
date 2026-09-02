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

ENGINE = "nghi-tts"
ENGINE_VERSION = "nghi-tts-1.0.0"
MODEL_SHA256 = "2140977786d76d834736c059dacfa553d4931dac2b2c7aaaea438bb2aa9da697"
CONFIG_SHA256 = "971f57f8d504223fee5b40d664f503cf769baf7db21f7d2ae0554a75d07de2f8"
TIMING_ALGORITHM = "whole-cue-piper-natural-first-v17"
VOICE_REVISION = MODEL_SHA256 + ":" + CONFIG_SHA256 + ":" + TIMING_ALGORITHM
VOICE_NAME = "ngoc_huyen"
SAMPLE_RATE = 22050
FIT_METHOD = "piper-length-scale"
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
FIT_HEADROOM = .995
TEMPO_RETRY_MARGIN = 1.001
MIN_GROUP_RATE_SCALE = MIN_RATE_SCALE
MAX_GROUP_ACTUAL_SPEED = MAX_ACTUAL_SPEED
# A sentence group may share only a continuously visible subtitle interval.
# Any positive gap means that the screen has no subtitle, so voice must not be
# packed into it.
MAX_GROUP_GAP_FRAMES = 0
MAX_GROUP_DURATION_FRAMES = 300 * SAMPLE_RATE
MAX_GROUP_CUES = 512
SILENCE_AMPLITUDE = 32
SILENCE_GUARD_FRAMES = round(.05 * SAMPLE_RATE)
MIN_PREFERRED_RATE_SCALE = .70
MAX_PREFERRED_RATE_SCALE = 1.0
PACKAGES = {"piper-tts": "1.7.0", "onnxruntime": "1.22.1", "vietnormalizer": "0.2.3", "numpy": "2.5.2"}
MIN_SPEECH_FRAMES = round(.10 * SAMPLE_RATE)
MIN_SPEECH_FRAMES_PER_UNIT = round(.07 * SAMPLE_RATE)


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
    normalized = unicodedata.normalize("NFC", normalizer.normalize(source)).strip()
    if not normalized:
        raise ValueError("Vietnamese normalization returned empty text")
    return normalized


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
            raise ValueError("NGHI clip must be mono PCM16 at 22050 Hz")
        frames = source.getnframes()
        data = source.readframes(frames)
    if frames <= 0 or len(data) != frames * 2:
        raise ValueError("NGHI clip is empty or truncated")
    samples = np.frombuffer(data, dtype="<i2").astype(np.float32) / 32768.0
    if not np.isfinite(samples).all() or float(np.max(np.abs(samples))) < 0.0001:
        raise ValueError("NGHI clip is silent or invalid")
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


def padding_budget(target_frames: int) -> int:
    # A short remainder is considered a precise fit. Larger trailing silence is
    # still safe because every model-produced PCM sample is retained, but the
    # cue is marked for review instead of rejecting valid speech.
    return max(1, min(round(.04 * SAMPLE_RATE), target_frames // 50))


def needs_rate_review(length_scale: float, base_length_scale: float) -> bool:
    return not MIN_PREFERRED_RATE_SCALE <= length_scale / base_length_scale <= MAX_PREFERRED_RATE_SCALE


def native_record_valid(record: dict, target_frames: int, natural_sample: bool,
                        minimum_generated_frames: int = 1) -> bool:
    base, scale = record["base_length_scale"], record["length_scale"]
    source, generated = record["source_frames"], record["generated_frames"]
    trimmed, padding = record["trimmed_silence_frames"], record["padding_frames"]
    attempts = record["synthesis_attempts"]
    maximum_attempts = (MAX_GROUP_SYNTHESIS_ATTEMPTS
                        if record.get("timing_source") == "sentence-group" else MAX_SYNTHESIS_ATTEMPTS)
    fit_method = record["fit_method"]
    tempo_fallback = fit_method == "piper-atempo"
    minimum_rate = MIN_GROUP_RATE_SCALE if record.get("timing_source") == "sentence-group" else MIN_RATE_SCALE
    if (fit_method not in (FIT_METHOD, "piper-atempo") or not math.isfinite(base) or base <= 0
            or not math.isfinite(scale) or not minimum_rate <= scale / base <= MAX_RATE_SCALE
            or type(source) is not int or source <= 0 or type(generated) is not int
            or generated <= 0
            or type(trimmed) is not int or not 0 <= trimmed < source
            or type(padding) is not int
            or not 0 <= padding < target_frames or generated + padding != record["frames"]
            or not natural_sample and record["frames"] != target_frames
            or type(attempts) is not int or not 1 <= attempts <= maximum_attempts):
        return False
    native_reference = record.get("native_reference_frames", 0)
    group_native = record.get("group_native_frames", 0)
    actual_speed = record.get("actual_speed_factor", 0)
    if (type(native_reference) is not int or native_reference <= 0
            or not isinstance(actual_speed, (int, float)) or not math.isfinite(actual_speed)
            or not MIN_ACTUAL_SPEED <= actual_speed <= MAX_ACTUAL_SPEED):
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
        if (type(tempo_input) is not int or tempo_input < minimum_generated_frames
                or tempo_input != source - trimmed or tempo_input <= generated
                or not isinstance(tempo_factor, (int, float)) or not math.isfinite(tempo_factor)
                or tempo_factor <= 1 or abs(tempo_input / generated - tempo_factor) > 1e-9
                or type(tempo_attempts) is not int or not 1 <= tempo_attempts <= MAX_TEMPO_ATTEMPTS):
            return False
    elif (generated < minimum_generated_frames or source - trimmed != generated
          or tempo_input != 0 or tempo_factor != 0 or tempo_attempts != 0):
        return False
    if attempts == 1:
        if scale != base:
            return False
        if record.get("timing_source") != "sentence-group" and native_reference != generated:
            return False
    if natural_sample and record.get("timing_source") != "sample":
        return False
    needs_review = (tempo_fallback or record.get("timing_source") == "sentence-group"
                    or needs_rate_review(scale, base) or trimmed > 0
                    or padding > padding_budget(target_frames))
    return record["status"] == ("review" if needs_review else "fit")


def load_clip(path: Path, key: str, cue: dict, minimum_generated_frames: int = 1):
    try:
        record = json.loads(path.with_suffix(".json").read_text(encoding="utf-8"))
        if record["key"] != key or record["sha256"] != sha256(path):
            return None
        samples = read_wav(path)
        duration = len(samples) / SAMPLE_RATE
        _, target_frames = cue_window(cue)
        if not math.isfinite(record["raw_duration"]) or record["raw_duration"] <= 0:
            return None
        natural_sample = cue["timing_source"] == "sample"
        if (record["frames"] != len(samples) or record["target_frames"] != target_frames
                or record["timing_source"] != cue["timing_source"] or len(samples) > target_frames
                or len(samples) != target_frames
                or not math.isfinite(record["fitted_duration"]) or abs(record["fitted_duration"] - duration) > 1e-9
                or not native_record_valid(record, target_frames, natural_sample, minimum_generated_frames)):
            return None
        return record
    except (OSError, ValueError, KeyError, TypeError, wave.Error):
        return None


def synthesize_cue(voice, text: str, temporary: Path, length_scale: float) -> None:
    from piper import SynthesisConfig
    # One complete cue per attempt. Only model-native phoneme duration changes;
    # keep the verified config's noise/speaker settings and the same loaded voice.
    syn_config = SynthesisConfig(length_scale=length_scale)
    with wave.open(str(temporary), "wb") as output:
        output.setnchannels(1)
        output.setsampwidth(2)
        output.setframerate(SAMPLE_RATE)
        for chunk in voice.synthesize(text, syn_config=syn_config):
            if (chunk.sample_rate, chunk.sample_width, chunk.sample_channels) != (SAMPLE_RATE, 2, 1):
                raise ValueError("Piper returned an unexpected audio format")
            output.writeframes(chunk.audio_int16_bytes)


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
    import numpy as np
    with wave.open(str(source_path), "rb") as source:
        frames = source.getnframes()
        data = source.readframes(frames)
        params = source.getparams()
    if frames <= 0 or len(data) != frames * 2:
        raise ValueError("NGHI group clip is empty or truncated")
    samples = np.frombuffer(data, dtype="<i2").astype(np.int32)
    active = np.flatnonzero(np.abs(samples) > SILENCE_AMPLITUDE)
    if len(active) == 0:
        raise ValueError("NGHI group clip has no active speech")
    if type(guard_frames) is not int or guard_frames < 0:
        raise ValueError("Invalid sentence-group silence guard")
    retained = min(frames, int(active[-1]) + 1 + guard_frames)
    if retained == frames:
        return 0
    with wave.open(str(output_path), "wb") as output:
        output.setparams(params)
        output.writeframes(data[:retained * 2])
    return frames - retained


def atempo_filter(factor: float) -> str:
    if not math.isfinite(factor) or factor <= 1:
        raise ValueError("FFmpeg tempo factor must be greater than one")
    stages = []
    remaining = factor
    while remaining > 2:
        stages.append(2.0)
        remaining /= 2
    if remaining > 1 + 1e-9:
        stages.append(remaining)
    return ",".join(f"atempo={stage:.12g}" for stage in stages)


def apply_atempo(ffmpeg: Path, source_path: Path, output_path: Path, factor: float) -> int:
    temporary = output_path.with_name(output_path.name + ".tmp.wav")
    temporary.unlink(missing_ok=True)
    completed = subprocess.run(
        [str(ffmpeg), "-hide_banner", "-loglevel", "error", "-y", "-i", str(source_path),
         "-af", atempo_filter(factor), "-ac", "1", "-ar", str(SAMPLE_RATE),
         "-c:a", "pcm_s16le", str(temporary)],
        capture_output=True, text=True, encoding="utf-8", errors="replace")
    if completed.returncode != 0 or not temporary.is_file():
        temporary.unlink(missing_ok=True)
        raise RuntimeError("FFmpeg atempo failed: " + completed.stderr.strip()[-1000:])
    frames = len(read_wav(temporary))
    temporary.replace(output_path)
    return frames


def tempo_fit_clip(ffmpeg: Path, source_path: Path, final_path: Path, target_frames: int,
                   native_reference_frames: int, run_root: Path, key: str):
    input_frames = len(read_wav(source_path))
    desired_frames = max(1, math.floor(target_frames * FIT_HEADROOM))
    factor = max(1.000001, input_frames / desired_frames * TEMPO_RETRY_MARGIN)
    for attempt in range(1, MAX_TEMPO_ATTEMPTS + 1):
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
    scale = base
    raw_frames = [0] * len(cues)
    selected = None
    native_retained_frames = 0
    last_total_retained = 0
    for attempt in range(1, MAX_GROUP_SYNTHESIS_ATTEMPTS + 1):
        measurements = []
        for local_index, (cue, text) in enumerate(zip(cues, texts)):
            on_attempt(local_index, attempt, scale)
            raw_path = run_root / f"group-{plan_key}-{local_index}-native.wav"
            trimmed_path = run_root / f"group-{plan_key}-{local_index}-trimmed.wav"
            synthesize_cue(voice, text, raw_path, scale)
            source_frames = len(read_wav(raw_path))
            if attempt == 1:
                raw_frames[local_index] = source_frames
            guard = SILENCE_GUARD_FRAMES if local_index == len(cues) - 1 else round(.01 * SAMPLE_RATE)
            trimmed = trim_trailing_silence(raw_path, trimmed_path, guard)
            retained = source_frames - trimmed
            if retained < minimum_speech_frames(text):
                raise RuntimeError(
                    f"NGHI returned only {retained / SAMPLE_RATE:.3f}s of active speech for a non-empty cue")
            measurements.append({"source_path": trimmed_path if trimmed else raw_path,
                                 "source_frames": source_frames, "trimmed": trimmed, "retained": retained})
        total_retained = sum(item["retained"] for item in measurements)
        if attempt == 1:
            native_retained_frames = total_retained
        actual_speed = native_retained_frames / total_retained
        last_total_retained = total_retained
        if (total_retained <= group_frames
                and MIN_ACTUAL_SPEED <= actual_speed <= MAX_ACTUAL_SPEED):
            selected = (attempt, scale, measurements, actual_speed)
            break
        desired_frames = max(1, math.floor(group_frames * FIT_HEADROOM))
        corrected = next_length_scale(scale, total_retained, desired_frames, base, None, None)
        if corrected >= scale:
            corrected = max(base * MIN_GROUP_RATE_SCALE, scale * .9)
        corrected = max(base * MIN_GROUP_RATE_SCALE, min(base * MAX_RATE_SCALE, corrected))
        if math.isclose(corrected, scale, rel_tol=0, abs_tol=1e-7):
            break
        scale = corrected
    tempo_used = False
    if selected is None:
        desired_frames = max(1, math.floor(group_frames * FIT_HEADROOM))
        tempo_factor = max(1.000001, last_total_retained / desired_frames * TEMPO_RETRY_MARGIN)
        for tempo_attempt in range(1, MAX_TEMPO_ATTEMPTS + 1):
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
            if transformed_total <= group_frames and MIN_ACTUAL_SPEED <= actual_speed <= MAX_ACTUAL_SPEED:
                selected = (attempt, scale, transformed, actual_speed)
                tempo_used = True
                break
            tempo_factor *= max(transformed_total / group_frames,
                                MIN_ACTUAL_SPEED / max(actual_speed, 1e-9)) * TEMPO_RETRY_MARGIN
    if selected is None:
        raise RuntimeError(
            f"Nhóm câu {cues[0]['id']}..{cues[-1]['id']} không thể nén đủ nguyên lời vào timecode")

    attempt, scale, measurements, actual_speed = selected
    retained_suffix = [0] * (len(measurements) + 1)
    for index in range(len(measurements) - 1, -1, -1):
        retained_suffix[index] = retained_suffix[index + 1] + measurements[index]["retained"]
    cursor = group_start
    entries, windows = [], []
    for index, (cue, text, measurement) in enumerate(zip(cues, texts, measurements)):
        if index == len(cues) - 1:
            window_end = group_end
        else:
            desired_end = round(cue["cue_end"] * SAMPLE_RATE)
            earliest_end = cursor + measurement["retained"]
            latest_end = group_end - retained_suffix[index + 1]
            window_end = max(earliest_end, min(desired_end, latest_end))
        target_frames = window_end - cursor
        padding = target_frames - measurement["retained"]
        if target_frames <= 0 or padding < 0:
            raise RuntimeError("Sentence group could not allocate every complete speech clip")
        active = dict(cue)
        active["voice_start"] = cursor / SAMPLE_RATE
        cursor = window_end
        active["voice_end"] = cursor / SAMPLE_RATE
        active["timing_source"] = "sentence-group"
        key = cache_identity(active, text, worker_sha)
        cached_path = clip_root / (key + ".wav")
        final_path = measurement["source_path"]
        if padding:
            padded = run_root / (key + "-group-fit.wav")
            pad_exact_clip(final_path, padded, target_frames)
            final_path = padded
        record = {"raw_duration": raw_frames[len(entries)] / SAMPLE_RATE,
                  "fitted_duration": target_frames / SAMPLE_RATE, "frames": target_frames,
                  "target_frames": target_frames, "timing_source": "sentence-group",
                  "fit_method": "piper-atempo" if tempo_used else FIT_METHOD,
                  "base_length_scale": base, "length_scale": scale,
                  "source_frames": measurement["source_frames"], "generated_frames": measurement["retained"],
                  "trimmed_silence_frames": measurement["trimmed"], "padding_frames": padding,
                  "native_reference_frames": native_retained_frames,
                  "group_native_frames": native_retained_frames, "actual_speed_factor": actual_speed,
                  "tempo_input_frames": measurement.get("tempo_input", 0),
                  "tempo_factor": measurement.get("tempo_factor", 0),
                  "tempo_attempts": measurement.get("tempo_attempts", 0),
                  "synthesis_attempts": attempt, "status": "review", "key": key,
                  "sha256": sha256(final_path)}
        final_path.replace(cached_path)
        atomic_json(cached_path.with_suffix(".json"), record)
        windows.append({"id": cue["id"], "start_sample": cursor - target_frames,
                        "target_frames": target_frames})
        entries.append((active, key, cached_path, record, False, attempt))
    if cursor != group_end:
        raise RuntimeError("Sentence group allocation did not preserve its source boundary")
    atomic_json(plan_path, {"schema": 1, "key": plan_key, "timing_source": "sentence-group",
                            "windows": windows})
    return entries


def fit_cue(voice, text: str, target_frames: int, run_root: Path, key: str,
            timing_source: str, ffmpeg: Path, on_attempt):
    if target_frames <= 0:
        raise ValueError("Source speech duration must contain at least one sample")
    base = float(voice.config.length_scale)
    if not math.isfinite(base) or base <= 0:
        raise ValueError("Invalid length_scale in verified model config")
    scale = base
    raw_frames = 0
    native_reference_frames = 0
    candidate = run_root / (key + "-native.wav")
    silence_fitted = run_root / (key + "-silence-fit.wav")
    final_path = run_root / (key + "-fit.wav")
    minimum_generated = minimum_speech_frames(text)
    for attempt in range(1, MAX_SYNTHESIS_ATTEMPTS + 1):
        on_attempt(attempt, scale)
        synthesize_cue(voice, text, candidate, scale)
        source_frames = len(read_wav(candidate))
        trimmed_silence = trim_trailing_silence(candidate, silence_fitted, SILENCE_GUARD_FRAMES)
        frames = source_frames - trimmed_silence
        fitted_source = silence_fitted if trimmed_silence else candidate
        if attempt == 1:
            raw_frames = source_frames
            native_reference_frames = frames
        actual_speed = native_reference_frames / frames
        if frames < minimum_generated:
            raise RuntimeError(
                f"NGHI returned only {frames / SAMPLE_RATE:.3f}s of active speech for a non-empty cue")
        if frames <= target_frames and MIN_ACTUAL_SPEED <= actual_speed <= MAX_ACTUAL_SPEED:
            padding = target_frames - frames
            output_path = fitted_source
            if padding:
                pad_exact_clip(fitted_source, final_path, target_frames)
                output_path = final_path
            output_frames = len(read_wav(output_path))
            if output_frames != frames + padding:
                raise RuntimeError("Voice duration did not match the native sample count")
            record = {"raw_duration": raw_frames / SAMPLE_RATE, "fitted_duration": output_frames / SAMPLE_RATE,
                      "frames": output_frames, "target_frames": target_frames, "timing_source": timing_source,
                      "fit_method": FIT_METHOD, "base_length_scale": base, "length_scale": scale,
                      "source_frames": source_frames, "generated_frames": frames,
                      "trimmed_silence_frames": trimmed_silence, "padding_frames": padding,
                      "native_reference_frames": native_reference_frames,
                      "group_native_frames": 0, "actual_speed_factor": actual_speed,
                      "synthesis_attempts": attempt,
                      "status": "review" if needs_rate_review(scale, base)
                      or trimmed_silence or padding > padding_budget(target_frames) else "fit"}
            return output_path, record
        desired_frames = max(1, math.floor(target_frames * FIT_HEADROOM))
        corrected = next_length_scale(scale, frames, desired_frames, base, None, None)
        if corrected >= scale:
            corrected = max(base * MIN_RATE_SCALE, scale * .9)
        corrected = max(base * MIN_RATE_SCALE, min(base * MAX_RATE_SCALE, corrected))
        if math.isclose(corrected, scale, rel_tol=0, abs_tol=1e-7):
            break
        scale = corrected
    output_path, tempo_input, generated, tempo_factor, tempo_attempts, actual_speed = tempo_fit_clip(
        ffmpeg, fitted_source, final_path, target_frames, native_reference_frames, run_root, key)
    output_frames = len(read_wav(output_path))
    padding = output_frames - generated
    record = {"raw_duration": raw_frames / SAMPLE_RATE, "fitted_duration": output_frames / SAMPLE_RATE,
              "frames": output_frames, "target_frames": target_frames, "timing_source": timing_source,
              "fit_method": "piper-atempo", "base_length_scale": base, "length_scale": scale,
              "source_frames": source_frames, "generated_frames": generated,
              "trimmed_silence_frames": trimmed_silence, "padding_frames": padding,
              "native_reference_frames": native_reference_frames,
              "group_native_frames": 0, "actual_speed_factor": actual_speed,
              "tempo_input_frames": tempo_input, "tempo_factor": tempo_factor,
              "tempo_attempts": tempo_attempts,
              "synthesis_attempts": attempt, "status": "review"}
    return output_path, record


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
    for cue in cues:
        start, end = cue["cue_start"], cue["cue_end"]
        if not math.isfinite(start) or not math.isfinite(end) or not 0 <= start < end <= duration:
            raise ValueError("TTS cue time is outside source duration")
        voice_start, voice_end = cue["voice_start"], cue["voice_end"]
        if (not math.isfinite(voice_start) or not math.isfinite(voice_end)
                or not start <= voice_start < voice_end <= end
                or cue["timing_source"] not in ("whisper", "srt-fallback", "sample")):
            raise ValueError("Missing or invalid source speech window")
        if cue["timing_source"] == "srt-fallback" and (voice_start != start or voice_end != end):
            raise ValueError("SRT fallback must use the complete external cue window")
        if cue["timing_source"] == "sample" and (len(cues) != 1 or cue["id"] != "voice-demo-cue"):
            raise ValueError("Natural sample mode is not valid for project subtitles")
        cue_window(cue)
        if not isinstance(cue["id"], str) or not cue["id"] or cue["id"] in ids:
            raise ValueError("TTS cue IDs must be unique")
        ids.add(cue["id"])
        if cue["voice"] != voice or not isinstance(cue.get("text"), str) or not cue["text"].strip():
            raise ValueError("TTS cue text or voice is invalid")
    return cues


def build_master(ffmpeg: Path, clips: list[dict], duration: float, run_root: Path, destination: Path) -> None:
    import numpy as np
    total_samples = math.ceil(duration * SAMPLE_RATE)
    block_size = 30 * SAMPLE_RATE
    pending = sorted(clips, key=lambda clip: clip["start"])
    if any(clip["end_sample"] - clip["start_sample"] != clip["frames"]
           or not 0 <= clip["start_sample"] < clip["end_sample"] <= total_samples for clip in pending):
        raise ValueError("Master must include every fitted PCM sample within the source timeline")
    active = []
    cursor = 0
    temporary = run_root / "voice-master.flac"
    # Stream bounded PCM blocks to FLAC; never build a full-video WAV/RAM buffer.
    with (run_root / "ffmpeg-master.log").open("wb") as error_log:
        process = subprocess.Popen(
            [str(ffmpeg), "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
             "-f", "s16le", "-ar", str(SAMPLE_RATE), "-ac", "1", "-i", "pipe:0",
             "-c:a", "flac", "-compression_level", "5", str(temporary)],
            stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=error_log)
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
        raise RuntimeError("NGHI master FLAC is missing")
    temporary.replace(destination)


def main() -> int:
    # Isolated Python ignores environment encoding variables on Windows.
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser()
    for name in ("manifest", "model", "config", "ffmpeg", "output-root", "run-root", "result", "master"):
        parser.add_argument("--" + name, required=True)
    parser.add_argument("--voice", default=VOICE_NAME)
    options = parser.parse_args()
    if options.voice != VOICE_NAME:
        raise ValueError("Invalid voice: no verified NGHI model")
    model, config = Path(options.model).resolve(), Path(options.config).resolve()
    for path, size, digest in ((model, 63516050, MODEL_SHA256), (config, 4855, CONFIG_SHA256)):
        if path.stat().st_size != size or sha256(path) != digest:
            raise ValueError("NGHI model/config size or SHA-256 mismatch")
    for package, version in PACKAGES.items():
        if importlib.metadata.version(package) != version:
            raise ValueError("NGHI runtime package mismatch: " + package)
    manifest = json.loads(Path(options.manifest).read_text(encoding="utf-8"))
    cues = validate_manifest(manifest, options.voice)
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
    from piper import PiperVoice
    normalizer = VietnameseNormalizer()
    # Load exactly once, fail closed on load failure, and reuse for all uncached cues.
    voice = PiperVoice.load(str(model), config_path=str(config), use_cuda=False)
    if voice.config.sample_rate != SAMPLE_RATE:
        raise ValueError("NGHI model sample rate mismatch")
    emit({"event": "ready", "cues": len(cues), "voice": VOICE_NAME, "model_loads": 1,
          "engine": ENGINE, "sample_rate": SAMPLE_RATE, "voice_revision": VOICE_REVISION})
    texts = [normalize_vietnamese_text(normalizer, cue["text"]) for cue in cues]

    def emit_attempt(index: int, cue: dict, attempt: int, maximum: int, timing_source: str, scale: float):
        emit({"event": "attempt", "index": index + 1, "total": len(cues), "id": cue["id"],
              "attempt": attempt, "max_attempts": maximum,
              "timing_source": timing_source, "length_scale": scale})

    def resolve_individual(index: int, cue: dict, text: str):
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
                    candidate_cue["timing_source"], Path(options.ffmpeg), on_attempt)
            except (ValueError, RuntimeError) as error:
                failures.append(f"{candidate_cue['timing_source']}: {error}")
                continue
            if phase_calls != candidate_record["synthesis_attempts"]:
                raise RuntimeError("TTS attempt accounting mismatch")
            candidate_record.update({"key": candidate_key, "sha256": sha256(final_path)})
            final_path.replace(candidate_path)
            atomic_json(candidate_path.with_suffix(".json"), candidate_record)
            return candidate_cue, candidate_key, candidate_path, candidate_record, False, synthesis_calls
        raise RuntimeError(
            f"Cue {index + 1} ({cue['id']}): không thể đọc vừa cả nhịp Whisper lẫn toàn timecode SRT. "
            + " | ".join(failures))

    ordered_entries = []
    for group_indices in sentence_groups(cues, texts):
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
            try:
                group_entries = [resolve_individual(index, cues[index], texts[index]) for index in group_indices]
            except RuntimeError as individual_error:
                if len(group_indices) == 1:
                    raise RuntimeError(
                        f"Cue {group_indices[0] + 1} không thể xử lý đủ nguyên câu trong timecode: "
                        f"{individual_error}") from individual_error

                def on_group_attempt(local_index, attempt, scale):
                    index = group_indices[local_index]
                    emit_attempt(index, cues[index], attempt, MAX_GROUP_SYNTHESIS_ATTEMPTS, "sentence-group", scale)
                try:
                    group_entries = synthesize_sentence_group(
                        voice, group_cues, group_texts, run_root, clip_root, worker_sha,
                        plan_path, plan_key, Path(options.ffmpeg), on_group_attempt)
                except (ValueError, RuntimeError) as group_error:
                    raise RuntimeError(
                        f"Nhóm cue {group_indices[0] + 1}..{group_indices[-1] + 1} không đủ thời gian "
                        f"để xử lý đủ nguyên lời kể cả sau tăng tốc động: {group_error}") from group_error
        ordered_entries.extend(group_entries)

    if len(ordered_entries) != len(cues):
        raise RuntimeError("TTS sentence grouping lost or duplicated cues")

    clips, results = [], []
    for index, (cue, entry) in enumerate(zip(cues, ordered_entries)):
        active_cue, key, cached_path, record, cache_hit, synthesis_calls = entry
        start_sample, target_frames = cue_window(active_cue)
        # The clip already has the required length. Place ALL its samples;
        # never hide a timing failure by truncating the clip at the SRT boundary.
        end_sample = start_sample + record["frames"]
        if end_sample > start_sample + target_frames:
            raise ValueError("Fitted voice exceeds the source speech window")
        clips.append({"path": cached_path, "start": active_cue["voice_start"], "frames": record["frames"],
                      "start_sample": start_sample, "end_sample": end_sample})
        output_status = ("review" if record["status"] != "fit"
                         or active_cue["timing_source"] in ("srt-fallback", "sentence-group") else "fit")
        results.append({"id": cue["id"], "voice": VOICE_NAME, "voice_review": output_status == "review",
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
                        "synthesis_calls": synthesis_calls})
        emit({"event": "cue", "index": index + 1, "total": len(cues), "id": cue["id"],
              "status": output_status, "cache_hit": cache_hit,
              "timing_source": active_cue["timing_source"], "synthesis_calls": synthesis_calls})
    build_master(Path(options.ffmpeg), clips, manifest["duration"], run_root, master_path)
    result = {"schema": 2, "engine": ENGINE, "engine_version": ENGINE_VERSION, "voice": VOICE_NAME,
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
