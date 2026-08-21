# UI v2.1 resolved source map

Baseline archive: `BiliSubStudio_source_v4.0.0-beta.12-NativeUI.zip`  
Archive SHA-256: `d4acd9ed2b9237f5b20f187e750059d17802d00fc5d3cd81fe6a4387e458d4da`  
Pinned source identity: `9be4abd8184d2d7d24159dd736b6accfbe1cda90`

The archive contains no `.git` directory, so the archive comment and SHA-256 are the identity gate. Do not claim a clean Git worktree for this extracted checkpoint.

## Resolved Go + Win32 reference symbols

| Responsibility | Exact source symbol |
|---|---|
| Production entry | `cmd/bilisub/main.go:main` |
| Persistent paths/config bootstrap | `internal/appstate/state.go:New` |
| Config normalization | `internal/appstate/state.go:normalizeConfig` |
| Config snapshot/update | `internal/appstate/state.go:SnapshotConfig`, `UpdateConfig` |
| Application construction | `internal/application/app.go:New` |
| Settings mutations | `internal/application/app.go:SetOutputDir`, `SetTheme`, `SetUpdateCheck` |
| Native UI entry | `internal/nativeui/ui_windows.go:Run` |
| Central window procedure | `internal/nativeui/ui_windows.go:wndProc` |
| Page/control creation | `internal/nativeui/ui_windows.go:(*window).build` |
| Settings geometry | `internal/nativeui/ui_windows.go:(*window).layoutSettings` |
| Single enabled-state owner | `internal/nativeui/ui_windows.go:(*window).syncControls` |
| Safe close | `internal/nativeui/ui_windows.go:(*window).requestClose` |

## Resolved C# + WinUI 3 migration symbols

| Responsibility | Exact source symbol |
|---|---|
| WinUI startup | `csharp/src/BiliSubStudio.App/App.xaml.cs:App.OnLaunched` |
| Composition root | `csharp/src/BiliSubStudio.App/MainWindow.xaml.cs:MainWindow` |
| Portable paths | `csharp/src/BiliSubStudio.Core/Configuration/AppPaths.cs:AppPaths` |
| Config schema | `csharp/src/BiliSubStudio.Core/Configuration/AppConfig.cs:AppConfig` |
| Config normalization | `csharp/src/BiliSubStudio.Core/Configuration/AppConfigNormalizer.cs:Normalize` |
| Atomic persistence | `csharp/src/BiliSubStudio.Core/Configuration/AtomicJsonFile.cs:WriteAsync` |
| Config state owner | `csharp/src/BiliSubStudio.Core/Configuration/JsonConfigStore.cs:JsonConfigStore` |
| Settings application boundary | `csharp/src/BiliSubStudio.Core/Application/SettingsApplicationService.cs` |
| Settings presentation state | `csharp/src/BiliSubStudio.App/ViewModels/SettingsViewModel.cs` |
| Settings native UI | `csharp/src/BiliSubStudio.App/Pages/SettingsPage.xaml` |
| Application/lifecycle composition | `csharp/src/BiliSubStudio.Core/Application/BiliSubApplication.cs` |
| Job state and immediate cancellation | `csharp/src/BiliSubStudio.Core/Jobs/AppJob.cs`, `JobManager.cs` |
| Native media probe/preview | `csharp/src/BiliSubStudio.Core/Media/MediaPreviewService.cs` |
| App-owned tools | `csharp/src/BiliSubStudio.Core/Tools/ToolManager.cs` |
| Strict Range transport | `csharp/src/BiliSubStudio.Core/Video/RangeDownloader.cs` |
| Video orchestration/fallback/remux | `csharp/src/BiliSubStudio.Core/Video/VideoDownloadService.cs` |
| Subtitle normalization/export | `csharp/src/BiliSubStudio.Core/Subtitle/SubtitleService.cs` |
| Hardware/CUDA probe | `csharp/src/BiliSubStudio.Core/Hardware/HardwareService.cs` |
| OCR install/worker/pool | `csharp/src/BiliSubStudio.Core/Ocr/OcrInstaller.cs`, `OcrWorkerClient.cs`, `OcrManager.cs` |
| OCR scan/checkpoint/reconcile | `csharp/src/BiliSubStudio.Core/Ocr/OcrScanner.cs`, `OcrCheckpointStore.cs` |
| Video editor render | `csharp/src/BiliSubStudio.Core/Editor/VideoEditorService.cs` |
| Login/session | `csharp/src/BiliSubStudio.Core/Authentication/BilibiliAuthService.cs`, `SessionStore.cs` |
| Update/report | `csharp/src/BiliSubStudio.Core/Maintenance/UpdateService.cs`, `BugReportService.cs` |
| Native feature pages | `csharp/src/BiliSubStudio.App/Pages/{Video,Subtitle,Ocr,Editor,Hardware,Account,Support}Page.xaml` |

All listed symbols are source-owned in P2. Windows compilation, compositor inspection, real dependency execution and field parity remain gates; this map does not claim a release candidate.
