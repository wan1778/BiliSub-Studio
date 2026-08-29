from __future__ import annotations

import argparse
import hashlib
import importlib.metadata
import json
import math
import os
import subprocess
import sys
import wave
from pathlib import Path

ENGINE = "nghi-tts"
ENGINE_VERSION = "nghi-tts-1.0.0"
MODEL_SHA256 = "2140977786d76d834736c059dacfa553d4931dac2b2c7aaaea438bb2aa9da697"
CONFIG_SHA256 = "971f57f8d504223fee5b40d664f503cf769baf7db21f7d2ae0554a75d07de2f8"
TIMING_ALGORITHM = "whole-cue-piper-rate-v5"
VOICE_REVISION = MODEL_SHA256 + ":" + CONFIG_SHA256 + ":" + TIMING_ALGORITHM
VOICE_NAME = "ngoc_huyen"
SAMPLE_RATE = 22050
FIT_METHOD = "piper-length-scale"
MAX_SYNTHESIS_ATTEMPTS = 10
# Preserve Vietnamese voice identity instead of forcing arbitrary slots. Rates
# outside the preferred range are review-only; rates outside the accepted range
# fail closed and require shorter SRT text or a wider source timecode.
MIN_RATE_SCALE = 0.85
MAX_RATE_SCALE = 1.20
MIN_PREFERRED_RATE_SCALE = 0.90
MAX_PREFERRED_RATE_SCALE = 1.15
PACKAGES = {"piper-tts": "1.7.0", "onnxruntime": "1.22.1", "vietnormalizer": "0.2.3", "numpy": "2.5.2"}


def emit(payload: dict) -> None:
    print(json.dumps(payload, ensure_ascii=False, separators=(",", ":")), flush=True)


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
    # Native model durations are quantized and may vary between passes. Accept
    # only a small short: at most 40 ms AND 2% of the target (minimum one frame).
    return max(1, min(round(.04 * SAMPLE_RATE), target_frames // 50))


def needs_rate_review(length_scale: float, base_length_scale: float) -> bool:
    return not MIN_PREFERRED_RATE_SCALE <= length_scale / base_length_scale <= MAX_PREFERRED_RATE_SCALE


def native_record_valid(record: dict, target_frames: int, natural_sample: bool) -> bool:
    base, scale = record["base_length_scale"], record["length_scale"]
    generated, padding, attempts = record["generated_frames"], record["padding_frames"], record["synthesis_attempts"]
    if (record["fit_method"] != FIT_METHOD or not math.isfinite(base) or base <= 0
            or not math.isfinite(scale) or not MIN_RATE_SCALE <= scale / base <= MAX_RATE_SCALE
            or type(generated) is not int or generated <= 0 or type(padding) is not int
            or not 0 <= padding <= padding_budget(target_frames) or generated + padding != record["frames"]
            or type(attempts) is not int or not 1 <= attempts <= MAX_SYNTHESIS_ATTEMPTS):
        return False
    if (attempts == 1 and (scale != base or abs(record["raw_duration"] - generated / SAMPLE_RATE) > 1e-9)
            or natural_sample and (padding != 0 or attempts != 1)):
        return False
    return record["status"] == ("review" if needs_rate_review(scale, base) else "fit")


def load_clip(path: Path, key: str, cue: dict):
    try:
        record = json.loads(path.with_suffix(".json").read_text(encoding="utf-8"))
        if record["key"] != key or record["sha256"] != sha256(path):
            return None
        samples = read_wav(path)
        duration = len(samples) / SAMPLE_RATE
        _, target_frames = cue_window(cue)
        if not math.isfinite(record["raw_duration"]) or record["raw_duration"] <= 0:
            return None
        natural_sample = cue["timing_source"] == "sample" and record["raw_duration"] <= target_frames / SAMPLE_RATE
        if (record["frames"] != len(samples) or record["target_frames"] != target_frames
                or record["timing_source"] != cue["timing_source"] or len(samples) > target_frames
                or (not natural_sample and len(samples) != target_frames)
                or (natural_sample and abs(record["raw_duration"] - duration) > 1 / SAMPLE_RATE)
                or not math.isfinite(record["fitted_duration"]) or abs(record["fitted_duration"] - duration) > 1e-9
                or not native_record_valid(record, target_frames, natural_sample)):
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
    # Copy all model-produced PCM unchanged; only a small trailing remainder may
    # be silence. This is not stretching, resampling or cutting spoken audio.
    with wave.open(str(source_path), "rb") as source:
        frames = source.getnframes()
        data = source.readframes(frames)
        if not 0 <= target_frames - frames <= padding_budget(target_frames) or len(data) != frames * 2:
            raise ValueError("Refusing to cut speech or hide a duration mismatch with silence")
        with wave.open(str(output_path), "wb") as output:
            output.setparams(source.getparams())
            output.writeframes(data)
            output.writeframes(b"\0\0" * (target_frames - frames))


def fit_cue(voice, text: str, target_frames: int, run_root: Path, key: str, timing_source: str, on_attempt):
    if target_frames <= 0:
        raise ValueError("Source speech duration must contain at least one sample")
    base = float(voice.config.length_scale)
    if not math.isfinite(base) or base <= 0:
        raise ValueError("Invalid length_scale in verified model config")
    scale = base
    raw_frames = 0
    aim_frames = max(1, target_frames - padding_budget(target_frames) // 2)
    lower, upper = None, None
    candidate = run_root / (key + "-native.wav")
    final_path = run_root / (key + "-fit.wav")
    for attempt in range(1, MAX_SYNTHESIS_ATTEMPTS + 1):
        on_attempt(attempt, scale)
        synthesize_cue(voice, text, candidate, scale)
        frames = len(read_wav(candidate))
        if attempt == 1:
            raw_frames = frames
        natural_sample = timing_source == "sample" and attempt == 1 and frames <= target_frames
        if natural_sample or 0 <= target_frames - frames <= padding_budget(target_frames):
            padding = 0 if natural_sample else target_frames - frames
            output_path = candidate
            if padding:
                pad_exact_clip(candidate, final_path, target_frames)
                output_path = final_path
            output_frames = len(read_wav(output_path))
            if output_frames != frames + padding:
                raise RuntimeError("Voice duration did not match the native sample count")
            record = {"raw_duration": raw_frames / SAMPLE_RATE, "fitted_duration": output_frames / SAMPLE_RATE,
                      "frames": output_frames, "target_frames": target_frames, "timing_source": timing_source,
                      "fit_method": FIT_METHOD, "base_length_scale": base, "length_scale": scale,
                      "generated_frames": frames, "padding_frames": padding, "synthesis_attempts": attempt,
                      "status": "review" if needs_rate_review(scale, base) else "fit"}
            return output_path, record
        if frames > target_frames:
            upper = scale if upper is None else min(upper, scale)
        else:
            lower = scale if lower is None else max(lower, scale)
        # Model randomness can contradict an earlier bracket; never treat it as
        # exact linear duration control. Measurements and the attempt cap decide.
        if lower is not None and upper is not None and lower >= upper:
            lower, upper = None, None
        corrected = next_length_scale(scale, frames, aim_frames, base, lower, upper)
        if math.isclose(corrected, scale, rel_tol=0, abs_tol=1e-7):
            break
        scale = corrected
    raise RuntimeError("Piper không thể đọc vừa timecode trong biên giữ chất giọng 0,85–1,20×; hãy rút gọn câu SRT Việt hoặc nới timecode. Không ép giọng, kéo tốc độ file hay cắt chữ")


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
                or not start <= voice_start < voice_end <= end or cue["timing_source"] not in ("whisper", "sample")):
            raise ValueError("Missing or invalid source speech window")
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
    clips, results = [], []
    for index, cue in enumerate(cues):
        text = normalizer.normalize(cue["text"]).strip()
        if not text:
            raise ValueError("Vietnamese normalization returned empty text")
        start_sample, target_frames = cue_window(cue)
        key = cache_identity(cue, text, worker_sha)
        cached_path = clip_root / (key + ".wav")
        record = load_clip(cached_path, key, cue)
        cache_hit = record is not None
        if record is None:
            def on_attempt(attempt, scale):
                emit({"event": "attempt", "index": index + 1, "total": len(cues), "id": cue["id"],
                      "attempt": attempt, "max_attempts": MAX_SYNTHESIS_ATTEMPTS, "length_scale": scale})
            try:
                final_path, record = fit_cue(voice, text, target_frames, run_root, key, cue["timing_source"], on_attempt)
            except (ValueError, RuntimeError) as error:
                raise RuntimeError(f"Cue {index + 1} ({cue['id']}): {error}") from error
            record.update({"key": key, "sha256": sha256(final_path)})
            final_path.replace(cached_path)
            atomic_json(cached_path.with_suffix(".json"), record)
        # The clip already has the required length. Place ALL its samples;
        # never hide a timing failure by truncating the clip at the SRT boundary.
        end_sample = start_sample + record["frames"]
        if end_sample > start_sample + target_frames:
            raise ValueError("Fitted voice exceeds the source speech window")
        clips.append({"path": cached_path, "start": cue["voice_start"], "frames": record["frames"],
                      "start_sample": start_sample, "end_sample": end_sample})
        results.append({"id": cue["id"], "voice": VOICE_NAME, "voice_review": record["status"] != "fit",
                        "raw_duration": record["raw_duration"], "fitted_duration": record["fitted_duration"],
                        "status": record["status"], "cache_hit": cache_hit,
                        "clip_path": str(cached_path), "clip_sha256": record["sha256"],
                        "clipped": False, "timing_source": cue["timing_source"], "target_frames": target_frames,
                        "frames": record["frames"], "clip_start_sample": start_sample, "clip_end_sample": end_sample,
                        "fit_method": record["fit_method"], "base_length_scale": record["base_length_scale"],
                        "length_scale": record["length_scale"], "generated_frames": record["generated_frames"],
                        "padding_frames": record["padding_frames"], "synthesis_attempts": record["synthesis_attempts"],
                        "synthesis_calls": 0 if cache_hit else record["synthesis_attempts"]})
        emit({"event": "cue", "index": index + 1, "total": len(cues), "id": cue["id"],
              "status": record["status"], "cache_hit": cache_hit,
              "synthesis_calls": 0 if cache_hit else record["synthesis_attempts"]})
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
