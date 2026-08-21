# C# + WinUI 3 production call map

Baseline Go source identity: `9be4abd8184d2d7d24159dd736b6accfbe1cda90`. Go remains a frozen behavioral reference only; the C# application does not compile or invoke the Go UI/backend.

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
      -> seven routed native WinUI pages
           Tải media / OCR phụ đề / Chỉnh video / Hiệu năng / Đăng nhập / Cập nhật & hỗ trợ / Cài đặt
```

`MainWindow` no longer routes or constructs a separate subtitle page. `SubtitlePage.xaml(.cs)` is retained only as an unrouted migration/reference source at this checkpoint; production navigation has one Bilibili download workflow.

```text
AppWindow.Closing
  -> BiliSubApplication.PrepareShutdownAsync
      -> PauseJobAsync for every pausable OCR job
      -> safe OCR checkpoint + fsync
      -> cancel remaining jobs
      -> OcrManager.StopAsync
      -> SessionStore.DeleteTemporaryAsync
  -> UpdateService.LaunchPrepared (verified update only)
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

VideoPage (Tải media) / OCRPage
  -> BiliSubApplication owner
  -> JsonConfigStore.UpdateAsync
  -> persist video speed/container/mode, subtitle format, OCR device/ROI
```

The existing JSON schema remains unchanged.

## Unified Bilibili media workflow

Production ownership is one visible page and one user action:

```text
VideoPage.LoadMetadata_Click
  -> BiliSubApplication.GetMetadataAsync
  -> SessionStore.WriteNetscapeFileAsync
  -> YtDlpResolver.GetMetadataAsync
  -> ToolManager.EnsureYtDlpAsync
  -> ProcessRunner -> app-owned yt-dlp.exe -J
  -> one VideoMetadata result containing BOTH qualities and subtitle tracks
  -> VideoPage populates QualityBox + TrackBox
```

Rules enforced in `VideoPage`:

- one Bilibili URL owns both video quality and subtitle track state;
- editing the URL immediately clears both quality and subtitle selections and disables Start;
- a metadata response is discarded if the URL changed while it was in flight;
- preferred default subtitle is official Chinese first, then another Chinese track, then other official/available tracks;
- one output directory and one progress/log surface are used for the bundle;
- the primary CTA is `Tải video + phụ đề`.

Start call path:

```text
VideoPage.Start_Click
  -> VideoDownloadRequest
       BundleSubtitleFormat = selected SRT/TXT/JSON
       BundleSubtitleTrack  = selected subtitle language identity
  -> BiliSubApplication.StartVideo
       if BundleSubtitleTrack is empty:
           legacy/single-video compatibility path remains unchanged
       else:
           JobManager.Create("media")
           persist video + subtitle preferences
           write one temporary Netscape cookie owner

           phase bundle-video (0..85%)
             -> child AppJob linked to parent cancellation
             -> VideoDownloadService.RunAsync
                 -> YtDlpResolver.ResolveAsync
                 -> RangeDownloader.DownloadAsync per selected DASH stream
                 -> Stable 1 / Fast 8 / Turbo 16 connection budget
                 -> strict Range validation + resume manifest
                 -> bounded URL refresh / yt-dlp fallback
                 -> FFmpeg stream-copy remux

           phase bundle-subtitle (85..100%)
             -> child AppJob linked to the same parent cancellation
             -> SubtitleService.RunAsync
                 -> selected subtitle track
                 -> JSON/json3/WebVTT/SRT parsing
                 -> normalized SRT/TXT/JSON
                 -> atomic output write

           -> parent AppJob logs both final paths
           -> parent Finish once
```

`Cancel` targets the single parent media Job. Parent cancellation is registered into the currently active child phase, so HTTP workers/yt-dlp/FFmpeg/subtitle fetch stop through their existing cancellation paths. Video Range/retry/remux algorithms and subtitle parsing algorithms are not modified by the bundling layer.

`BiliSubApplication.StartSubtitle` remains as a low-level compatibility entry point but is not exposed as a separate production navigation tab.

## Authentication

```text
AccountPage
  -> BilibiliAuthService.StartQrAsync / PollQrAsync
  -> QrMatrixEncoder -> native Canvas
  -> SessionStore -> Windows DPAPI -> Data/session.bin
```

No browser/WebView is required for QR presentation.

## Native media preview

```text
OCRPage or EditorPage
  -> FilePickerService
  -> MediaPreviewService.ProbeAsync -> ffprobe.exe
  -> MediaPreviewService.GetFrameJpegAsync -> ffmpeg.exe
  -> native WinUI preview/player surface
```

## OCR

```text
OCRPage.Prepare
  -> BiliSubApplication.PrepareOcrAsync
  -> OcrManager / OcrInstaller
  -> private Python + Paddle runtime under Tools/OCR

OCRPage.Scan
  -> BiliSubApplication.StartOcrScan
  -> OcrScanner.RunAsync
  -> Auto/1/2/4/8/16 deterministic lane topology
  -> shared PaddleOCR worker pool
  -> lane-local SubtitleTracker
  -> ChineseSubtitleNormalizer
  -> schema-4 safe pause/resume checkpoint
  -> ExportOcrAsync -> SRT
```

## Video editor

```text
EditorPage
  -> MediaPreviewService/native player
  -> BiliSubApplication.StartEditor
  -> VideoEditorService.RunAsync
  -> FFmpeg Blur/Mosaic/Cover graph
  -> temporary render -> non-empty verification -> atomic final output
```

Editor code does not own or alter downloader concurrency/resume state.

## Hardware, update and report

```text
HardwarePage -> HardwareService (CPU/RAM + NVIDIA Driver API/NVML)
SupportPage.Check -> UpdateService.CheckAsync
SupportPage.Prepare -> SHA-256 verified WinUI portable payload staging
SupportPage.Report -> BugReportService.Sanitize -> bounded POST
```

## Publish, installer and startup gates

```text
verify.ps1
  -> exact .NET 10.0.400
  -> static migration contract
  -> generated C# code map check
  -> Release build + Core contracts
  -> self-contained win-x64 publish
  -> XBF + PRI validation
  -> exact published EXE startup sentinel
  -> routed-page layout smoke at 800x600 / 1000x700 / 1500x900

package_windows_candidate.ps1
  -> Inno Setup x64 current-user installer
  -> custom-directory install smoke
  -> exact installed-EXE startup/layout smoke
  -> uninstall + protected-root preservation
```

## Ownership exclusions

- `BiliSubStudio.Core` has no WinUI/WinRT UI dependency.
- WinUI pages do not spawn FFmpeg/Python/yt-dlp directly.
- No localhost production server, `/api` UI adapter, WebView/WebView2 or `BiliSubStudioCore.exe` exists in the C# production path.
- Go production directories are not imported or invoked by the C# application.
- The unified media change is orchestration/UI only; `RangeDownloader`, `VideoDownloadService` transport behavior and `SubtitleService` normalization behavior remain separate owners.
