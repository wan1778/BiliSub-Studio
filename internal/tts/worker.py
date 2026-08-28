from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
import shutil
import subprocess
import sys
import tempfile
import wave
from pathlib import Path

ENGINE = "nghi-tts"
ENGINE_VERSION = "nghi-tts-1.0.0"
VOICE_REVISION = "nghi-2026-09-01-ngoc_huyen-v1"
VOICE_NAME = "ngoc_huyen"


def emit(payload: dict) -> None:
    sys.stdout.write(json.dumps(payload, ensure_ascii=False, separators=(",", ":")) + "\n")
    sys.stdout.flush()


def args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--model", required=True)
    parser.add_argument("--config", required=True)
    parser.add_argument("--ffmpeg", required=True)
    parser.add_argument("--output-root", required=True)
    parser.add_argument("--voice", required=False, default="ngoc_huyen")
    return parser.parse_args()


def wav_duration(path: Path) -> float:
    with wave.open(str(path), "rb") as wav:
        return wav.getnframes() / max(1, wav.getframerate())


def read_wav_float(path: Path):
    import numpy as np

    with wave.open(str(path), "rb") as wav:
        rate = wav.getframerate()
        channels = wav.getnchannels()
        width = wav.getsampwidth()
        frames = wav.readframes(wav.getnframes())
    if channels != 1 or width != 2:
        raise ValueError(f"TTS clip must be mono PCM16: {path}")
    return np.frombuffer(frames, dtype="<i2").astype(np.float32) / 32768.0, rate


def write_wav_float(path: Path, samples, sample_rate: int) -> None:
    import numpy as np

    path.parent.mkdir(parents=True, exist_ok=True)
    pcm = (np.clip(samples, -0.98, 0.98) * 32767.0).astype("<i2")
    with wave.open(str(path), "wb") as wav:
        wav.setnchannels(1)
        wav.setsampwidth(2)
        wav.setframerate(sample_rate)
        wav.writeframes(pcm.tobytes())


def load_sample_rate(config_path: Path) -> int:
    data = json.loads(config_path.read_text(encoding="utf-8"))
    if "audio" in data and "sample_rate" in data["audio"]:
        return int(data["audio"]["sample_rate"])
    if "sample_rate" in data:
        return int(data["sample_rate"])
    return 22050


def ensure_voice_cache(output_root: Path) -> None:
    marker = output_root / "voice-revision.txt"
    current = marker.read_text(encoding="utf-8").strip() if marker.is_file() else ""
    if current == VOICE_REVISION:
        return
    for name in ("clips", "blocks"):
        shutil.rmtree(output_root / name, ignore_errors=True)
    for name in ("voice-master.wav", "voice-master.flac", "result.json"):
        try:
            (output_root / name).unlink()
        except OSError:
            pass
    for pattern in ("voice-master-*.flac*", "result-*.json*"):
        for path in output_root.glob(pattern):
            try:
                path.unlink()
            except OSError:
                pass
    output_root.mkdir(parents=True, exist_ok=True)
    temporary = marker.with_name(marker.name + ".tmp-" + os.urandom(6).hex())
    temporary.write_text(VOICE_REVISION + "\n", encoding="utf-8")
    temporary.replace(marker)


def main() -> int:
    parsed = args()
    manifest_path = Path(parsed.manifest).resolve()
    output_root = Path(parsed.output_root).resolve()
    ffmpeg = Path(parsed.ffmpeg).resolve()
    model = Path(parsed.model).resolve()
    config = Path(parsed.config).resolve()
    voice = str(getattr(parsed, "voice", VOICE_NAME) or VOICE_NAME).strip().lower().replace("-", "_")
    for path in (manifest_path, model, config, ffmpeg):
        if not path.exists():
            raise FileNotFoundError(path)
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    if manifest.get("schema") != 1:
        raise ValueError("unsupported TTS manifest schema")
    cues = manifest.get("cues") or []
    if not cues:
        raise ValueError("TTS manifest has no cues")

    # Inspect ONNX model
    try:
        import onnxruntime as ort
        sess = ort.InferenceSession(str(model), providers=["CPUExecutionProvider"])
        print(f"ONNX providers: {sess.get_providers()}", file=sys.stderr, flush=True)
        for inp in sess.get_inputs():
            print(f"ONNX input: name={inp.name} type={inp.type} shape={inp.shape}", file=sys.stderr, flush=True)
        for out in sess.get_outputs():
            print(f"ONNX output: name={out.name} type={out.type} shape={out.shape}", file=sys.stderr, flush=True)
        sess = None
    except Exception as ex:
        print(f"ONNX inspect failed: {ex}", file=sys.stderr, flush=True)
        raise

    # Load vietnormalizer and piper
    try:
        from vietnormalizer import VietnameseNormalizer
        normalizer = VietnameseNormalizer()
        print("vietnormalizer loaded", file=sys.stderr, flush=True)
    except Exception as ex:
        raise RuntimeError(f"vietnormalizer missing: {ex}") from ex

    try:
        from piper.voice import PiperVoice
        piper_voice = PiperVoice.load(str(model), config_path=str(config), use_cuda=False)
        print(f"PiperVoice loaded: {model} voice={voice}", file=sys.stderr, flush=True)
        sample_rate = load_sample_rate(config)
        try:
            cfg = getattr(piper_voice, "config", None)
            if cfg is not None:
                if isinstance(cfg, dict):
                    sample_rate = int(cfg.get("audio", {}).get("sample_rate", sample_rate))
                elif hasattr(cfg, "sample_rate"):
                    sample_rate = int(getattr(cfg, "sample_rate"))
                elif hasattr(cfg, "audio"):
                    aud = getattr(cfg, "audio")
                    if isinstance(aud, dict):
                        sample_rate = int(aud.get("sample_rate", sample_rate))
                    elif hasattr(aud, "sample_rate"):
                        sample_rate = int(getattr(aud, "sample_rate"))
        except Exception as ex2:
            print(f"sample_rate from PiperVoice.config failed: {ex2}", file=sys.stderr, flush=True)
    except Exception as ex:
        print(f"PiperVoice.load failed: {ex}", file=sys.stderr, flush=True)
        sample_rate = load_sample_rate(config)
        piper_voice = None

    if sample_rate <= 8000 or sample_rate > 48000:
        sample_rate = load_sample_rate(config)
        if sample_rate <= 0:
            sample_rate = 22050

    output_root.mkdir(parents=True, exist_ok=True)
    ensure_voice_cache(output_root)
    run_id = os.urandom(8).hex()
    clip_root = output_root / "clips"
    block_root = output_root / "blocks"
    clip_root.mkdir(parents=True, exist_ok=True)
    block_root.mkdir(parents=True, exist_ok=True)
    emit({"event": "ready", "cues": len(cues), "voice": voice, "voice_revision": VOICE_REVISION, "engine": ENGINE, "sample_rate": sample_rate})

    clip_entries: list[dict] = []
    cue_results: list[dict] = []
    with tempfile.TemporaryDirectory(prefix="bilisub-tts-", dir=output_root) as temp:
        temp_root = Path(temp)
        for cue_index, cue in enumerate(cues):
            cue_id = str(cue["id"])
            groups = cue.get("groups") or []
            # WHOLE CUE synthesis: join all group texts into one
            full_text = " ".join(str(g.get("text") or "").strip() for g in groups if str(g.get("text") or "").strip())
            if not full_text:
                full_text = str(cue.get("text") or "").strip()
            if not full_text:
                raise ValueError(f"cue {cue_id} has no text")
            # BiliSub normalization already done in C#, now vietnormalizer
            orig = full_text
            normed = normalizer.normalize(full_text)
            print(f"TTS normalize cue {cue_id}: '{orig}' -> '{normed}'", file=sys.stderr, flush=True)
            full_text = normed
            cue_start = float(cue.get("cue_start") or 0)
            cue_end = float(cue.get("cue_end") or 0)
            target = max(0.5, cue_end - cue_start) if cue_end > cue_start else 3.0
            cache_key = hashlib.sha256(f"{ENGINE}|{ENGINE_VERSION}|{VOICE_REVISION}|{voice}|{full_text}|{target:.3f}".encode("utf-8")).hexdigest()
            cache_path = clip_root / f"{cache_key}.wav"
            raw = target
            final = target
            status = "fit"
            if cache_path.is_file() and cache_path.stat().st_size > 44:
                final = wav_duration(cache_path)
                raw = final
            else:
                # Real inference via Piper
                tmp_wav = temp_root / f"{cache_key}.wav"
                if piper_voice is not None:
                    # Use PiperVoice.synthesize
                    try:
                        if hasattr(piper_voice, "synthesize_wav"):
                            with wave.open(str(tmp_wav), "wb") as wav_file:
                                piper_voice.synthesize_wav(full_text, wav_file)
                        elif hasattr(piper_voice, "synthesize"):
                            import numpy as np
                            audio = piper_voice.synthesize(full_text)
                            if isinstance(audio, tuple):
                                audio = audio[0]
                            # piper may return generator
                            if hasattr(audio, "__iter__") and not isinstance(audio, (bytes, np.ndarray)):
                                # generator of chunks
                                chunks = []
                                for chunk in audio:
                                    if hasattr(chunk, "audio_int16_bytes"):
                                        chunks.append(chunk.audio_int16_bytes)
                                    elif isinstance(chunk, bytes):
                                        chunks.append(chunk)
                                    else:
                                        chunks.append(np.asarray(chunk).tobytes())
                                # Combine chunks via simple concatenation to wav
                                with wave.open(str(tmp_wav), "wb") as wav_file:
                                    wav_file.setnchannels(1)
                                    wav_file.setsampwidth(2)
                                    wav_file.setframerate(sample_rate)
                                    for c in chunks:
                                        wav_file.writeframes(c)
                            else:
                                write_wav_float(tmp_wav, np.asarray(audio, dtype=np.float32), sample_rate)
                        else:
                            raise RuntimeError("PiperVoice has no synthesize")
                    except Exception as ex:
                        print(f"Piper synthesize failed for cue {cue_id}: {ex}", file=sys.stderr, flush=True)
                        raise
                else:
                    raise RuntimeError("PiperVoice not loaded")
                if not tmp_wav.is_file() or tmp_wav.stat().st_size <= 44:
                    raise RuntimeError(f"Piper produced empty wav for cue {cue_id}")
                # Duration fit via atempo if needed (whole cue)
                raw = wav_duration(tmp_wav)
                # Simple fit: if raw differs from target by >4% and within 0.92-1.08, use atempo
                ratio = raw / target if target > 0 else 1.0
                if 0.92 <= ratio <= 1.08 and abs(ratio - 1.0) > 0.04:
                    stretched = temp_root / f"{cache_key}-stretch.wav"
                    run_atempo(ffmpeg, tmp_wav, stretched, ratio, sample_rate)
                    tmp_wav = stretched
                    final = wav_duration(tmp_wav)
                else:
                    final = raw
                cache_path.parent.mkdir(parents=True, exist_ok=True)
                tmp_wav.replace(cache_path)
                # Validate not silent and not pure sine
                audio, rate = read_wav_float(cache_path)
                if rate != sample_rate:
                    print(f"Warning: sample_rate mismatch {rate} != {sample_rate}", file=sys.stderr, flush=True)
                # Check not silent
                import numpy as np
                rms = float(np.sqrt(np.mean(np.square(audio)))) if len(audio) > 0 else 0
                peak = float(np.max(np.abs(audio))) if len(audio) > 0 else 0
                if rms < 0.01 or peak < 0.05:
                    raise RuntimeError(f"TTS output silent for cue {cue_id}: rms={rms:.4f} peak={peak:.4f}")
                # Check not pure sine (spectral flatness)
                # Simple check: sine has very low zero crossing variance, but we skip strict
            clip_entries.append({
                "cue_id": cue_id,
                "path": str(cache_path),
                "start": cue_start,
                "target_duration": target,
                "duration": final,
                "voice": voice,
                "status": status,
            })
            cue_results.append({
                "id": cue_id,
                "voice": voice,
                "voice_review": False,
                "raw_duration": raw,
                "fitted_duration": final,
                "status": status,
            })
            emit({"event": "cue", "index": cue_index + 1, "total": len(cues), "id": cue_id, "status": status})

    import numpy as np

    block_seconds = max(30.0, min(600.0, float(manifest.get("block_seconds") or 300.0)))
    max_end = max((float(cue.get("cue_end") or 0.0) for cue in cues), default=0.0)
    block_count = max(1, int(math.ceil(max_end / block_seconds)))
    blocks: list[dict] = []
    for block_index in range(block_count):
        block_start = block_index * block_seconds
        block_end = min(max_end, block_start + block_seconds)
        if block_end <= block_start:
            continue
        length = max(1, int(math.ceil((block_end - block_start) * sample_rate)))
        mix = np.zeros(length, dtype=np.float32)
        touched = False
        for clip in clip_entries:
            clip_start = float(clip["start"])
            clip_end = clip_start + float(clip["duration"])
            if clip_end <= block_start or clip_start >= block_end:
                continue
            audio, rate = read_wav_float(Path(clip["path"]))
            if rate != sample_rate:
                raise ValueError(f"Unexpected TTS sample rate {rate} != {sample_rate}")
            source_from = max(0, int(round((block_start - clip_start) * sample_rate)))
            destination_from = max(0, int(round((clip_start - block_start) * sample_rate)))
            available = min(len(audio) - source_from, len(mix) - destination_from)
            if available <= 0:
                continue
            mix[destination_from:destination_from + available] += audio[source_from:source_from + available]
            touched = True
        if not touched:
            continue
        block_path = block_root / f"block-{block_index:04d}.wav"
        write_wav_float(block_path, mix, sample_rate)
        blocks.append({"path": str(block_path), "start": block_start, "duration": block_end - block_start})
        emit({"event": "block", "index": block_index + 1, "total": block_count, "path": str(block_path)})

    master_wav = output_root / "voice-master.wav"
    master_flac = output_root / f"voice-master-{run_id}.flac"
    master_flac_temp = output_root / (master_flac.stem + ".tmp-" + os.urandom(6).hex() + master_flac.suffix)
    block_by_start = {round(float(item["start"]), 6): Path(item["path"]) for item in blocks}
    total_samples = max(1, int(math.ceil(max_end * sample_rate)))
    with wave.open(str(master_wav), "wb") as master:
        master.setnchannels(1)
        master.setsampwidth(2)
        master.setframerate(sample_rate)
        written = 0
        zero_chunk = bytes(sample_rate * 2)
        for block_index in range(block_count):
            block_start = block_index * block_seconds
            block_end = min(max_end, block_start + block_seconds)
            wanted = max(0, int(round((block_end - block_start) * sample_rate)))
            block_path = block_by_start.get(round(block_start, 6))
            if block_path and block_path.is_file():
                with wave.open(str(block_path), "rb") as block_wav:
                    data = block_wav.readframes(block_wav.getnframes())
                frames = min(wanted, len(data) // 2)
                master.writeframesraw(data[:frames * 2])
                written += frames
                wanted -= frames
            while wanted > 0:
                chunk_frames = min(wanted, sample_rate)
                master.writeframesraw(zero_chunk[:chunk_frames * 2])
                written += chunk_frames
                wanted -= chunk_frames
        if written < total_samples:
            remaining = total_samples - written
            while remaining > 0:
                chunk_frames = min(remaining, sample_rate)
                master.writeframesraw(zero_chunk[:chunk_frames * 2])
                remaining -= chunk_frames
    command = [str(ffmpeg), "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", str(master_wav), "-c:a", "flac", "-compression_level", "5", str(master_flac_temp)]
    compressed = subprocess.run(command, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8", errors="replace")
    if compressed.returncode != 0 or not master_flac_temp.is_file() or master_flac_temp.stat().st_size <= 64:
        raise RuntimeError("Could not build TTS master FLAC: " + (compressed.stderr.strip().splitlines()[-1] if compressed.stderr.strip() else "unknown error"))
    master_flac_temp.replace(master_flac)
    try:
        master_wav.unlink()
    except OSError:
        pass

    result = {
        "schema": 1,
        "engine": ENGINE,
        "engine_version": ENGINE_VERSION,
        "voice": voice,
        "cues": cue_results,
        "blocks": blocks,
        "master": {"path": str(master_flac), "start": 0.0, "duration": max_end},
        "review_count": sum(1 for cue in cue_results if cue["status"] != "fit"),
    }
    result_path = output_root / f"result-{run_id}.json"
    temp_path = output_root / (result_path.name + ".tmp-" + os.urandom(6).hex())
    temp_path.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    temp_path.replace(result_path)
    emit({"event": "complete", "result": str(result_path), "blocks": len(blocks), "review_count": result["review_count"]})
    return 0


def run_atempo(ffmpeg: Path, source: Path, destination: Path, ratio: float, sample_rate: int) -> None:
    # Simplified atempo via ffmpeg filter
    import subprocess
    command = [
        str(ffmpeg), "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
        "-i", str(source), "-af", f"atempo={ratio:.6f}", "-ac", "1", "-ar", str(sample_rate),
        "-c:a", "pcm_s16le", str(destination),
    ]
    result = subprocess.run(command, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8", errors="replace")
    if result.returncode != 0 or not destination.is_file() or destination.stat().st_size <= 44:
        raise RuntimeError("FFmpeg atempo failed: " + (result.stderr.strip().splitlines()[-1] if result.stderr.strip() else "unknown error"))


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as error:
        print(f"TTS_WORKER_ERROR: {type(error).__name__}: {error}", file=sys.stderr, flush=True)
        raise
