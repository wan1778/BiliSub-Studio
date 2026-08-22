# OCR resource-safe topology and owned Cancel cleanup

Pinned baseline: `d8432fa3d8e9f1e39c8fa58b4d85b4182684130f` (`main`, public 4.0.9 / technical beta 23).

Field status: **4.0.9 FAIL**. A 4 GB-class laptop GPU that is useful at eight pipelines reached candidate 16 and could commit it because correctness-only probes had no RAM/VRAM reserve or throughput-gain gate. Pre-configuration telemetry also showed `16/16` while the retained PASS pool still contained eight workers, which made candidate and actual topology ambiguous. Cancel removed the checkpoint but deliberately retained the Python pool, and the user observed many console/shell host processes after cancellation.

## Required state machine

1. Evaluate every ladder candidate `1 -> 2 -> 4 -> 8 -> 16`.
2. Before allocating candidate `N`, read live physical RAM with `GlobalMemoryStatusEx` and NVIDIA free/total VRAM with native NVML.
3. Preserve at least 15% RAM (minimum 2 GiB) and 15% VRAM (minimum 512 MiB).
4. Budget 384 MiB RAM and 384 MiB VRAM per added GPU worker; budget 768 MiB RAM per added CPU worker.
5. Reject the candidate before allocation when total capacity or live headroom cannot preserve those reserves.
6. For an allowed candidate, create exactly `N` Python workers, run an N-way warm-up and two timed N-way FFmpeg-to-OCR rounds on distinct video frames.
7. Re-check that exactly `N` workers remain alive after inference.
8. Advance only when measured throughput is at least 10% above the previous PASS level.
9. On resource rejection, insufficient gain, error, OOM or timeout, rebuild and verify the previous PASS worker count before scan Commit.
10. Telemetry distinguishes preflight (still showing the previous pool) from the phase where all candidate workers and pipelines are actually live.

Manual and Resume never silently downgrade their requested/saved topology. They must pass the same resource preflight and exact probe, or report a clear failure.

## Cancel ownership

- Every OCR FFmpeg process is registered in one per-scan `OwnedProcessGroup`.
- Cancellation kills and reaps the full FFmpeg child tree before checkpoint deletion.
- `OcrManager.StopAsync` kills and reaps every private Python/Paddle worker tree instead of retaining it after Cancel.
- Core verifies zero OCR worker roots and zero owned scan processes before verifying checkpoint absence.
- UI returns to `Quét từ đầu` only after that transaction completes; failure to reap remains an error instead of a false Cancel success.

## Regression gates

1. A 3.75 GiB VRAM fixture allows expansion 4 -> 8 with sufficient live headroom but rejects 8 -> 16.
2. Low live RAM rejects 8 -> 16 even when total VRAM is large.
3. A 5% throughput gain stops and rolls back; an 11% gain may advance.
4. Candidate telemetry changes from resource preflight to exactly N live workers before the N-way probe.
5. A nested child-process fixture is killed and reaped when its owned parent operation is cancelled.
6. Running and paused Cancel both stop Python workers, reap owned FFmpeg trees, delete the exact checkpoint and then publish terminal Cancelled.
