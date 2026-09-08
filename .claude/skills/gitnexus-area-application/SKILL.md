---
name: gitnexus-area-application
description: "Skill for the Application area of BiliSub-Studio. 31 symbols across 12 files."
---

# Application

31 symbols | 12 files | Cohesion: 61%

## When to Use

- Working with code in `csharp/`
- Understanding how StorageUsageReader, SettingsApplicationService, InitializeAsync work
- Modifying application-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `csharp/src/BiliSubStudio.Core/Application/SettingsApplicationService.cs` | BuildSnapshotAsync, InitializeAsync, SetOcrOutputDirectoryAsync, SetOutputDirectoryAsync, SetThemeAsync (+3) |
| `csharp/src/BiliSubStudio.Core/Application/BiliSubApplication.cs` | InitializeAsync, RunPhaseAsync, Publish, StartVideo |
| `csharp/src/BiliSubStudio.Core/Application/StorageUsageReader.cs` | StorageUsageReader, DirectorySize, ReadAsync |
| `csharp/src/BiliSubStudio.Core/Video/VideoDownloadService.cs` | Report, AggregateTransport, FormatSpeed |
| `csharp/src/BiliSubStudio.Core/Application/IStorageUsageReader.cs` | IStorageUsageReader, ReadAsync |
| `csharp/src/BiliSubStudio.Core/Configuration/AtomicJsonFile.cs` | TryDelete, WriteAsync |
| `csharp/src/BiliSubStudio.Core/Configuration/IConfigStore.cs` | InitializeAsync, UpdateAsync |
| `csharp/src/BiliSubStudio.Core/Editor/LocalAsrService.Hybrid.cs` | TranscribeHybridAsync, ValidHybridCue |
| `csharp/src/BiliSubStudio.Core/Jobs/AppJob.cs` | Set, SetTransport |
| `csharp/src/BiliSubStudio.Core/Configuration/JsonConfigStore.cs` | UpdateAsync |

## Entry Points

Start here when exploring this area:

- **`StorageUsageReader`** (Class) — `csharp/src/BiliSubStudio.Core/Application/StorageUsageReader.cs:4`
- **`SettingsApplicationService`** (Class) — `csharp/src/BiliSubStudio.Core/Application/SettingsApplicationService.cs:4`
- **`InitializeAsync`** (Method) — `csharp/src/BiliSubStudio.Core/Application/BiliSubApplication.cs:93`
- **`InitializeAsync`** (Method) — `csharp/src/BiliSubStudio.Core/Application/SettingsApplicationService.cs:9`
- **`SetOcrOutputDirectoryAsync`** (Method) — `csharp/src/BiliSubStudio.Core/Application/SettingsApplicationService.cs:29`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `StorageUsageReader` | Class | `csharp/src/BiliSubStudio.Core/Application/StorageUsageReader.cs` | 4 |
| `SettingsApplicationService` | Class | `csharp/src/BiliSubStudio.Core/Application/SettingsApplicationService.cs` | 4 |
| `IStorageUsageReader` | Interface | `csharp/src/BiliSubStudio.Core/Application/IStorageUsageReader.cs` | 4 |
| `ISettingsApplicationService` | Interface | `csharp/src/BiliSubStudio.Core/Application/ISettingsApplicationService.cs` | 2 |
| `InitializeAsync` | Method | `csharp/src/BiliSubStudio.Core/Application/BiliSubApplication.cs` | 93 |
| `InitializeAsync` | Method | `csharp/src/BiliSubStudio.Core/Application/SettingsApplicationService.cs` | 9 |
| `SetOcrOutputDirectoryAsync` | Method | `csharp/src/BiliSubStudio.Core/Application/SettingsApplicationService.cs` | 29 |
| `SetOutputDirectoryAsync` | Method | `csharp/src/BiliSubStudio.Core/Application/SettingsApplicationService.cs` | 18 |
| `SetThemeAsync` | Method | `csharp/src/BiliSubStudio.Core/Application/SettingsApplicationService.cs` | 40 |
| `SetUpdateCheckAsync` | Method | `csharp/src/BiliSubStudio.Core/Application/SettingsApplicationService.cs` | 56 |
| `ReadAsync` | Method | `csharp/src/BiliSubStudio.Core/Application/StorageUsageReader.cs` | 6 |
| `WriteAsync` | Method | `csharp/src/BiliSubStudio.Core/Configuration/AtomicJsonFile.cs` | 16 |
| `UpdateAsync` | Method | `csharp/src/BiliSubStudio.Core/Configuration/JsonConfigStore.cs` | 60 |
| `StartVideo` | Method | `csharp/src/BiliSubStudio.Core/Application/BiliSubApplication.cs` | 107 |
| `Set` | Method | `csharp/src/BiliSubStudio.Core/Jobs/AppJob.cs` | 163 |
| `SetTransport` | Method | `csharp/src/BiliSubStudio.Core/Jobs/AppJob.cs` | 219 |
| `RunPhaseAsync` | Function | `csharp/src/BiliSubStudio.Core/Application/BiliSubApplication.cs` | 184 |
| `Publish` | Function | `csharp/src/BiliSubStudio.Core/Application/BiliSubApplication.cs` | 196 |
| `Report` | Function | `csharp/src/BiliSubStudio.Core/Video/VideoDownloadService.cs` | 208 |
| `ReadAsync` | Method | `csharp/src/BiliSubStudio.Core/Application/IStorageUsageReader.cs` | 6 |

## Execution Flows

| Flow | Type | Steps |
|------|------|-------|
| `OnLoaded → StorageUsage` | cross_community | 6 |
| `OnLoaded → DirectorySize` | cross_community | 6 |
| `OnLoaded → AppConfig` | cross_community | 6 |
| `OnLoaded → TryDelete` | cross_community | 6 |
| `Cleanup_Click → StorageUsage` | cross_community | 6 |
| `Cleanup_Click → DirectorySize` | cross_community | 6 |
| `Cleanup_Click → AppConfig` | cross_community | 6 |
| `Cleanup_Click → TryDelete` | cross_community | 6 |
| `ResetTools_Click → StorageUsage` | cross_community | 6 |
| `ResetTools_Click → DirectorySize` | cross_community | 6 |

## How to Explore

1. `context({name: "StorageUsageReader"})` — see callers and callees
2. `query({search_query: "application"})` — find related execution flows
3. Read key files listed above for implementation details
4. `explain({target: "<file or symbol>"})` — persisted taint findings (source→sink data flows), when indexed with `--pdg`
