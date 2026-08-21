# BiliSub Studio 4 Native Windows Architecture

This tree is the maintainable source for the v4 backend. It intentionally replaces the old layered binary-patching architecture.

## Non-negotiable ownership rule

There is exactly **one BiliSub Studio backend process**.

- `cmd/bilisub` owns process startup/shutdown, Windows Job Object containment, native-window startup, and self-update handoff only.
- `internal/application` is the application boundary used by the native UI. It owns feature orchestration but not OCR/video algorithms.
- `internal/nativeui` owns the native Windows x64 window, Win32 controls/dialogs, layout, QR presentation, direct-manipulation input, and UI state. It never starts FFmpeg/Python itself.
- `internal/nativeplayer` owns local media preview playback using the app-owned FFmpeg binary plus native Windows frame/audio output. It does not own OCR timing or scan accuracy.
- `internal/qrcode` owns the browser-free QR matrix encoder used by native Bilibili login.
- `internal/api` and the embedded HTML UI remain only as a regression/parity adapter during migration. `cmd/bilisub` does not import or start them in production.
- OCR remains one BiliSub-owned subsystem. It owns private PaddleOCR workers only: a normal primary worker lifecycle plus a bounded dynamic shared worker pool during RC13 parallel full-video scans. Hybrid may keep one limited CPU helper beside GPU workers. These workers do not expose a BiliSub HTTP API or localhost port.
- `yt-dlp.exe` is a child resolver/fallback downloader only.
- `ffmpeg.exe` is a child mux/remux tool only.
- There is no `BiliSubStudioCore.exe`, no second BiliSub HTTP server, and no wrapper/core proxy pair.

This rule prevents the old failure mode where the wrapper remained alive while the embedded core/OCR port had died (`connectex: target machine actively refused it`).

## Process graph

```text
BiliSubStudio.exe  (single native Windows owner)
├─ Win32 native UI (`internal/nativeui`)
│  ├─ native file/folder dialogs
│  ├─ native QR rendering
│  └─ native media surface + controls
├─ Application layer (`internal/application`)
│  ├─ Video download service
│  │  ├─ Tools/yt-dlp.exe
│  │  └─ Tools/ffmpeg.exe
│  ├─ Video cleanup editor
│  │  └─ Tools/ffmpeg.exe
│  ├─ Subtitle service
│  │  └─ Tools/yt-dlp.exe
│  └─ Native preview (`internal/nativeplayer`)
│     └─ Tools/ffmpeg.exe + Windows audio/frame output
└─ OCR subsystem
   ├─ Tools/ffmpeg.exe × N (bounded deterministic lane streams)
   ├─ Tools/OCR/runtime/cpu/.../python.exe -u worker.py --device cpu
   └─ Tools/OCR/runtime/gpu/.../python.exe -u worker.py --device gpu:0 × M

All normal helper processes inherit BiliSub's Windows Job Object and are killed when the app exits/crashes. The verified self-updater alone is launched with breakaway permission so it can replace the old EXE.
```

## Startup and lifecycle

`cmd/bilisub/main.go`

1. If running as the breakaway self-updater, perform the verified atomic EXE swap before creating process containment.
2. Create the Windows Job Object (`KILL_ON_JOB_CLOSE`) for the main BiliSub process and normal helpers.
3. Resolve the portable root from the running EXE directory and initialize `Data`, `Tools`, `Tools/OCR`, `Temp`, `Cache`, and `Downloads`.
4. Create `application.App`, which wires jobs/tools/OCR/video/subtitle/editor services.
5. Enter `nativeui.Run` on the locked Windows UI thread and create the BiliSub native window/message loop.
6. Native controls call `application.App` methods directly. Production startup creates no HTTP listener, localhost UI, browser tab, WebView, or WebView2.
7. On `WM_CLOSE`, `application.PrepareShutdown` requests Pause for every active pausable OCR job and waits for the scanner's fsynced safe checkpoint. If safe Pause fails/times out, close is refused instead of discarding OCR progress. Only after all pausable OCR jobs are safe are remaining jobs cancelled and OCR workers stopped.
8. If an update was prepared, `cmd/bilisub` launches the downloaded updater with `proc.Breakaway`; the old app exits, the updater swaps the executable, then starts the installed native EXE.

`internal/api` + embedded HTML remain test-only parity infrastructure. They are not imported by `cmd/bilisub` and their route strings must not appear in the production release binary.

## Native UI -> application boundary

The production UI does not use `/api/*`. Main native call paths are:

- Subtitle: `nativeui` -> `application.Metadata` / `StartSubtitle` -> `internal/subtitle`.
- Video: `nativeui` -> `application.Metadata` / `StartVideo` -> `internal/video`.
- OCR preview: native picker -> `application.PreviewInfo` / `EnsureFFmpeg` -> `internal/nativeplayer`.
- OCR frame test: `application.OCRFrame` -> FFmpeg ROI extraction -> `ocr.Manager.Run`.
- OCR scan: `application.StartOCRScan` -> `ocr.Scanner.Run`.
- OCR Pause: `application.PauseJob` -> `jobs.Job.RequestPause` -> scanner safe boundary -> fsynced checkpoint -> `PauseComplete`.
- OCR checkpoint inspection: `application.InspectOCRCheckpoint` reports schema 3/4, aggregate work %, contiguous safe frontier, cue count/recent cues, lane topology and cumulative OCR/visual timing counters. Native Pause completion refreshes from this checkpoint instead of displaying far-lane cues from the aggregate scan result.
- OCR telemetry: live, paused-checkpoint and final states use the same native telemetry model. It reports list/total cues, frames, OCR images/inferences, images-per-cue, batch average, lane selected/active/completed, boundary merges, visual skip/confirm/retry, decoder/fallback, pipeline/visual/encode/OCR/elapsed time, realtime speed, Auto benchmark information when available, and latest text/confidence. Cue-list summary is a separate control so rendering the list cannot overwrite telemetry.
- OCR timeline QC: native trackbar -> `nativeplayer.Seek` -> nearest cue selection; cue LISTBOX selection -> `nativeplayer.Seek(cue.Start)`.
- OCR export: `application.ExportOCR` -> final Chinese-only validator -> SRT writer.
- Editor: native picker/player/direct manipulation -> `application.StartEditor` -> `videoedit.Service.Run`.
- Login QR: `application.QRStart` -> `qrcode.Encode` -> native GDI QR render -> `application.QRPoll`.
- Settings/update/storage: native controls -> corresponding `application.App` methods.

## Video pipeline

### Resolve

`internal/video/resolver.go`

1. yt-dlp resolves Bilibili metadata and signed stream URLs.
2. The requested height is selected.
3. For MP4, H.264/AVC is preferred **only among streams at the same height** for editing compatibility. Resolution is never reduced merely to obtain AVC.
4. Video-only and audio-only DASH streams are selected separately.
5. Each resolve increments a generation used for signed URL refresh coordination.

### Download

`internal/video/downloader.go`

1. Probe `Range: bytes=0-0` and validate the returned `Content-Range`.
2. If Range is valid, split the stream into 32 MiB inclusive segments (the last segment may be smaller).
3. Preallocate one `<track>.stream.partial` work file to the exact probed stream size. Workers own disjoint byte ranges and write directly at their segment offsets; there is no per-segment data file and no final full-stream concat copy.
4. Persist completed segment indexes in `<track>.stream.resume.json`. A segment becomes a resume checkpoint only after strict status/range/length/body validation, the segment bytes are written, the shared work file is synced, and the manifest is atomically replaced.
5. A short/oversized body or invalid Range response leaves that segment uncommitted. A retry starts again at the same segment boundary and overwrites any uncommitted partial bytes in that range.
6. Every two generic failures, and immediately for strong CDN-body failures, workers keep beta.5's generation-locked yt-dlp URL refresh behavior. CDN ranking/backup rotation is intentionally a separate later stage.
7. Completed manifest entries remain across a failed/cancelled job and are reused. An invalid/mismatched manifest never trusts same-sized stale work-file bytes; uncommitted ranges must be downloaded again.
8. Resume directories include the actual resolved video/audio format IDs, preventing data from different `best` resolutions/codecs from being mixed.
9. A previously completed `.stream` may be reused after a later ffmpeg failure only inside the same stream-specific resume directory and only when its exact probed size matches. A work file is promoted to `.stream` only when every segment is committed and the final file size exactly matches the probe result.
10. When Range is unsupported/broken after bounded retries, the service falls back to yt-dlp's downloader with resume/retry enabled.

### Global connection budget

`stable = 1`, `fast = 4`, `turbo = 8` total connections per video job.

For video+audio with parallel mode, one connection is reserved for audio and the remainder is assigned to video. Failure of one sibling does not cancel already useful work from the other sibling.

### Mux

`internal/video/service.go`

- video+audio: ffmpeg maps video stream 0 and audio stream 1 and stream-copies them.
- video-only/audio-only: ffmpeg performs a proper single-track remux rather than renaming raw `.m4s` bytes.
- output is verified non-empty before the resumable work cache is deleted.

## Video cleanup editor

The editor is a separate owner from the Bilibili downloader. The native UI performs direct manipulation and local preview through `internal/nativeplayer`; `internal/videoedit` performs the final FFmpeg render.

Flow:
1. Native UI opens the source through the Win32 file picker.
2. `internal/nativeplayer` seeks/decodes local frames with the app-owned FFmpeg binary and renders them directly in the native window; codec support never depends on Chrome/Edge.
3. Regions are stored as normalized `x/y/w/h` coordinates and are edited directly on the native preview.
4. Each region owns `Blur`, `Mosaic`, or `Cover`, plus whole-video or `start/end` scope.
5. `application.StartEditor` creates a normal cancellable job. `internal/videoedit` converts normalized regions to source pixels and builds a sequential FFmpeg filter graph. Editor tool preparation is intentionally FFmpeg-only; local editing does not depend on yt-dlp or Bilibili cookie state. MP4 export normalizes to H.264/yuv420p + AAC for reliable playback.
6. FFmpeg writes to a temporary render path, reports progress through `-progress pipe:1`, then atomically renames only after a non-empty output exists.
7. The input file is never overwritten; name collisions generate a new output path.

The editor must never change downloader concurrency/CDN/resume behavior.

## OCR pipeline

`internal/ocr/manager.go`

The OCR engine is **not an HTTP backend**. `internal/ocr.Manager` owns the single OCR subsystem under `Tools/OCR`. It keeps the normal primary CPU/GPU worker lifecycle for manual-frame OCR, and RC13 may temporarily expand that private worker set into a bounded shared scan pool for parallel full-video OCR. Hybrid remains GPU-first with one tightly limited CPU helper; workers never expose a BiliSub HTTP port.

Installation/lifecycle contract:

- `Tools/OCR` is the only current OCR root.
- one-click preparation downloads a pinned `uv` bootstrap and private uv-managed Python under `Tools/OCR/python`; CPU and GPU use separate venvs under `Tools/OCR/runtime/cpu` and `Tools/OCR/runtime/gpu`; each runtime has its own manifest while both use the shared embedded `worker.py` and `Tools/OCR/models` cache;
- CPU installs pinned `paddlepaddle` + `paddleocr`; GPU installs pinned `paddlepaddle-gpu` + `paddleocr` from the supported CUDA wheel index selected from the NVIDIA driver; no system CUDA toolkit is modified;
- OCR device modes are `auto`, `cpu`, `gpu`, and `hybrid`; Auto prefers a usable NVIDIA GPU and falls back to CPU if GPU initialization fails, while explicit GPU/Hybrid reports failure rather than pretending to use CPU;
- no system Python, PATH, CUDA, or user pip setup is required;
- `PADDLE_PDX_CACHE_HOME` is set under `Tools/OCR/models` before importing PaddleOCR so official model downloads remain portable;
- the worker must report exactly `PP-OCRv6_small_det` + `PP-OCRv6_small_rec` before the manager enters Ready;
- JSON-line requests carry request IDs, so a cancelled request cannot be mistaken for a later response. The RC12 bounded `images_base64` batch protocol remains readable/usable only as a legacy transport path; RC13 full-video performance comes from multiple independent scan lanes feeding a shared pool of private PaddleOCR workers;
- after the new worker reaches Ready, the obsolete sibling `Tools/RapidOCR` directory may be removed.

Manual-frame flow:

1. `application.OCRFrame` receives local video path + current native-player timestamp + normalized ROI.
2. App-owned FFmpeg extracts exactly that ROI/frame; native preview/player state does not own OCR accuracy.
3. `ocr.Manager.Run` sends the encoded frame to the active PaddleOCR worker. Hybrid is GPU-first: sequential work stays on GPU, while CPU accepts an independent request only when GPU is already occupied; this avoids fixed 50/50 scheduling on laptop-class CPUs.
4. The worker returns detection-backed text lines, confidence, and boxes.

Full-scan flow:

1. `application.StartOCRScan` starts a **pausable** OCR job and ensures FFmpeg + the same PaddleOCR manager. Native UI sends `parallelism=auto|1|2|4|8|16`; an empty field is reserved for the RC12 legacy single-timeline contract.
2. `ocr.Scanner.Run` either resumes an existing schema-4 topology or chooses a fresh parallelism. Auto follows **Predict → Probe → Commit**: after each short real-video level it samples Windows CPU/RAM plus NVIDIA GPU/VRAM, predicts whether the next 1 → 2 → 4 → 8 → 16 level can preserve resource safety margins, and only then expands the worker pool. It stops before expansion on RAM/VRAM pressure or CPU/GPU saturation, also stops below 10% additional throughput or at the duration cap, and keeps HF1's bounded timeout/cancel/reset path if a permitted probe later stalls. The chosen topology is locked for the whole scan/resume lifecycle.
3. `buildScanSegments` divides the unique core timeline into N deterministic ranges and adds bounded pre/post overlap. Every lane launches its own bounded FFmpeg `-ss` + `-t` stream and retains the existing NVDEC/software fallback, FPS sampler, sparse visual gate, enhanced retry, and lane-local `subtitleTracker`. Tracker state is never shared between lanes.
4. Genuine OCR candidates from all active lanes acquire free workers from the shared `ocr.Manager` scan pool. A full pool applies backpressure; candidates are never dropped. Hybrid scheduling remains GPU-first and CPU help is deliberately capped.
5. Every tracker observation and restored cue passes `NormalizeChineseSubtitleText`: a valid Chinese OCR cue must contain at least one Han ideograph and cannot contain letters from another script. Digits/punctuation/symbols remain valid only when Han text is present; repeated spaced punctuation artifacts such as `， ，` are normalized. Invalid OCR samples are inconclusive (not empty), so they cannot create a cue or prematurely close an active Chinese cue. Cue ownership is then determined by cue start inside each lane's non-overlapping core. `reconcileSegmentCues` applies the same validator again, sorts owned cues globally and performs conservative near-boundary duplicate/continuation reconciliation; completion order of lanes must not change final subtitle order.
6. Progress/realtime telemetry counts **unique core seconds**, never overlap seconds. `application.JobSnapshot` exposes selected/active/completed lanes, OCR image/inference counts, boundary merges, decoder state, realtime speed, and visual-confirmation telemetry. Native UI keeps a bounded recent-cue list while running and renders the full completed cue set; trackbar seek selects the nearest cue and cue selection seeks the native player. The UI keeps one preview and never opens N players or owns scan accuracy.
7. `application.ExportOCR` reapplies `NormalizeChineseSubtitleText` before numbering/writing SRT entries, so stale/legacy/frontend cue state cannot reintroduce foreign-script OCR garbage into the Chinese SRT. RC12 request micro-batching remains only on the legacy schema-3 path. New RC13 parallel lanes force request batch 1 so performance and scheduling semantics are unambiguous.

Pause/resume flow:

1. Native **Tạm dừng** calls `application.PauseJob`, which calls `jobs.Job.RequestPause` and waits for the scanner handshake.
2. Each active lane may stop only after its own `subtitleTracker.CanCheckpoint()` boundary has produced a safe lane state. The parallel coordinator waits for **all** active lanes (completed lanes are already safe), then writes one schema-4 topology checkpoint through temp-file + file `Sync` + atomic rename and only then returns `ErrScanPaused`.
3. Schema 4 stores selected topology, deterministic segment ranges, per-lane media/cues/active tracker state/frames/cumulative telemetry. `application.OCRCheckpoint` reads that durable state so reopening the app can expose `Tiếp tục quét` without guessing.
4. Resume does not repartition or recalibrate Auto: completed lanes stay complete and unfinished lanes resume from their saved safe positions using the saved topology. A valid RC12 schema-3 checkpoint with no schema-4 owner is still resumed through the legacy one-lane path so existing progress is not discarded.
5. `Quét lại từ đầu` explicitly removes both matching schema-4 and legacy schema-3 checkpoints through `application.RemoveOCRCheckpoint`. A normal clear-result action does not destroy resumable progress.

## Tool installation

`internal/tools/manager.go` and `internal/ocr/install.go`

- `yt-dlp.exe` -> `Tools`
- `ffmpeg.exe` / `ffprobe.exe` -> `Tools`
- OCR runtime/models -> `Tools/OCR`
- downloads are written to `.tmp` and renamed only after a complete copy

## Cookie handling

`internal/appstate`

- Bilibili cookie is stored in `Data/session.bin` using Windows DPAPI.
- A temporary Netscape cookie file is generated only for yt-dlp.
- No cookie is embedded into the executable.

## Update flow

Beta/RC builds read the dedicated Drive `version_beta.json` manifest (file ID `18gW_x8Y_jD-PMyk5kv7tXYF--qzsQDiT`); stable builds read the release `version.json`. `download_url` may be either a bare Google Drive file ID or a full Drive URL; the updater normalizes both forms before downloading. This keeps prerelease builds isolated from the stable 3.x/4.x update channel.

The downloaded update is verified by expected size and SHA-256 before any swap. The downloaded EXE launches in `--apply-self-update` mode and swaps the installed executable after the old process exits. There is no separate embedded updater executable.

## Regression gates before release

A release is not accepted unless all of these pass:

```text
go test -count=1 ./...
go vet ./...
go test -race -count=1 ./...
python scripts/audit_ui_contract.py                    # legacy parity adapter
python scripts/audit_native_ui.py                     # production native UI/startup contract
python scripts/audit_standalone_gpu.py                # no nvidia-smi/PowerShell GPU dependency
python scripts/generate_code_map.py --check
go run scripts/generate_ocr_call_map.go --check
python -u scripts/browser_e2e.py                      # legacy parity oracle only
GOOS=windows GOARCH=amd64 CGO_ENABLED=0 go test -exec=/bin/true -count=1 ./...
GOOS=windows GOARCH=amd64 CGO_ENABLED=0 go build ... ./cmd/bilisub
python scripts/validate_release.py <candidate.exe>
```

The release validator requires one PE32+ x86-64 image, native-window/OCR/tool markers, and rejects legacy `/api/...` route markers linked into the production binary.

## C# + WinUI 3 migration lane

The new implementation lives under `csharp/` and migrates one ownership slice at a time. The Go + Win32 executable remains the frozen behavioral reference until the C# candidate passes module parity, full regression, real WinUI visual QA, Windows publish verification and field testing.

Current checkpoint: `CSharp-P2-SourceComplete`. Every planned production owner has a C# implementation, while the frozen Go executable remains the release oracle until the Windows field gate passes.

```text
App.OnLaunched
  -> MainWindow composition root
  -> BiliSubApplication
     -> Settings / DPAPI authentication / jobs / updater
     -> app-owned FFmpeg + ffprobe + yt-dlp
     -> media preview / HTTP Range video / subtitle
     -> hardware / private PaddleOCR / schema-4 checkpoint
     -> native editor render
```

The lane does not call the Go HTTP parity adapter, create a local server, host browser UI, or bypass the application boundary for production jobs. Exact mappings, cancellation edges and exclusions are recorded in `docs/migration/CSHARP_WINUI3_CALL_MAP.md`, `docs/migration/CSHARP_CODE_MAP.generated.md` and `docs/migration/MIGRATION_LEDGER.md`.

## Windows child-process policy

Every normal non-interactive child process (`yt-dlp`, `ffmpeg`/`ffprobe`, pinned `uv`, and private PaddleOCR Python workers) must be created through `internal/proc.Hide` and inherit BiliSub's kill-on-close Windows Job Object. The verified self-updater is the sole exception and is started through `proc.Breakaway` so it can outlive the old process long enough to swap/restart the EXE. On Windows this sets `HideWindow` plus `CREATE_NO_WINDOW`; direct `exec.Command*` calls are not allowed for those helpers because they cause visible CMD flashes.

### OCR install integrity
`Tools/OCR` is healthy only when each requested runtime manifest under `Tools/OCR/runtime/<device>/install.json` matches the embedded worker checksum and its private venv exists. Final readiness additionally requires every active worker to initialize PaddleOCR, report the exact PP-OCRv6 Small detection/recognition pair, and confirm the requested device/CUDA state. Missing/corrupt runtime or model state is repaired by the same one-click prepare action; a lone executable or cache folder is never enough.

## Preview controls

OCR transport controls live outside the video pixels so hard subtitles at the bottom edge are never obscured. OCR region geometry is normalized against the native preview rectangle. Video Editor follows the same direct-manipulation coordinate contract.

- Long-video OCR stores durable stable-state checkpoints in `Data/OCRCheckpoints`; source metadata + ROI + scan mode/sensitivity + active-guard semantics are part of checkpoint identity, and successful completion removes the checkpoint. New RC13 parallel scans use schema 4 with per-lane topology/state and cumulative telemetry; valid RC12 schema-3 checkpoints remain resumable through the legacy one-lane path. Older RC11 schema-2 checkpoints are not treated as RC12/RC13 resumable state.
- RC13 preserves the RC11/RC12 NVIDIA CUDA/NVDEC path independently per bounded scan lane. On success, FFmpeg performs FPS selection while frames remain CUDA hardware references, downloads only sampled frames, then runs the existing CPU ROI crop/scale/RGB path. Any probe failure uses software decoding; a later lane-local NVDEC failure falls back through that lane path without changing subtitle thresholds or dropping OCR candidates.
