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

```text
VideoPage.LoadMetadata_Click
  -> BiliSubApplication.GetMetadataAsync
  -> SessionStore.WriteNetscapeFileAsync
  -> YtDlpResolver.GetMetadataAsync
  -> ToolManager.EnsureYtDlpAsync
  -> ProcessRunner -> app-owned yt-dlp.exe -J
  -> one VideoMetadata result containing BOTH qualities and subtitle tracks
  -> VideoPage populates QualityBox + TrackBox

VideoPage.Start_Click
  -> VideoDownloadRequest(BundleSubtitleFormat, BundleSubtitleTrack)
  -> BiliSubApplication.StartVideo
  -> one parent JobManager job kind `media`
       -> bundle-video child phase 0..85%
          -> VideoDownloadService -> RangeDownloader -> fallback/remux
       -> bundle-subtitle child phase 85..100%
          -> SubtitleService -> selected track -> normalized output
  -> one parent Finish / Cancel owner
```

URL edits invalidate both quality and subtitle state. Parent cancellation is linked to whichever child phase is currently active. Video transport and subtitle normalization retain separate service ownership.

## Authentication

```text
AccountPage
  -> BilibiliAuthService.StartQrAsync / PollQrAsync
  -> QrMatrixEncoder -> native Canvas
  -> SessionStore -> Windows DPAPI -> Data/session.bin
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
  -> VideoEditorService.RunAsync
  -> FFmpeg Blur/Mosaic/Cover graph
  -> temporary render -> non-empty verification -> atomic final output
```

## GitHub update channel

Google Drive is no longer in the production update call path.

```text
SupportPage.Check / startup auto-check
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
- Update discovery and payload hosting are GitHub-only; transactional update safety remains owned by `UpdateService`.
