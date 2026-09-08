---
name: gitnexus-area-subtitle
description: "Skill for the Subtitle area of BiliSub-Studio. 12 symbols across 2 files."
---

# Subtitle

12 symbols | 2 files | Cohesion: 67%

## When to Use

- Working with code in `csharp/`
- Understanding how ParseCues, RenderSrt, RunAsync work
- Modifying subtitle-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `csharp/src/BiliSubStudio.Core/Subtitle/SubtitleService.cs` | CleanText, Normalize, ParseCues, ParseTimedText, TryParseTimestamp (+6) |
| `csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs` | SubtitleTimedTextContractAsync |

## Entry Points

Start here when exploring this area:

- **`ParseCues`** (Method) — `csharp/src/BiliSubStudio.Core/Subtitle/SubtitleService.cs:66`
- **`RenderSrt`** (Method) — `csharp/src/BiliSubStudio.Core/Subtitle/SubtitleService.cs:153`
- **`RunAsync`** (Method) — `csharp/src/BiliSubStudio.Core/Subtitle/SubtitleService.cs:32`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `ParseCues` | Method | `csharp/src/BiliSubStudio.Core/Subtitle/SubtitleService.cs` | 66 |
| `RenderSrt` | Method | `csharp/src/BiliSubStudio.Core/Subtitle/SubtitleService.cs` | 153 |
| `RunAsync` | Method | `csharp/src/BiliSubStudio.Core/Subtitle/SubtitleService.cs` | 32 |
| `CleanText` | Method | `csharp/src/BiliSubStudio.Core/Subtitle/SubtitleService.cs` | 232 |
| `Normalize` | Method | `csharp/src/BiliSubStudio.Core/Subtitle/SubtitleService.cs` | 215 |
| `ParseTimedText` | Method | `csharp/src/BiliSubStudio.Core/Subtitle/SubtitleService.cs` | 114 |
| `TryParseTimestamp` | Method | `csharp/src/BiliSubStudio.Core/Subtitle/SubtitleService.cs` | 139 |
| `SubtitleTimedTextContractAsync` | Method | `csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs` | 683 |
| `FetchAsync` | Method | `csharp/src/BiliSubStudio.Core/Subtitle/SubtitleService.cs` | 166 |
| `FormatSrtTime` | Method | `csharp/src/BiliSubStudio.Core/Subtitle/SubtitleService.cs` | 236 |
| `RenderText` | Method | `csharp/src/BiliSubStudio.Core/Subtitle/SubtitleService.cs` | 230 |
| `WriteAtomicAsync` | Method | `csharp/src/BiliSubStudio.Core/Subtitle/SubtitleService.cs` | 242 |

## Execution Flows

| Flow | Type | Steps |
|------|------|-------|
| `StartSubtitle → BvidRegex` | cross_community | 6 |
| `RunAsync → SendBytesAsync` | cross_community | 6 |
| `RunAsync → PartRegex` | cross_community | 5 |
| `RunAsync → Compact` | cross_community | 5 |
| `RunAsync → ReadCookieHeaderAsync` | cross_community | 4 |
| `RunAsync → ValidateUrl` | cross_community | 3 |
| `StartSubtitle → Set` | cross_community | 3 |

## How to Explore

1. `context({name: "ParseCues"})` — see callers and callees
2. `query({search_query: "subtitle"})` — find related execution flows
3. Read key files listed above for implementation details
4. `explain({target: "<file or symbol>"})` — persisted taint findings (source→sink data flows), when indexed with `--pdg`
