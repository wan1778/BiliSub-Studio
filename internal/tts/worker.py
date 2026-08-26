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


TARGET_SAMPLE_RATE = 24000
VOICE_REVISION = "9f210d622209fcc216fe2ac6159fed2ff381cb8a-ngoc-huyen-v1"
VOICE_NAME = "ngoc-huyen"


def emit(payload: dict) -> None:
    sys.stdout.write(json.dumps(payload, ensure_ascii=False, separators=(",", ":")) + "\n")
    sys.stdout.flush()


def args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--model", required=True)
    parser.add_argument("--voicepack", required=True)
    parser.add_argument("--config", required=True)
    parser.add_argument("--ffmpeg", required=True)
    parser.add_argument("--output-root", required=True)
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


class KokoroNgocHuyen:
    def __init__(self, model_path: Path, voicepack_path: Path, config_path: Path) -> None:
        import numpy as np
        import onnxruntime as ort
        import torch

        self.np = np
        self.phonemize = __import__("vig2p", fromlist=["phonemize_text"]).phonemize_text
        self.config = json.loads(config_path.read_text(encoding="utf-8"))
        self.context_length = int(self.config["plbert"]["max_position_embeddings"])
        self.voicepack = torch.load(voicepack_path, map_location="cpu", weights_only=True).detach().cpu().numpy()
        if self.voicepack.ndim != 3 or self.voicepack.shape[1:] != (1, 256):
            raise ValueError(f"Unexpected Ngoc Huyen voicepack shape {self.voicepack.shape}")
        self.session = ort.InferenceSession(str(model_path), providers=["CPUExecutionProvider"])

    def synthesize(self, text: str, length_scale: float):
        phonemes = self.phonemize(text)
        if not phonemes:
            raise ValueError("Kokoro received no pronounceable Vietnamese text")
        token_ids = [self.config["vocab"][symbol] for symbol in phonemes if symbol in self.config["vocab"]]
        if len(token_ids) + 2 > self.context_length:
            raise ValueError(f"Kokoro phoneme group too long ({len(token_ids) + 2} > {self.context_length})")
        index = min(max(1, len(phonemes)), self.voicepack.shape[0]) - 1
        speed = self.np.asarray(1.0 / max(0.20, float(length_scale)), dtype=self.np.float32)
        waveform, _ = self.session.run(None, {
            "input_ids": self.np.asarray([[0, *token_ids, 0]], dtype=self.np.int64),
            "ref_s": self.np.asarray(self.voicepack[index], dtype=self.np.float32),
            "speed": speed,
        })
        return self.np.asarray(waveform, dtype=self.np.float32).reshape(-1)


def synth_wav(voice: KokoroNgocHuyen, text: str, length_scale: float, path: Path) -> None:
    samples = voice.synthesize(text, length_scale)
    if len(samples) == 0:
        raise RuntimeError("Kokoro produced an empty WAV")
    write_wav_float(path, samples, TARGET_SAMPLE_RATE)


def run_ffmpeg_filter(ffmpeg: Path, source: Path, destination: Path, audio_filter: str) -> None:
    command = [
        str(ffmpeg), "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
        "-i", str(source), "-af", audio_filter, "-ac", "1", "-ar", str(TARGET_SAMPLE_RATE),
        "-c:a", "pcm_s16le", str(destination),
    ]
    result = subprocess.run(
        command,
        stdin=subprocess.DEVNULL,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
        errors="replace",
    )
    if result.returncode != 0 or not destination.is_file() or destination.stat().st_size <= 44:
        raise RuntimeError("FFmpeg audio filter failed: " + (result.stderr.strip().splitlines()[-1] if result.stderr.strip() else "unknown error"))


def run_atempo(ffmpeg: Path, source: Path, destination: Path, ratio: float) -> None:
    run_ffmpeg_filter(ffmpeg, source, destination, f"atempo={ratio:.6f}")


def fit_group(voice, text: str, target: float, cache_path: Path, ffmpeg: Path, temp_root: Path) -> tuple[float, float, float, str]:
    """Returns raw_duration, final_duration, length_scale, status."""
    if cache_path.is_file() and cache_path.stat().st_size > 44:
        final = wav_duration(cache_path)
        status = "fit" if abs(final - target) <= max(0.07, target * 0.04) else "review"
        return final, final, 1.0, status

    identity = hashlib.sha256((str(cache_path) + text).encode("utf-8")).hexdigest()[:16]
    baseline = temp_root / f"{identity}-base.wav"
    tuned = temp_root / f"{identity}-tuned.wav"
    stretched = temp_root / f"{identity}-stretch.wav"
    synth_wav(voice, text, 1.0, baseline)
    raw = wav_duration(baseline)
    if raw <= 0.02 or target <= 0.08:
        cache_path.parent.mkdir(parents=True, exist_ok=True)
        baseline.replace(cache_path)
        return raw, raw, 1.0, "review"

    desired = max(0.86, min(1.16, target / raw))
    candidate = baseline
    if abs(desired - 1.0) >= 0.025:
        synth_wav(voice, text, desired, tuned)
        candidate = tuned
    current = wav_duration(candidate)
    ratio = current / target
    bounded = max(0.92, min(1.08, ratio))
    if abs(bounded - 1.0) >= 0.006:
        run_atempo(ffmpeg, candidate, stretched, bounded)
        candidate = stretched
    final = wav_duration(candidate)
    tolerance = max(0.07, target * 0.04)
    status = "fit" if abs(final - target) <= tolerance and 0.92 <= ratio <= 1.08 else "review"
    cache_path.parent.mkdir(parents=True, exist_ok=True)
    if cache_path.exists():
        cache_path.unlink()
    candidate.replace(cache_path)
    return raw, final, desired, status


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
    voicepack = Path(parsed.voicepack).resolve()
    config = Path(parsed.config).resolve()
    for path in (manifest_path, model, voicepack, config, ffmpeg):
        if not path.exists():
            raise FileNotFoundError(path)
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    if manifest.get("schema") != 1:
        raise ValueError("unsupported TTS manifest schema")
    cues = manifest.get("cues") or []
    if not cues:
        raise ValueError("TTS manifest has no cues")

    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["TRANSFORMERS_OFFLINE"] = "1"
    voice = KokoroNgocHuyen(model, voicepack, config)
    output_root.mkdir(parents=True, exist_ok=True)
    ensure_voice_cache(output_root)
    run_id = os.urandom(8).hex()
    clip_root = output_root / "clips"
    block_root = output_root / "blocks"
    clip_root.mkdir(parents=True, exist_ok=True)
    block_root.mkdir(parents=True, exist_ok=True)
    emit({"event": "ready", "cues": len(cues), "voice": VOICE_NAME, "voice_revision": VOICE_REVISION})

    clip_entries: list[dict] = []
    cue_results: list[dict] = []
    # Temp clips must share the cache volume: Path.replace is intentionally atomic
    # and Windows cannot move a file from %TEMP% on C: into a portable cache on E:.
    with tempfile.TemporaryDirectory(prefix="bilisub-tts-", dir=output_root) as temp:
        temp_root = Path(temp)
        for cue_index, cue in enumerate(cues):
            cue_id = str(cue["id"])
            voice_name = VOICE_NAME
            groups = cue.get("groups") or []
            cue_review = False
            raw_total = 0.0
            final_total = 0.0
            statuses: list[str] = []
            for group_index, group in enumerate(groups):
                text = str(group.get("text") or "").strip()
                start = float(group["start"])
                end = float(group["end"])
                target = max(0.08, end - start)
                if not text:
                    continue
                cache_key = str(group.get("cache_key") or hashlib.sha256(f"{cue_id}|{group_index}|{VOICE_REVISION}|{text}|{target:.3f}".encode("utf-8")).hexdigest())
                cache_path = clip_root / f"{cache_key}.wav"
                raw, final, length_scale, status = fit_group(voice, text, target, cache_path, ffmpeg, temp_root)
                raw_total += raw
                final_total += final
                statuses.append(status)
                clip_entries.append({
                    "cue_id": cue_id,
                    "group": group_index,
                    "path": str(cache_path),
                    "start": start,
                    "target_duration": target,
                    "duration": final,
                    "voice": voice_name,
                    "length_scale": length_scale,
                    "status": status,
                })
            status = "review" if cue_review or not statuses or any(value != "fit" for value in statuses) else "fit"
            cue_results.append({
                "id": cue_id,
                "voice": voice_name,
                "voice_review": cue_review,
                "raw_duration": raw_total,
                "fitted_duration": final_total,
                "status": status,
            })
            emit({"event": "cue", "index": cue_index + 1, "total": len(cues), "id": cue_id, "status": status})

    import numpy as np

    block_seconds = max(30.0, min(600.0, float(manifest.get("block_seconds") or 300.0)))
    sample_rate = TARGET_SAMPLE_RATE
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
                raise ValueError(f"Unexpected TTS sample rate {rate}")
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
    # Keep the final extension while writing: FFmpeg selects its muxer from it.
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
        "engine": "kokoro-vietnamese-onnx",
        "engine_version": manifest.get("engine_version", "unknown"),
        "voice": VOICE_NAME,
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


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as error:
        print(f"TTS_WORKER_ERROR: {type(error).__name__}: {error}", file=sys.stderr, flush=True)
        raise
