---
name: gitnexus-area-bilisubstudio-subtitleregression
description: "Skill for the BiliSubStudio.SubtitleRegression area of BiliSub-Studio. 8 symbols across 1 files."
---

# BiliSubStudio.SubtitleRegression

8 symbols | 1 files | Cohesion: 100%

## When to Use

- Working with code in `csharp/`
- Understanding how BuildProtobufSubtitle, Bytes, BytesField work
- Modifying bilisubstudio.subtitleregression-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `csharp/tests/BiliSubStudio.SubtitleRegression/Program.cs` | BuildProtobufSubtitle, Bytes, BytesField, Concat, Json (+3) |

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `BuildProtobufSubtitle` | Method | `csharp/tests/BiliSubStudio.SubtitleRegression/Program.cs` | 160 |
| `Bytes` | Method | `csharp/tests/BiliSubStudio.SubtitleRegression/Program.cs` | 150 |
| `BytesField` | Method | `csharp/tests/BiliSubStudio.SubtitleRegression/Program.cs` | 172 |
| `Concat` | Method | `csharp/tests/BiliSubStudio.SubtitleRegression/Program.cs` | 190 |
| `Json` | Method | `csharp/tests/BiliSubStudio.SubtitleRegression/Program.cs` | 141 |
| `SendAsync` | Method | `csharp/tests/BiliSubStudio.SubtitleRegression/Program.cs` | 73 |
| `StringField` | Method | `csharp/tests/BiliSubStudio.SubtitleRegression/Program.cs` | 170 |
| `Varint` | Method | `csharp/tests/BiliSubStudio.SubtitleRegression/Program.cs` | 177 |

## How to Explore

1. `context({name: "BuildProtobufSubtitle"})` — see callers and callees
2. `query({search_query: "bilisubstudio.subtitleregression"})` — find related execution flows
3. Read key files listed above for implementation details
4. `explain({target: "<file or symbol>"})` — persisted taint findings (source→sink data flows), when indexed with `--pdg`
