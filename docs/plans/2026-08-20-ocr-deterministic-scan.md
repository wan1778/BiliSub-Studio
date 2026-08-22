# OCR deterministic scan plan — 2026-08-20

## Trigger

Field test on beta.8 reported frequent burned-subtitle misses even with the UI set to `4x Accurate` and `Sensitive`.

## Verified root cause in beta.8

Execution path:

`OCR page -> requestVideoFrameCallback -> ocrFrameCallback -> signature/stability gate -> browser PNG capture -> /api/ocr -> RapidOCR`

The beta.8 path is lossy for accuracy work:

1. OCR candidate sampling depends on frames *presented by Chromium*. At elevated playback rates Chromium may drop presented frames to keep playback moving; dropped frames cannot be recovered by the OCR queue.
2. The `Sensitive` selector only changes the final signature-difference threshold. A separate hard-coded stability gate (`stable > 0.12`, two stable samples required) can reject moving-background subtitle frames before the selected threshold is considered.
3. The beta.8 signature targets mostly bright near-neutral text with dark neighbors, so colored subtitle styles can be under-detected.
4. The queue backpressure prevents some OCR overload, but it cannot recover a frame the browser never presented.

## Implementation boundary

Production changes are limited to:

- `internal/ocr`: deterministic FFmpeg scan pipeline + subtitle-oriented change detector.
- `internal/api`: `/api/ocr/scan` wiring and FFmpeg tool preparation.
- `internal/jobs`: optional job result payload for long-running OCR scan results.
- embedded web UI: switch OCR source to native local path and poll the backend scan job.

Downloader, subtitle fetch, updater, cookie, video editor rendering, and download CDN/resume behavior are out of scope.

## New execution path

`OCR page -> /api/ocr/scan -> jobs.Manager -> internal/ocr.Scanner -> ffmpeg crop/fps rawvideo pipe -> candidate detector + periodic guard -> Manager.Run -> RapidOCR`

Contracts:

- Scan time is media-timestamp based, not browser playback based.
- FFmpeg stdout uses pipe backpressure. If RapidOCR is slower than decode, FFmpeg blocks instead of discarding scan samples.
- No scan frame is written to disk.
- Accurate mode samples 8 FPS and forces a guard OCR at least every 0.40 media seconds even when the change detector stays quiet.
- Change detection is subtitle-oriented and accepts white or colored high-contrast text.
- Existing one-RapidOCR-process serialized line protocol remains unchanged.
- Existing `/api/ocr` single-frame test remains available.

## Regression scenarios

1. Colored subtitle-like edges trigger the candidate signature.
2. Accurate mode uses 8 FPS and <=0.5 s guard interval.
3. Fake FFmpeg rawvideo stream is fully consumed and returns a cue through backpressure.
4. `/api/ocr/scan` API fixture returns a completed job with cues.
5. Existing OCR engine lifecycle tests continue to pass.
6. Embedded UI and source UI remain byte-identical.
7. Full go test/vet/race + Windows amd64 build + PE validator must pass before release.
