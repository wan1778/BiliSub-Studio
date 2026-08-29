from __future__ import annotations

import argparse
import json
import os
import sys
import time
from pathlib import Path


# Keep both directory registrations and loaded DLLs alive for this worker only.
_cuda_handles = []


def configure_cuda(directories: list[str]) -> None:
    if sys.platform != "win32" or not directories:
        raise RuntimeError("Private Windows CUDA libraries were not prepared for ASR")
    import ctypes

    resolved = [Path(value).resolve(strict=True) for value in directories]
    if any(not directory.is_dir() for directory in resolved):
        raise RuntimeError("Invalid private CUDA directory")
    # PATH is child-process local, for libraries that use LoadLibrary internally.
    # add_dll_directory handles Python's restricted Windows DLL search as well.
    os.environ["PATH"] = os.pathsep.join(map(str, resolved)) + os.pathsep + os.environ.get("PATH", "")
    for directory in resolved:
        _cuda_handles.append(os.add_dll_directory(str(directory)))
    for name in ("cudart64_12.dll", "cublasLt64_12.dll", "cublas64_12.dll", "cudnn64_9.dll"):
        library = next((directory / name for directory in resolved if (directory / name).is_file()), None)
        if library is None:
            raise RuntimeError(f"Private ASR CUDA library is missing: {name}")
        try:
            _cuda_handles.append(ctypes.WinDLL(str(library)))
        except OSError as error:
            raise RuntimeError(f"Cannot load private ASR CUDA library {name}: {error}") from error


def emit(payload: dict) -> None:
    sys.stdout.write(json.dumps(payload, ensure_ascii=False, separators=(",", ":")) + "\n")
    sys.stdout.flush()


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--model", required=True)
    parser.add_argument("--audio", required=True)
    parser.add_argument("--device", choices=("cpu", "cuda", "hybrid"), required=True)
    parser.add_argument("--cuda-bin", action="append", default=[])
    parser.add_argument("--compute", required=True)
    parser.add_argument("--threads", type=int, required=True)
    parser.add_argument("--offset", type=float, default=0.0)
    parser.add_argument("--core-start", type=float, default=0.0)
    parser.add_argument("--beam", type=int, default=5)
    parser.add_argument("--probe", action="store_true")
    return parser.parse_args()


def recognition(model, audio, beam):
    return model.transcribe(
        audio, language="zh", task="transcribe", beam_size=max(1, min(beam, 8)),
        temperature=0.0, condition_on_previous_text=True, word_timestamps=True,
        vad_filter=True, vad_parameters={"min_silence_duration_ms": 250, "speech_pad_ms": 120},
    )


def segment_payload(segment, offset):
    words = []
    for word in segment.words or []:
        value = str(word.word).strip()
        if not value or word.start is None or word.end is None:
            continue
        words.append({"start": offset + max(0.0, float(word.start)),
                      "end": offset + max(float(word.start) + 0.01, float(word.end)),
                      "text": value, "raw": str(word.word), "probability": float(word.probability or 0.0)})
    start = max(0.0, float(segment.start))
    return {"event": "segment", "start": offset + start,
            "end": offset + max(start + 0.05, float(segment.end)),
            "text": " ".join(str(segment.text).strip().split()),
            "avg_logprob": float(segment.avg_logprob), "no_speech_prob": float(segment.no_speech_prob), "words": words}


def hybrid_plan(audio, offset, core_start, probe):
    import wave
    import numpy as np

    with wave.open(str(audio), "rb") as source:
        rate, total = source.getframerate(), source.getnframes()
        if rate != 16000 or source.getnchannels() != 1 or source.getsampwidth() != 2:
            raise RuntimeError("Hybrid requires the prepared mono 16 kHz PCM16 WAV")
        start = max(0, round((core_start - offset) * rate))
        step = max(1, (total - start) // 2) if probe else 60 * rate
        chunks = []
        while start < total:
            # Exactly two probe chunks (apart from a one-sample input), avoiding
            # a one-sample third chunk when the WAV frame count is odd.
            end = total if probe and chunks else min(total, start + step)
            if not probe and end < total:
                # Prefer >=300 ms of low energy near the target cut. If there
                # is music/no silence, retain overlap and reconcile real words.
                left, right = max(start + rate, end - 3 * rate), min(total, end + 3 * rate)
                source.setpos(left)
                samples = np.frombuffer(source.readframes(right - left), dtype="<i2").astype(np.float32) / 32768.0
                width = 320
                windows = samples[:len(samples) // width * width].reshape(-1, width)
                quiet = np.mean(windows * windows, axis=1) < 0.0001
                candidates = [left + (index + 7) * width for index in range(max(0, len(quiet) - 14))
                              if quiet[index:index + 15].all()]
                if candidates:
                    end = min(candidates, key=lambda at: abs(at - end))
            chunks.append((start, end, max(0, start - 2 * rate), min(total, end + 2 * rate)))
            start = end
    return chunks, rate, total


def hybrid_words(result):
    return [word for segment in result for word in segment["words"]]


def hybrid_seam(left, right, boundary, lower):
    # Anchor identical, contemporaneous words from the two overlap readings.
    # Keep the anchor once (left) and start right after it, despite timing jitter.
    a, b = hybrid_words(left), hybrid_words(right)
    midpoint = lambda word: (word["start"] + word["end"]) / 2
    key = lambda word: "".join(char for char in word["text"] if char.isalnum())
    matches = []
    for i in range(lower, len(a)):
        if abs(midpoint(a[i]) - boundary) > 1.25 or not key(a[i]):
            continue
        for j, word in enumerate(b):
            if key(a[i]) == key(word) and abs(midpoint(a[i]) - midpoint(word)) <= 0.4:
                matches.append((abs(midpoint(a[i]) - boundary) + abs(midpoint(a[i]) - midpoint(word)), i, j))
    if matches:
        _, i, j = min(matches)
        return i + 1, j + 1, a[i]["end"]
    # Disjoint half-open ownership when there is no matching speech in overlap.
    upper = next((i for i in range(lower, len(a)) if midpoint(a[i]) >= boundary), len(a))
    next_lower = next((j for j, word in enumerate(b) if midpoint(word) >= boundary), len(b))
    frontier = max(boundary, a[upper - 1]["end"] if upper > lower else boundary)
    return upper, next_lower, frontier


def hybrid_project(result, lower, upper, start, end):
    output, cursor = [], 0
    for segment in result:
        selected = [word for index, word in enumerate(segment["words"], cursor) if lower <= index < upper]
        cursor += len(segment["words"])
        if not selected:
            continue
        clipped = [dict(word, start=max(start, word["start"]), end=min(end, word["end"])) for word in selected]
        if any(word["end"] <= word["start"] for word in clipped):
            raise RuntimeError("Hybrid seam has conflicting word timing; uncommitted chunk was not saved")
        # Rebuild text from the retained real words, never retain the full text
        # of a segment after dropping its overlap words.
        text = " ".join("".join(word.get("raw", word["text"]) for word in selected).strip().split())
        projected = dict(segment, start=clipped[0]["start"], end=clipped[-1]["end"], text=text, words=clipped)
        if output and projected["start"] < output[-1]["end"]:
            # Faster-Whisper can return adjacent segments whose boundary words
            # overlap slightly even though their text is distinct. Split that
            # jitter at one shared boundary; never discard either real word.
            previous = output[-1]
            left, right = previous["words"][-1], projected["words"][0]
            epsilon = 0.001
            earliest = max(left["start"] + epsilon,
                           max((word["end"] for word in previous["words"][:-1]), default=left["start"]))
            latest = min(right["end"] - epsilon,
                         min((word["start"] for word in projected["words"][1:]), default=right["end"]))
            if earliest > latest:
                raise RuntimeError("Hybrid adjacent segments have irreconcilable word timing; uncommitted chunk was not saved")
            boundary = min(latest, max(earliest, (left["end"] + right["start"]) / 2))
            previous["words"][-1] = dict(left, end=boundary)
            previous["end"] = boundary
            projected["words"][0] = dict(right, start=boundary)
            projected["start"] = boundary
        output.append(projected)
    return output


def run_hybrid(args, model_dir, audio):
    import wave
    import numpy as np
    from concurrent.futures import ThreadPoolExecutor, wait, FIRST_COMPLETED

    configure_cuda(args.cuda_bin)
    from faster_whisper import WhisperModel

    started = time.perf_counter()
    chunks, rate, total = hybrid_plan(audio, args.offset, args.core_start, args.probe)
    # One independent model per device, reused across all chunks. CTranslate2
    # releases the GIL during inference; do not concurrently reuse one model.
    models = {
        "cuda": WhisperModel(str(model_dir), device="cuda", compute_type=args.compute, cpu_threads=2, num_workers=1, local_files_only=True),
        "cpu": WhisperModel(str(model_dir), device="cpu", compute_type="int8", cpu_threads=max(1, min(args.threads, 8)), num_workers=1, local_files_only=True),
    }
    emit({"event": "ready", "device": "hybrid", "compute": args.compute + "+int8"})

    def process_chunk(device, chunk):
        with wave.open(str(audio), "rb") as source:
            source.setpos(chunk[2])
            raw = source.readframes(chunk[3] - chunk[2])
        if len(raw) != (chunk[3] - chunk[2]) * 2:
            raise RuntimeError("Hybrid audio chunk is truncated")
        samples = np.frombuffer(raw, dtype="<i2").astype(np.float32) / 32768.0
        segments, _ = recognition(models[device], samples, args.beam)
        result = [segment_payload(segment, args.offset + chunk[2] / rate) for segment in segments if str(segment.text).strip()]
        if any(not segment["words"] for segment in result):
            raise RuntimeError("Hybrid cannot reconcile a spoken segment without word timestamps")
        return result

    pending, lower, futures = {}, {}, {}
    idle, next_index, emit_index = ["cuda", "cpu"], 0, 0
    counts, segment_count, word_count = {"cuda": 0, "cpu": 0}, 0, 0
    frontier = max(args.offset, args.core_start)
    with ThreadPoolExecutor(max_workers=2, thread_name_prefix="asr-hybrid") as pool:
        while emit_index < len(chunks):
            # A four-chunk lookahead caps out-of-order memory when one device is
            # much slower. Faster device takes the next available chunk, not 50%.
            while idle and next_index < len(chunks) and next_index < emit_index + 4:
                device = idle.pop(0)
                futures[pool.submit(process_chunk, device, chunks[next_index])] = (device, next_index)
                next_index += 1
            while emit_index in pending and (emit_index + 1 in pending or emit_index == len(chunks) - 1):
                current = pending[emit_index]
                first = lower.get(emit_index, next((i for i, word in enumerate(hybrid_words(current))
                    if (word["start"] + word["end"]) / 2 >= frontier), len(hybrid_words(current))))
                if emit_index + 1 < len(chunks):
                    upper, next_lower, end = hybrid_seam(current, pending[emit_index + 1], args.offset + chunks[emit_index][1] / rate, first)
                    lower[emit_index + 1] = next_lower
                else:
                    upper, end = len(hybrid_words(current)), args.offset + total / rate
                if end <= frontier:
                    raise RuntimeError("Hybrid frontier failed to advance")
                owned = hybrid_project(current, first, upper, frontier, end)
                for segment in owned:
                    emit(segment)
                    segment_count += 1
                    word_count += len(segment["words"])
                emit({"event": "chunk_complete", "index": emit_index, "start": frontier, "frontier": end,
                      "cpu_chunks": counts["cpu"], "gpu_chunks": counts["cuda"]})
                frontier = end
                del pending[emit_index]
                lower.pop(emit_index, None)
                emit_index += 1
            if emit_index == len(chunks):
                break
            if idle and next_index < len(chunks) and next_index < emit_index + 4:
                continue
            if not futures:
                # New room may have opened after committing contiguous chunks.
                if idle and next_index < len(chunks):
                    continue
                raise RuntimeError("Hybrid scheduler has an unfinished gap")
            finished, _ = wait(futures, return_when=FIRST_COMPLETED)
            for future in finished:
                device, index = futures.pop(future)
                pending[index] = future.result()
                counts[device] += 1
                idle.append(device)
    emit({"event": "complete", "segments": segment_count, "words": word_count, "latest": frontier,
          "chunks": len(chunks), "cpu_chunks": counts["cpu"], "gpu_chunks": counts["cuda"],
          "elapsed_seconds": time.perf_counter() - started, "probe": bool(args.probe)})
    return 0


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
    if args.device == "hybrid":
        return run_hybrid(args, model_dir, audio)
    if args.device == "cuda":
        configure_cuda(args.cuda_bin)
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
