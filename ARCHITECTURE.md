# BiliSub Studio architecture

BiliSub Studio is a native Windows x64 application implemented in **C# + .NET 10 + WinUI 3**. This document describes the production tree only.

## Runtime ownership

```text
BiliSubStudio.exe             root NativeAOT launcher
└─ Runtime\BiliSubStudio.exe  self-contained WinUI application
   ├─ BiliSubStudio.App       UI, navigation, pickers, visual state
   ├─ BiliSubStudio.Core      application/core services
   ├─ FFmpeg / ffprobe        media decode, mux and render helpers
   ├─ yt-dlp                  Bilibili metadata/format resolver and final fallback downloader
   └─ Python OCR worker       private PaddleOCR worker managed by Core
```

There is no Go runtime, Go backend, localhost UI, browser UI, WebView or second BiliSub process in the production architecture.

## Repository ownership

- `csharp/src/BiliSubStudio.App` — WinUI shell/pages/services and application startup.
- `csharp/src/BiliSubStudio.Core` — configuration, jobs, media, video, subtitle, OCR, authentication, maintenance and editor logic.
- `csharp/src/BiliSubStudio.Launcher` — tiny root launcher that starts `Runtime\BiliSubStudio.exe`.
- `csharp/tests` — package-free Core and transport regression executables.
- `csharp/installer` + `csharp/scripts` — Windows verification, packaging and installer contracts.
- `internal/ocr/worker.py` — the only non-C# runtime source kept outside `csharp/`; it is embedded into the WinUI publish and checksum-verified.

Legacy Go source is intentionally absent from the production tree. Historical implementation remains retrievable through Git history.

## Application shell

Top-level navigation is deliberately small:

- `Tải media`
- `OCR phụ đề`
- `Chỉnh video`
- `Cài đặt`

`Cài đặt` owns the General, Performance, Login and Update/Support sections. A single application-wide log is owned by the shell and receives job/service events from all features.

## Unified media pipeline

`Tải media` owns one Bilibili URL and optional Video / Thumbnail / Subtitle selections. When the user selects nothing, the default contract is to download all available assets.

Video path:

1. `YtDlpResolver` resolves metadata and selects the requested DASH format.
2. `BilibiliPlayurlClient` enriches the selected format with Bilibili `baseUrl + backupUrl[]` endpoints.
3. `VideoDownloadService` maintains independent video/audio CDN state.
4. `RangeDownloader` uses inclusive 4 MiB HTTP/1.1 Range segments and preserves partial bytes after short bodies.
5. A weak primary route rotates to cached backup endpoints without losing the exact missing-byte offset.
6. Playurl is refetched only after cached endpoints are exhausted; format ID and stream size must remain identical before partial bytes can continue.
7. Adaptive single-connection recovery is attempted before yt-dlp becomes the final fallback.
8. FFmpeg stream-copies/remuxes the completed tracks into the requested output container.

The transport contract is covered by explicit regressions for short reads, HTTP 503 and primary-to-backup CDN continuation with byte-for-byte output validation.

## Subtitle pipeline

Subtitle tracks come from Bilibili/yt-dlp metadata. Official and AI tracks are ranked explicitly, converted to the selected SRT/TXT/JSON output, and can be bundled into the parent media job. Missing optional subtitle/thumbnail assets produce an explicit warning rather than silently substituting another asset.

## OCR pipeline

The C# OCR subsystem owns:

- private app-managed Python/PaddleOCR runtime installation under `Tools\OCR`;
- CPU/GPU/Hybrid worker lifecycle;
- local video frame extraction through app-owned FFmpeg;
- ROI validation, Chinese subtitle normalization and cue tracking;
- full-pipeline Auto benchmarking/capacity selection before scan work starts;
- deterministic OCR topology with one FFmpeg segment lane, one Python/PaddleOCR worker and one lane-local tracker per selected pipeline;
- explicit Fresh/Resume scan intent plus pause/checkpoint/cancel-and-delete and final SRT export.

Auto evaluates the complete ladder `1 -> 2 -> 4 -> 8 -> 16` with `Predict -> Probe -> Commit`. Before expanding to candidate `N`, Core reads live Windows physical RAM and native NVIDIA NVML VRAM, preserves explicit OS/app reserves, and rejects an unsafe expansion without allocating it. An allowed candidate must then create exactly `N` live Python workers, warm every worker and complete repeated `N`-way concurrent FFmpeg-to-OCR rounds on distinct real video frames. It advances only when the exact topology stays alive and measured throughput improves by at least 10%. Resource rejection, insufficient gain, startup/error/OOM or timeout transactionally restores the immediately preceding PASS level and only then begins the real scan. Manual `N` is never silently downgraded, but it must pass the same resource preflight and exact-topology probe.

For pausable OCR jobs, every OCR FFmpeg process belongs to a per-scan owned process group. Cancel is not terminal until that full FFmpeg process tree and the private Python worker pool both reach zero, and the exact matching checkpoint is verified absent. Resume preserves and re-probes the saved full topology; Fresh removes the matching checkpoint before running a new ladder. Paused cue preview is restricted to the contiguous safe frontier even when later segments have processed more work.

`internal/ocr/worker.py` is an implementation asset, not a second BiliSub backend. It communicates with the C# process through the private worker protocol and exposes no BiliSub HTTP server.

Weak single-glyph OCR receives one tighter-box retry on the same image/model; only a spatially overlapping, high-confidence single glyph can replace the initial reading. A matching already-confirmed glyph may corroborate a weaker actual reread, but never invent a missing recognition. Blank retry is requested only during active-cue recovery, not for every empty frame. `OcrCueReconciler` merges exactly identical touching/overlapping cues in live history and final lane reconciliation, without bridging real blank gaps or merging different words. Checkpoint schema 8 rejects schema-7 recognition artifacts while retaining old files until explicit Fresh/Cancel. The opt-in `--ocr-fragments-runtime <isolated-root> <field-video>` contract checks the first 12 seconds of the field fixture with actual Paddle, exact frame timing, pause/resume and SRT export.

## Editor / preview

WinUI pages own direct region interaction: create, select, move and resize over the displayed video rectangle. Saved regions keep stable IDs and time spans; the page owns selection plus Undo/Redo presentation, while `EditorRegionDocument` owns deterministic document history.

`EditorProjectStore` persists one schema-versioned project per normalized source path under `Data/Projects`. Writes are serialized, write-through and replace the project file only after serialization completes. Invalid or corrupt project files are quarantined instead of partially loading into the UI. Resizing the preview/window only recalculates screen geometry; normalized source coordinates remain unchanged.

The Editor keeps one fixed native preview and a compact right-side icon rail. Each tool owns one inspector; switching modes cannot make another ROI consume the same gesture. Vietnamese SRT selection and voice selection/sample are available before video selection. A validated SRT selected first remains pending and is attached atomically when the video-backed project opens. Windows layout smoke exercises the inspectors and these initial actions.

Core media services own ffprobe, exact processed-frame extraction and FFmpeg render orchestration. Direct manipulation uses a processed still frame for immediate feedback. `Xem bản chỉnh` plays continuously from the current playhead to the end of the source. Internally, the app creates short H.264/AAC proxy segments under its owned `Temp/Editor/Preview` root and chains them without exposing cache boundaries in the UX. Each proxy shifts intersecting region/cue timecodes into its segment window, then applies the same `VideoEditorService.BuildFilter`, ASS builder and source-audio policy as final export. This works independently of the source container's native-player compatibility, locks direct manipulation while playing, maps proxy position back to the source timeline, returns automatically to the editable frame and removes temporary files. Final outputs remain non-destructive and never share downloader concurrency/CDN/resume ownership. Editor export jobs use cleanup-aware cancellation: Cancel is not terminal until the FFmpeg process tree has exited and the partial `.rendering` artifact has been removed.

The Editor consumes externally translated Vietnamese SRT only. `EditorSubtitleDocument.UseVietnameseSrt` immediately populates spoken Vietnamese text while preserving the source fingerprint and every SRT block number, order and timing line. The single import action works without a Chinese SRT or translation runtime. The cue editor changes only Vietnamese text and writes a sidecar; explicit SRT export creates a separate file. `EditorVietnameseSubtitleWorkflow` compares actual draft content and explains every Voice readiness blocker. A video plus complete saved Vietnamese cues enables Voice.

AI preparation, model selection, translate/retranslate, lock-for-translation and live translation UI have been removed from Editor. Legacy translation Core and checkpoints remain for compatibility, but Editor no longer prepares or starts them. The explicit `vietnamese-srt-v1` project policy restores imported Vietnamese text and saved edits without an AI policy dependency. Legacy source-only projects require an explicit Vietnamese import instead of language guessing.

`LocalAsrService` supplies speech timing for Voice; its generated Chinese SRT is not substituted for the imported Vietnamese SRT. `LocalAsrInstaller` reuses the error-448-safe exact-patch Python bootstrap but creates a separate ASR venv, pins faster-whisper/CTranslate2 and downloads the multilingual small model from one immutable Hugging Face revision. Every model file is exact-size/SHA-256 verified and the worker is forced offline against that local directory. Before transcription, Core extracts a bounded real audio sample and benchmarks the complete GPU path; CUDA/VRAM/library failure or an unacceptably slow probe falls back to a measured CPU/int8 path before the real scan begins. Segment events are checkpointed atomically under `Data/Projects/ASR`; cancellation is terminal only after the owned Python/FFmpeg trees exit and temporary audio is removed.

ASR runtime manifests use one shared snake_case JSON policy for both writing and readiness checks. Offline contracts exercise the actual installer write/read round-trip, reopen, corruption and version/worker mismatch rejection. The opt-in `--asr-voice-runtime <isolated-root> <short-real-video> <vietnamese-srt>` contract exercises public ASR and TTS jobs end to end with installed real models, verifies runtime reuse, whole-cue cache and processed preview, and never substitutes fake speech provenance.

Subtitle placement is a distinct normalized video-space rectangle rendered over the native preview. Resizing the preview only recomputes its display geometry. Vietnamese SRT export preserves the source timecode/order; Editor render converts the completed cues and placement into a temporary ASS file and applies it in the same FFmpeg graph for real hardsub output.

Editor projects persist ASR provenance and the explicit source-audio policy: keep, duck with a normalized gain, or mute. Monitor mute/volume remains local player state only. Processed preview and final render share the same FFmpeg source/voice audio graph.

Voice/TTS uses the verified NGHI Ngọc Huyền ONNX/config pair through pinned local Piper. One model is loaded per job and reused; each uncached subtitle cue is synthesized once as a complete text, never split by Whisper pauses. The sample action uses the same generator without fabricated source/timing data. Cache entries bind model/config hashes, runtime/worker identity, cue time and text, and are hash-checked before reuse. Bounded atempo and measured duration determine fit/review; overlong speech remains intact in the cue cache while master placement respects SRT boundaries and requires user review. A bounded PCM-to-FLAC pipeline creates a unique master per run. C# verifies result order, identity, master hash and decoded format/duration; cancellation waits for process/file-handle cleanup and preserves previous outputs. See docs/engineering/EDITOR_NGHITTS_AUDIT.md for exact artifacts and listening status.

## Jobs, shutdown and logs

`JobManager` / `AppJob` own cancellation/progress/result state. Application-wide logging is centralized and persisted under the app data/log location. Safe shutdown coordinates active jobs and OCR checkpoint state before process teardown.

## Installation and update

Installed layout:

```text
BiliSub Studio\
├─ BiliSubStudio.exe
├─ Runtime\
├─ Data\
├─ Tools\
├─ Temp\
├─ Cache\
├─ Downloads\
└─ Uninstall\
```

The updater replaces only the verified runtime payload, not the protected data roots. Update payloads are GitHub Release ZIPs with exact SHA-256/size metadata from `update/beta.json` or `update/stable.json`.

## Verification rule

Production changes are source-first. Do not patch shipped binaries. Every significant change must pass the Windows verification workflow and the relevant targeted regression before release.
