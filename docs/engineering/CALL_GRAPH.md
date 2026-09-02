# BiliSub Studio verified call map

Verified against the beta.12 RC source. This file explains execution paths. The machine-generated inventory in `CODE_MAP.generated.md` is regenerated from source and checked by the release gate; current source and tests remain authoritative.

## Ownership map

```text
cmd/bilisub        process startup / shutdown / self-update handoff
internal/api       HTTP contract + Windows shell/dialog integration + feature wiring
internal/jobs      job state + cancellation
internal/tools     external tool discovery/install
internal/video     Bilibili resolve/download/retry/resume/remux
internal/videoedit local video region effects + FFmpeg export
internal/ocr       managed PaddleOCR/PP-OCRv6 Small CPU/GPU install/lifecycle/scheduling + deterministic FFmpeg OCR scan
internal/subtitle  subtitle fetch/parse/export
internal/appstate  config + cookie persistence
internal/proc      hidden child-process policy for non-interactive helpers
web                embedded product UI only
```

## Startup / lifecycle

```text
cmd/bilisub.main
  -> appstate.New
  -> api.New
       -> jobs.NewManager
       -> tools.New
       -> ocr.New
       -> video.YTDLPResolver
       -> video.Service
       -> subtitle.Service
       -> videoedit.Service
  -> Server.Handler
  -> HTTP Serve
  -> Server.Launch
  -> wait for explicit Exit / self-update / OS signal

Desktop lifecycle
  -> no idle watchdog
  -> no browser heartbeat dependency
  -> browser timer throttling or Windows sleep cannot terminate the backend
  -> POST /api/exit -> Server.exitHandler -> Server.requestExit
  -> updater apply -> downloaded EXE handoff -> Server.requestExit

UI bootstrap status
  -> script initialization completes
  -> refreshAppStatus()
  -> GET /api/status
  -> side #version <- response.version
  -> side #driveSide <- response.drive
  -> settings #updateCurrent <- response.version
  -> OCR engine fact: ocrEngineReady <- response.ocr_ready
  -> ocrSyncControls() recomputes OCR control states from shared preview facts
  -> opening Settings may refresh status again, but is not required for initial sidebar state

OCR UI state authority
  -> ocrSetPath / ocrEnableDirect / ocrEnableFallback / ocrEnsure / ocrSetRunning
       update facts only
  -> ocrSyncControls()
       sole writer of critical OCR .disabled states
  -> refreshAppStatus MUST NOT inspect ocrVideo.duration/videoWidth for readiness
       because FFmpeg fallback has a valid preview while the hidden <video> has no decoded track
```

## Shared local filesystem integration

These routes are feature-neutral platform integration owned by `internal/api`.
Do not classify them as OCR, Video Editor, or downloader backend code.

```text
OCR UI #ocrPick --------------------+
                                     +-> POST /api/pick-video
Video Editor UI #editorPick --------+      -> api.Server.pickVideoHandler
                                            -> pickVideoNative
                                               Windows: GetOpenFileNameW (native Win32 common dialog)

OCR preview ------------------------+
Video Editor preview ----------------+-> POST /api/preview-info
                                     |      -> api.Server.previewInfoHandler
                                     |      -> tools.Manager.EnsureFFprobe
                                     |      -> mediapreview.ProbePreview
                                     |
                                     +-> if browser-direct compatible:
                                     |      GET /api/media?path=...
                                     |      -> api.Server.mediaHandler
                                     |      -> http.ServeFile with Range support
                                     |
                                     +-> otherwise (or browser decode failure):
                                            GET /api/preview-frame?path=...&time=...
                                            -> api.Server.previewFrameHandler
                                            -> api.Server.ensureFFmpeg
                                            -> mediapreview.PreviewFrameJPEG
                                            -> FFmpeg decodes one JPEG frame for the requested media time


output-folder controls ----------------> POST /api/pick-folder
Settings #defaultOutPick -------------/     -> api.Server.pickFolderHandler
                                          -> appstate.Config.OutputDir (persist selected default)
                                          -> pickFolderNative
                                             Windows: lock OS thread
                                             -> CoInitializeEx(COINIT_APARTMENTTHREADED)
                                             -> SHBrowseForFolderW
                                             -> SHGetPathFromIDListW
                                             -> CoTaskMemFree / CoUninitialize
```

Windows picker policy:
- interactive file/folder dialogs run inside `BiliSubStudio.exe`; they do not spawn PowerShell or WinForms helper processes
- `pickVideoNative` uses the Win32 file dialog and returns cancel separately from dialog failure
- `pickFolderNative` initializes COM on the locked OS thread before the Shell folder browser
- `hidden()` (`HideWindow + CREATE_NO_WINDOW`) remains reserved for non-interactive child tools only

## Bilibili video download

```text
POST /api/video/download
  -> api.Server.videoDownloadHandler
  -> api.Server.ensureTools(needFFmpeg=true)
       -> tools.Manager.EnsureYTDLP
       -> appstate.State.WriteNetscapeCookieFile
       -> tools.Manager.EnsureFFmpeg
  -> video.Service.Run
       -> YTDLPResolver.Resolve
       -> SpeedConnections
       -> DownloadStream
            -> probe
            -> segments
            -> openResumeWork
            -> downloadSegment
            -> resumeState.Commit
            -> signed-URL refresh closure -> YTDLPResolver.Resolve
       -> fallbackStream on bounded Range failure
       -> remux / singleTrack / audioOnly
```

Ownership boundary: `internal/video` does not own local file pickers, OCR scanning, or editor filters.

## Video Editor

```text
POST /api/editor/export
  -> api.Server.editorExportHandler
       -> api.Server.ensureEditorFFmpeg
            -> api.Server.ensureFFmpeg
            -> tools.Manager.EnsureFFmpeg
       -> videoedit.Service.Run
            -> videoedit.BuildFilter
            -> proc.Hide(exec.CommandContext(ffmpeg))
            -> temporary render
            -> validate non-empty output
            -> atomic rename
```

Editor does not require yt-dlp or a Bilibili cookie.

## OCR engine

```text
POST /api/ocr/engine/ensure
  -> api.Server.ocrEnsureHandler
  -> ocr.Manager.Ensure
       -> Manager.ensureInstalled
            -> validateInstall
            -> Manager.installManagedRuntime (only when missing/corrupt)
                 -> Manager.ensureUV -> pinned uv.exe + SHA-256 verification
                 -> uv python install -> Tools/OCR/python
                 -> CPU venv -> Tools/OCR/runtime/cpu/venv -> paddlepaddle + paddleocr
                 -> GPU venv -> Tools/OCR/runtime/gpu/venv -> paddlepaddle-gpu + paddleocr (when requested/available)
                 -> write shared embedded worker.py + per-runtime install.json
       -> Manager.startMode
            -> CPU: private python -u worker.py --model-cache Tools/OCR/models --device cpu
            -> GPU: private python -u worker.py --model-cache Tools/OCR/models --device gpu:0
            -> Hybrid: start GPU then CPU; both workers stay private/no HTTP
            -> PP-OCRv6_medium_det + PP-OCRv6_medium_rec + requested-device ready handshake

POST /api/ocr
  -> api.Server.ocrHandler
     -> api.Server.ensureFFmpeg
     -> ocr.CaptureFramePNGBase64(path, time, ROI)
        -> proc.Hide(exec.CommandContext(ffmpeg ... exact frame crop ...))
     -> ocr.Manager.Run
        -> Manager.Ensure
        -> active PaddleOCR worker JSON-line request with request ID
        -> Hybrid: concurrent CPU/GPU requests for paired frames; result remains attached to its frame
```

## Deterministic full-video OCR scan

```text
POST /api/ocr/scan
  -> api.Server.ocrScanHandler
     -> api.Server.ensureFFmpeg
        -> tools.Manager.EnsureFFmpeg
     -> ocr.Manager.Ensure
     -> api.Server.newOCRScanner
        -> CheckpointDir = Data/OCRCheckpoints
     -> ocr.Scanner.Run
        -> newScanCheckpointSession -> source/ROI/mode identity -> optional resume media offset
        -> proc.Hide(exec.CommandContext(ffmpeg ... -ss resumeAt ... ROI rawvideo pipe))
        -> makeEdgeSignature / edgeSignatureActivity / edgeSignatureDiff
        -> shouldRunOCR (skip inactive blank ROI; change/guard/confirmation gate)
        -> ocr.Manager.Run -> PaddleOCR detector + recognizer
             -> CPU/GPU: serialized per worker
             -> Hybrid: paired consecutive sampled frames run concurrently
        -> commit OCR candidates in media timestamp order
        -> subtitleTracker.Observe
             -> two-step temporal confirmation for normal/CJK text
             -> stronger confirmation for short ASCII noise
             -> one-miss hysteresis for active subtitles
        -> confirmed cue result
        -> scanCheckpointSession.MaybeSave (stable tracker state every 5 media minutes)
        -> success: remove checkpoint; cancel/error: latest durable checkpoint remains

GET /api/job
  -> jobs.Job.Snapshot
     -> live result includes media_seconds, cue_count, last_text/confidence and recent_cues while scanning
     -> OCR UI ocrPoll()
        -> ocrFollowScanPreview(media_seconds) -> shared preview time/frame
        -> renders #cueList immediately instead of waiting for the final scan result

Preview ownership rule: backend FFmpeg scan remains the source of truth. Browser playback never drives OCR timing. During a scan, OCR preview follows backend `media_seconds`; direct-decode video seeks to that timestamp, while unsupported codecs fetch the matching frame from the shared `/api/preview-frame` pipeline. Manual Play/seek are temporarily disabled while scan owns preview time.
```

Full-video OCR accuracy is owned by `internal/ocr/scanner.go`, not browser playback.

## Subtitle

```text
POST /api/subtitle/download
  -> api.Server.subtitleDownloadHandler
  -> subtitle.Service.Run
       -> yt-dlp resolver/subtitle fetch
       -> parse/convert/export
```

## Update channel

```text
GET /api/update/check
  -> api.Server.updateCheckHandler
  -> fetchManifest(currentVersion)
       prerelease -> fixed beta manifest Drive file ID
       stable     -> stable manifest Drive file ID

POST /api/update/apply
  -> api.Server.updateApplyHandler
  -> download candidate
  -> verify manifest size + SHA-256
  -> launch new EXE --apply-self-update
  -> old process exits
  -> new EXE swaps/relaunches
```

## Route inventory

`Server.Handler` currently registers 30 authenticated `/api/*` routes. Any route handler can have no ordinary Go caller because HTTP registration is an external entrypoint; zero AST callers does not mean dead code.
