# C# + WinUI 3 production call map

Baseline Go source identity: `9be4abd8184d2d7d24159dd736b6accfbe1cda90`. Go remains a frozen behavioral reference only; the C# application does not compile or invoke the Go UI/backend.

## Composition and lifecycle

```text
App.OnLaunched
  -> UpdateService.TryApplyFromCommandLineAsync (updater mode only)
  -> MainWindow
      -> AppPaths.FromExecutableDirectory
      -> ApplicationLog(Data/Logs/application.log)
      -> BiliSubApplication
          -> WindowsProcessContainment (KILL_ON_JOB_CLOSE + updater BREAKAWAY_OK)
          -> one JobManager / ToolManager / ProcessRunner / HttpClient
          -> Settings, Auth, Media, Video, Subtitle, Hardware, OCR, Editor, Update, Report owners
      -> JobManager.AttachLog(ApplicationLog)
      -> four routed native WinUI destinations
           Tải media / OCR phụ đề / Chỉnh video / Cài đặt
      -> SettingsPage embeds four internal sections
           Chung / Hiệu năng / Đăng nhập / Cập nhật & hỗ trợ
      -> one shell-owned global diagnostic drawer shared by every routed destination
```

`MainWindow` no longer routes or constructs a separate subtitle page. `SubtitlePage.xaml(.cs)` is retained only as an unrouted migration/reference source at this checkpoint; production navigation has one Bilibili download workflow. Hardware, account and support remain real native modules but are hosted inside `SettingsPage` rather than occupying separate top-level navigation items.

```text
AppWindow.Closing
  -> ApplicationLog.Info("Ứng dụng", safe-close start)
  -> BiliSubApplication.PrepareShutdownAsync
      -> PauseJobAsync for every pausable OCR job
      -> safe OCR checkpoint + fsync
      -> cancel remaining jobs
      -> OcrManager.StopAsync
      -> SessionStore.DeleteTemporaryAsync
  -> UpdateService.LaunchPrepared (verified update only)
  -> ApplicationLog.Info/Error
  -> Window.Close
```

## Shared diagnostic log

There is one application-level diagnostic stream, not one independent log box per feature page.

```text
MainWindow
  -> new ApplicationLog(AppPaths.Data)
      -> Data/Logs/application.log
      -> rotate to application.log.1 above 5 MiB
      -> retain up to 2,000 entries in memory
  -> JobManager.AttachLog(ApplicationLog)

AppJob.Log(message)
  -> AppLogLevel.Info
AppJob.Warn(message)
  -> AppLogLevel.Warning
AppJob.Error(message)
  -> AppLogLevel.Error
AppJob.Finish(error != null)
  -> AppLogLevel.Error

ApplicationLog.Write
  -> append persistent timestamp / level / source / optional job id / message
  -> EntryAdded event
  -> MainWindow.OnGlobalLogEntry
      -> one shared ListView
      -> green normal / yellow warning / red error presentation
      -> error counter
      -> auto-expand drawer on error
```

Direct shell, Media, Settings, Hardware, Account and Support actions also write to the same `ApplicationLog`. Cookie values are never written to the diagnostic stream. `SupportPage` includes up to the latest 500 shared entries in the bug-report diagnostic payload; `BugReportService` remains the sanitization boundary before network submission.

`Xóa màn hình` clears only the current visual list/error counter. The persistent `Data/Logs/application.log` file is retained so a diagnostic trail is not destroyed while investigating a failure.

## Settings/config and consolidated operational sections

```text
MainWindow -> SettingsPage
  -> section Chung
      -> SettingsViewModel
      -> SettingsApplicationService
      -> JsonConfigStore
      -> Data/config.json
  -> section Hiệu năng
      -> embedded HardwarePage
      -> HardwareService / ToolManager / OcrManager
  -> section Đăng nhập
      -> embedded AccountPage
      -> BilibiliAuthService / SessionStore
  -> section Cập nhật & hỗ trợ
      -> embedded SupportPage
      -> UpdateService / BugReportService
```

The existing config JSON schema remains unchanged. Moving Hardware/Account/Support into Settings changes UI composition only; their application/core service ownership is unchanged.

## Unified Bilibili media workflow

```text
VideoPage.LoadMetadata_Click
  -> BiliSubApplication.GetMetadataAsync
  -> SessionStore.WriteNetscapeFileAsync
  -> YtDlpResolver.GetMetadataAsync
  -> ToolManager.EnsureYtDlpAsync
  -> ProcessRunner -> app-owned yt-dlp.exe -J
  -> one VideoMetadata result containing qualities + thumbnail + subtitle tracks
  -> VideoPage populates QualityBox + TrackBox
  -> ApplicationLog Media info/warning/error

VideoPage.Start_Click
  -> no asset checkbox selected
       => Video + Thumbnail + Subtitle-if-available
     one or more asset checkboxes selected
       => only explicitly selected assets
  -> VideoDownloadRequest(MediaBundle=true, BundleVideo, BundleThumbnail, BundleSubtitleIfAvailable)
  -> BiliSubApplication.StartVideo
  -> one parent JobManager job kind `media`
       -> optional bundle-video phase
          -> VideoDownloadService
          -> RangeDownloader
             -> 4 MiB Range segments
             -> preserve partial bytes
             -> retry from exact missing byte
             -> exact HTTP/1.1 workers
             -> refresh signed stream after repeated pathological tiny reads
          -> yt-dlp fallback with resumable state / 4 MiB HTTP chunks
       -> optional thumbnail phase
       -> optional bundle-subtitle phase
          -> SubtitleService -> normalized output
  -> one parent Finish / Cancel owner
  -> AppJob diagnostics -> shared ApplicationLog
```

URL edits invalidate quality/subtitle state. Parent cancellation is linked to the currently active child phase. Media no longer owns a page-local `LogBox`; progress/status stays on the Media page while diagnostics are written to the global shell log.

The Media desktop composition uses available width rather than stacking every stage vertically:

```text
Source (full width)
  -> MainColumns
       left  1.35*: content selection + quality/format
       right 0.95*: output folder + start/cancel
  -> compact progress (full width)
```

## Authentication

```text
SettingsPage / Đăng nhập
  -> AccountPage
  -> BilibiliAuthService.StartQrAsync / PollQrAsync
  -> QrMatrixEncoder -> native Canvas
  -> SessionStore -> Windows DPAPI -> Data/session.bin
  -> ApplicationLog status/error without cookie contents
```

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
  -> JobManager/AppJob -> shared ApplicationLog
  -> OcrScanner.RunAsync
  -> deterministic lane topology + shared PaddleOCR pool
  -> SubtitleTracker + ChineseSubtitleNormalizer
  -> schema-4 safe pause/resume checkpoint
  -> ExportOcrAsync -> SRT
```

## Video editor

```text
EditorPage
  -> MediaPreviewService/native player
  -> BiliSubApplication.StartEditor
  -> JobManager/AppJob -> shared ApplicationLog
  -> VideoEditorService.RunAsync
  -> FFmpeg Blur/Mosaic/Cover graph
  -> temporary render -> non-empty verification -> atomic final output
```

## GitHub update channel

Google Drive is no longer in the production update call path.

```text
SettingsPage / Cập nhật & hỗ trợ / SupportPage.Check
or startup auto-check
  -> UpdateService.CheckAsync
  -> CurrentVersion contains '-' ? beta : stable
  -> HTTPS raw GitHub manifest on main
       stable -> raw.githubusercontent.com/wan1778/BiliSub-Studio/main/update/stable.json
       beta   -> raw.githubusercontent.com/wan1778/BiliSub-Studio/main/update/beta.json
  -> channel_ready=false => report unpublished channel; download disabled
  -> channel_ready=true
       -> require payload_kind=winui3-portable-zip
       -> require exact version / size / 64-hex SHA-256
       -> require download_url host/path:
          https://github.com/wan1778/BiliSub-Studio/releases/download/...

SupportPage.Prepare
  -> UpdateService.PrepareAsync
  -> download exact GitHub Release asset
  -> verify byte size + SHA-256 while streaming
  -> safe ZIP extraction
  -> reject protected Data/Tools/Temp/Cache/Downloads roots
  -> validate root BiliSubStudio.exe as PE x86-64 / PE32+
  -> copy current runtime to breakaway updater staging

App close after prepared update
  -> BreakawayLauncher (CREATE_BREAKAWAY_FROM_JOB)
  -> wait old PID
  -> ApplyPayloadTransactionalAsync
       -> backup unprotected runtime entries
       -> durable copy new payload
       -> revalidate PE
       -> delete backup on success
       -> restore backup + relaunch old app on failure
```

Channel publication policy is documented in `docs/migration/GITHUB_UPDATE_CHANNEL.md`. During field QA both repository manifests remain `channel_ready=false`; no GitHub Release is created automatically.

## Publish, installer and startup gates

```text
verify.ps1
  -> exact .NET 10.0.400
  -> static migration contract
  -> generated C# code map check
  -> shared-log / consolidated-shell / compact-media contract
  -> Release build + Core contracts
  -> 379-byte short-read regression executable
  -> self-contained win-x64 publish
  -> XBF + PRI validation
  -> exact published EXE startup sentinel
  -> routed-page layout smoke at 800x600 / 1000x700 / 1500x900
       -> Tải media / OCR / Editor / Settings
       -> Settings internal Chung / Hiệu năng / Đăng nhập / Cập nhật & hỗ trợ
       -> global log drawer open / closed

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
- Update discovery and payload hosting are GitHub-only; transactional update safety remains owned by `UpdateService`.
- The shared log refactor does not alter Range downloader, OCR scanner topology or editor render algorithms; it changes diagnostic routing and shell/page composition.
