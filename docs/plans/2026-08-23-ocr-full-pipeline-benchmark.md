# OCR full-pipeline Auto benchmark

> Superseded on 2026-08-23 by `2026-08-23-ocr-resource-safe-topology-cancel.md`. Public 4.0.9 implemented exact-count probes but could still commit 16 without a live RAM/VRAM reserve or meaningful-throughput gate; its Cancel path also retained the Python pool.

Pinned baseline: `b833960744aa0572a3646e579554cb066971e572` (`main`, public 4.0.8 / technical beta 22).

Field status: **4.0.8 FAIL** for Auto topology. It separates FFmpeg segment lanes from Python workers and statically caps how far each selector may probe. That does not implement the required meaning of an OCR “luồng”.

## Required meaning

One selected OCR pipeline owns:

- one deterministic FFmpeg segment lane;
- one live Python/PaddleOCR worker;
- one lane-local subtitle tracker.

Selected level `N` therefore means exactly `N` FFmpeg lanes and `N` Python workers during the real scan.

## Auto state machine

1. Remove the exact matching checkpoint for Fresh.
2. Measure CPU/RAM for diagnostics only; this result cannot cap Auto.
3. Probe the fixed sequence `1 -> 2 -> 4 -> 8 -> 16`.
4. For candidate `N`, create exactly `N` workers and run `N` concurrent FFmpeg-to-OCR requests using distinct real video positions.
5. PASS advances to the next level.
6. Error, OOM, worker death or the bounded probe timeout stops the ladder.
7. Restore and verify the immediately preceding PASS worker pool.
8. Create the `N`-segment checkpoint only after the benchmark finishes.
9. Start the real scan only after worker count and segment count both equal committed `N`.

Examples:

- `1, 2, 4 PASS; 8 FAIL` -> restore 4 -> commit 4 -> start scan.
- `1, 2, 4, 8 PASS; 16 FAIL` -> restore 8 -> commit 8 -> start scan.
- every level PASS -> commit 16 -> start scan.

Manual `N` probes the exact full topology and either commits `N` or reports failure. It is never silently downgraded. Resume preserves the saved segment count and re-probes that exact worker count before continuing.

## Rollback ownership

Worker pool growth is transactional. Scaling from 4 to 8 preserves the four existing PASS workers until all four additions are Ready. If an added worker cannot start, only the additions are disposed and the prior four-worker channel is rebuilt. If a later inference kills or corrupts the candidate pool, Core rebuilds the last PASS count from a clean pool. The scanner verifies the restored count before committing scan topology.

## Required regressions

1. The ladder is exactly `1, 2, 4, 8, 16` with no static prediction filter.
2. A simulated failure at 8 after PASS at 4 attempts `1,2,4,8`, restores 4 once, returns 4 and does not attempt 16.
3. An all-PASS fixture returns 16 without rollback.
4. Every candidate calls `ConfigureWorkerPoolAsync(N)`, launches `N` distinct FFmpeg captures and submits `N` OCR requests.
5. Scan start rejects any mismatch between selected lanes and live workers.
6. Benchmark telemetry exposes candidate, last PASS, worker count and rollback.
7. Pause is disabled during benchmark; Cancel remains available.
