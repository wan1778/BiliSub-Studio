# Shared preview pipeline — RC5 internal plan

## Trigger
RC4 field test: Video Editor showed the selected HEVC/unsupported-codec video through FFmpeg fallback, while OCR showed duration/time but a black stage for the same class of video.

## Verified call-map before edit
- OCR preview: `/api/media` + `<video>` only.
- Editor preview: probe -> direct `/api/media` OR FFmpeg frame fallback.
- OCR full scan itself remained healthy and independent via `internal/ocr/scanner.go`.

## Root cause
Preview capability was duplicated by feature. Editor received codec fallback in RC3; OCR did not. A browser can parse duration/container yet fail to decode the video track, producing a black OCR preview even while backend OCR continues correctly.

## Change boundary
- Add one shared media-preview package and feature-neutral `/api/preview-info`, `/api/preview-frame` routes.
- OCR and Editor both consume the same probe/frame backend.
- OCR gets an `<img>` fallback frame and follows scan `media_seconds` through the same shared frame route.
- Remove feature-specific preview ownership; the current API exposes only shared `/api/preview-info` and `/api/preview-frame` routes.

## Do not change
- OCR scanner/detection/cue merge
- RapidOCR manager/models
- video downloader/CDN/resume
- editor export filters/rendering
- updater/lifecycle/native picker

## Acceptance
- Browser-direct fixture works in OCR and Editor.
- Unsupported-codec fixture shows a real fallback frame in OCR and Editor.
- OCR fallback follows live `media_seconds`.
- OCR ROI/Test current frame still works in fallback mode.
- Full app E2E + Go test/vet/race + Windows build/PE gate.
