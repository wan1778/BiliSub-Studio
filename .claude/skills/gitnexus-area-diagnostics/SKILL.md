---
name: gitnexus-area-diagnostics
description: "Skill for the Diagnostics area of BiliSub-Studio. 12 symbols across 3 files."
---

# Diagnostics

12 symbols | 3 files | Cohesion: 88%

## When to Use

- Working with code in `csharp/`
- Understanding how Redact, SendAsync, Dispose work
- Modifying diagnostics-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `csharp/src/BiliSubStudio.Core/Diagnostics/ApplicationLog.cs` | Dispose, Flush, FlushCore, OnProcessExit, RotateIfNeeded (+1) |
| `csharp/src/BiliSubStudio.Core/Diagnostics/LogRedactor.cs` | QuerySecret, Redact, SecretKeyValue, UserPath |
| `csharp/src/BiliSubStudio.Core/Maintenance/BugReportService.cs` | SendAsync, Truncate |

## Entry Points

Start here when exploring this area:

- **`Redact`** (Method) — `csharp/src/BiliSubStudio.Core/Diagnostics/LogRedactor.cs:9`
- **`SendAsync`** (Method) — `csharp/src/BiliSubStudio.Core/Maintenance/BugReportService.cs:19`
- **`Dispose`** (Method) — `csharp/src/BiliSubStudio.Core/Diagnostics/ApplicationLog.cs:81`
- **`Flush`** (Method) — `csharp/src/BiliSubStudio.Core/Diagnostics/ApplicationLog.cs:75`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `Redact` | Method | `csharp/src/BiliSubStudio.Core/Diagnostics/LogRedactor.cs` | 9 |
| `SendAsync` | Method | `csharp/src/BiliSubStudio.Core/Maintenance/BugReportService.cs` | 19 |
| `Dispose` | Method | `csharp/src/BiliSubStudio.Core/Diagnostics/ApplicationLog.cs` | 81 |
| `Flush` | Method | `csharp/src/BiliSubStudio.Core/Diagnostics/ApplicationLog.cs` | 75 |
| `QuerySecret` | Method | `csharp/src/BiliSubStudio.Core/Diagnostics/LogRedactor.cs` | 23 |
| `SecretKeyValue` | Method | `csharp/src/BiliSubStudio.Core/Diagnostics/LogRedactor.cs` | 17 |
| `UserPath` | Method | `csharp/src/BiliSubStudio.Core/Diagnostics/LogRedactor.cs` | 20 |
| `Truncate` | Method | `csharp/src/BiliSubStudio.Core/Maintenance/BugReportService.cs` | 35 |
| `FlushCore` | Method | `csharp/src/BiliSubStudio.Core/Diagnostics/ApplicationLog.cs` | 132 |
| `OnProcessExit` | Method | `csharp/src/BiliSubStudio.Core/Diagnostics/ApplicationLog.cs` | 92 |
| `RotateIfNeeded` | Method | `csharp/src/BiliSubStudio.Core/Diagnostics/ApplicationLog.cs` | 161 |
| `WriterLoopAsync` | Method | `csharp/src/BiliSubStudio.Core/Diagnostics/ApplicationLog.cs` | 140 |

## Execution Flows

| Flow | Type | Steps |
|------|------|-------|
| `GenerateTts_Click → QuerySecret` | cross_community | 8 |
| `GenerateTts_Click → SecretKeyValue` | cross_community | 8 |
| `GenerateTts_Click → UserPath` | cross_community | 8 |
| `StartScanAsync → QuerySecret` | cross_community | 8 |
| `StartScanAsync → SecretKeyValue` | cross_community | 8 |
| `StartScanAsync → UserPath` | cross_community | 8 |
| `StartEditor → QuerySecret` | cross_community | 7 |
| `StartEditor → SecretKeyValue` | cross_community | 7 |
| `StartEditor → UserPath` | cross_community | 7 |
| `StartEditorTranslation → QuerySecret` | cross_community | 7 |

## How to Explore

1. `context({name: "Redact"})` — see callers and callees
2. `query({search_query: "diagnostics"})` — find related execution flows
3. Read key files listed above for implementation details
4. `explain({target: "<file or symbol>"})` — persisted taint findings (source→sink data flows), when indexed with `--pdg`
