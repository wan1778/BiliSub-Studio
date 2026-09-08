---
name: gitnexus-area-hardware
description: "Skill for the Hardware area of BiliSub-Studio. 12 symbols across 1 files."
---

# Hardware

12 symbols | 1 files | Cohesion: 87%

## When to Use

- Working with code in `csharp/`
- Understanding how TryRead, TryReadMemory work
- Modifying hardware-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `csharp/src/BiliSubStudio.Core/Hardware/HardwareService.cs` | TryRead, cuDeviceGet, cuDeviceGetCount, cuDeviceGetName, cuDeviceTotalMem_v2 (+7) |

## Entry Points

Start here when exploring this area:

- **`TryRead`** (Method) — `csharp/src/BiliSubStudio.Core/Hardware/HardwareService.cs:121`
- **`TryReadMemory`** (Method) — `csharp/src/BiliSubStudio.Core/Hardware/HardwareService.cs:141`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `TryRead` | Method | `csharp/src/BiliSubStudio.Core/Hardware/HardwareService.cs` | 121 |
| `TryReadMemory` | Method | `csharp/src/BiliSubStudio.Core/Hardware/HardwareService.cs` | 141 |
| `cuDeviceGet` | Method | `csharp/src/BiliSubStudio.Core/Hardware/HardwareService.cs` | 177 |
| `cuDeviceGetCount` | Method | `csharp/src/BiliSubStudio.Core/Hardware/HardwareService.cs` | 176 |
| `cuDeviceGetName` | Method | `csharp/src/BiliSubStudio.Core/Hardware/HardwareService.cs` | 178 |
| `cuDeviceTotalMem_v2` | Method | `csharp/src/BiliSubStudio.Core/Hardware/HardwareService.cs` | 179 |
| `cuDriverGetVersion` | Method | `csharp/src/BiliSubStudio.Core/Hardware/HardwareService.cs` | 175 |
| `cuInit` | Method | `csharp/src/BiliSubStudio.Core/Hardware/HardwareService.cs` | 174 |
| `nvmlDeviceGetHandleByIndex_v2` | Method | `csharp/src/BiliSubStudio.Core/Hardware/HardwareService.cs` | 182 |
| `nvmlDeviceGetMemoryInfo` | Method | `csharp/src/BiliSubStudio.Core/Hardware/HardwareService.cs` | 183 |
| `nvmlInit_v2` | Method | `csharp/src/BiliSubStudio.Core/Hardware/HardwareService.cs` | 180 |
| `nvmlShutdown` | Method | `csharp/src/BiliSubStudio.Core/Hardware/HardwareService.cs` | 181 |

## Execution Flows

| Flow | Type | Steps |
|------|------|-------|
| `RecognizeSingleAsync → CuDeviceGet` | cross_community | 7 |
| `RecognizeSingleAsync → CuDeviceGetCount` | cross_community | 7 |
| `RecognizeSingleAsync → CuDeviceGetName` | cross_community | 7 |
| `RecognizeSingleAsync → CuInit` | cross_community | 7 |
| `RunGuardedLaneAsync → CuDeviceGet` | cross_community | 6 |
| `RunGuardedLaneAsync → CuDeviceGetCount` | cross_community | 6 |
| `RunGuardedLaneAsync → CuDeviceGetName` | cross_community | 6 |
| `RunGuardedLaneAsync → CuInit` | cross_community | 6 |
| `Benchmark_Click → CuDeviceGet` | cross_community | 5 |
| `Benchmark_Click → CuDeviceGetCount` | cross_community | 5 |

## How to Explore

1. `context({name: "TryRead"})` — see callers and callees
2. `query({search_query: "hardware"})` — find related execution flows
3. Read key files listed above for implementation details
4. `explain({target: "<file or symbol>"})` — persisted taint findings (source→sink data flows), when indexed with `--pdg`
