---
name: gitnexus-area-maintenance
description: "Skill for the Maintenance area of BiliSub-Studio. 31 symbols across 3 files."
---

# Maintenance

31 symbols | 3 files | Cohesion: 87%

## When to Use

- Working with code in `csharp/`
- Understanding how PrepareUpdateAsync, CheckAsync, IsNewerVersion work
- Modifying maintenance-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `csharp/src/BiliSubStudio.Core/Maintenance/UpdateService.cs` | Parse, CheckAsync, CopyPayload, CopyRuntime, DownloadVerifiedAsync (+23) |
| `csharp/src/BiliSubStudio.Core/Application/BiliSubApplication.cs` | PrepareUpdateAsync, LaunchPendingUpdate |
| `csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs` | UpdateVersionContractAsync |

## Entry Points

Start here when exploring this area:

- **`PrepareUpdateAsync`** (Method) — `csharp/src/BiliSubStudio.Core/Application/BiliSubApplication.cs:869`
- **`CheckAsync`** (Method) — `csharp/src/BiliSubStudio.Core/Maintenance/UpdateService.cs:41`
- **`IsNewerVersion`** (Method) — `csharp/src/BiliSubStudio.Core/Maintenance/UpdateService.cs:347`
- **`PrepareAsync`** (Method) — `csharp/src/BiliSubStudio.Core/Maintenance/UpdateService.cs:55`
- **`TryApplyFromCommandLineAsync`** (Method) — `csharp/src/BiliSubStudio.Core/Maintenance/UpdateService.cs:92`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `PrepareUpdateAsync` | Method | `csharp/src/BiliSubStudio.Core/Application/BiliSubApplication.cs` | 869 |
| `CheckAsync` | Method | `csharp/src/BiliSubStudio.Core/Maintenance/UpdateService.cs` | 41 |
| `IsNewerVersion` | Method | `csharp/src/BiliSubStudio.Core/Maintenance/UpdateService.cs` | 347 |
| `PrepareAsync` | Method | `csharp/src/BiliSubStudio.Core/Maintenance/UpdateService.cs` | 55 |
| `TryApplyFromCommandLineAsync` | Method | `csharp/src/BiliSubStudio.Core/Maintenance/UpdateService.cs` | 92 |
| `LaunchPendingUpdate` | Method | `csharp/src/BiliSubStudio.Core/Application/BiliSubApplication.cs` | 875 |
| `Start` | Method | `csharp/src/BiliSubStudio.Core/Maintenance/UpdateService.cs` | 405 |
| `LaunchPrepared` | Method | `csharp/src/BiliSubStudio.Core/Maintenance/UpdateService.cs` | 83 |
| `Parse` | Function | `csharp/src/BiliSubStudio.Core/Maintenance/UpdateService.cs` | 349 |
| `CopyPayload` | Method | `csharp/src/BiliSubStudio.Core/Maintenance/UpdateService.cs` | 204 |
| `CopyRuntime` | Method | `csharp/src/BiliSubStudio.Core/Maintenance/UpdateService.cs` | 186 |
| `DownloadVerifiedAsync` | Method | `csharp/src/BiliSubStudio.Core/Maintenance/UpdateService.cs` | 139 |
| `ExtractSafe` | Method | `csharp/src/BiliSubStudio.Core/Maintenance/UpdateService.cs` | 166 |
| `FetchManifestAsync` | Method | `csharp/src/BiliSubStudio.Core/Maintenance/UpdateService.cs` | 117 |
| `NormalizeDownloadUrl` | Method | `csharp/src/BiliSubStudio.Core/Maintenance/UpdateService.cs` | 311 |
| `SafeName` | Method | `csharp/src/BiliSubStudio.Core/Maintenance/UpdateService.cs` | 381 |
| `TryDelete` | Method | `csharp/src/BiliSubStudio.Core/Maintenance/UpdateService.cs` | 391 |
| `ValidateManifest` | Method | `csharp/src/BiliSubStudio.Core/Maintenance/UpdateService.cs` | 131 |
| `UpdateVersionContractAsync` | Method | `csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs` | 2810 |
| `ApplyPayloadTransactionalAsync` | Method | `csharp/src/BiliSubStudio.Core/Maintenance/UpdateService.cs` | 211 |

## Execution Flows

| Flow | Type | Steps |
|------|------|-------|
| `PrepareUpdate_Click → UpdateManifest` | cross_community | 5 |
| `PrepareUpdate_Click → Parse` | cross_community | 5 |
| `PrepareUpdate_Click → NormalizeDownloadUrl` | cross_community | 5 |
| `OnLaunched → CopyPayloadDurableAsync` | cross_community | 4 |
| `OnLaunched → MoveUnprotectedRootEntries` | cross_community | 4 |
| `OnLaunched → ValidatePayloadLayout` | cross_community | 4 |
| `OnLaunched → ValidatePe` | cross_community | 4 |
| `PrepareUpdate_Click → SafeName` | cross_community | 4 |
| `CheckForUpdatesOnLaunchAsync → UpdateManifest` | cross_community | 4 |
| `CheckForUpdatesOnLaunchAsync → Parse` | cross_community | 4 |

## How to Explore

1. `context({name: "PrepareUpdateAsync"})` — see callers and callees
2. `query({search_query: "maintenance"})` — find related execution flows
3. Read key files listed above for implementation details
4. `explain({target: "<file or symbol>"})` — persisted taint findings (source→sink data flows), when indexed with `--pdg`
