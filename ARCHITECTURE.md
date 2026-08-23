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

## Editor / preview

WinUI pages own direct region interaction: create, select, move and resize over the displayed video rectangle. Saved regions keep stable IDs and time spans; the page owns selection plus Undo/Redo presentation, while `EditorRegionDocument` owns deterministic document history.

`EditorProjectStore` persists one schema-versioned project per normalized source path under `Data/Projects`. Writes are serialized, write-through and replace the project file only after serialization completes. Invalid or corrupt project files are quarantined instead of partially loading into the UI. Resizing the preview/window only recalculates screen geometry; normalized source coordinates remain unchanged.

The Editor keeps one fixed native preview and a compact right-side icon rail. Subtitle, Blur, Audio and Export each own exactly one inspector; switching mode cannot make the other ROI type consume the same pointer gesture. SRT selection and local-model preparation are intentionally available before video selection. A validated SRT selected first remains pending and is attached atomically when the video-backed project opens. Windows layout smoke cycles all four inspectors and rejects a build if those two initial actions regress to disabled.

Core media services own ffprobe, exact processed-frame extraction and FFmpeg render orchestration. Direct manipulation uses a processed still frame for immediate feedback. `Xem bản chỉnh` creates a bounded 12-second H.264/AAC proxy from the current playhead under the app-owned `Temp/Editor/Preview` root. The proxy shifts intersecting region/cue timecodes into the segment window, then applies the same `VideoEditorService.BuildFilter`, ASS builder and source-audio policy as final export. It works independently of the source container's native-player compatibility, locks direct manipulation while playing, maps proxy position back to the source timeline, returns automatically to the editable frame and removes its temporary file. Final outputs remain non-destructive and never share downloader concurrency/CDN/resume ownership. Editor export jobs use cleanup-aware cancellation: Cancel is not terminal until the FFmpeg process tree has exited and the partial `.rendering` artifact has been removed.

The common subtitle path is native Editor state, not the downloader's normalizing subtitle path. `EditorSubtitleDocument` strictly preserves every imported SRT block number, order and timing line. `TranslationSkillBundle` loads the exact bundled `Dịch Trung Tu Tiên` ZIP only after SHA-256, entry-count, expanded-size, required-file and path-traversal checks; core rules are always present and reference sections are retrieved by the current Chinese source terms.

`LocalSubtitleTranslationService` owns a pinned, app-managed Qwen3-8B Q4_K_M GGUF and pinned llama.cpp Vulkan/CPU runtime under `Tools/Translation`. Both downloads are exact-size/SHA-256 verified; the model URL is commit-pinned and partial downloads are resumable. Inference is an owned `llama-cli` child over files/stdout with strict cue-ID JSON, never Ollama, localhost or a second BiliSub backend. A whole-SRT terminology/character bible is accumulated before bounded translation batches. Completed batches are atomically checkpointed under `Data/Projects/Translation`; cancellation reaps the process but preserves completed cues.

For video without SRT, `LocalAsrService` owns the Chinese speech-to-SRT path. `LocalAsrInstaller` reuses the error-448-safe exact-patch Python bootstrap but creates a separate ASR venv, pins faster-whisper/CTranslate2 and downloads the multilingual small model from one immutable Hugging Face revision. Every model file is exact-size/SHA-256 verified and the worker is forced offline against that local directory. Before transcription, Core extracts a bounded real audio sample and benchmarks the complete GPU path; CUDA/VRAM/library failure or an unacceptably slow probe falls back to a measured CPU/int8 path before the real scan begins. Segment events are checkpointed atomically under `Data/Projects/ASR`; cancellation is terminal only after the owned Python/FFmpeg trees exit and temporary audio is removed.

Subtitle placement is a distinct normalized video-space rectangle rendered over the native preview. Resizing the preview only recomputes its display geometry. Vietnamese SRT export preserves the source timecode/order; Editor render converts the completed cues and placement into a temporary ASS file and applies it in the same FFmpeg graph for real hardsub output.

Schema-4 Editor projects persist both ASR provenance and the explicit source-audio policy introduced in schema 3: keep, duck with a normalized gain, or mute. Monitor mute/volume remains local player state only, while the processed proxy and final render both map the persisted source policy through the same exact FFmpeg audio argument builder. Filtered audio is encoded, unchanged final MKV audio may be stream-copied, and mute emits no source-audio stream. Future TTS clips and separated stems must extend this shared preview/render audio graph rather than bypassing it with a player-only approximation.

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
