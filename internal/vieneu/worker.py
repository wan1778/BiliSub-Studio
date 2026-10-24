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
# Backbone pin (HF rev đã verify tải được, không token). SDK tự quản cache;
# worker chỉ kiểm tra rev + file backbone tồn tại, hash đầy đủ do installer pin.
MODEL_REVISION = "61b85e3d937fbbacb387714180e8182823512523"
MODEL_SHA256 = "61b85e3d937fbbacb387714180e8182823512523"
TIMING_ALGORITHM = "whole-cue-vieneu-natural-first-v1"
VOICE_REVISION = MODEL_REVISION + ":" + TIMING_ALGORITHM
VOICE_NAME = "Hải Đăng"
SAMPLE_RATE = 22050
VIENEU_SAMPLE_RATE = 48000
# VieNeu không có núm tốc độ native như Piper: chỉ đọc nhịp tự nhiên, vừa
# khung thì pad, chật thì rút gọn/atempo/nhóm (toàn đồ engine-agnostic).
# base = length = 1.0 nên mọi bound validator (scale .30-1.20, preferred band)
# giữ nguyên ý nghĩa mà không cần nhánh riêng.
FIT_METHOD = "vieneu-natural"
# Batch synth tự nhiên (30/09, RTX 3050 4GB): profile 8 cue đọc đơn tốn 98%
# thời gian ở synth GPU tuần tự (RTF 0.255). Đo 16 câu (~55s audio):
# batch8 RTF 0.071 peak 3251MB, batch16 RTF 0.061 peak 3283MB, batch32 RTF
# 0.065 peak 3283MB (bão hòa, không nhanh thêm). Chốt 16: nhanh nhất mà peak
# vẫn cách trần 4GB ~800MB. Vượt là fallback đọc đơn, không chết job.
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
# Luật pace (01/10 + micro-band 02/10, user duyệt 1.8x rồi 2.0x; chỉ
# VieNeu): tăng tốc KHÔNG vượt 1.5x mọi đường (đơn, nhóm, dự phòng), NGOẠI
# TRỪ cue micro (khung <1.1s) được 2.0x vì carrier prosody từ đơn (~1.0s) mà
# seed nào cũng bó tay (vụ "Vâng" need 1.97x); cue rộng stretch tối đa 1.4x
# thời lượng (pace không dưới ~0.71x). HARD_MAX_SPEED áp trên need
# (input/target); factor ghi record có thể chạm ~1.5005 do FIT_HEADROOM +
# TEMPO_RETRY_MARGIN nên trần record là 1.51 (micro: 3.01 tương ứng). C#
# mirror y hệt theo window, comment lý do.
HARD_MAX_SPEED = 1.5
HARD_MAX_FACTOR = 1.51
# Micro-band (02/10 2.0x, user 07/10 nghe thu mo 2.5x, user 25/10 mo 3.0x): cue micro
    # (khung <1.1s, thuong 1-3 chu) mang carrier prosody co dinh (~1.0s cho
    # tu don nhu "Cam") nen need thuc 1.5-3.0x ma seed nao cung bo tay;
    # atempo tren clip <1s hau nhu trong suot nen tran rieng 3.0x (record
    # 3.01 theo cung margin FIT+T margin). Moi cue con lai giu 1.5x. Luon
    # review (duong tempo). C# mirror y het theo window.
    MICRO_MAX_SPEED = 3.0
    MICRO_MAX_FACTOR = 3.01
MICRO_WINDOW_FRAMES = round(1.1 * SAMPLE_RATE)
# Overflow (user duyet): cue don qua dai (truncate chi giu <50% chu) thi doc
# NGUYEN CAU toc do toi tran window-cap, cho tran khung toi da 3.0s, chap nhan
# de len 1-2 cue sau (khong doi timeline de khoi lech sub hang loat). Chi cue
# don (khong sentence-group/sample). Luon review + spoken_text day du de truy
# vet. C# mirror y het (OverflowBudgetFrames).
OVERFLOW_MAX_SECONDS = 3.0
OVERFLOW_MAX_FRAMES = int(OVERFLOW_MAX_SECONDS * SAMPLE_RATE)
# Tran tre placement nhom (07/10, user BAT BUOC sub hien la voice phai
# doc luon): thanh vien sentence-group dat voice tre hon gio sub du 1
# frame cung khong nhan — tang atempo trong tran (giu dong nhat toc do
# nhom, C# khong can doi) hoac raise de tang tren tach don (single co
# widen/muon khe + review, dat dung gio). Som hon sub van duoc (voice
# dang doc khi sub hien la dung). Khong sua chu, khong vuot pace.
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
# Seeded-retry (02/10): raw chạm đúng 1.440s giữa các lần chạy độc lập =
# model truncate giữa chừng ở max-length (prosody đứt đoạn). Bản raw chạm
# ngưỡng này không bao giờ được nhận làm voice, dù trimmed có vừa.
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
SILENCE_AMPLITUDE = 32
SILENCE_GUARD_FRAMES = round(.05 * SAMPLE_RATE)
# P1 (02/10): model đắp ~0.02-0.07s im tuyệt đối trước mọi cue ngắn.
LEADING_SILENCE_GUARD_FRAMES = round(.005 * SAMPLE_RATE)
MIN_PREFERRED_RATE_SCALE = .70
MAX_PREFERRED_RATE_SCALE = 1.0
# Gói pilot (bản chính xác do installer pin khi đấu C# sau).
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
    # Luật pace (01/10 + micro-band 02/10 2.0x, user 07/10 mo 2.5x):
    # record tempo (nhanh/chậm) nằm trong [SLOW_MIN_SPEED, 1.51] chung
    # hoặc [.., 2.51] khi khung <1.1s; record thường giữ nguyên [1, 100]
    # như cũ. C# mirror y hệt theo window.
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
        # 30/09 (VieNeu, không vòng scale): atempo ngay lượt 1 là bình thường
        # (probe tự nhiên rồi atempo, không vòng lặp scale như Piper) nên
        # native != generated ở record tempo là trung thực, không phải dối.
        # Điều kiện native==generated chỉ áp cho lượt tự nhiên (non-tempo).
        if (fit_method != ATEMPO_METHOD and record.get("timing_source") != "sentence-group"
                and native_reference != generated):
            return False
    if natural_sample and record.get("timing_source") != "sample":
        return False
    needs_review = (tempo_fallback or record.get("timing_source") == "sentence-group"
                    or needs_rate_review(scale, base) or trimmed > 0
                    or padding > padding_budget(target_frames)
                    # 28/09 (M11): rút gọn chữ đọc luôn review dù vừa khít tự
                    # nhiên — không là cache retry tự từ chối + C# lệch status.
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


def try_overflow_voice(index, cue_id, total, active_cue, text, target_frames, voice,
                       base_scale, run_root, clip_root, worker_sha, ffmpeg, on_attempt):
    """Doc NGUYEN CAU toc do toi tran window-cap, cho tran khung (user duyet).

    Tra entry (active, key, path, record, cache_hit, calls) nhu escalate, hoac
    None de caller rot ve truncate cu. Chi cue don (khong sentence-group /
    sample). Budget tran cung OVERFLOW_MAX_FRAMES; qua budget -> None. Moi
    synth dem attempt qua on_attempt de C# accounting khop; record atempo theo
    dung hinh finish_tempo + overflow_frames + spoken_text day du."""
    if active_cue.get("timing_source") in ("sentence-group", "sample"):
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
                    and hit_record["overflow_frames"] > 0
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
    aim_factor = max(cap - 0.02, 1.0)
    out_frames = max(1, math.ceil(natural / aim_factor))
    if out_frames <= target_frames or out_frames - target_frames > OVERFLOW_MAX_FRAMES - 500:
        return None
    final_path = run_root / (overflow_key + "-fit.wav")
    generated = apply_atempo(ffmpeg, trimmed_probe, final_path, natural / max(1, out_frames))
    if generated <= target_frames or generated - target_frames > OVERFLOW_MAX_FRAMES:
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
                      f"(de len cue sau) + co review.")})
    return (active_cue, overflow_key, cached_path, record, False, 1)


def escalate_truncated_voice(index, cue_id, total, active_cue, text, target_frames, voice,
                             base_scale, run_root, clip_root, worker_sha, ffmpeg,
                             on_attempt):
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
    if (esc_units > 8 and active_cue.get("timing_source") not in ("sentence-group", "sample")
            and _overflow_likely_severe(text, target_frames)):
        overflow_entry = try_overflow_voice(
            index, cue_id, total, active_cue, text, target_frames, voice,
            base_scale, run_root, clip_root, worker_sha, ffmpeg, on_attempt)
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
            overflow_entry = try_overflow_voice(
                index, cue_id, total, active_cue, text, target_frames, voice,
                base_scale, run_root, clip_root, worker_sha, ffmpeg, on_attempt)
            if overflow_entry is not None:
                return overflow_entry
            raise RuntimeError("truncate cũng bó tay: không prefix/seed nào vừa khung ở pace cho phép")
        prefix, trunc_key = hit
        preloaded = {str(run_root / (trunc_key + "-native.wav")): True}
    else:
        # Cue dài: phương sai nhỏ nên đo-1-lần + fit-lại như cũ vẫn đúng.
        # Bound theo trần pace của window (micro-band nếu khung <1.1s).
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
        # Ngân sách seed theo độ dài prefix: bản dài cần trúng draw ngắn
        # (hiếm) nên cho nhiều; bản ngắn draw nào cũng vừa, cho ít. Tổng
        # worst-case có chặn (~10+7×4) mà prefix ngắn luôn tới lượt (vụ #351:
        # cap tổng cũ cắt mất prefix 'Không thể nào!' ở đuôi).
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
            # Bóp chi phí (04/10): model đã vào chế độ phình (raw chạm trần)
            # thì seed tiếp vô ích — bằng chứng "Đừng quan tâm" 16/16 draw
            # đều chạm trần. Chỉ đếm ở đây; veto/cắt bên dưới vẫn chạy bình
            # thường cho draw hiện tại, break ở cuối vòng.
            if source >= SEED_TRUNC_SUSPECT_FRAMES:
                capped_streak += 1
            else:
                capped_streak = 0
            # Veto/cắt nội dung TẮT khi chữ tự lặp (audio giống nhau là đúng).
            # Luật hạt nhân chỉ xét bản ≥0.6s nên prefix ngắn ('Không' 0.36s)
            # không bao giờ bị giết oan.
            prefix_repeats = (not text_self_repeats(prefix)
                              and speech_repeats(trim_tmp, speakable_units(prefix)))
            if (kept >= prefix_min and kept > 0
                    and source < SEED_TRUNC_SUSPECT_FRAMES
                    and kept / max(1, target_frames) <= cap_speed_for(target_frames)
                    and not prefix_repeats):
                return prefix, trunc_key
            if (kept >= prefix_min and kept > 0 and prefix_repeats):
                # Bản nhân đôi: cắt instance đầu đè lên candidate để fit_cue
                # tái dùng đúng bản sạch (preloaded).
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
            # 4 draw phình liên tiếp -> prefix này vô vọng, xuống prefix ngắn
            # hơn (đừng đốt thêm GPU; prefix ngắn draw nào cũng vừa).
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
    # natural_sample dùng ở cả 2 nhánh (silence-fallback lẫn native thường):
    # khởi tạo trước nhánh để nhánh native không dính UnboundLocalError khi
    # revalidate cache hit (job editor-tts 08:56 27/09 chết đúng chỗ này).
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
    # VieNeu đọc tag cảm xúc thành tiếng ([cười] là cười thật giữa câu!).
    # Chỉ lột đúng tag điều khiển đã document của VieNeu; ngoặc khác
    # ([nhạc nền], [Phần 1]...) giữ nguyên đọc như chữ thường.
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
    # Shim để fit machinery (viết cho Piper) chạy được mà không sửa call-site:
    # config.length_scale luôn 1.0 (nhịp tự nhiên duy nhất của VieNeu).
    def __init__(self, engine, label: str):
        import types
        self.engine = engine
        self.label = label
        self.config = types.SimpleNamespace(length_scale=1.0, sample_rate=VIENEU_SAMPLE_RATE)


def synthesize_cue(voice, text: str, temporary: Path, length_scale: float = 1.0) -> None:
    import numpy as np
    import soxr
    # length_scale giữ nguyên chữ ký cho khỏi sửa call-site; VieNeu chỉ có
    # nhịp tự nhiên nên tham số này bị bỏ qua có chủ ý.
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
    # Bucket ổn định theo độ dài (và vị trí gốc để thứ tự batch lặp lại
    # nào cũng giống nhau): chunk đồng đều -> ít padding phí + đỉnh VRAM
    # thấp hơn chunk trộn dài-ngắn.
    jobs.sort(key=lambda job: (len(job[1]), job[0]))
    preloaded: dict = {}
    size = max(1, int(batch_size))
    for start in range(0, len(jobs), size):
        chunk = jobs[start:start + size]
        try:
            synthesize_cue_batch(voice, [(text, path) for _, text, path in chunk], size)
        except Exception as error:
            # Chỉ chunk này đọc đơn lại; wav dở của chunk lỗi không vào
            # dict nên fit_cue ghi đè sạch, không bao giờ tái dùng nhầm.
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
    # P1 (02/10): cắt im tuyệt đối cả HAI đầu. Guard đuôi giữ nguyên tham số
    # (bảo vệ decay/reverb); guard đầu cố định 5ms — chỉ sample dưới -60dBFS
    # mới bị cắt nên giọng (kể cả attack mềm) không bao giờ chạm. Trả tổng số
    # mẫu đã cắt; file ra = speech nguyên vẹn trừ im đầu/đuôi nên mọi đẳng
    # thức frames của validator (source-trimmed==generated) giữ nguyên.
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
    lead_cut = max(0, int(active[0]) - LEADING_SILENCE_GUARD_FRAMES)
    retained_end = min(frames, int(active[-1]) + 1 + guard_frames)
    # KHÔNG return sớm khi không có gì để cắt: caller (nhánh cắt lặp escalate)
    # dùng file output VÔ ĐIỀU KIỆN — return sớm để lại bytes cũ từ lần trim
    # trước, copyfile sẽ chép nhầm audio cũ vào candidate rồi fit đo sai
    # (vụ #93b45d9a: chép 1.587s thay vì 0.65s). Luôn ghi để output ≡ input đã
    # trim; tốn 1 lần ghi file không đáng kể.
    with wave.open(str(output_path), "wb") as output:
        output.setparams(params)
        output.writeframes(data[lead_cut * 2:retained_end * 2])
    return frames - (retained_end - lead_cut)


def atempo_filter(factor: float, floor: float = _MIN_TEMPO_FACTOR) -> str:
    # Floor mặc định (2026-09-24): factor ở/dưới pace tự nhiên nghĩa là "không
    # tăng tốc", không phải lỗi. Đường stretch truyền floor=0.5 (giới hạn
    # 1 stage của ffmpeg, stage hợp lệ 0.5..100).
    if not math.isfinite(factor) or factor <= floor:
        # Đường nhanh giữ nguyên ngữ nghĩa cũ (passthrough ~1.0, không raise
        # để vòng retry nhóm tiếp tục hội tụ); đường chậm floor ở 0.5.
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


# Cắt lặp (03/10): khi mọi bản đọc đều nhân đôi, cắt lấy instance đầu ở khe
# im giữa file (đã verify: cắt "Vâng! Vâng!" ở 0.36s ra "Vâng!" nguyên vẹn).
# Chỉ cắt khi khe im thật (<-50dBFS rộng ≥60ms, nằm giữa 15%-85% file),
# snap zero-crossing ±3ms + fade 64 mẫu chống click. Raise nếu không đủ điều
# kiện để caller thử cách khác.
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


# Chống đọc lặp (03/10): model hay đọc nhân đôi câu ngắn ("Vâng! Vâng!",
# "Vào đi! Vào đi!" — đã cho Whisper nghe lại xác nhận). Hai luật phát hiện:
# (1) autocorrelation envelope peak ≥0.30 ở lag ~nửa độ dài (nhân đôi rõ);
# (2) luật hạt nhân: đỉnh năng lượng vượt số chữ + có khe xa ≥0.35s (bắt cả
# nhân đôi khác ngữ điệu mà ACF lọt, vụ "Vâng! Vâng!" peak ACF chỉ 0.10).
# Hiệu chuẩn trên mẫu thật: câu đơn không bao giờ đủ cả hai. Audio <0.4s
# không xét (ACF nhiễu). numpy-only, ~1ms.
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


# U1 (03/10, tốc độ): atempo in-process bằng phase vocoder của torchaudio
# thay vì spawn ffmpeg.exe mỗi lần (Windows spawn ~0.1-0.3s x hàng chục nghìn
# lượt cho 16k cue). Cùng ngữ nghĩa floor/stage với atempo_filter; độ dài ra
# do caller đo lại như cũ nên vòng retry/validation không đổi gì. Lỗi bất kỳ
# -> rớt về FFmpeg (đường cũ giữ nguyên để so AB).
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
    # Trần pace (02/10): 1.5x chung, 2.0x micro-band khi khung <1.1s. Need
    # vượt trần thì raise ngay, khỏi tốn synth/GPU (caller rớt sang rút gọn/
    # truncate theo chuỗi cấm-im).
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
        # Chưa đầy khung mà pace vẫn trên trần chậm -> kéo thêm (factor nhỏ
        # dần); pace chạm trần thì raise ở vòng sau.
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
    # VieNeu: 1 pass nhịp tự nhiên duy nhất (không vòng scale — đo lặp cùng
    # nhịp chỉ tốn synth). Không vừa thì rơi xuống atempo bên dưới như cũ.
    attempt = 1
    measurements = []
    # Batch probe tự nhiên cả nhóm (30/09): N synth độc lập gộp thành ceil(N/8)
    # infer_batch. Lỗi batch (kể cả OOM) -> đọc đơn từng cue như cũ.
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
        # Chống đọc lặp (03/10): thành viên ngắn bị nhân đôi thì cắt lấy
        # instance đầu (rẻ, chắc); không cắt được mới đọc đơn lại 1 seed.
        # Chữ tự lặp thì bỏ qua (audio giống nhau là đúng).
        # FIX 05/10 (vụ C# line 495: 8 cue sentence-group file dài hơn số
        # liệu khai): cắt xong mà re-trim trả 0 thì trimmed_path đang giữ
        # BẢN CẮT (raw vẫn là audio cũ chưa cắt) — bắt buộc dùng
        # trimmed_path, cấm rơi về raw_path. cut_ok=True nghĩa là candidate
        # nằm ở trimmed_path bất kể trimmed có 0 hay không (chỉ khi cut_ok
        # False — tức không cắt hoặc đã re-synth ghi đè raw — mới được
        # rơi về raw theo trimmed).
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
        # Trần pace nhóm (02/10): cùng micro-band theo tổng khung nhóm
        # (nhóm micro <0.8s cũng được 2.0x) — need vượt trần thì raise ngay
        # (rớt về voice lẻ/truncate từng cue, cấm im).
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
            # 07/10: tre placement (BAT BUOC bang 0) cung la ly do GROW nhu
            # overflow — tang toc deu ca nhom (giu dong nhat) cho toi khi vua
            # khung, cham tran thi raise de tach don (single dat dung gio).
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
    # Luoi phong thu 07/10: moi duong vao day deu phai qua chon loc
    # (natural/atempo co tran tre) — lech la raise ngay de tang tren tach
    # don dat dung gio, khong bao gio de record tre lot xuong validator.
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
        # 28/09 vụ 2h xong lỗi 1 cue: cờ atempo là của CẢ NHÓM, nhưng
        # thành viên không tăng tốc thật (factor ~1, output == input)
        # mà khai atempo là record dối -> C# từ chối cả 17611
        # cue. Chỉ thành viên tăng tốc thật mới khai atempo.
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
        # FIX 05/10: lưới phòng thủ — file cache PHẢI dài đúng target_frames
        # (vụ C# line 495: số liệu khai 8418 nhưng file 31752 do nhầm
        # raw/trimmed sau cắt lặp). Lệch là raise ngay kèm id để tầng trên
        # cách ly cue đó (P2), không bao giờ để record dối lọt xuống validator
        # rồi đánh sập cả job 9k cue.
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


# Rút gọn chữ đọc cho cue chật (user 27/09: cấm đọc nhanh hơn gió).
# Tầng tích lũy, thuần logic + deterministic (test được không cần model):
# V1 dọn lặp/khoảng trắng -> V2 bỏ tiếng gọi đầu câu -> V3 bỏ lơi cuối câu
# -> V4 bỏ từ nhấn mạnh. Mỗi tầng chỉ thêm khi ngắn đi thật và còn chữ đọc
# được. Không đụng SRT hiển thị; bản đọc được ghi vào spoken_text + cờ review.
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
    # Thứ tự 07/10 (user: voice đọc khác sub vì mất chữ lặp diễn cảm):
    # bỏ lơi/thừa trước (V2/V3/V4, ít đau nhất), gộp từ lặp (V1) CUỐI CÙNG
    # — chỉ khi các bản nhẹ không vừa mới đụng tới chữ lặp. Caller nhận bản
    # đầu vừa khít nên thứ tự này giữ "chậm thôi, chậm thôi" nguyên vẹn
    # mỗi khi bỏ "Này"/"quá" là đủ vừa.
    current = tokens
    # V2: bỏ tiếng gọi đầu câu khi còn đủ chữ.
    if len(current) > 3 and _strip_punct(current[0]).lower() in _FILLER_LEAD:
        current = push(current[1:], "bỏ tiếng gọi đầu câu")
    # V3: bỏ lơi cuối câu khi còn đủ chữ.
    if len(current) > 3 and _strip_punct(current[-1]).lower() in _FILLER_TRAIL:
        current = push(current[:-1], "bỏ lơi cuối câu")
    # V4: bỏ từ nhấn mạnh (giữ mệnh đề, nhẹ sắc thái).
    lightened = _drop_intensifiers(current)
    if len(lightened) != len(current):
        push(lightened, "bỏ từ nhấn mạnh")
        current = lightened
    # V1 CUỐI: dọn từ lặp liên tiếp (giữ 1) + khoảng trắng thừa.
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
        # Câu dài (>8 đơn vị): giữ nguyên hành vi V1 (ACF-only). Luật hạt
        # nhân theo số chữ + tripwire + 2-cụm chỉ hiệu chuẩn cho cue ngắn;
        # trên câu dài chúng báo oan hàng loạt (đo 12/10: 36/36 câu control
        # sạch bị gắn cờ khi gọi nuclear với max_words).
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
            preloaded: dict | None = None):
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
    # Ca ship nghi lặp mà mọi cách đều lặp (không còn bản sạch để chọn):
    # vẫn ship để không im lặng (luật L1) nhưng cắm cờ -> spoken_text +
    # status review để user tìm nghe lại. Chỉ set ở điểm ship Phase-3
    # (đường natural cần cờ; đường tempo review sẵn). Không set khi chỉ
    # loại bản vẽ (tránh flood review oan).
    repeat_suspect = False

    def measure(active_text, active_scale):
        nonlocal attempt, probe_reused
        attempt += 1
        on_attempt(attempt, active_scale)
        # Probe batch từ pre-pass (chỉ đúng 1 lần đầu, đúng text gốc, đúng
        # file đã tổng hợp): variant rút gọn chữ khác text nên không bao giờ
        # tái dùng nhầm. Sai lệch gì cũng rớt về đọc đơn như cũ.
        if (not probe_reused and active_text == text and preloaded is not None
                and str(candidate) in preloaded and candidate.is_file()):
            probe_reused = True
        else:
            synthesize_cue(voice, active_text, candidate, active_scale)
        return measure_file(candidate)

    measure_seq = [0]

    def measure_file(path):
        # Mỗi LẦN ĐO có file trim RIÊNG (số thứ tự đơn điệu): cùng input mà
        # tổng hợp lại nội dung khác (variant dùng chung candidate, reseed)
        # thì lần đo sau vẫn ghi đè tuple lần trước — record vênh
        # (tempo_in != source-trimmed) và rớt validation oan, thậm chí voice
        # nhầm bytes (vụ #93b45d9a: variant 1.587s đè bytes Phase-0 1.4s).
        # Tên file tạm không vào record/key nên cache/sha an toàn tuyệt đối.
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
            # Chữ đọc đúng như hiển thị nhưng audio nghi lặp: cắm chữ để C#
            # (và native_record_valid) bắt review, user tìm nghe lại được.
            # SRT hiển thị không dùng field này.
            record["spoken_text"] = active_text
            emit({"event": "notice", "code": "condense-fit",
                  "message": (f"Cue {cue_id} quá chật: rút gọn ({note}) đọc nhịp tự nhiên + cờ review. "
                              f"SRT hiển thị giữ nguyên.")})
        return output_path, record

    def finish_tempo_or_natural(tempo_out, tempo_in, gen, factor, tempo_tries, speed,
                                raw, native, src, trim, m_text, m_note):
        # Passthrough suy biến (floor-clamp factor đúng 1.0, in==gen — gặp khi
        # cue vừa-y-hệ, vocoder ra byte-chuẩn nên validator `tempo_input >
        # generated` rớt): audio LÀ bản tự nhiên -> ghi record natural cho
        # trung thực. Review giữ nguyên (trim/pad đã đủ điều kiện review).
        # Không đụng validator (worker + C# đều chấp nhận natural+pad+review).
        if factor == 1.0 and tempo_in == gen:
            return finish(m_text, m_note, (src, trim, gen, tempo_out),
                          scale, raw, native, speed)
        return finish_tempo(tempo_out, tempo_in, gen, factor, tempo_tries, speed,
                            raw, native, src, trim)

    def finish_tempo(tempo_out, tempo_in, gen, factor, tempo_tries, speed,
                     raw, native, src, trim):
        # Record atempo y hệt nhánh cũ (validator C#/native_record_valid chỉ
        # đọc hình dạng record, không đọc thứ tự phase): đọc nhanh nguyên câu
        # luôn review, giữ cao độ, không cắt chữ.
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

    # Phase 0: probe bản gốc ở nhịp base (attempt 1, đo đạc y hệt vòng lặp cũ).
    source_frames, trimmed_silence, frames, fitted_source = measure(text, scale)
    base_measured = (source_frames, trimmed_silence, frames, fitted_source)
    raw_frames, native_reference_frames = source_frames, frames
    actual_speed = 1.0
    if frames < minimum_generated:
        raise RuntimeError(
            f"VieNeu returned only {frames / SAMPLE_RATE:.3f}s of active speech for a non-empty cue")
    # Chống đọc lặp (03/10): bản đọc-duy-nhất vừa khung nhưng bị nhân đôi
    # ("Vâng! Vâng!") thì KHÔNG nhận — ép sang reseed tìm bản sạch. Hai dấu
    # hiệu: veto nội dung (tự tương quan + hạt nhân), hoặc dài bất thường so
    # với chữ (ngưỡng hiệu chuẩn: chữ đơn đọc đúng ~0.3-0.5s, bản ≥0.55s là
    # nghi — hoặc đọc chậm thật, hoặc đã nhân đôi). Chỉ cue ngắn (bản dài lặp
    # từng phần cần căn chữ, làm phase sau).
    short_units = speakable_units(text)
    too_long_for_text = (short_units <= 8
                         and frames > (short_units * 0.35 + 0.2) * SAMPLE_RATE)
    primary_repeats = (short_units <= 8 and not text_self_repeats(text)
                         and speech_repeats(fitted_source, short_units))
    primary_suspect = primary_repeats or too_long_for_text
    # Kiểm cuối góc ACF-leaning (11/10): primary lọt veto trên nhưng ACF vẫn
    # thấy lặp (đã chứng minh trên bản ship lặp thật) thì cũng coi là nghi —
    # dồn về seed loop tìm bản sạch thay vì ship thẳng. Rẻ (~ms), fail-open.
    if not primary_suspect and not text_self_repeats(text) \
            and final_pick_repeats(fitted_source, text):
        primary_suspect = True
    if primary_suspect:
        emit({"event": "notice", "code": "repeat-retry",
              "message": (f"Cue {cue_id} bản đọc nghi nhân đôi/dài bất thường, "
                          f"thử lại seed khác tìm bản sạch + cờ review.")})
    if frames <= target_frames and MIN_ACTUAL_SPEED <= actual_speed <= MAX_ACTUAL_SPEED \
            and not primary_suspect:
        # Phase 0b (01/10, stretch): cue rộng mà pad im quá budget review ->
        # đọc chậm nguyên câu cho đầy khung (tối đa 1.4x thời lượng, giữ cao
        # độ) thay vì 0.5s chữ + 1s im. Vượt trần thì pad như cũ. Luôn review.
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
    # Phase 1 (30/09, atempo-first): cue chật vừa phải -> đọc nhanh NGUYÊN CÂU
    # bằng atempo giữ cao độ trước; rút gọn chữ chỉ khi vượt trần pace. Trần
    # dùng chung ngưỡng FAST_SINGLE_NEED_RATIO của watch-list (single cần
    # >1.43x là ứng viên sửa SRT thủ công): trong trần thì pace nhanh vẫn
    # tự nhiên (4.80 x 1.43 ~ 6.9 chữ/s, phủ tới p90 khung giờ).
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
    # Phase 1.5 (02/10, seeded-retry): synth diffusion phương sai KHỔNG LỒ ở
    # cue ngắn — cùng chữ "Vào đi." synth 5 lần ra raw 0.80s / 1.44s x4
    # (trimmed 0.58s / 1.27-1.44s). torch.manual_seed khóa được output (cùng
    # seed -> bit-identical, đã verify 02/10) nên khi nguyên câu vượt
    # pace-first mà chữ ngắn thì thử lại ≤4 seed định trước (sha từ key nên
    # chạy lại nào cũng cùng dãy seed -> byte-identical, cache/sha an toàn),
    # giữ bản NGẮN NHẤT còn nguyên rồi atempo như Phase 1. Không trúng thì
    # rơi xuống Phase 2 như cũ. Không đụng trần 1.5x (L2), không đụng chữ,
    # không rút gọn oan. Chỉ cue ngắn (≤8 đơn vị): cue dài phương sai nhỏ,
    # retry không cứu được mà tốn synth; primary vẫn unseded để giữ cache cũ.
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
            # Họ seed xa ĐI TRƯỚC (V2): primary đã nghi thì seed gần hay cùng
            # attractor — 1 draw họ xa ngay đầu thường thoát sớm, đỡ tốn attempts.
            # Salt khác hẳn họ near nên dãy seed vẫn định trước, cache/sha an toàn.
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
                # Bóp chi phí (04/10): draw chạm trần liên tiếp = model kẹt chế
                # độ phình với chữ này, thử tiếp vô ích (bằng chứng 16/16).
                # Dừng sớm, để condense/truncate xử lý tiếp.
                if seed_source >= SEED_TRUNC_SUSPECT_FRAMES:
                    capped_streak += 1
                    if capped_streak >= 4:
                        break
                    continue
                capped_streak = 0
                if (seed_frames < minimum_generated or seed_frames <= 0):
                    continue
                if not text_self_repeats(text) and speech_repeats(seed_fitted, speakable_units(text)):
                    # Bản nhân đôi: thử cắt lấy instance đầu (khe im giữa).
                    # Bản sạch luôn ưu tiên hơn bản cắt (tự nhiên hơn).
                    # Chữ tự lặp thì bỏ qua (audio giống nhau là đúng).
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
                # Chốt sớm CHỈ khi vừa khung VÀ sạch lưới V2 (11/10 chỉ check
                # vừa khung nên bản nhân đôi-vừa-khung lọt thẳng; 12/10 thêm
                # cửa thống nhất: ACF/hạt nhân/dài-bất-thường/2-cụm).
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
                # Kiểm cuối (11/10): bản thắng mà vẫn nghi lặp thì thử punct-swap
                # có trần trước khi ship; vẫn lặp thì ship (tempo luôn review nên
                # user tìm được, không cần cờ riêng ở nhánh này).
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
    # Phase 2: cue quá chật (vượt trần pace) -> rút gọn chữ đọc theo tầng
    # (user 27/09: cấm đọc nhanh hơn gió). Variant độc lập nhau nên batch 1
    # lượt thay vì đọc đơn từng bản (30/09): nhận bản đầu vừa khít, bản thừa
    # bỏ. Batch lỗi -> đọc đơn từng bản như cũ. Tổng synth toàn hàm không quá
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
        # Kiểm cuối (11/10): veto trên đã lọc lặp rõ; góc ACF-leaning bắt thêm
        # ca lọt (đã chứng minh trên bản ship lặp thật) — bỏ qua variant này,
        # còn variant khác thì thử tiếp, hết thì rớt xuống Phase 3 xử lý.
        if not text_self_repeats(variant) and final_pick_repeats(variant_fitted, variant):
            continue
        short_text, short_note = variant, note
        short_measured = measured
        if variant_frames <= target_frames:
            return finish(variant, note, measured, base,
                          variant_source, variant_frames, 1.0)
    # Phase 3 VieNeu: KHÔNG có núm tốc độ native — vòng lặp scale sẽ đo đi đo
    # lại cùng 1 nhịp, đốt synth vô ích (mỗi vòng trả cùng số frames). Bỏ vòng,
    # đi thẳng atempo trên bản NGẮN NHẤT giữa Phase 0 và Phase 2 (xấu nhất cũng
    # là ngắn-nhanh + review). Lấy ngắn nhất thật theo số đo (vụ #93b45d9a:
    # bản Phase 0 vừa trần bị bỏ để atempo bản variant dài hơn rồi chết oan).
    # Giữ ngữ nghĩa raw/native như vòng đầu của bản cũ.
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
    # Kiểm cuối Phase-3 (11/10): bản định ship mà nghi lặp (lọt mọi veto trên)
    # thì thử punct-swap có trần; vẫn lặp thì ship + cờ review (finish() lo),
    # không bao giờ im, không raise thêm (raise ở đây giết cả job oan).
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
        # Trung thực nội dung (01/10): atempo trên bản rút gọn thì khai
        # spoken_text như nhánh condense (review đã có sẵn do tempo).
        if active_text != text and not record.get("spoken_text"):
            record["spoken_text"] = active_text
        return tempo_out, record
    except RuntimeError as error:
        # Luật 01/10 (L1): CẤM im lặng mọi cue — atempo chạm trần mà rút gọn
        # vẫn không vừa thì raise để chuỗi trên (truncate/escalate) xử lý,
        # tuyệt đối không trả clip câm. Caller bắt (ValueError, RuntimeError).
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
        raise RuntimeError("NGHI master FLAC is missing")
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
    # Luật 01/10 (L3): cue không-Việt là lỗi dữ liệu — báo 1 lần liệt kê ĐỦ
    # cue (số thứ tự/id/timecode/chữ) TRƯỚC khi tốn GPU, user sửa SRT rồi chạy
    # lại. Cấm im lặng, cấm cố đọc bừa.
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
    # GPU bắt buộc (user chốt): không CUDA là fail ngay, không rớt CPU lặng lẽ.
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
                    cue_id=candidate_cue.get("id", ""), preloaded=preload)
            except (ValueError, RuntimeError) as error:
                failures.append(f"{candidate_cue['timing_source']}: {error}")
                continue
            if phase_calls != candidate_record["synthesis_attempts"]:
                raise RuntimeError("TTS attempt accounting mismatch")
            candidate_record.update({"key": candidate_key, "sha256": sha256(final_path)})
            final_path.replace(candidate_path)
            atomic_json(candidate_path.with_suffix(".json"), candidate_record)
            return candidate_cue, candidate_key, candidate_path, candidate_record, False, synthesis_calls
        # Luật 01/10 (L1): CẤM im lặng — mọi candidate đều rớt thì truncate
        # (prefix dài nhất vừa ở pace ≤1.5x, luôn voice + spoken_text + review);
        # truncate cũng bó tay (cửa sổ bệnh) thì raise fail-loud kèm id cue.
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
                Path(options.ffmpeg), on_escalate)
            return (entry[0], entry[1], entry[2], entry[3], False, synthesis_calls)
        except (ValueError, RuntimeError) as error:
            raise RuntimeError(
                f"Cue {index + 1} ({cue['id']}) bất khả thi: {' | '.join(failures)}; "
                f"truncate cũng bó tay ({error})") from error

    # Pre-pass toàn cục cho cue đơn (không đường group-plan nên preload luôn
    # được tiêu thụ; cue cache-hit đã bị bỏ qua bên trong preload_probes).
    # NOTE: all_groups phải gán TRƯỚC call này (UnboundLocalError 10/10 khi
    # refactor đặt call lên trước dòng gán) — test wiring-pin bên dưới.
    ordered_entries = []
    # P2 (02/10, cách ly lỗi): cue vật lý bất khả thi không được giết cả job
    # (vụ 169/1095/3407: chết con đầu -> rerun chết con tiếp, vô hạn). Ghi nhận
    # {n, id, chữ, cửa sổ, lý do} rồi chạy TIẾP hết project; cuối job fail 1
    # lần duy nhất với DANH SÁCH ĐẦY ĐỦ (không master dở, không im lặng L1,
    # không vượt trần L2). Clip cache khiến cue đã voice không bao giờ lỗi
    # lại -> mỗi vòng danh sách chỉ co lại, không lặp vô hạn.
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
    # Pre-pass toàn cục cho cue đơn (không đường group-plan nên preload luôn
    # được tiêu thụ; cue cache-hit đã bị bỏ qua bên trong preload_probes).
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
            # Nhóm nhiều cue rớt plan-cache: batch probe thành viên rồi mới
            # đọc đơn từng cue (preload chỉ tiêu khi load_clip rớt thật).
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
                    # P2: cách ly thay vì giết job — ghi nhận rồi chạy tiếp,
                    # cuối job báo đủ danh sách 1 lần.
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
                        # Nhóm không nén vừa nhưng từng câu lẻ đã có kết quả
                        # riêng (đọc nhanh/truncate + cờ review): giữ lại thay
                        # vì fail lây cả nhóm. Mỗi entry lẻ đã qua cùng
                        # validation với chế độ đọc đơn nên timeline và review
                        # flag giữ nguyên ý nghĩa. (L1: entry lẻ không bao giờ
                        # im — resolve_individual đã cấm silence.)
                        emit({"event": "notice", "code": "silence-fallback",
                              "message": (f"Nhóm cue {group_indices[0] + 1}..{group_indices[-1] + 1} "
                                          f"không nén vừa timecode ({group_error}); giữ lại voice từng câu, "
                                          f"cần nghe lại.")})
                        group_entries = resolved
                    else:
                        # L1: nhóm rớt mà không có kết quả lẻ nào -> truncate
                        # từng cue (luôn voice + review); cue nào truncate cũng
                        # bó tay thì fail-loud kèm id để sửa SRT.
                        group_entries = []
                        group_ok = True
                        for pos in range(len(group_indices)):
                            gindex = group_indices[pos]
                            try:
                                _, gtarget = cue_window(group_cues[pos])
                            except ValueError as window_error:
                                # P2: timecode vô lý cũng chỉ cách ly cue đó,
                                # vẫn thử nốt đuôi nhóm để danh sách đủ ngay.
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
                                    Path(options.ffmpeg), on_group_truncate)
                            except (ValueError, RuntimeError) as trunc_error:
                                # P2: cue truncate bó tay -> cách ly + thử nốt
                                # đuôi nhóm để danh sách đủ ngay trong 1 run.
                                # Entry lẻ thành công vẫn bị bỏ (giữ căn
                                # timeline tuyệt đối); chạy lại sẽ tổng hợp lại.
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

    # Gắn lại nhóm (P2b, 04/10): record thành viên nhóm do hợp giọng chung —
    # đọc đơn lại 1 thành viên ở vòng sau giữ nhãn nhóm nhưng số liệu đơn lẻ
    # (native/speed riêng), C# đòi cả nhóm đồng nhất (native/speed/placement)
    # nên chết cả job dù từng cue đều voice được (vụ df0b: hàng chục nhóm).
    # Quét trước vòng ráp, mirror y hệt C# ValidateSentenceGroupWindows +
    # per-cue validation: nhóm nào không đồng nhất thì tổng hợp lại CẢ NHÓM
    # (đồng nhất theo cấu tạo); nhóm nào vẫn hỏng thì cách ly cả nhóm, chạy
    # tiếp. Không bao giờ vá số liệu nhóm vào record đơn (dối metadata).
    # (worker/C# gom nhóm cùng rule trên cùng manifest nên span trùng nhau.)
    dead: set[int] = set()

    def _group_entries_uniform(group_indices) -> bool:
        # Mọi truy cập record/window đều có thể hỏng (dữ liệu cache cũ) —
        # trả False để re-synth/cách ly, không bao giờ throw ra ngoài.
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
            # Mirror C# y hệt (low có -1e-9, high strict): nhóm nào C# sẽ đánh
            # rớt thì phải gắn lại/cách ly ngay tại đây, không để lọt xuống
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
            # Tầng 2: tách nhóm ra đọc đơn từng cue (timing đơn lẻ -> C# bỏ
            # qua kiểm tra nhóm, chỉ còn per-cue validation). Vòng ráp sau
            # (refit/escalate/cách ly từng cue) xử lý nốt như cue đơn thường.
            # Chỉ cue nào bó tay thật mới vào danh sách cuối.
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
                    # Bắt rộng có chủ ý ở tầng tách nhóm: 1 cue lạ không được
                    # giết cả job 16k cue; lý do ghi đủ vào danh sách cuối.
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
        # Fail-fast từng cue (28/09, vụ 2h xong lỗi vì 1 record atempo rỗng).
        # Luật user 01/10 (L1+L3): CẤM IM mọi cue — record hỏng thì đọc đơn
        # lại, không xong thì truncate (voice + spoken_text + review). Cue
        # không-Việt hoặc vật lý bất khả thi thì BÁO LỖI để sửa SRT (preflight
        # đã quét trước GPU; đây là lưới phòng thủ). native_record_valid
        # mirror y hệt C# nên chỉ chặn đúng thứ C# chặn.
        try:
            min_frames = minimum_speech_frames(texts[index])
        except ValueError:
            min_frames = None
        speakable = min_frames is not None and vietnamese_word_ratio(texts[index]) >= 0.5
        if min_frames is not None and not native_record_valid(
                record, target_frames, active_cue["timing_source"] == "sample", min_frames):
            if not speakable:
                # L3: cue không-Việt là lỗi dữ liệu (preflight báo trước GPU;
                # tới đây nghĩa là lọt lưới) — P2: cách ly + chạy tiếp, cuối
                # job báo đủ danh sách, cấm im.
                isolate_failure(index, "không phải tiếng Việt")
                continue
            else:
                if active_cue["timing_source"] == "sentence-group":
                    # Tripwire P2b: pre-pass đã gắn lại/cách ly mọi nhóm hỏng
                    # nên không bao giờ tới đây. Đọc đơn lẻ sẽ phá đồng nhất
                    # nhóm (native/speed riêng) -> cách ly thay vì voice bậy.
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
                            Path(options.ffmpeg), on_refit)
                    except (ValueError, RuntimeError) as trunc_error:
                        # P2: truncate bó tay -> cách ly + chạy tiếp (cuối job
                        # báo đủ danh sách), tuyệt đối không im.
                        isolate_failure(index, trunc_error)
                        continue
            active_cue, key, cached_path, record, cache_hit, synthesis_calls = entry
            start_sample, target_frames = cue_window(active_cue)
        # The clip already has the required length. Place ALL its samples;
        # never hide a timing failure by truncating the clip at the SRT boundary.
        end_sample = start_sample + record["frames"]
        overflow = record.get("overflow_frames", 0)
        overflow_ok = (type(overflow) is int and 0 < overflow <= OVERFLOW_MAX_FRAMES
                       and record["frames"] == target_frames + overflow
                       and end_sample <= math.ceil(manifest["duration"] * SAMPLE_RATE))
        if end_sample > start_sample + target_frames and not overflow_ok:
            # P2: cách ly + chạy tiếp như mọi lỗi từng cue khác.
            isolate_failure(index, "voice vượt cửa sổ speech")
            continue
        clips.append({"path": cached_path, "start": active_cue["voice_start"], "frames": record["frames"],
                      "start_sample": start_sample, "end_sample": end_sample})
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
