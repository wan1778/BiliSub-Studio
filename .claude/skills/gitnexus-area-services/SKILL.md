---
name: gitnexus-area-services
description: "Skill for the Services area of BiliSub-Studio. 40 symbols across 11 files."
---

# Services

40 symbols | 11 files | Cohesion: 77%

## When to Use

- Working with code in `csharp/`
- Understanding how MainWindow, FilePickerService, FolderPickerService work
- Modifying services-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `csharp/src/BiliSubStudio.App/Services/StartupDiagnostics.cs` | DescribeException, Initialize, MessageBoxW, ShowFatalError, Write (+6) |
| `csharp/src/BiliSubStudio.App/Services/FilePickerService.cs` | CommDlgExtendedError, GetOpenFileNameW, PickAsync, PickImageAsync, PickSubtitleAsync (+4) |
| `csharp/src/BiliSubStudio.App/Services/EditorImageOverlayComposer.cs` | BuildGraph, HasAudioStreamAsync, Normalize, RenderAsync, TryDelete (+2) |
| `csharp/src/BiliSubStudio.App/MainWindow.xaml.cs` | ApplyTheme, OnLoaded, ResizeInitialWindow |
| `csharp/src/BiliSubStudio.Core/Ocr/OcrFrameExtractor.cs` | ParseFps, ParseTimeBase, ProbeUncachedAsync |
| `csharp/src/BiliSubStudio.App/App.xaml.cs` | OnLaunched, OnUnhandledException |
| `csharp/src/BiliSubStudio.App/MainWindow.Version.cs` | MainWindow |
| `csharp/src/BiliSubStudio.Core/Tools/ToolManager.cs` | EnsureFfprobeAsync |
| `csharp/src/BiliSubStudio.App/Services/IFilePickerService.cs` | IFilePickerService |
| `csharp/src/BiliSubStudio.App/Services/FolderPickerService.cs` | FolderPickerService |

## Entry Points

Start here when exploring this area:

- **`MainWindow`** (Class) — `csharp/src/BiliSubStudio.App/MainWindow.Version.cs:6`
- **`FilePickerService`** (Class) — `csharp/src/BiliSubStudio.App/Services/FilePickerService.cs:8`
- **`FolderPickerService`** (Class) — `csharp/src/BiliSubStudio.App/Services/FolderPickerService.cs:7`
- **`Initialize`** (Method) — `csharp/src/BiliSubStudio.App/Services/StartupDiagnostics.cs:18`
- **`ShowFatalError`** (Method) — `csharp/src/BiliSubStudio.App/Services/StartupDiagnostics.cs:60`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `MainWindow` | Class | `csharp/src/BiliSubStudio.App/MainWindow.Version.cs` | 6 |
| `FilePickerService` | Class | `csharp/src/BiliSubStudio.App/Services/FilePickerService.cs` | 8 |
| `FolderPickerService` | Class | `csharp/src/BiliSubStudio.App/Services/FolderPickerService.cs` | 7 |
| `IFilePickerService` | Interface | `csharp/src/BiliSubStudio.App/Services/IFilePickerService.cs` | 2 |
| `IFolderPickerService` | Interface | `csharp/src/BiliSubStudio.App/Services/IFolderPickerService.cs` | 2 |
| `Initialize` | Method | `csharp/src/BiliSubStudio.App/Services/StartupDiagnostics.cs` | 18 |
| `ShowFatalError` | Method | `csharp/src/BiliSubStudio.App/Services/StartupDiagnostics.cs` | 60 |
| `Write` | Method | `csharp/src/BiliSubStudio.App/Services/StartupDiagnostics.cs` | 35 |
| `WriteException` | Method | `csharp/src/BiliSubStudio.App/Services/StartupDiagnostics.cs` | 58 |
| `WriteSmokeSentinelAsync` | Method | `csharp/src/BiliSubStudio.App/Services/StartupDiagnostics.cs` | 75 |
| `RenderAsync` | Method | `csharp/src/BiliSubStudio.App/Services/EditorImageOverlayComposer.cs` | 22 |
| `EnsureFfprobeAsync` | Method | `csharp/src/BiliSubStudio.Core/Tools/ToolManager.cs` | 47 |
| `PickImageAsync` | Method | `csharp/src/BiliSubStudio.App/Services/FilePickerService.cs` | 40 |
| `PickSubtitleAsync` | Method | `csharp/src/BiliSubStudio.App/Services/FilePickerService.cs` | 26 |
| `PickTranslatedSubtitleAsync` | Method | `csharp/src/BiliSubStudio.App/Services/FilePickerService.cs` | 33 |
| `StartupDiagnostics` | Class | `csharp/src/BiliSubStudio.App/Services/StartupDiagnostics.cs` | 5 |
| `OnLaunched` | Method | `csharp/src/BiliSubStudio.App/App.xaml.cs` | 28 |
| `OnUnhandledException` | Method | `csharp/src/BiliSubStudio.App/App.xaml.cs` | 67 |
| `ApplyTheme` | Method | `csharp/src/BiliSubStudio.App/MainWindow.xaml.cs` | 235 |
| `OnLoaded` | Method | `csharp/src/BiliSubStudio.App/MainWindow.xaml.cs` | 174 |

## Execution Flows

| Flow | Type | Steps |
|------|------|-------|
| `InspectAsync → TryDelete` | cross_community | 9 |
| `InspectAsync → IsOwnedTool` | cross_community | 8 |
| `ReconcileCheckpointAsync → TryDelete` | cross_community | 8 |
| `InspectAsync → Kill` | cross_community | 8 |
| `ReconcileRangeAsync → TryDelete` | cross_community | 8 |
| `InspectAsync → Lease` | cross_community | 8 |
| `CheckBoundaryDetailedAsync → TryDelete` | cross_community | 8 |
| `ReconcileCheckpointAsync → IsOwnedTool` | cross_community | 7 |
| `ReconcileRangeAsync → IsOwnedTool` | cross_community | 7 |
| `CheckBoundaryDetailedAsync → IsOwnedTool` | cross_community | 7 |

## How to Explore

1. `context({name: "MainWindow"})` — see callers and callees
2. `query({search_query: "services"})` — find related execution flows
3. Read key files listed above for implementation details
4. `explain({target: "<file or symbol>"})` — persisted taint findings (source→sink data flows), when indexed with `--pdg`
