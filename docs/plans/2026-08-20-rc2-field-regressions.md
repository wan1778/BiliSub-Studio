# Beta 12 RC2 field-regression plan — HISTORICAL / SUPERSEDED

Pinned baseline commit: `c0c6859217d29313b9aef68a73a786fdb43fe158`.

Status: RC2 rejected by Windows field test. This document is historical evidence only. Current preview ownership is defined by `docs/engineering/CALL_GRAPH.md` and the generated code map; do not use the old Editor preview route names below as current architecture.

## Reported regressions

1. Settings did not expose a discoverable default download/export folder control.
2. OCR showed a live recognized string and a non-zero subtitle count, but the subtitle list remained empty and labels mixed technical English/Vietnamese in a confusing way.
3. Video Editor selected the source successfully, reported a valid duration, but the preview stayed black (`0×0` video decode in the browser).

## Execution paths / ownership

### Default output folder
`Settings UI -> #defaultOutPick -> POST /api/pick-folder -> api.Server.pickFolderHandler -> pickFolderNative -> appstate.Config.OutputDir`

### OCR live cue list
`OCR UI -> POST /api/ocr/scan -> api.Server.ocrScanHandler -> internal/ocr.Scanner.Run -> jobs.Job result -> GET /api/job -> ocrPoll -> #cueList`

### Editor preview
Historical RC2 path: `Editor UI -> POST /api/pick-video -> editorSetPath -> direct /api/media preview when browser-decodable; otherwise the then-editor-specific preview probe/frame fallback`. Current RC architecture uses shared `/api/preview-info` and `/api/preview-frame` owned by `internal/mediapreview`.

## Acceptance criteria

- Settings visibly exposes the current default output folder, can pick/open it, persists it through `appstate.Config.OutputDir`, and propagates the newly selected default to all feature output fields.
- OCR list renders live cues while scan is running; final cues keep rendering after completion; export is enabled when final cues exist; labels are plain Vietnamese where a technical term is not needed.
- Editor supports browser-decodable video directly and falls back to FFmpeg frame preview for unsupported codec/container instead of presenting a black stage. Fallback seek must request a real frame at the selected timestamp and region overlays/effects must still work.
- Full browser E2E, Go test/vet/race, UI contract audit, Windows cross-build and PE validation pass after the changes.
- Windows native file/folder picker remains `WINDOWS-REQUIRED`; do not modify its implementation in this task.

## Do not change

- Bilibili downloader concurrency, chunking, CDN/retry/resume/fallback behavior.
- RapidOCR package/model selection or deterministic scan sampling thresholds unless a test proves they are directly involved.
- Updater download/verify/swap logic.
- Process lifecycle/exit policy.
- Windows native picker implementation already confirmed by RC2 field test.
