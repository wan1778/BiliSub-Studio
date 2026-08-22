# C# + WinUI 3 production call map

Baseline Go source identity: `9be4abd8184d2d7d24159dd736b6accfbe1cda90`. Go remains a frozen behavioral reference only; the C# application does not compile or invoke the Go UI/backend.

## Composition and lifecycle

```text
App.OnLaunched
  -> UpdateService.TryApplyFromCommandLineAsync (updater mode only)
  -> MainWindow
      -> AppPaths.FromExecutableDirectory
           installed layout: ...\BiliSub Studio\Runtime\BiliSubStudio.exe
             -> data root resolves to parent ...\BiliSub Studio
           portable layout: BiliSubStudio.exe at root
             -> data root remains executable root
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

## Installed directory ownership

The installer keeps framework/runtime implementation details out of the user-visible application root.

```text
BiliSub Studio\
  Runtime\        verified self-contained WinUI/.NET publish
                  EXE / DLL / XBF / PRI / locale resource folders
  Data\           persistent config/session/logs
  Tools\          app-owned yt-dlp / FFmpeg / OCR runtime
  Temp\           temporary/update/rollback data
  Cache\          cache
  Downloads\      legacy/default protected download root
```

Start Menu, desktop shortcut, uninstall display icon and post-install launch point to `Runtime\BiliSubStudio.exe`. `AppPaths.FromExecutableDirectory` recognizes the exact `Runtime` directory name and resolves the parent as the persistent app root, so `Data/Tools/Temp/Cache/Downloads` never move inside the runtime folder.

Installer upgrade from the previous flat layout is checksum-owned: if root `BiliSubStudio.exe` plus root `SHA256SUMS.txt` exist, Inno removes only runtime files named by that verified inventory, then collapses empty runtime directories before installing the new publish under `Runtime\`. The migration explicitly does not delete unknown root files and never deletes `Data/Tools/Temp/Cache/Downloads`.

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
          -> VideoDownloadService.DownloadOneAsync
          -> RangeDownloader.DownloadAsync
             -> probe with Range bytes=0-0
                 transient HTTP/network failure
                   -> refresh signed stream/CDN
                   -> retry probe, bounded to 6 attempts
                 successful non-206 body
                   -> true RangeNotSupported -> final fallback path
             -> 4 MiB Range segments
             -> exact HTTP/1.1 workers
             -> preserve partial bytes
             -> short body
                 -> retry from exact missing byte
                 -> repeated pathological tiny reads refresh signed stream
                 -> healthy large continuation does not rotate URL unnecessarily
             -> HTTP 403/408/429/5xx, including field HTTP 503
                 -> HttpRequestException transport condition, not RangeNotSupported
                 -> preserve resume state
                 -> refresh signed stream/CDN immediately
                 -> retry same missing range
             -> segment recovery bounded to 32 attempts
          -> if multi-connection Range still fails
             -> ApplicationLog warning with root transport cause
             -> retry the same stream through RangeDownloader at 1 connection
             -> resume completed segment manifest
          -> only after Range recovery/degradation fails
             -> yt-dlp fallback with resumable state / 4 MiB HTTP chunks
       -> optional thumbnail phase
       -> optional bundle-subtitle phase
          -> SubtitleService -> normalized output
  -> one parent Finish / Cancel owner
  -> AppJob diagnostics -> shared ApplicationLog
```

The 2026-08-22 real-machine failure on installer SHA `7f37dcb5...` proved why HTTP status classification matters: a segment HTTP `503` was previously converted immediately to `RangeNotSupportedException`, which forced yt-dlp fallback; yt-dlp then failed with `985 bytes read, 4144194 more expected`. The current path keeps transient CDN errors inside bounded Range recovery and only treats a successful response that actually ignores the Range header as no-Range support.

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
  -> HardwareService.RecommendedOcrLanes
       -> conservative CPU/RAM/GPU/VRAM starting prediction
  -> HardwareService.RecommendedOcrProbeCeiling
       -> at most one topology level above the prediction
  -> Auto live probe 1/2/4/8/16 up to duration/resource ceiling
       -> create the requested worker pool
       -> sample one real frame from the center of every candidate segment
       -> run N concurrent FFmpeg decode + OCR pipelines for N candidate lanes
       -> require at least one live Python worker per requested lane
       -> OOM/worker error keeps the last stable level
       -> measured throughput is diagnostic only; it cannot collapse real segment topology
  -> deterministic lane topology + shared PaddleOCR pool
       -> require checkpoint segment count == selected lanes
       -> Commit log exposes N FFmpeg lanes + N-or-more Python workers
  -> SubtitleTracker + ChineseSubtitleNormalizer
  -> schema-4 safe pause/resume checkpoint
  -> ExportOcrAsync -> SRT

OCRPage.Pause
  -> BiliSubApplication.PauseJobAsync
  -> all lanes stop at safe tracker boundaries
  -> OcrCheckpointStore.SaveAsync + write-through fsync
  -> OcrScanResult(Paused=true)
  -> page retains the exact OcrScanRequest
  -> Continue + Cancel remain enabled; partial Export remains disabled

OCRPage.Cancel (paused)
  -> BiliSubApplication.RemoveOcrCheckpointAsync(exact paused request)
  -> OcrCheckpointStore.RemoveAsync
  -> clear partial cues/progress/telemetry
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
  -> validate payload BiliSubStudio.exe as PE x86-64 / PE32+
  -> copy current Runtime directory to breakaway updater staging

App close after prepared update
  -> BreakawayLauncher (CREATE_BREAKAWAY_FROM_JOB)
  -> target = AppContext.BaseDirectory
       installed app => ...\BiliSub Studio\Runtime
       portable app  => current portable root
  -> wait old PID
  -> ApplyPayloadTransactionalAsync
       -> nested installed layout: rollback backup under parent Temp/Update
       -> backup current runtime entries only
       -> durable copy new payload into Runtime
       -> revalidate PE
       -> delete backup on success
       -> restore runtime + relaunch old app on failure
```

The update transaction therefore replaces implementation/runtime bytes only. Persistent parent `Data/Tools/Temp/Cache/Downloads` are outside the update target in the installed layout.

Channel publication policy is documented in `docs/migration/GITHUB_UPDATE_CHANNEL.md`. During field QA both repository manifests remain `channel_ready=false`; no GitHub Release is created automatically.

## Publish, installer and startup gates

```text
verify.ps1
  -> exact .NET 10.0.400
  -> static migration contract
  -> generated C# code map check
  -> shared-log / consolidated-shell / compact-media contract
  -> Release build + Core contracts
  -> Range field regression executable
       -> 379-byte short-read continuation
       -> segment HTTP 503 -> refresh -> Range success
       -> probe HTTP 503 -> refresh -> Range success
  -> self-contained win-x64 publish
  -> XBF + PRI validation
  -> exact published EXE startup sentinel
  -> routed-page layout smoke at 800x600 / 1000x700 / 1500x900
       -> Tải media / OCR / Editor / Settings
       -> Settings internal Chung / Hiệu năng / Đăng nhập / Cập nhật & hỗ trợ
       -> global log drawer open / closed

workflow pre-gates
  -> verify_media_bundle_contract.py
       -> short-read + HTTP503 + adaptive Range source contracts
  -> verify_installer_runtime_layout_contract.py
       -> Runtime destination/shortcuts
       -> AppPaths parent-root resolution
       -> runtime-only updater target
       -> checksum-owned legacy migration contract

package_windows_candidate.ps1
  -> Inno Setup x64 current-user installer
  -> create legacy flat-layout fixture from the exact verified publish
  -> add protected Data/Tools/Temp/Cache/Downloads markers + unknown root file
  -> install over fixture
  -> require old root BiliSubStudio.exe removed
  -> require Runtime/BiliSubStudio.exe + full publish checksums
  -> require protected markers + unknown root file preserved
  -> startup smoke from Runtime
  -> uninstall + protected-root/unknown-file preservation
```

## Ownership exclusions

- `BiliSubStudio.Core` has no WinUI/WinRT UI dependency.
- WinUI pages do not spawn FFmpeg/Python/yt-dlp directly.
- No localhost production server, `/api` UI adapter, WebView/WebView2 or `BiliSubStudioCore.exe` exists in the C# production path.
- Go production directories are not imported or invoked by the C# application.
- Update discovery and payload hosting are GitHub-only; transactional update safety remains owned by `UpdateService`.
- The transient-CDN hardening changes only video transport classification/retry/degradation; it does not alter OCR scanner topology, editor render algorithms, metadata ownership or the unified Media UI contract.
- The tidy installer layout changes only installed runtime placement and path ownership; the verified publish bytes themselves remain unchanged inside `Runtime\`.
