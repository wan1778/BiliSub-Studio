# BiliSub Studio v4.0.0-beta.12 — Native Windows UI completion report

## Native UI/workflow completion scope

This report supersedes the browser-era candidate as the current native release evidence. Production startup is native-only. The legacy browser adapter remains test-only as a parity oracle.

UI completion in this change includes:

- Segoe UI + per-monitor DPI scaling, minimum window size, keyboard navigation (`Tab`/`Shift+Tab`, `Ctrl+1..5`, `F1`) and native tooltips;
- beginner-facing title/help text, progress, empty/loading/error/success and disabled states for all five pages;
- explicit DPI-scaled layout for every named native control plus geometry audit across 1080x800, 1100x820, 1400x840, 1600x900 and 1920x1080 logical clients;
- Subtitle and Video URL-bound metadata: editing the URL invalidates prior track/quality immediately, and stale metadata cannot start a download;
- strict OCR live ROI validation: finite 0–100 values, `Bottom > Top`, `Right > Left`, minimum ROI size; invalid state disables Test/Start and explains the error;
- strict Editor live validation: finite X/Y/W/H, nonzero size, `X + Width <= 100`, `Y + Height <= 100`, strength 2–40, valid time range; invalid state is not silently clamped into the model;
- all native `EnableWindow` writes centralized in `syncControls()`; text edits that affect button state are wired with control IDs/`EN_CHANGE`;
- Editor timed controls disable while `Áp dụng toàn video` is checked;
- QR/login/update/bug-report controls have explicit loading/ready/error/success lifecycle;
- OCR checkpoint inspection/full telemetry, cue↔timeline seek, safe-close Pause/checkpoint and native fullscreen remain preserved.

## Current automated matrix — working tree before final commit

PASS:

```text
go test -count=1 ./...
go vet ./...
go test -race -count=1 ./...
python scripts/audit_ui_contract.py
python scripts/audit_native_ui.py
python scripts/audit_native_usability.py
python scripts/audit_native_layout_geometry.py
python scripts/audit_native_interactions.py
python scripts/audit_feature_parity.py
python scripts/audit_standalone_gpu.py
python scripts/audit_application_boundary.py
python scripts/audit_dependency_process.py
python scripts/generate_code_map.py --check
go run scripts/generate_ocr_call_map.go --check
python -u scripts/browser_e2e.py
GOOS=windows GOARCH=amd64 CGO_ENABLED=0 go test -exec=/bin/true -count=1 ./...
GOOS=windows GOARCH=amd64 CGO_ENABLED=0 go build -trimpath -ldflags="-s -w -H=windowsgui" ./cmd/bilisub
python scripts/validate_release.py <candidate>
```

Observed parity oracle: `BROWSER E2E: PASS (102 API calls, 31 unique API routes, all static product buttons exercised)`. Windows cross-build is `PE32+`, `x86-64`, GUI.

The final candidate is not promoted until the matrix is re-run on the exact clean commit and the Windows-required field checklist is executed on that exact SHA-256 binary. Linux cross-build cannot truthfully prove visible Windows DPI/font rendering, native dialogs, real audio/codec playback, fullscreen/multi-monitor, Task Manager cleanup or updater self-swap.

---

## Historical RC13/HF reports

# BiliSub Studio beta.12 RC13-HF7 — HEVC direct-playback probe automated report

## RC13-HF7 HEVC preview root-cause correction

Windows field testing of HF6 showed the Play/Mute controls still hard-disabled for the real source even while no OCR scan owned the preview. Source audit found the real owner: `internal/mediapreview.parsePreviewInfo()` classified HEVC/H.265 in MP4/M4V/MOV as `direct_compatible=false`, forcing frame-fallback before Chromium was allowed to try hardware/system HEVC decode. HF6 therefore fixed only transient readiness inside direct mode; the user's file never entered direct mode.

HF7 changes the shared preview contract from **HEVC pre-rejection** to **optimistic direct attempt with runtime fallback**:

- MP4-family `hevc` is returned as a direct-playback candidate.
- OCR/Editor set `/api/media` on the `<video>` element and let the actual browser/runtime decide.
- Existing `video.onerror` remains the fallback boundary and switches to FFmpeg frame preview when decode really fails.
- No fake enabling: Play/Mute are usable only while the real `<video>` path is active; fallback frame mode still disables audio/playback semantics it cannot provide.

This change does not touch OCR, trackers, parallel lanes, Auto, checkpointing, SRT filtering, or timestamp logic.

## Scope

RC13 is built from the RC12 source baseline recorded as upstream commit `c7da66add544748eaadc618f2f9e8d5c8aaf15ca`. The source archive supplied for RC12 did not include `.git`; `docs/engineering/RC13_IMPLEMENTATION_LEDGER.md` records that provenance, and local commits are used only to bisect RC13 implementation phases.


## RC13-HF6 direct-preview Play/Mute state correction

Windows field testing of HF5 showed a direct-compatible OCR source with a valid preview/timeline while the `Phát` and `Bật tiếng` buttons remained disabled outside any scan. Root cause was UI state ownership, not media decoding: `ocrSyncControls()` gated Play/Mute on `ocrFrameReady()`, which requires a decoded frame (`readyState >= HAVE_CURRENT_DATA`) at the exact moment state is recomputed. Seeking or checkpoint/status refresh can transiently lower `readyState`; the later `seeked` path refreshed labels but did not necessarily recover the disabled state.

HF6 separates **direct playback readiness** from **current decoded-frame readiness**:

- `ocrDirectPlaybackReady()` requires direct-preview mode, valid preview metadata, and a real media source;
- while no OCR scan owns preview time, direct `#ocrPlay` and `#ocrMute` are enabled from that contract even if frame readiness is transiently false;
- `#ocrScrub` remains tied to preview metadata;
- FFmpeg JPEG fallback still disables Play/Mute because it is frame-by-frame preview, not browser playback/audio;
- starting a scan still disables manual Play/Mute/Scrub so the backend owns preview time;
- `play`, `pause`, and `seeked` now resynchronize control state as well as labels.

Browser E2E explicitly forces `ocrFrameReady()` false while a direct media source is valid and verifies Play, Mute, and Scrub remain enabled, then verifies fallback mode still disables both Play and Mute. The change does not touch OCR recognition, Chinese-only filtering, parallel lanes, Auto resource preflight, NVDEC, tracker state, checkpoint schemas, or SRT export.


## RC13-HF5 Chinese-only output and manual-QC list

Windows field review of the HF4 SRT found remaining foreign OCR artifacts beyond the old 1-3-letter guard, including `ILLC`, `AIYI`, `铺 U 碎` and `2/`. HF5 replaces that narrow heuristic with `NormalizeChineseSubtitleText`: output cues must contain Han text and cannot contain letters from another script. The validator is applied in tracker observation, checkpoint restore/commit, parallel reconciliation, and `/api/ocr/export`. Foreign samples are inconclusive rather than empty, preserving active Chinese cue timing. Repeated spaced punctuation such as `， ，` is normalized.

HF5 also fixes the completed-scan QC UI. Live job polling still returns a bounded recent window (max 120) to avoid growing JSON overhead, but once the job completes the frontend renders the entire final cue array and shows `displayed / total`. Source-video scrub/seek runs `ocrSyncCueListToTime`, highlights the nearest cue, and scrolls that row into view; clicking a cue seeks the preview to the cue start.

Added regression evidence:

- repeated `ILLC`, `AIYI`, `2/`, `铺 U 碎`, `AI时代`, Latin/Kana/Hangul-only samples cannot become cues;
- those foreign samples do not shorten/replace an active Chinese cue;
- Chinese text with digits/punctuation such as `第3集` remains valid;
- legacy checkpoint/reconciler state containing foreign text is dropped;
- `/api/ocr/export` revalidates all cues and normalizes `， ，`;
- browser E2E injects 130 completed cues and verifies all 130 rows render (not 120), then seeks the source timeline and verifies one nearest subtitle row becomes active at the expected timestamp.
- browser mock completion now clears the checkpoint exactly like production, preventing stale Resume state from masking fallback-preview lifecycle regressions.

## RC13-HF4 parallel telemetry and paused-checkpoint UI correction

HF3 Windows field review exposed UI/telemetry inconsistencies specific to parallel scanning rather than subtitle content: after Pause the UI could retain the last live `active_lanes` and realtime speed, schema-4 checkpoint percentage was recomputed from the contiguous preview frontier instead of aggregate work across every lane, and the recent cue list could show far-future timestamps from another lane while the preview intentionally followed the contiguous frontier. Parallel live telemetry also omitted confidence and several cumulative metrics that the single-lane path already exposed.

HF4 keeps the existing scan/checkpoint/SRT architecture and changes only observability/state presentation:
- `CheckpointInfo` now exposes aggregate `progress_percent`, zero `active_lanes` for an inspectable paused checkpoint, and boundary-merge count;
- schema-4 aggregate progress sums unique processed core seconds across lanes, while `media_seconds` remains the contiguous safe frontier;
- live and checkpoint recent cues are filtered to starts at/before that frontier so cue text/confidence matches the preview;
- parallel live metrics now aggregate visual confirmations and cumulative timing values, and expose aligned `last_text`/`last_confidence`;
- live parallel polling no longer serializes the full global cue array every cycle; final completion still returns the complete cue list;
- UI labels explicitly identify cumulative FFmpeg/visual timings, and paused realtime speed is cleared instead of showing stale live data.

Regression covers aggregate-vs-frontier progress, paused active-lane state, frontier-aligned recent cues, and browser E2E assertions for checkpoint text, active lanes, confidence and stale-speed clearing. No OCR/tracker threshold or final cue ownership/reconciliation rule is changed.

## RC13-HF3 subtitle correctness correction

HF2 Windows field testing exposed a subtitle correctness regression: standalone Latin OCR noise such as `A` and `N` could again appear in recorded cues. The root cause was not the HF2 Auto resource gate. The inherited RC12 tracker only increased the confirmation requirement for short ASCII fragments; sufficiently repeated false detections could still reach `promoteCandidate`. Parallel lanes create more independent warm-up regions, increasing the opportunity for that inherited weakness to surface.

HF3 adds a Chinese-OCR-specific standalone short-Latin noise guard at three output boundaries:
- `subtitleTracker.Observe`: 1-3 standalone Latin letters are ignored as inconclusive OCR samples so they neither form cues nor close/replace an active Chinese cue;
- tracker restore/commit: stale schema-3/schema-4 cue state from older builds cannot preserve the same garbage;
- `reconcileSegmentCues`: legacy lane cues are filtered before live/final global output.

The guard intentionally preserves numeric-only subtitles, mixed CJK+Latin text such as `AI时代`, and Latin text longer than three letters. OCR thresholds, PaddleOCR models/detector, visual sampling, parallel topology, Auto resource preflight, NVDEC, checkpoint schema and Pause/Resume semantics are unchanged.

Targeted regression covers repeated `A/N/W/OV` at high confidence, noise while a real Chinese cue is active, preservation of numbers/mixed/long Latin text, legacy checkpoint restore, and reconciler filtering.

## RC13-HF2 Auto resource-preflight correction

HF1 fixed the observed 16-lane hang, but field review exposed a separate policy gap: Auto still expanded the worker pool for the next level before deciding whether the machine had enough resource headroom to justify that probe. HF2 moves the safety decision before worker/lane expansion.

New control path:

```text
benchmark current level
-> sample Windows CPU/RAM + NVIDIA GPU/VRAM during the real-video benchmark
-> predict next-level RAM/VRAM headroom from measured baseline/current cost
-> reject next level before spawning it when resource guard fails
-> otherwise configure next worker level
-> run bounded benchmark with HF1 watchdog
-> commit level only when throughput gain remains worthwhile
```

HF2 resource rules:
- preserve at least max(2 GiB, 15% of physical RAM);
- preserve at least max(768 MiB, 18% of dedicated NVIDIA VRAM);
- stop expansion when average benchmark CPU reaches 90% or average NVIDIA GPU utilization reaches 96%;
- measure worst observed RAM/VRAM headroom during calibration while averaging CPU/GPU samples, avoiding false stops from one short utilization spike;
- when telemetry is unavailable, keep the bounded HF1 timeout/cancel/reset fallback rather than making an unsafe hard assumption.

Windows RAM/CPU telemetry uses native kernel32 APIs. NVIDIA memory/utilization uses `nvidia-smi`, already required by the existing NVIDIA detection path; HF2 adds no Python/pip/CUDA SDK dependency.

Regression tests cover CPU pressure, GPU pressure, predicted RAM exhaustion, predicted VRAM exhaustion, healthy headroom, unknown-telemetry fallback, and worst-memory aggregation. Windows cross-compilation covers the native resource probe implementation.

No subtitle correctness algorithm, tracker, threshold, segment ownership, checkpoint schema, PaddleOCR model/detector, NVDEC path, or manual parallelism contract is changed.

## RC13-HF1 Windows field regression

The first RC13 Windows candidate reached Auto calibration level 16, then CPU/GPU/RAM activity dropped close to idle while the job remained stuck at `Đang đo 16 luồng quét OCR trên video thật...`.

Root cause was in the Auto calibration control path, not subtitle tracking: `benchmarkParallelLevel` waited for exactly N lane outcomes using an unconditional channel receive. If one calibration lane failed to return, Auto had no per-level deadline. The subsequent worker-pool restore path also used the parent scan context and could wait indefinitely for a worker still marked busy.

HF1 adds:
- a 15-second hard deadline for each end-to-end Auto benchmark level;
- bounded worker-pool expansion and restore;
- context-aware outcome collection so one missing lane cannot block forever;
- cancellation of all lanes in a failed/timed-out calibration level;
- fallback to the last completed lower Auto level;
- managed hard reset/rebuild of the OCR worker pool if canceled workers refuse to drain during restore;
- regression tests for a missing benchmark lane and a worker pool that does not drain.

No tracker, OCR threshold, segment-boundary ownership, checkpoint-schema, detector, PaddleOCR model, or manual lane behavior is changed by HF1.

The recognition stack is intentionally unchanged: PaddleOCR 3.7.0, PaddlePaddle 3.2.x private runtimes, PP-OCRv6 Small detection + recognition, sparse visual confirmation and the RC11 NVDEC/software decoder path.

## RC12 field evidence that triggered RC13

On the real Windows test machine (Ryzen 7 4800H, RTX 3050 Laptop 4 GB, 32 GB RAM), RC12 snapshots on the same scan path showed approximately:

- Batch 1: 5.5x realtime, average request batch 1.00.
- Batch 4: 5.6x realtime, average request batch 2.86.

This was not enough end-to-end gain. RC13 therefore changes concurrency at the video-scan level rather than continuing to optimize request coalescing first.

## RC13 execution path

```text
OCR UI #ocrParallelism Auto/1/2/4/8/16
-> /api/ocr/scan ScanRequest.Parallelism
-> Scanner.Run
-> schema-3 legacy-resume guard OR schema-4 parallel path
-> Auto selector (optional end-to-end 1->2->4->8->16 calibration)
-> deterministic Segment Planner
-> ParallelScanCoordinator
   -> N bounded FFmpeg/NVDEC scan lanes (-ss + -t)
   -> lane-local sampler + sparse visual gate + subtitleTracker
   -> strict Chinese-only cue normalization
   -> shared dynamic OCR worker pool
      -> private PaddleOCR workers
   -> lane safe states
-> core cue ownership
-> deterministic boundary reconciler
-> global ordered cues
-> completed full cue list + timeline-synced QC UI
-> /api/ocr/export Chinese-only final defense
-> schema-4 checkpoint / final SRT
```

## Correctness constraints implemented

- Core ranges have exact coverage with no gaps and no core overlap.
- Pre/post overlap is scan context only; cue-start core ownership prevents normal overlap duplication.
- Each lane has an independent tracker; no tracker is shared by concurrent lanes.
- OCR pool saturation blocks producers instead of dropping candidates.
- Parallel completion order does not determine final cue ordering.
- Manual short-video parallelism is rejected instead of silently pretending to run the requested number of lanes.
- Auto is duration-capped, performs a resource preflight before every higher level, and stops on RAM/VRAM guard, CPU/GPU saturation, <10% throughput gain, or worker-capacity failure.
- New RC13 lanes force request batch 1. RC12 micro-batch remains a legacy schema-3 path only.
- Every final cue is Chinese-output validated before tracker commit/reconcile/export; non-Han-only or foreign-letter text cannot enter the SRT.

## Checkpoint schema 4

Schema 4 stores:

- requested + selected parallel topology;
- deterministic segment core/scan ranges;
- per-lane stable media position;
- per-lane committed cues and active tracker cue;
- per-lane frame count and cumulative OCR/visual/retry/timing telemetry;
- lane completion state.

Pause is an all-lane barrier. A lane returns `ErrScanPaused` only after its tracker can checkpoint and its safe state has been captured. The coordinator waits for every active lane, then writes the global checkpoint via temp file -> `Sync` -> close -> atomic rename. Only the API layer then marks the job paused.

A valid RC12 schema-3 checkpoint is still detected and resumed through the legacy one-lane path when no schema-4 checkpoint owns the scan identity.

## Added regression evidence

Targeted tests now cover:

- parallelism normalization and 1/2/4/8/16 segment topology;
- short-video duration cap and 2-hour 16-lane eligibility;
- core ownership for a cue spanning a boundary and a cue starting exactly on a boundary;
- bounded FFmpeg `-ss` + `-t` range while preserving NVDEC arguments;
- deterministic boundary reconciliation independent of lane completion order;
- shared worker-pool acquisition of distinct workers and cancellation while the pool is full;
- schema-4 round trip, inspection/removal and schema-3 key stability;
- schema-3 legacy handoff when RC13 UI sends parallelism;
- integration: two scan lanes reach OCR concurrently, Pause waits for the all-lane barrier, schema 4 is durable, and a new scanner instance resumes to completion.

## Automated gates

The following have been exercised in the Linux build environment during RC13 development and are release blockers before packaging:

```text
go test -count=1 ./...
go vet ./...
go test -race -count=1 ./...
python scripts/audit_ui_contract.py
python scripts/generate_code_map.py --check
go run scripts/generate_ocr_call_map.go --check
python -u scripts/browser_e2e.py
GOOS=windows GOARCH=amd64 CGO_ENABLED=0 go test -exec=/bin/true -count=1 ./...
GOOS=windows GOARCH=amd64 CGO_ENABLED=0 go build -trimpath -ldflags="-s -w -H=windowsgui" ... ./cmd/bilisub
python scripts/validate_release.py <candidate.exe>
```

For HF2, the complete automated matrix passed after the resource-preflight change: Go unit/integration tests, `go vet`, full race tests, UI contract, generated code/call-map checks, browser E2E, Windows cross-test compilation, Windows x64 GUI build, and static release validation. The race gate also exposed a legacy RC12 partial-batch test whose 750 ms wall-clock assertion included process startup under `-race`; the test was made scheduler-independent by keeping the fake next frame five seconds away while allowing three seconds for pre-candidate startup. Ten consecutive targeted race runs passed before the full race matrix was repeated successfully. Browser E2E also had a wall-clock race in its mock OCR backend: the first mock scan could auto-complete before Playwright clicked Pause. The mock now deterministically keeps the first two OCR jobs running until Pause and reserves completion for the Resume job, so the test proves pause/restart/resume behavior rather than browser scheduling speed. Exact final binary size/SHA and source commit are recorded in the external HF2 build-info/package files.

## Windows-required field gate

Linux cross-build cannot certify native Windows/NVIDIA behavior. The exact RC13 EXE must still be tested on the real Windows machine before Google Drive promotion:

1. Same video + ROI + Cân bằng 2.5 fps + GPU: compare 1 lane, 2 lanes, 4 lanes and Auto; test 8 only if Auto/resource behavior makes it sensible.
2. Record realtime, OCR images/inferences, selected/active/completed lanes, CPU, GPU, dedicated VRAM, NVDEC, OCR/cue, visual confirmations and cue count.
3. Verify multiple `python.exe` OCR workers and multiple bounded `ffmpeg.exe` lanes actually exist when selected parallelism > 1, without orphan processes after finish/cancel/exit.
4. Pause -> Resume.
5. Pause -> close app -> reopen -> Resume.
6. Quét lại từ đầu.
7. Compare subtitle text/timestamps at segment boundaries; no missing cue, duplicate cue, A/W/OV garbage or timestamp disorder.
8. Soak long enough to expose thermal throttling, RAM/VRAM growth, CUDA OOM or worker instability.

**Google Drive promotion remains BLOCKED until this Windows field gate passes.**

## Native desktop migration — Phase 13 native NVIDIA probing

- Result: PASS (automated/cross-build).
- Production no longer executes the NVIDIA CLI for GPU discovery or Auto resource telemetry.
- GPU usability and Paddle wheel selection use the Windows CUDA Driver API (`nvcuda.dll`).
- NVIDIA VRAM/GPU utilization uses the NVML driver DLL directly (`nvml.dll`).
- NVML absence degrades only optional GPU/VRAM telemetry; the existing CPU/RAM resource gate and hard benchmark watchdog remain active.
- `go test`, `go vet`, targeted/full race coverage, standalone GPU audit, UI contract, CODE_MAP, OCR_CALL_MAP, browser E2E, Windows amd64 cross-test/build, and release static validation passed.

## Native Windows migration final automated gate — checkpoint inspection + full OCR telemetry

Status: **MATRIX 1 PASS on the synchronized working tree; exact clean-commit rerun required before packaging.**

Final native OCR UI changes verified in this gate:

- schema-3 and schema-4 checkpoint inspection now exposes the schema explicitly to native UI;
- paused parallel OCR refreshes `RecentCues` from the fsynced checkpoint, keeping the list aligned to the contiguous safe frontier instead of applying far-lane aggregate cues;
- cue list summary is a separate native label and no longer overwrites the telemetry panel;
- live, checkpoint and final OCR states use one telemetry formatter covering cue/list totals, frames, OCR images, inference count, images-per-cue, average batch, lane topology, boundary merges, visual skips/confirmations/retries, decoder/fallback, pipeline/visual/encode/OCR/elapsed time, realtime speed, Auto benchmark fields when present, and latest text/confidence;
- parallel live telemetry now publishes frames, elapsed seconds, average batch and total lane count; single-lane live telemetry publishes frames, elapsed/progress and explicit one-lane topology;
- native feature-parity, application-boundary and dependency/process audits are release blockers.

Matrix 1 evidence:

- `go test -count=1 ./...`: PASS
- `go vet ./...`: PASS
- `go test -race -count=1 ./...`: PASS
- `python scripts/audit_ui_contract.py`: PASS (159/159 unique DOM ids, 31 UI API routes, 32 server routes, 63 buttons)
- `python scripts/audit_native_ui.py`: PASS (96 named native controls with explicit layout)
- `python scripts/audit_feature_parity.py`: PASS
- `python scripts/audit_standalone_gpu.py`: PASS
- `python scripts/audit_application_boundary.py`: PASS
- `python scripts/audit_dependency_process.py`: PASS
- `python scripts/generate_code_map.py --check`: PASS
- `go run scripts/generate_ocr_call_map.go --check`: PASS
- `python -u scripts/browser_e2e.py`: PASS (102 API calls, 31 unique routes, all static product buttons exercised)
- `GOOS=windows GOARCH=amd64 CGO_ENABLED=0 go test -exec=/bin/true -count=1 ./...`: PASS
- Windows amd64 GUI cross-build: PASS, PE32+ x86-64 GUI
- `python scripts/validate_release.py <matrix1.exe>`: PASS (native-only production binary)

The matrix-1 EXE is a temporary validation artifact only and is not a release/package candidate. Packaging remains blocked until the same full matrix passes again on the exact clean Git commit.
