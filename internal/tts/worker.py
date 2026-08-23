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
    parser.add_argument("--config", required=True)
    parser.add_argument("--text", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--length-scale", type=float, default=1.0)
    parser.add_argument("--speaker", type=int, default=0)
    parser.add_argument("--probe", action="store_true")
    return parser.parse_args()


def wav_duration(path: Path) -> float:
    with wave.open(str(path), "rb") as source:
        frames = source.getnframes()
        rate = source.getframerate()
        if frames <= 0 or rate <= 0:
            return 0.0
        return frames / float(rate)


def main() -> int:
    args = parse_args()
    model = Path(args.model).resolve()
    config = Path(args.config).resolve()
    output = Path(args.output).resolve()
    if not model.is_file() or model.stat().st_size <= 0:
        raise FileNotFoundError("TTS model is missing or empty")
    if not config.is_file() or config.stat().st_size <= 0:
        raise FileNotFoundError("TTS config is missing or empty")
    text = " ".join(str(args.text).strip().split())
    if not text:
        raise ValueError("TTS text is empty")
    length_scale = max(0.55, min(float(args.length_scale), 1.8))

    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["TRANSFORMERS_OFFLINE"] = "1"
    from piper import PiperVoice, SynthesisConfig

    started = time.perf_counter()
    voice = PiperVoice.load(model, config_path=config, use_cuda=False)
    emit({
        "event": "ready",
        "sample_rate": int(voice.config.sample_rate),
        "num_speakers": int(voice.config.num_speakers),
    })
    synth = SynthesisConfig(
        speaker_id=max(0, int(args.speaker)),
        length_scale=length_scale,
        normalize_audio=True,
        volume=1.0,
    )
    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = output.with_name(output.name + ".tmp.wav")
    try:
        if temporary.exists():
            temporary.unlink()
        with wave.open(str(temporary), "wb") as wav_file:
            first = True
            wrote = False
            for chunk in voice.synthesize(text, synth):
                if first:
                    wav_file.setframerate(int(chunk.sample_rate))
                    wav_file.setsampwidth(int(chunk.sample_width))
                    wav_file.setnchannels(int(chunk.sample_channels))
                    first = False
                wav_file.writeframes(chunk.audio_int16_bytes)
                wrote = True
            if not wrote:
                raise RuntimeError("Piper produced no audio")
        duration = wav_duration(temporary)
        if not math.isfinite(duration) or duration <= 0:
            raise RuntimeError("Piper generated an invalid WAV duration")
        temporary.replace(output)
        emit({
            "event": "complete",
            "duration_seconds": duration,
            "elapsed_seconds": time.perf_counter() - started,
            "length_scale": length_scale,
            "probe": bool(args.probe),
        })
        return 0
    finally:
        try:
            if temporary.exists():
                temporary.unlink()
        except OSError:
            pass


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as error:
        print(f"TTS_WORKER_ERROR: {type(error).__name__}: {error}", file=sys.stderr, flush=True)
        raise
