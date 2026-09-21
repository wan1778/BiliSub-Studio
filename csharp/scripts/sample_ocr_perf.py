"""External resource sampler for OCR perf benchmark runs.

Polls CPU/RAM of the app + OCR worker + FFmpeg processes and NVIDIA GPU
utilization/memory. Read-only: never touches the measured processes.
Usage:
    python sample_ocr_perf.py --duration 300 --interval 2 --out samples.csv
"""
import argparse
import csv
import subprocess
import sys
import time

import psutil


def gpu_sample():
    try:
        out = subprocess.run(
            ["nvidia-smi",
             "--query-gpu=utilization.gpu,memory.used,memory.total",
             "--format=csv,noheader,nounits"],
            capture_output=True, text=True, timeout=10)
    except (OSError, subprocess.SubprocessError):
        return None, None, None
    if out.returncode != 0:
        return None, None, None
    parts = out.stdout.strip().split(",")
    if len(parts) != 3:
        return None, None, None
    try:
        return float(parts[0].strip()), float(parts[1].strip()), float(parts[2].strip())
    except ValueError:
        return None, None, None


def process_rss_mb(names):
    totals = {name: 0.0 for name in names}
    counts = {name: 0 for name in names}
    for process in psutil.process_iter(["name", "memory_info"]):
        try:
            name = (process.info["name"] or "").lower()
        except (psutil.NoSuchProcess, psutil.AccessDenied):
            continue
        for key in names:
            if key in name:
                try:
                    totals[key] += process.info["memory_info"].rss / 1048576.0
                    counts[key] += 1
                except (psutil.NoSuchProcess, psutil.AccessDenied):
                    pass
                break
    return totals, counts


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--duration", type=float, required=True)
    parser.add_argument("--interval", type=float, default=2.0)
    parser.add_argument("--out", required=True)
    args = parser.parse_args()
    names = ("bilisubstudio", "python", "ffmpeg")
    deadline = time.time() + args.duration
    with open(args.out, "w", newline="", encoding="utf-8") as handle:
        writer = csv.writer(handle)
        writer.writerow(["t_s", "app_mb", "python_mb", "ffmpeg_mb",
                         "procs", "gpu_pct", "gpu_used_mb", "gpu_total_mb"])
        start = time.time()
        while time.time() < deadline:
            totals, counts = process_rss_mb(names)
            gpu_pct, gpu_used, gpu_total = gpu_sample()
            writer.writerow([
                round(time.time() - start, 1),
                round(totals["bilisubstudio"], 1),
                round(totals["python"], 1),
                round(totals["ffmpeg"], 1),
                sum(counts.values()),
                gpu_pct if gpu_pct is not None else "",
                gpu_used if gpu_used is not None else "",
                gpu_total if gpu_total is not None else "",
            ])
            handle.flush()
            time.sleep(args.interval)
    print(f"sampler done: {args.out}")


if __name__ == "__main__":
    sys.exit(main())
