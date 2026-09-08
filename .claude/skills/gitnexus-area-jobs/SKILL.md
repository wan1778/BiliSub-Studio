---
name: gitnexus-area-jobs
description: "Skill for the Jobs area of BiliSub-Studio. 19 symbols across 9 files."
---

# Jobs

19 symbols | 9 files | Cohesion: 67%

## When to Use

- Working with code in `csharp/`
- Understanding how SequentialJobQueue, DisposeAsync, Dispose work
- Modifying jobs-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `csharp/src/BiliSubStudio.Core/Jobs/SequentialJobQueue.cs` | DisposeAsync, StopAsync, PauseAndDrainAsync, Resume, SequentialJobQueue (+1) |
| `csharp/src/BiliSubStudio.Core/Application/BiliSubApplication.cs` | DisposeAsync, PauseJobAsync, PrepareShutdownAsync |
| `csharp/src/BiliSubStudio.Core/Jobs/JobManager.cs` | CancelAll, Dispose, TryGet |
| `csharp/src/BiliSubStudio.Core/Processes/WindowsProcessContainment.cs` | CloseHandle, Dispose |
| `csharp/src/BiliSubStudio.App/MainWindow.xaml.cs` | OnClosed |
| `csharp/src/BiliSubStudio.App/Pages/VideoPage.xaml.cs` | StopMonitoring |
| `csharp/src/BiliSubStudio.Core/Configuration/JsonConfigStore.cs` | Dispose |
| `csharp/tests/BiliSubStudio.Core.ContractTests/OcrFragmentRuntimeContract.cs` | Scan |
| `csharp/src/BiliSubStudio.App/Pages/OcrPage.xaml.cs` | Pause_Click |

## Entry Points

Start here when exploring this area:

- **`SequentialJobQueue`** (Class) — `csharp/src/BiliSubStudio.Core/Jobs/SequentialJobQueue.cs:8`
- **`DisposeAsync`** (Method) — `csharp/src/BiliSubStudio.Core/Application/BiliSubApplication.cs:898`
- **`Dispose`** (Method) — `csharp/src/BiliSubStudio.Core/Configuration/JsonConfigStore.cs:83`
- **`CancelAll`** (Method) — `csharp/src/BiliSubStudio.Core/Jobs/JobManager.cs:53`
- **`Dispose`** (Method) — `csharp/src/BiliSubStudio.Core/Jobs/JobManager.cs:64`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `SequentialJobQueue` | Class | `csharp/src/BiliSubStudio.Core/Jobs/SequentialJobQueue.cs` | 8 |
| `DisposeAsync` | Method | `csharp/src/BiliSubStudio.Core/Application/BiliSubApplication.cs` | 898 |
| `Dispose` | Method | `csharp/src/BiliSubStudio.Core/Configuration/JsonConfigStore.cs` | 83 |
| `CancelAll` | Method | `csharp/src/BiliSubStudio.Core/Jobs/JobManager.cs` | 53 |
| `Dispose` | Method | `csharp/src/BiliSubStudio.Core/Jobs/JobManager.cs` | 64 |
| `DisposeAsync` | Method | `csharp/src/BiliSubStudio.Core/Jobs/SequentialJobQueue.cs` | 156 |
| `StopAsync` | Method | `csharp/src/BiliSubStudio.Core/Jobs/SequentialJobQueue.cs` | 77 |
| `Dispose` | Method | `csharp/src/BiliSubStudio.Core/Processes/WindowsProcessContainment.cs` | 60 |
| `PauseJobAsync` | Method | `csharp/src/BiliSubStudio.Core/Application/BiliSubApplication.cs` | 791 |
| `PrepareShutdownAsync` | Method | `csharp/src/BiliSubStudio.Core/Application/BiliSubApplication.cs` | 822 |
| `TryGet` | Method | `csharp/src/BiliSubStudio.Core/Jobs/JobManager.cs` | 32 |
| `PauseAndDrainAsync` | Method | `csharp/src/BiliSubStudio.Core/Jobs/SequentialJobQueue.cs` | 98 |
| `Resume` | Method | `csharp/src/BiliSubStudio.Core/Jobs/SequentialJobQueue.cs` | 113 |
| `Scan` | Function | `csharp/tests/BiliSubStudio.Core.ContractTests/OcrFragmentRuntimeContract.cs` | 29 |
| `OnClosed` | Method | `csharp/src/BiliSubStudio.App/MainWindow.xaml.cs` | 415 |
| `StopMonitoring` | Method | `csharp/src/BiliSubStudio.App/Pages/VideoPage.xaml.cs` | 356 |
| `CloseHandle` | Method | `csharp/src/BiliSubStudio.Core/Processes/WindowsProcessContainment.cs` | 119 |
| `Pause_Click` | Method | `csharp/src/BiliSubStudio.App/Pages/OcrPage.xaml.cs` | 560 |
| `CompletedIdle` | Method | `csharp/src/BiliSubStudio.Core/Jobs/SequentialJobQueue.cs` | 161 |

## Execution Flows

| Flow | Type | Steps |
|------|------|-------|
| `DisposeAsync → Kill` | cross_community | 7 |
| `OnAppWindowClosing → JobSnapshot` | cross_community | 5 |
| `DisposeAsync → TryCancel` | cross_community | 4 |
| `PrepareShutdownAsync → TryCancel` | cross_community | 4 |
| `DisposeAsync → JobSnapshot` | cross_community | 4 |
| `DisposeAsync → MinimumWorkerTarget` | cross_community | 4 |
| `OnAppWindowClosing → ActiveSnapshots` | cross_community | 3 |
| `OnAppWindowClosing → PauseAndDrainAsync` | cross_community | 3 |
| `Cancel_Click → TryGet` | cross_community | 3 |
| `DisposeAsync → StopAsync` | intra_community | 3 |

## How to Explore

1. `context({name: "SequentialJobQueue"})` — see callers and callees
2. `query({search_query: "jobs"})` — find related execution flows
3. Read key files listed above for implementation details
4. `explain({target: "<file or symbol>"})` — persisted taint findings (source→sink data flows), when indexed with `--pdg`
