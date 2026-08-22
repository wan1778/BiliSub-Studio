# RC12 GPU micro-batch and pause/resume

Baseline: RC11 commit `c3a177d735eadb51194e989ae8302803c1f16f78`.

## Target hardware
- Ryzen 7 4800H
- NVIDIA RTX 3050 Laptop 4 GB
- 32 GB RAM

## Goals
1. Add GPU OCR micro-batch modes Auto/1/2/4 without fabricating OCR work solely to fill a batch.
2. Preserve timestamp ordering, tracker semantics and checkpoint determinism across batched OCR results.
3. Add explicit pause/resume with a safe checkpoint handshake; paused UI/state must not reset.
4. Detect resumable checkpoints after restart and offer Continue vs Start from zero.
5. Keep Auto OCR GPU-first on a healthy NVIDIA runtime; CPU+GPU remains a manual option.

## Execution map — OCR batch
```text
OCR scan candidate
-> sequence/timestamp assignment
-> bounded batch queue
-> size OR max-wait flush
-> Manager GPU batch worker
-> PaddleOCR worker batch request
-> per-image results
-> reorder by sequence/timestamp
-> subtitleTracker
-> checkpoint
```

## Execution map — pause/resume
```text
UI pause
-> /api/job/pause
-> jobs.Manager pause request
-> Scanner sees pause request
-> stop admitting new OCR work
-> drain/commit safe in-flight work
-> checkpoint.SaveNow + fsync
-> job state paused
-> UI keeps progress/cues/stats

UI resume
-> /api/ocr/scan resume intent
-> checkpoint restore
-> FFmpeg seek to saved media time
-> tracker/cues restore
-> continue scan
```

## Safety constraints
- Keep one OCR subsystem only.
- Keep PaddleOCR 3.7.0 + PP-OCRv6 Small detector/recognizer.
- Keep RC11 NVDEC probe/runtime fallback semantics unchanged unless required by pause safety.
- Do not use CPU+GPU merely because both devices exist; Auto remains GPU-first.
- Do not delete/replace a valid checkpoint except explicit Start from zero.
- No Google Drive promotion before exact RC12 Windows field evidence.

## Required regressions
- Batch size 1/2/4 request/response contract.
- Partial batch timeout flush.
- Cancellation while batch waiting and while batch running.
- Batch results preserve input ordering/timestamps.
- No extra OCR candidate is created to fill a batch.
- Pause idle, pause during OCR, pause while a batch is pending.
- Pause returns only after checkpoint is durable.
- Resume restores media time, cues, active tracker and metrics that are persisted.
- Close/reopen detects resumable checkpoint.
- Explicit Start from zero removes only the matching checkpoint.
- NVDEC pause/resume and GPU batch pause/resume remain deterministic.

## Implementation status

Completed on RC12 branch before clean commit:
- GPU batch worker protocol and Manager `RunBatch` for 1–4 images.
- Auto 1/2/4 benchmark plus bounded 25 ms partial-batch flush.
- Scanner batch collection without manufactured stable-frame OCR work.
- Ordered candidate/event commit before tracker/checkpoint mutation.
- Pausable jobs, `/api/job/pause`, schema-3 checkpoint telemetry, checkpoint inspect/delete API.
- UI Batch selector, true Pause, Continue, restart-from-zero and restart checkpoint discovery.
- UI/API/call-map/browser/race/Windows prebuild gates PASS; exact evidence is recorded in `TEST_REPORT.md`.
