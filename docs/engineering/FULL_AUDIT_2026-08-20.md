# Full source audit — beta.12 RC5 rejection -> RC6 internal

## Trigger

Windows field test on RC5:

- shared preview worked;
- OCR engine reached `Bộ nhận diện đã sẵn sàng`;
- immediately afterward `Bắt đầu quét chính xác` was disabled and could not be clicked.

This audit was required before any further fix because repeated local patches had allowed frontend state rules and hand-written call maps to drift.

## Exact RC5 inventory scanned before editing

- Go/HTML/Python source: 9,027 lines
- Production Go functions: 235
- Authenticated API routes: 30
- Frontend-referenced API routes: 29
- Product buttons: 62
- DOM ids: 143
- Production packages: `cmd/bilisub` plus 10 `internal/*` packages

All production Go files, the complete embedded/source HTML UI, release scripts, architecture docs, plans, and tests were included in the scan.

## Generated package graph

The exact dependency/route inventory is now generated into `docs/engineering/CODE_MAP.generated.md` by `scripts/generate_code_map.py` and checked during release. This removes route/module counts from hand-maintained memory.

High-level ownership remains:

```text
cmd/bilisub
  -> internal/api
     -> appstate
     -> jobs
     -> tools
     -> mediapreview
     -> ocr
     -> video
     -> subtitle
     -> videoedit
  -> internal/appstate
  -> internal/proc
```

No OCR scan algorithm, RapidOCR manager, downloader, editor export backend, updater, lifecycle, or native picker production code is changed by the RC6 fix.

## Root cause found from source

The bug is frontend state ownership, not OCR backend readiness.

Execution path before the fix:

```text
OCR fallback video selected
  -> ocrPreviewInfo is valid
  -> ocrFallbackMode = true
  -> hidden <video> has no decoded duration/videoWidth

Prepare detector
  -> ocrEnsure()
  -> /api/ocr/engine/ensure
  -> /api/ocr/engine/status == ready
  -> ocrEnsure enables #startOCR correctly using ocrPreviewReady()
  -> ocrEnsure calls refreshAppStatus()
  -> /api/status says ocr_ready=true
  -> refreshAppStatus writes:
       #testOCR.disabled  = !ocrVideo.videoWidth
       #startOCR.disabled = !ocrVideo.duration
  -> hidden direct <video> is not the active preview in fallback mode
  -> #startOCR becomes disabled again
```

The critical discovery was structural: before the fix, `#startOCR` had 7 independent `.disabled` writers and `#testOCR` had 8. Several used different definitions of "ready". This allowed one valid transition to be overwritten by another subsystem refresh.

## State-ownership correction

Critical OCR button state now has one authority only:

```text
facts:
  ocrEngineReady
  ocrPreparing
  ocrTesting
  ocrRunning
  selected path
  shared preview ready
  direct/fallback frame ready
  final cue count
       |
       v
ocrSyncControls()
       |
       +-> Prepare detector
       +-> Test current frame
       +-> Start scan
       +-> Stop scan
       +-> Clear/export
       +-> Play/seek/mute/fullscreen
       +-> Subtitle preset
```

`refreshAppStatus`, preview mode switches, engine transitions, scan transitions, and frame-load events now update facts and then call `ocrSyncControls()`. They no longer maintain competing enable/disable formulas.

The release audit explicitly fails if any critical OCR control gains a second `.disabled` writer or if `refreshAppStatus()` again derives OCR readiness from `ocrVideo.duration` / `ocrVideo.videoWidth`.

## Regression that was missing from RC5

RC5 browser E2E tested:

- engine preparation in browser-direct mode;
- fallback preview scanning after the engine was already ready.

It did **not** test the exact Windows failure sequence:

```text
fallback preview
-> engine initially not ready
-> Prepare detector
-> engine becomes ready
-> refreshAppStatus
-> Start must remain enabled
```

That exact scenario is now in `scripts/browser_e2e.py` and is required to pass.

## Documentation drift found

The full scan also found historical plan text that could mislead a later edit:

- RC2 plan still named old editor-specific preview routes;
- shared-preview plan still claimed legacy editor preview aliases that the current server no longer registers.

Historical plans are now explicitly marked/sanitized and current ownership points to `CALL_GRAPH.md` + generated code map.

## Known multi-writer areas not changed in this fix

Video Editor still has several controls updated from render/export/poll transitions. Current field tests and Chromium E2E exercise direct preview, fallback preview, region editing, export, cancel, and completion. No failing Editor state invariant was found in this audit, so it is intentionally not refactored inside the OCR state fix. A future Editor state refactor must be its own plan and acceptance run.

## Release rule after this audit

A candidate is blocked unless all of these pass on the exact tree:

```text
go test -count=1 ./...
go vet ./...
go test -race -count=1 ./...
python scripts/audit_ui_contract.py
python scripts/generate_code_map.py --check
python -u scripts/browser_e2e.py
Windows amd64 compile/build
PE release validator
```

Windows-native/real-package behavior remains field-tested on the exact RC binary before Drive promotion.
