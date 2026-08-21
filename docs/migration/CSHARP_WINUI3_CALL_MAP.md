# C# + WinUI 3 production call map

Baseline: Go source identity `9be4abd8184d2d7d24159dd736b6accfbe1cda90`. The Go production tree is unchanged and remains the behavioral oracle until the exact C# publish passes Windows field gates.

## Composition and lifecycle

```text
App.OnLaunched
  -> UpdateService.TryApplyFromCommandLineAsync (updater mode only)
  -> MainWindow
      -> AppPaths.FromExecutableDirectory
      -> BiliSubApplication
          -> WindowsProcessContainment (KILL_ON_JOB_CLOSE + updater BREAKAWAY_OK)
          -> one JobManager / ToolManager / ProcessRunner / HttpClient
          -> Settings, Auth, Media, Video, Subtitle, Hardware, OCR, Editor, Update, Report owners
      -> native WinUI pages (no HTTP adapter/WebView)

AppWindow.Closing
  -> BiliSubApplication.PrepareShutdownAsync
      -> PauseJobAsync for every pausable OCR job
      -> OcrScanner reaches safe tracker boundary
      -> OcrCheckpointStore.SaveAsync + Flush(flushToDisk: true) + atomic move
      -> cancel remaining jobs
      -> OcrManager.StopAsync
      -> SessionStore.DeleteTemporaryAsync (remove plaintext Netscape cookie)
  -> UpdateService.LaunchPrepared (only when a verified update is pending)
  -> Window.Close
```

## Settings/config

```text
SettingsPage / SettingsViewModel
  -> SettingsApplicationService
      -> JsonConfigStore
          -> AppConfigNormalizer
          -> AtomicJsonFile
          -> Data/config.json

VideoPage / SubtitlePage / OCRPage
  -> BiliSubApplication job or ROI owner
  -> JsonConfigStore.UpdateAsync
  -> persist video speed/container/mode, subtitle format, OCR device/ROI
```

The twelve legacy JSON fields and one-sided ROI normalization rules remain unchanged. Maintenance buttons call `BiliSubApplication` owners only after a native confirmation dialog and are rejected while jobs are active.

## Bilibili auth

```text
AccountPage
  -> BilibiliAuthService.StartQrAsync
      -> Bilibili QR endpoint
      -> QrMatrixEncoder (fixed native QR v10-L matrix)
      -> AccountPage Canvas render
  -> BilibiliAuthService.PollQrAsync
      -> collect callback/Set-Cookie values
      -> nav validation
      -> SessionStore.SetCookieAsync
          -> Windows DPAPI CryptProtectData
          -> Data/session.bin

Resolver / Subtitle
  -> SessionStore.WriteNetscapeFileAsync
  -> Temp/bilibili_cookies.txt
  -> yt-dlp child
```

## Media preview

```text
OCRPage or EditorPage
  -> FilePickerService (native HWND initialized FileOpenPicker)
  -> MediaPreviewService.ProbeAsync
      -> ToolManager.EnsureFfprobeAsync
      -> ProcessRunner -> ffprobe.exe
  -> MediaPreviewService.GetFrameJpegAsync
      -> ToolManager.EnsureFfmpegAsync
      -> ProcessRunner -> ffmpeg.exe -> JPEG pipe
  -> BitmapImage in native WinUI Image
```

Preview does not depend on browser codecs. OCR ROI and editor regions are normalized from pointer coordinates against the displayed video rectangle.

## Video metadata and download

```text
VideoPage.LoadMetadata
  -> BiliSubApplication.GetMetadataAsync
  -> YtDlpResolver.GetMetadataAsync
  -> ToolManager.EnsureYtDlpAsync
  -> ProcessRunner -> yt-dlp.exe -J

VideoPage.Start
  -> BiliSubApplication.StartVideo
  -> JobManager.Create("video")
  -> VideoDownloadService.RunAsync
      -> YtDlpResolver.ResolveAsync
      -> RangeDownloader.DownloadAsync per selected DASH stream
          -> strict bytes=0-0 probe / Content-Range validation
          -> Stable 1, Fast 8, Turbo 16 global connection budget
          -> disjoint .seg.tmp -> fsynced completed .seg
          -> atomic resume manifest
          -> exact-size assembly
      -> bounded URL refresh via YtDlpResolver
      -> yt-dlp single-fragment fallback when Range is broken/unsupported
      -> ProcessRunner -> ffmpeg stream-copy remux
      -> verified non-empty final output
```

Cancel path:

```text
VideoPage.Cancel
  -> BiliSubApplication.CancelJob
  -> AppJob.Cancel / shared CancellationToken
  -> cancel every HTTP worker and kill yt-dlp/FFmpeg process tree
  -> remove .tmp/.part/.assembling files
  -> retain only completed .seg checkpoints and completed output files
  -> UI returns to ready state
```

## Subtitle

```text
SubtitlePage.Load
  -> BiliSubApplication.GetMetadataAsync -> YtDlpResolver
SubtitlePage.Start
  -> BiliSubApplication.StartSubtitle
  -> SubtitleService.RunAsync
      -> fetch selected official/AI track
      -> keep official and AI tracks as distinct selectable identities
      -> parse Bilibili body JSON, json3 events, WebVTT or SRT
      -> normalize/sort/merge duplicate cues
      -> render normalized SRT/TXT/pretty JSON (never rename raw timed text as JSON)
      -> atomic output write
```

URL edits invalidate the loaded track/quality owner in both Video and Subtitle pages.

## Hardware and benchmark

```text
HardwarePage
  -> HardwareService.Snapshot
      -> CPU registry guarded by OperatingSystem.IsWindows
      -> GC memory information
      -> nvcuda.dll Driver API (no nvidia-smi/PowerShell)
  -> HardwareService.BenchmarkAsync
      -> bounded SHA-256 CPU and memory-copy probes
      -> recommended OCR lane ceiling
```

## OCR

```text
OCRPage.Prepare
  -> BiliSubApplication.PrepareOcrAsync
  -> OcrManager.ConfigureDeviceAsync / EnsureAsync
  -> OcrInstaller.EnsureAsync
      -> pinned uv 0.12.0 + checksum
      -> private Python 3.12
      -> paddle 3.2.0 / paddleocr 3.7.0
      -> CPU/GPU separate runtime manifests
      -> embedded worker.py checksum
  -> OcrWorkerClient.StartAsync
      -> private python.exe -u worker.py --model-cache ... --device ...
      -> validate Ready + PP-OCRv6_small_det + PP-OCRv6_small_rec + device

OCRPage.TestFrame
  -> OcrScanner.RecognizeFrameAsync
      -> FFmpeg ROI JPEG
      -> OcrManager shared worker pool
      -> request-id JSON line protocol
      -> optional enhanced retry for low confidence

OCRPage.Scan
  -> BiliSubApplication.StartOcrScan (pausable AppJob)
  -> OcrScanner.RunAsync
      -> load schema-4 checkpoint or select fresh topology
      -> Auto Predict (hardware) -> Probe (real frame/real OCR throughput) -> Commit
      -> deterministic core segments + bounded overlap
      -> one FFmpeg image pipe per lane (NVDEC probe with software fallback)
      -> shared PaddleOCR worker pool with backpressure
      -> lane-local SubtitleTracker
      -> ChineseSubtitleNormalizer
      -> cue ownership + deterministic boundary reconciliation
      -> schema-4 safe pause/resume
  -> ExportOcrAsync reapplies Chinese validator -> SRT
```

## Video editor

```text
EditorPage native picker / FFmpeg preview / pointer regions
  -> VideoEditorService.BuildFilter
      -> normalized region -> source pixels
      -> sequential Blur / Mosaic / Cover graph
      -> whole-video or between(t,start,end)
  -> BiliSubApplication.StartEditor
  -> VideoEditorService.RunAsync
      -> ToolManager.EnsureFfmpegAsync
      -> ProcessRunner -> ffmpeg -progress pipe:1
      -> .rendering temp output
      -> verify non-empty
      -> atomic unique output name
```

The editor never imports or modifies downloader concurrency/resume/CDN state.

## Update/report

```text
SupportPage.Check
  -> UpdateService.CheckAsync
      -> stable/beta Drive manifest
      -> require payload_kind=winui3-portable-zip
      -> reject legacy Go payloads
SupportPage.Prepare
  -> strict size + SHA-256 download
  -> traversal-safe ZIP staging
  -> validate BiliSubStudio.exe PE marker
  -> reject payload roots containing Data/Tools/Temp/Cache/Downloads
  -> copy updater runtime outside install target
App close
  -> CreateProcessW CREATE_BREAKAWAY_FROM_JOB
  -> --apply-portable-update waits old PID
  -> revalidate payload and copy runtime while preserving portable data directories
  -> restart native executable

SupportPage.Report
  -> BugReportService.Sanitize
  -> redact cookie/token/user paths
  -> bounded report POST
```

## Ownership exclusions

- `BiliSubStudio.Core` has no `Microsoft.UI` or `Windows.Storage` reference.
- WinUI pages do not invoke FFmpeg, Python, yt-dlp or HTTP Range directly.
- No localhost server, `/api` route, browser UI, WebView/WebView2 or `BiliSubStudioCore.exe` exists in the C# production path.
- The Go production directories are not imported or modified; only the pinned OCR `worker.py` is linked as a build content asset and executed by the C# OCR owner.
