# OCR core replacement: RapidOCR -> PaddleOCR PP-OCRv6 Small

## Scope lock

This is a replacement of the single existing OCR subsystem. It does not add a second OCR feature, page, route family, or user-visible engine selector.

Frozen unless a verified OCR call path requires a compatibility change:

- downloader
- subtitle fetch/export outside OCR SRT export
- video editor
- updater
- native pickers
- lifecycle
- shared media preview

## User-facing invariant

The existing flow remains:

```text
OCR phụ đề
  -> Chọn video
  -> ROI
  -> Chuẩn bị bộ nhận diện
  -> Đọc thử khung hiện tại
  -> Bắt đầu quét chính xác
  -> Xuất SRT
```

`Chuẩn bị bộ nhận diện` remains one-click: BiliSub owns runtime installation, package/model cache, health check, repair, and worker startup under `Tools/OCR`.

## Source-owned call map before editing

```text
web #prepareOCR
  -> POST /api/ocr/engine/ensure
  -> api.Server.ocrEnsureHandler
  -> ocr.Manager.Ensure
  -> ocr.Manager.ensureInstalled
  -> ocr.Manager.start

web #testOCR
  -> POST /api/ocr {path,time,region}
  -> api.Server.ocrHandler
  -> ensureFFmpeg
  -> ocr.CaptureFramePNGBase64
  -> ocr.Manager.Run

web #startOCR
  -> POST /api/ocr/scan
  -> api.Server.ocrScanHandler
  -> ensureFFmpeg
  -> ocr.Manager.Ensure
  -> ocr.Scanner.Run
  -> OCRRunner.Run
  -> job result
  -> GET /api/job
  -> web ocrPoll

web remove OCR
  -> POST /api/ocr/engine/remove
  -> api.Server.ocrRemoveHandler
  -> ocr.Manager.Remove
```

## Target call map

```text
web routes: unchanged
  -> api handlers: unchanged contract
  -> ocr.Manager
       -> installer: managed private runtime under Tools/OCR
       -> worker.py: one persistent PaddleOCR process
       -> PP-OCRv6_small_det + PP-OCRv6_small_rec
       -> JSON-line request/response with request IDs
  -> ocr.Scanner
       -> FFmpeg ROI stream
       -> cheap candidate filter
       -> PaddleOCR detector+recognizer only for candidates
       -> temporal confirmation/cue state
       -> 5-minute durable checkpoints under Data/OCRCheckpoints + exact resume offset
```

## Engine lock

- PaddleOCR package: pinned, not floating latest.
- PaddlePaddle CPU runtime: pinned.
- OCR models: PP-OCRv6 Small detection + recognition only.
- Document orientation, document unwarping, and text-line orientation modules: disabled.
- Default device: CPU. No CUDA requirement.
- PaddleX model cache redirected under `Tools/OCR/models` before importing PaddleOCR.
- No user Python/PIP/PATH setup.

## Edit discipline

For every production change:

1. identify current owner and callers;
2. add/adjust the smallest regression test first;
3. make one cohesive source change;
4. run `gofmt` if Go changed;
5. run the nearest package test immediately;
6. run compile immediately;
7. inspect `git diff` for unrelated changes;
8. regenerate/check code map if call ownership changed;
9. only then continue.

No Drive promotion until exact Windows candidate passes field tests.


## Long-video scanner lock

- Supported design target: local video up to at least 10 hours.
- Modes use one PP-OCRv6 Small model; modes alter sampling strategy only.
- Sampling: Fast 1.5 FPS / Balanced 2.5 FPS / Accurate 4 FPS.
- Inactive ROI with no subtitle-like edge activity must not invoke PaddleOCR merely because the guard elapsed.
- A candidate/new/empty transition forces the next sampled frame through OCR for temporal confirmation.
- Normal/CJK text requires two matching observations; short ASCII fragments require stronger confirmation.
- One missed empty OCR result does not split an active subtitle.
- Durable checkpoint identity includes source file metadata + ROI + scan contract.
- Stable tracker state is saved every 5 media minutes under Data/OCRCheckpoints.
- Resume seeks FFmpeg to the saved media offset and restores confirmed cues/current active cue.
- Successful completion removes the checkpoint; cancel/error preserves the latest stable checkpoint.
- The continuous FFmpeg ROI pipe remains one process during a run to minimize process-boundary regressions; the 5-minute durability boundary is logical rather than a forced FFmpeg restart.
