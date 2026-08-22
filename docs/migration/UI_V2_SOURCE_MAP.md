# UI v2.1 resolved source map

Baseline archive: `BiliSubStudio_source_v4.0.0-beta.12-NativeUI.zip`  
Archive SHA-256: `d4acd9ed2b9237f5b20f187e750059d17802d00fc5d3cd81fe6a4387e458d4da`  
Pinned source identity: `9be4abd8184d2d7d24159dd736b6accfbe1cda90`

The archive contains no `.git` directory, so the archive comment and SHA-256 are the identity gate. The Go tree is a frozen behavioral reference only for the C# migration.

## Resolved Go + Win32 reference symbols

| Responsibility | Exact source symbol |
|---|---|
| Production entry reference | `cmd/bilisub/main.go:main` |
| Persistent paths/config bootstrap | `internal/appstate/state.go:New` |
| Config normalization | `internal/appstate/state.go:normalizeConfig` |
| Config snapshot/update | `internal/appstate/state.go:SnapshotConfig`, `UpdateConfig` |
| Application construction | `internal/application/app.go:New` |
| Native UI entry reference | `internal/nativeui/ui_windows.go:Run` |
| Single enabled-state owner | `internal/nativeui/ui_windows.go:(*window).syncControls` |
| Safe close reference | `internal/nativeui/ui_windows.go:(*window).requestClose` |

## Resolved C# + WinUI 3 migration symbols

| Responsibility | Exact source symbol |
|---|---|
| WinUI startup | `csharp/src/BiliSubStudio.App/App.xaml.cs:App.OnLaunched` |
| Four-item production navigation/composition | `csharp/src/BiliSubStudio.App/MainWindow.xaml(.cs):MainWindow` |
| Shared persistent diagnostic owner | `csharp/src/BiliSubStudio.Core/Diagnostics/ApplicationLog.cs:ApplicationLog` |
| Global green/yellow/red log drawer | `csharp/src/BiliSubStudio.App/MainWindow.xaml(.cs)`, `ViewModels/GlobalLogItem.cs` |
| Unified Bilibili media UI | `csharp/src/BiliSubStudio.App/Pages/VideoPage.xaml(.cs)` (`Tải media`) |
| Compact Media desktop composition | `VideoPage.xaml:MainColumns` — left content/quality `1.35*`, right output/action `0.95*` |
| Unified media application boundary | `csharp/src/BiliSubStudio.Core/Application/BiliSubApplication.cs:StartVideo` |
| Media request compatibility envelope | `csharp/src/BiliSubStudio.Core/Video/VideoModels.cs:VideoDownloadRequest` |
| Video transport/orchestration | `csharp/src/BiliSubStudio.Core/Video/VideoDownloadService.cs`, `RangeDownloader.cs` |
| Subtitle normalization/export | `csharp/src/BiliSubStudio.Core/Subtitle/SubtitleService.cs` |
| Portable paths | `csharp/src/BiliSubStudio.Core/Configuration/AppPaths.cs:AppPaths` |
| Config state owner | `csharp/src/BiliSubStudio.Core/Configuration/JsonConfigStore.cs` |
| Settings boundary | `csharp/src/BiliSubStudio.Core/Application/SettingsApplicationService.cs` |
| Consolidated Settings UI | `csharp/src/BiliSubStudio.App/Pages/SettingsPage.xaml(.cs)` |
| Settings / Hiệu năng section | embedded `HardwarePage.xaml(.cs)` |
| Settings / Đăng nhập section | embedded `AccountPage.xaml(.cs)` |
| Settings / Cập nhật & hỗ trợ section | embedded `SupportPage.xaml(.cs)` |
| Job state/cancellation + diagnostic forwarding | `csharp/src/BiliSubStudio.Core/Jobs/AppJob.cs`, `JobManager.cs` |
| Native media probe/preview | `csharp/src/BiliSubStudio.Core/Media/MediaPreviewService.cs` |
| App-owned tools | `csharp/src/BiliSubStudio.Core/Tools/ToolManager.cs` |
| Hardware/CUDA probe | `csharp/src/BiliSubStudio.Core/Hardware/HardwareService.cs` |
| OCR install/worker/pool | `csharp/src/BiliSubStudio.Core/Ocr/OcrInstaller.cs`, `OcrWorkerClient.cs`, `OcrManager.cs` |
| OCR scan/checkpoint/reconcile | `csharp/src/BiliSubStudio.Core/Ocr/OcrScanner.cs`, `OcrCheckpointStore.cs` |
| Video editor | `csharp/src/BiliSubStudio.Core/Editor/VideoEditorService.cs` |
| Login/session | `csharp/src/BiliSubStudio.Core/Authentication/BilibiliAuthService.cs`, `SessionStore.cs` |
| Update/report | `csharp/src/BiliSubStudio.Core/Maintenance/UpdateService.cs`, `BugReportService.cs` |
| Top-level routed native feature pages | `VideoPage`, `OcrPage`, `EditorPage`, `SettingsPage` |

`SubtitlePage.xaml(.cs)` remains in this checkpoint only as unrouted migration/reference source. `MainWindow` does not construct or navigate to it, so production exposes no second Bilibili download tab.

`HardwarePage`, `AccountPage` and `SupportPage` are no longer top-level shell routes. They remain native feature modules and are hosted by `SettingsPage.SectionHost`, preserving their existing service ownership while reducing shell navigation to four primary destinations.

## Shared visual language

The Settings visual system is now the reference language for the remaining production UI:

- `CanvasBrush` page background.
- `CardBorderStyle` for functional groups.
- `RaisedSurfaceBrush` for secondary information surfaces.
- `AccentSoftBrush` / `AccentPrimaryBrush` for active/native capability context.
- `SuccessSoftBrush` / `SuccessBrush` for normal/ready state.
- `WarningSoftBrush` / `WarningBrush` for recoverable warning state.
- `DangerSoftBrush` / `DangerBrush` for errors/destructive actions.
- Breadcrumb + page-title hierarchy on Media, OCR and Editor.
- Compact horizontal desktop grouping where width is available instead of serial vertical stacking.

Media no longer contains its own log text box. Operational diagnostics from Media jobs and direct Media actions are routed to the shell-owned `ApplicationLog`. The same shell drawer remains visible/available regardless of which top-level page is active.

All listed C# symbols are source-owned in the C# migration. Windows compilation, exact installed-EXE startup/layout smoke and real-machine field parity remain mandatory gates; this map does not claim a release candidate.
