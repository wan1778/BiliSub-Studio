---
name: gitnexus-area-processes
description: "Skill for the Processes area of BiliSub-Studio. 16 symbols across 6 files."
---

# Processes

16 symbols | 6 files | Cohesion: 49%

## When to Use

- Working with code in `csharp/`
- Understanding how SetOcrRegionAsync, CanonicalRegion, NormalizeRegion work
- Modifying processes-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `csharp/src/BiliSubStudio.Core/Processes/OwnedProcessGroup.cs` | DisposeAsync, RemoveExited, StopAsync, WaitAsync, Kill (+1) |
| `csharp/src/BiliSubStudio.Core/Processes/ProcessRunner.cs` | BuildStartInfo, CaptureBytesAsync, Kill, ReapAsync, RunStreamingAsync |
| `csharp/src/BiliSubStudio.Core/Ocr/OcrCheckpointStore.cs` | CanonicalRegion, NormalizeRegion |
| `csharp/src/BiliSubStudio.Core/Application/BiliSubApplication.cs` | SetOcrRegionAsync |
| `csharp/src/BiliSubStudio.Core/Ocr/OcrScanner.cs` | RunAsync |
| `csharp/tests/BiliSubStudio.Core.ContractTests/TimelineMuxContract.cs` | ExtractRawFrameAsync |

## Entry Points

Start here when exploring this area:

- **`SetOcrRegionAsync`** (Method) — `csharp/src/BiliSubStudio.Core/Application/BiliSubApplication.cs:649`
- **`CanonicalRegion`** (Method) — `csharp/src/BiliSubStudio.Core/Ocr/OcrCheckpointStore.cs:207`
- **`NormalizeRegion`** (Method) — `csharp/src/BiliSubStudio.Core/Ocr/OcrCheckpointStore.cs:200`
- **`RunAsync`** (Method) — `csharp/src/BiliSubStudio.Core/Ocr/OcrScanner.cs:49`
- **`DisposeAsync`** (Method) — `csharp/src/BiliSubStudio.Core/Processes/OwnedProcessGroup.cs:77`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `SetOcrRegionAsync` | Method | `csharp/src/BiliSubStudio.Core/Application/BiliSubApplication.cs` | 649 |
| `CanonicalRegion` | Method | `csharp/src/BiliSubStudio.Core/Ocr/OcrCheckpointStore.cs` | 207 |
| `NormalizeRegion` | Method | `csharp/src/BiliSubStudio.Core/Ocr/OcrCheckpointStore.cs` | 200 |
| `RunAsync` | Method | `csharp/src/BiliSubStudio.Core/Ocr/OcrScanner.cs` | 49 |
| `DisposeAsync` | Method | `csharp/src/BiliSubStudio.Core/Processes/OwnedProcessGroup.cs` | 77 |
| `StopAsync` | Method | `csharp/src/BiliSubStudio.Core/Processes/OwnedProcessGroup.cs` | 32 |
| `CaptureBytesAsync` | Method | `csharp/src/BiliSubStudio.Core/Processes/ProcessRunner.cs` | 113 |
| `RunStreamingAsync` | Method | `csharp/src/BiliSubStudio.Core/Processes/ProcessRunner.cs` | 9 |
| `RemoveExited` | Method | `csharp/src/BiliSubStudio.Core/Processes/OwnedProcessGroup.cs` | 50 |
| `WaitAsync` | Method | `csharp/src/BiliSubStudio.Core/Processes/OwnedProcessGroup.cs` | 70 |
| `Kill` | Method | `csharp/src/BiliSubStudio.Core/Processes/OwnedProcessGroup.cs` | 65 |
| `Track` | Method | `csharp/src/BiliSubStudio.Core/Processes/OwnedProcessGroup.cs` | 19 |
| `BuildStartInfo` | Method | `csharp/src/BiliSubStudio.Core/Processes/ProcessRunner.cs` | 90 |
| `Kill` | Method | `csharp/src/BiliSubStudio.Core/Processes/ProcessRunner.cs` | 158 |
| `ReapAsync` | Method | `csharp/src/BiliSubStudio.Core/Processes/ProcessRunner.cs` | 163 |
| `ExtractRawFrameAsync` | Method | `csharp/tests/BiliSubStudio.Core.ContractTests/TimelineMuxContract.cs` | 328 |

## Execution Flows

| Flow | Type | Steps |
|------|------|-------|
| `GenerateSampleAsync → Kill` | cross_community | 10 |
| `GenerateCuesAsync → Lease` | cross_community | 9 |
| `PrepareAsync → Kill` | cross_community | 8 |
| `PrepareAsync → Lease` | cross_community | 8 |
| `InspectAsync → Kill` | cross_community | 8 |
| `GenerateSampleAsync → Lease` | cross_community | 8 |
| `InspectAsync → Lease` | cross_community | 8 |
| `Cancel_Click → OcrRegion` | cross_community | 7 |
| `Cancel_Click → NormalizeRegion` | cross_community | 7 |
| `PrepareAsync → BuildStartInfo` | cross_community | 7 |

## How to Explore

1. `context({name: "SetOcrRegionAsync"})` — see callers and callees
2. `query({search_query: "processes"})` — find related execution flows
3. Read key files listed above for implementation details
4. `explain({target: "<file or symbol>"})` — persisted taint findings (source→sink data flows), when indexed with `--pdg`
