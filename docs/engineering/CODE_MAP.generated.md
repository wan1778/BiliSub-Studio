# BiliSub Studio generated code map

> GENERATED FROM CURRENT SOURCE by `scripts/generate_code_map.py`. Do not hand-edit.
> The release gate runs this generator with `--check`; a stale map blocks release.

## Inventory

- Production Go functions: **716**
- Registered authenticated API routes: **32**
- Frontend-referenced API routes: **31**
- Product buttons: **63**
- DOM ids: **159**

## Package dependency graph

```text
cmd/bilisub
  -> internal/application
  -> internal/appstate
  -> internal/nativeui
  -> internal/proc
internal/api
  -> internal/appstate
  -> internal/jobs
  -> internal/mediapreview
  -> internal/ocr
  -> internal/proc
  -> internal/subtitle
  -> internal/tools
  -> internal/video
  -> internal/videoedit
internal/application
  -> internal/appstate
  -> internal/jobs
  -> internal/mediapreview
  -> internal/ocr
  -> internal/subtitle
  -> internal/tools
  -> internal/video
  -> internal/videoedit
internal/appstate
  -> (stdlib only)
internal/jobs
  -> (stdlib only)
internal/mediapreview
  -> internal/proc
internal/nativeplayer
  -> internal/proc
internal/nativeui
  -> internal/application
  -> internal/nativeplayer
  -> internal/ocr
  -> internal/qrcode
  -> internal/subtitle
  -> internal/video
  -> internal/videoedit
internal/ocr
  -> internal/jobs
  -> internal/proc
internal/proc
  -> (stdlib only)
internal/qrcode
  -> (stdlib only)
internal/subtitle
  -> internal/jobs
  -> internal/video
internal/tools
  -> (stdlib only)
internal/video
  -> internal/jobs
  -> internal/proc
internal/videoedit
  -> internal/jobs
  -> internal/proc
```

## HTTP route ownership

| Route | Handler | Used by frontend |
|---|---|---|
| `/api/status` | `api.Server.statusHandler` | yes |
| `/api/ping` | `api.Server.pingHandler` | no |
| `/api/cookie` | `api.Server.cookieHandler` | yes |
| `/api/login/qr/start` | `api.Server.qrStartHandler` | yes |
| `/api/login/qr/poll` | `api.Server.qrPollHandler` | yes |
| `/api/metadata` | `api.Server.metadataHandler` | yes |
| `/api/video/download` | `api.Server.videoDownloadHandler` | yes |
| `/api/editor/export` | `api.Server.editorExportHandler` | yes |
| `/api/pick-video` | `api.Server.pickVideoHandler` | yes |
| `/api/media` | `api.Server.mediaHandler` | yes |
| `/api/preview-info` | `api.Server.previewInfoHandler` | yes |
| `/api/preview-frame` | `api.Server.previewFrameHandler` | yes |
| `/api/subtitle/download` | `api.Server.subtitleDownloadHandler` | yes |
| `/api/job` | `api.Server.jobHandler` | yes |
| `/api/job/cancel` | `api.Server.cancelHandler` | yes |
| `/api/job/pause` | `api.Server.pauseHandler` | yes |
| `/api/ocr/engine/ensure` | `api.Server.ocrEnsureHandler` | yes |
| `/api/ocr/engine/status` | `api.Server.ocrStatusHandler` | yes |
| `/api/ocr/engine/remove` | `api.Server.ocrRemoveHandler` | yes |
| `/api/ocr` | `api.Server.ocrHandler` | yes |
| `/api/ocr/scan` | `api.Server.ocrScanHandler` | yes |
| `/api/ocr/checkpoint` | `api.Server.ocrCheckpointHandler` | yes |
| `/api/ocr/export` | `api.Server.ocrExportHandler` | yes |
| `/api/storage/cleanup` | `api.Server.storageCleanupHandler` | yes |
| `/api/tools/reset` | `api.Server.toolsResetHandler` | yes |
| `/api/pick-folder` | `api.Server.pickFolderHandler` | yes |
| `/api/open-folder` | `api.Server.openFolderHandler` | yes |
| `/api/update/check` | `api.Server.updateCheckHandler` | yes |
| `/api/update/setting` | `api.Server.updateSettingHandler` | yes |
| `/api/theme` | `api.Server.themeHandler` | yes |
| `/api/update/apply` | `api.Server.updateApplyHandler` | yes |
| `/api/exit` | `api.Server.exitHandler` | yes |

## OCR control-state ownership

Critical OCR controls must have exactly one state writer: `ocrSyncControls`.
This prevents a status refresh, preview-mode switch, engine transition, or scan transition from re-enabling/disabling controls with incompatible rules.

| Control | `.disabled` writers |
|---|---|
| `#startOCR` | `ocrSyncControls` |
| `#testOCR` | `ocrSyncControls` |
| `#ocrPlay` | `ocrSyncControls` |
| `#ocrScrub` | `ocrSyncControls` |
| `#ocrMute` | `ocrSyncControls` |
| `#ocrFullscreen` | `ocrSyncControls` |
| `#ocrSubtitlePreset` | `ocrSyncControls` |
| `#ocrDevice` | `ocrSyncControls` |
| `#ocrParallelism` | `ocrSyncControls` |
| `#stopOCR` | `ocrSyncControls` |
| `#restartOCR` | `ocrSyncControls` |
| `#clearOCR` | `ocrSyncControls` |
| `#exportOCR` | `ocrSyncControls` |

## Verified top-level execution map

```text
BiliSubStudio.exe
  -> cmd/bilisub.main
     -> proc.EnableContainment -> Windows Job Object (normal helpers die with app)
     -> appstate.New
     -> application.New
        -> jobs.Manager
        -> tools.Manager (app-owned ffmpeg/ffprobe/yt-dlp only)
        -> ocr.Manager
        -> video.Service / YTDLPResolver
        -> subtitle.Service
        -> videoedit.Service
     -> nativeui.Run -> native Windows x64 window/message loop
        -> application.App methods directly; no localhost/browser/WebView runtime
        -> nativeplayer.Player -> app-owned FFmpeg decode -> GDI frame render + Windows audio
        -> qrcode.Encode -> native QR matrix render
        -> WM_CLOSE -> application.PrepareShutdown -> PauseJob for every active pausable OCR job -> fsynced checkpoint -> cancel remaining work -> close
     -> update result -> proc.Breakaway updater -> atomic self-swap -> restart native EXE

Native OCR UI
  -> native Windows file picker -> application.PreviewInfo / EnsureFFmpeg -> nativeplayer.Player
  -> timeline WM_HSCROLL -> nativeplayer.Seek -> syncCueToTime -> nearest cue highlight/scroll
  -> cue LISTBOX selection -> seekSelectedCue -> nativeplayer.Seek
  -> native preview drag -> ROI controls -> ocr.ScanRegion
  -> Auto/CPU/GPU/Hybrid -> application.ConfigureOCRDevice -> ocr.Manager.ConfigureDevice
  -> Auto/1/2/4/8/16 -> ScanRequest.Parallelism -> ParallelScanCoordinator
  -> Test OCR -> application.OCRFrame -> FFmpeg crop -> PaddleOCR
  -> Start/Resume -> application.StartOCRScan -> ocr.Scanner.Run -> schema-3 legacy or schema-4 parallel scan
  -> Pause -> application.PauseJob -> jobs.Job.RequestPause -> tracker-safe boundary -> checkpoint fsync -> PauseComplete
  -> Restart -> application.RemoveOCRCheckpoint -> new scan from zero
  -> Export -> application.ExportOCR -> NormalizeChineseSubtitleText -> Chinese-only sequential SRT
  -> Fullscreen -> borderless native monitor window; Escape restores previous window style/rect

Legacy browser regression adapter (source/tests only; not imported by cmd/bilisub)
  -> internal/api + embedded HTML remain as a parity oracle during migration
  -> browser_e2e.py exercises legacy contracts but does not define production runtime

Native Video Editor UI
  -> native picker -> application.PreviewInfo / EnsureFFmpeg -> nativeplayer.Player
  -> preview drag -> editor X/Y/W/H controls
  -> timeline -> nativeplayer.Seek
  -> Export -> application.StartEditor -> videoedit.Service.Run -> app-owned FFmpeg output
```
