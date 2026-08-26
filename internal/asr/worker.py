from __future__ import annotations

import argparse
import json
import os
import sys
import time
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
        emit({
            "event": "segment",
            "start": start,
            "end": end,
            "text": text,
            "avg_logprob": float(segment.avg_logprob),
            "no_speech_prob": float(segment.no_speech_prob),
            "words": words,
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
