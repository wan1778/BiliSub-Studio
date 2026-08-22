# RC10 long-video OCR optimization

Baseline commit: b50fa5d3f253eec959e9b7e4539cae84bf6642ee (RC9)
Target hardware profile: Ryzen 7 4800H + RTX 3050 Laptop 4 GB + 32 GB RAM.

## Goal
Reduce OCR work on 7-8 hour videos without weakening subtitle accuracy, timestamp ordering, checkpoint determinism, or the single-OCR-subsystem boundary.

## Execution map
OCR page -> /api/ocr/scan -> api.Server.ocrScanHandler -> api.Server.newOCRScanner
-> ocr.Scanner.Run
   -> FFmpeg ROI sample stream
   -> edgeSignature activity/diff
   -> visual confirmation/active-cue extension
   -> sparse OCR candidate encode
   -> ocr.Manager.Run -> persistent PaddleOCR worker
   -> subtitleTracker
   -> checkpoint -> live result/SRT

Hybrid manager path:
Scanner OCR goroutines -> ocr.Manager.Run -> bounded worker availability queue
-> CPU/GPU workerClient.run -> ordered scanner commit.

## Changes
1. Add scan telemetry: sampled frames, visual skips, visual confirmations, OCR retries, encode/OCR wall time and calls-per-cue.
2. Let a stable visual frame confirm a high-confidence non-short-ASCII subtitle candidate, avoiding a second PaddleOCR call solely for confirmation.
3. Extend an already confirmed active cue from stable visual frames without repeated OCR; keep a longer periodic OCR guard for safety.
4. Remove RC9 Hybrid's unconditional OCR of the next sampled frame. A second frame may run in parallel only if it independently qualifies for OCR under the pre-commit state.
5. Change Hybrid manager dispatch from fixed round-robin to an availability queue so the faster worker naturally receives more work under concurrent demand.
6. Preserve the existing RC9 GPU runtime installer in this performance RC; worker startup remains the final device truth test. Runtime-package policy changes require a separate Windows field-test scope.
7. Preserve PP-OCRv6 Small, detector+recognizer, private runtime, checkpoint key semantics, and CPU/GPU manual choices.

## Regression contracts
- Blank ROI still performs zero OCR.
- Short ASCII garbage still cannot become a cue from visual confirmation.
- High-confidence CJK candidate can promote after one OCR + one stable visual confirmation.
- Active cue end advances on stable visual frames.
- Hybrid does not OCR a stable lookahead frame merely because two workers exist.
- Hybrid worker acquisition is dynamic and cancellation-safe.
- Checkpoint resume/full completion remain deterministic.
- All generated call-map gates and full release gates pass.
