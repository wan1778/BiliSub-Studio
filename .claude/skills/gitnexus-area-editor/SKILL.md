---
name: gitnexus-area-editor
description: "Skill for the Editor area of BiliSub-Studio. 296 symbols across 28 files."
---

# Editor

296 symbols | 28 files | Cohesion: 68%

## When to Use

- Working with code in `csharp/`
- Understanding how EditorRegionDocument, LocalSubtitleTranslationService, TranslateAsync work
- Modifying editor-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `csharp/src/BiliSubStudio.Core/Editor/LocalSubtitleTranslationService.cs` | PushLive, SetLive, CountAdaptiveTranslationBatches, CreateAdaptiveTranslationBatch, CreateBatches (+54) |
| `csharp/src/BiliSubStudio.Core/Editor/VideoEditorService.cs` | BuildPreviewSlice, RegionEnable, Even, BuildPreviewArguments, CreatePreviewSegmentAsync (+28) |
| `csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs` | EditorRegionDocument, Add, BeginChange, EnsureIdentity, Redo (+19) |
| `csharp/src/BiliSubStudio.Core/Editor/LocalAsrService.cs` | New, ExtractAudioAsync, LastLine, LoadCheckpointAsync, SrtTime (+17) |
| `csharp/src/BiliSubStudio.Core/Editor/LocalTtsInstaller.cs` | Progress, DownloadVerifiedAsync, EnsureWorkerAsync, FileMatchesStamp, HashAsync (+13) |
| `csharp/src/BiliSubStudio.Core/Editor/LocalTtsService.cs` | CleanupRunAsync, GenerateCuesAsync, GetInt, GetString, HashAsync (+13) |
| `csharp/src/BiliSubStudio.Core/Editor/VietnameseTtsTextNormalizer.cs` | DateRegex, DecimalRegex, IntegerRegex, Normalize, NumberExpression (+7) |
| `csharp/src/BiliSubStudio.Core/Editor/LocalAsrInstaller.cs` | DownloadVerifiedAsync, EnsureWorkerAsync, HashAsync, TryDelete, WriteRuntimeManifestAsync (+7) |
| `csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs` | EditorDeleteContractAsync, EditorDocumentContractAsync, EditorRedoContractAsync, EditorRegionMoveContractAsync, EditorRegionPresetContractAsync (+5) |
| `csharp/src/BiliSubStudio.Core/Editor/EditorSpeechAnalysis.cs` | NormalizeVoiceClass, ResolveVoice, LowerBoundPrefixEnd, LowerBoundSegmentStart, LowerBoundWord (+5) |

## Entry Points

Start here when exploring this area:

- **`EditorRegionDocument`** (Class) — `csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs:932`
- **`LocalSubtitleTranslationService`** (Class) — `csharp/src/BiliSubStudio.Core/Editor/LocalSubtitleTranslationService.cs:45`
- **`TranslateAsync`** (Method) — `csharp/src/BiliSubStudio.Core/Editor/LocalSubtitleTranslationService.cs:154`
- **`New`** (Method) — `csharp/src/BiliSubStudio.Core/Editor/LocalSubtitleTranslationService.cs:1297`
- **`Add`** (Method) — `csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs:968`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `EditorRegionDocument` | Class | `csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs` | 932 |
| `LocalSubtitleTranslationService` | Class | `csharp/src/BiliSubStudio.Core/Editor/LocalSubtitleTranslationService.cs` | 45 |
| `TranslateAsync` | Method | `csharp/src/BiliSubStudio.Core/Editor/LocalSubtitleTranslationService.cs` | 154 |
| `New` | Method | `csharp/src/BiliSubStudio.Core/Editor/LocalSubtitleTranslationService.cs` | 1297 |
| `Add` | Method | `csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs` | 968 |
| `BeginChange` | Method | `csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs` | 956 |
| `Redo` | Method | `csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs` | 996 |
| `RemoveSelected` | Method | `csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs` | 985 |
| `ReplaceSelected` | Method | `csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs` | 975 |
| `Reset` | Method | `csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs` | 945 |
| `Select` | Method | `csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs` | 954 |
| `Undo` | Method | `csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs` | 995 |
| `PrepareAsync` | Method | `csharp/src/BiliSubStudio.Core/Editor/LocalTtsInstaller.cs` | 73 |
| `PrepareAsync` | Method | `csharp/src/BiliSubStudio.Core/Editor/LocalTtsInstaller.cs` | 75 |
| `New` | Method | `csharp/src/BiliSubStudio.Core/Editor/LocalAsrService.cs` | 502 |
| `TranscribeAsync` | Method | `csharp/src/BiliSubStudio.Core/Editor/LocalAsrService.cs` | 53 |
| `Normalize` | Method | `csharp/src/BiliSubStudio.Core/Editor/VietnameseTtsTextNormalizer.cs` | 37 |
| `BuildVideoEncoderArguments` | Method | `csharp/src/BiliSubStudio.Core/Editor/EditorExportSettings.cs` | 89 |
| `BuildVideoGraph` | Method | `csharp/src/BiliSubStudio.Core/Editor/EditorExportSettings.cs` | 68 |
| `Normalize` | Method | `csharp/src/BiliSubStudio.Core/Editor/EditorExportSettings.cs` | 28 |

## Execution Flows

| Flow | Type | Steps |
|------|------|-------|
| `GenerateSampleAsync → Kill` | cross_community | 10 |
| `GenerateCuesAsync → Lease` | cross_community | 9 |
| `PrepareAsync → Kill` | cross_community | 8 |
| `PrepareAsync → Lease` | cross_community | 8 |
| `GenerateSampleAsync → Lease` | cross_community | 8 |
| `PrepareAsync → BuildStartInfo` | cross_community | 7 |
| `GenerateSampleAsync → ManagedPythonPatch` | cross_community | 7 |
| `GenerateSampleAsync → ProcessResult` | cross_community | 7 |
| `GenerateSampleAsync → Kill` | cross_community | 7 |
| `GenerateSampleAsync → TryDelete` | cross_community | 7 |

## How to Explore

1. `context({name: "EditorRegionDocument"})` — see callers and callees
2. `query({search_query: "editor"})` — find related execution flows
3. Read key files listed above for implementation details
4. `explain({target: "<file or symbol>"})` — persisted taint findings (source→sink data flows), when indexed with `--pdg`
