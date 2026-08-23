from __future__ import annotations

import argparse
import json
import math
import os
import sys
import time
import wave
from pathlib import Path


def emit(payload: dict) -> None:
    sys.stdout.write(json.dumps(payload, ensure_ascii=False, separators=(",", ":")) + "\n")
    sys.stdout.flush()


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--model", required=True)
    parser.add_argument("--audio", required=True)
    parser.add_argument("--device", choices=("cpu", "cuda"), required=True)
    parser.add_argument("--compute", required=True)
    parser.add_argument("--threads", type=int, required=True)
    parser.add_argument("--offset", type=float, default=0.0)
    parser.add_argument("--beam", type=int, default=5)
    parser.add_argument("--probe", action="store_true")
    return parser.parse_args()


def load_pcm16_mono(path: Path):
    import numpy as np

    with wave.open(str(path), "rb") as wav:
        if wav.getnchannels() != 1 or wav.getsampwidth() != 2:
            raise ValueError("ASR analysis audio must be mono PCM16")
        sample_rate = wav.getframerate()
        data = wav.readframes(wav.getnframes())
    return np.frombuffer(data, dtype="<i2").astype(np.float32) / 32768.0, sample_rate


def estimate_voice_class(samples, sample_rate: int, start: float, end: float) -> tuple[str, float, float]:
    """Return male_like/female_like/uncertain using a lightweight language-neutral F0 heuristic.

    This is an advisory routing signal for selecting a TTS voice, never a biological-gender claim.
    """
    import numpy as np

    if end <= start + 0.12:
        return "uncertain", 0.0, 0.0
    left = max(0, int(start * sample_rate))
    right = min(len(samples), int(end * sample_rate))
    clip = samples[left:right]
    if clip.size < int(sample_rate * 0.12):
        return "uncertain", 0.0, 0.0

    frame_size = max(256, int(sample_rate * 0.05))
    hop = max(128, int(sample_rate * 0.025))
    min_lag = max(1, int(sample_rate / 320.0))
    max_lag = min(frame_size - 2, int(sample_rate / 70.0))
    pitches: list[float] = []
    attempted = 0
    window = np.hanning(frame_size).astype(np.float32)

    for pos in range(0, max(1, clip.size - frame_size + 1), hop):
        frame = clip[pos:pos + frame_size]
        if frame.size != frame_size:
            continue
        attempted += 1
        frame = (frame - float(frame.mean())) * window
        rms = float(np.sqrt(np.mean(frame * frame) + 1e-12))
        if rms < 0.012:
            continue
        corr = np.correlate(frame, frame, mode="full")[frame_size - 1:]
        base = float(corr[0])
        if not math.isfinite(base) or base <= 1e-7 or max_lag <= min_lag:
            continue
        span = corr[min_lag:max_lag + 1]
        lag = int(np.argmax(span)) + min_lag
        strength = float(corr[lag] / base)
        if strength < 0.28:
            continue
        # Parabolic interpolation around the autocorrelation peak.
        refined = float(lag)
        if 1 <= lag < len(corr) - 1:
            y0, y1, y2 = float(corr[lag - 1]), float(corr[lag]), float(corr[lag + 1])
            denom = y0 - 2.0 * y1 + y2
            if abs(denom) > 1e-12:
                refined += 0.5 * (y0 - y2) / denom
        pitch = sample_rate / max(1e-6, refined)
        if 70.0 <= pitch <= 320.0 and math.isfinite(pitch):
            pitches.append(float(pitch))

    if not pitches:
        return "uncertain", 0.0, 0.0
    median_pitch = float(np.median(np.asarray(pitches, dtype=np.float32)))
    voiced_ratio = len(pitches) / max(1, attempted)
    if median_pitch <= 155.0:
        label = "male_like"
        distance = min(1.0, (170.0 - median_pitch) / 70.0)
    elif median_pitch >= 185.0:
        label = "female_like"
        distance = min(1.0, (median_pitch - 170.0) / 90.0)
    else:
        label = "uncertain"
        distance = abs(median_pitch - 170.0) / 15.0
    confidence = min(0.97, max(0.0, 0.35 + 0.45 * distance + 0.20 * min(1.0, voiced_ratio / 0.45)))
    if label == "uncertain":
        confidence = min(confidence, 0.59)
    return label, float(confidence), median_pitch


def main() -> int:
    args = parse_args()
    model_dir = Path(args.model).resolve()
    audio = Path(args.audio).resolve()
    if not model_dir.is_dir() or not (model_dir / "model.bin").is_file():
        raise FileNotFoundError("ASR model directory is incomplete")
    if not audio.is_file() or audio.stat().st_size <= 44:
        raise FileNotFoundError("ASR audio input is missing or empty")

    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["TRANSFORMERS_OFFLINE"] = "1"
    from faster_whisper import WhisperModel

    samples, sample_rate = load_pcm16_mono(audio)
    started = time.perf_counter()
    model = WhisperModel(
        str(model_dir),
        device=args.device,
        compute_type=args.compute,
        cpu_threads=max(1, min(args.threads, 32)),
        num_workers=1,
        local_files_only=True,
    )
    emit({"event": "ready", "device": args.device, "compute": args.compute})
    segments, info = model.transcribe(
        str(audio),
        language="zh",
        task="transcribe",
        beam_size=max(1, min(args.beam, 8)),
        temperature=0.0,
        condition_on_previous_text=True,
        word_timestamps=True,
        vad_filter=True,
        vad_parameters={"min_silence_duration_ms": 250, "speech_pad_ms": 120},
    )
    count = 0
    word_count = 0
    latest = args.offset
    for segment in segments:
        text = " ".join(str(segment.text).strip().split())
        if not text:
            continue
        local_start = max(0.0, float(segment.start))
        local_end = max(local_start + 0.05, float(segment.end))
        start = args.offset + local_start
        end = args.offset + local_end
        words = []
        for word in segment.words or []:
            value = str(word.word).strip()
            if not value or word.start is None or word.end is None:
                continue
            word_start = args.offset + max(0.0, float(word.start))
            word_end = args.offset + max(float(word.start) + 0.01, float(word.end))
            words.append({
                "start": word_start,
                "end": word_end,
                "text": value,
                "probability": float(word.probability or 0.0),
            })
        voice_class, voice_confidence, median_pitch = estimate_voice_class(samples, sample_rate, local_start, local_end)
        emit({
            "event": "segment",
            "start": start,
            "end": end,
            "text": text,
            "avg_logprob": float(segment.avg_logprob),
            "no_speech_prob": float(segment.no_speech_prob),
            "words": words,
            "voice_class": voice_class,
            "voice_confidence": voice_confidence,
            "median_pitch_hz": median_pitch,
        })
        count += 1
        word_count += len(words)
        latest = end
    emit({
        "event": "complete",
        "segments": count,
        "words": word_count,
        "latest": latest,
        "language": str(info.language),
        "language_probability": float(info.language_probability),
        "elapsed_seconds": time.perf_counter() - started,
        "probe": bool(args.probe),
    })
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as error:
        print(f"ASR_WORKER_ERROR: {type(error).__name__}: {error}", file=sys.stderr, flush=True)
        raise
