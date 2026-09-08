---
name: gitnexus-area-bilisubstudio-core-contracttests
description: "Skill for the BiliSubStudio.Core.ContractTests area of BiliSub-Studio. 650 symbols across 113 files."
---

# BiliSubStudio.Core.ContractTests

650 symbols | 113 files | Cohesion: 75%

## When to Use

- Working with code in `csharp/`
- Understanding how EditorPreviewRequestCoordinator, EditorSubtitleStylePolicy, TimelineCommandHistory work
- Modifying bilisubstudio.core.contracttests-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs` | Arguments, AssertGraphGeometry, AssertProjectedBoundary, Graph, Constant (+78) |
| `csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs` | ArchiveCorruptState, ArchiveSourceChanged, CreateFreshProject, DeleteAsync, DeleteDirectory (+20) |
| `csharp/src/BiliSubStudio.Core/Timeline/TimelineEditingController.cs` | Apply, ApplyToProject, CurrentState, DeleteSelected, FindMissingMediaAssets (+15) |
| `csharp/tests/BiliSubStudio.Core.ContractTests/EditorTtsTimingPropertyContract.cs` | Analysis, AssertManifestInvariants, BuildWholeCueMethod, Cue, DoubleProperty (+14) |
| `csharp/tests/BiliSubStudio.Core.ContractTests/TimelineFidelityContract.cs` | AssTextMatchesClassicBurn, EffectMappingUsesSourceTime, LogoMappingUsesSourceGeometry, SingleClipPlan, Check (+11) |
| `csharp/tests/BiliSubStudio.Core.ContractTests/OcrLiveTailContract.cs` | CheckAsync, CheckAsync, Release, WaitEntered, Check (+11) |
| `csharp/src/BiliSubStudio.Core/Application/BiliSubApplication.cs` | DeleteEditorProjectAsync, GetEditorProjectPath, LoadEditorCueSpeechTimingAsync, RelinkEditorProjectSourceAsync, RunJobAsync (+10) |
| `csharp/tests/BiliSubStudio.Core.ContractTests/EditorLicensedVoiceProfileContract.cs` | VerifyEditorProjectAsync, Cue, Constant, Equal, True (+8) |
| `csharp/tests/BiliSubStudio.Core.ContractTests/TimelineMuxContract.cs` | CleanPlan, Check, ColorDominance, ConcatAndProbeArgumentsAreExact, FieldMuxJoinsMixedFormatSegmentsAsync (+7) |
| `csharp/tests/BiliSubStudio.Core.ContractTests/TimelinePreviewPlaybackContract.cs` | Project, RecordingConcatenator, RecordingSegmentRenderer, RejectingMixer, Check (+7) |

## Entry Points

Start here when exploring this area:

- **`EditorPreviewRequestCoordinator`** (Class) — `csharp/src/BiliSubStudio.Core/Editor/EditorPreviewRequestCoordinator.cs:2`
- **`EditorSubtitleStylePolicy`** (Class) — `csharp/src/BiliSubStudio.Core/Editor/EditorSubtitleStyle.cs:19`
- **`TimelineCommandHistory`** (Class) — `csharp/src/BiliSubStudio.Core/Timeline/TimelineSequenceOps.cs:119`
- **`TimelineAudioEditHistory`** (Class) — `csharp/src/BiliSubStudio.Core/Timeline/TimelineAudio.cs:628`
- **`OcrLiveTailReconciler`** (Class) — `csharp/src/BiliSubStudio.Core/Ocr/OcrLiveTailReconciler.cs:11`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `EditorPreviewRequestCoordinator` | Class | `csharp/src/BiliSubStudio.Core/Editor/EditorPreviewRequestCoordinator.cs` | 2 |
| `EditorSubtitleStylePolicy` | Class | `csharp/src/BiliSubStudio.Core/Editor/EditorSubtitleStyle.cs` | 19 |
| `TimelineCommandHistory` | Class | `csharp/src/BiliSubStudio.Core/Timeline/TimelineSequenceOps.cs` | 119 |
| `TimelineAudioEditHistory` | Class | `csharp/src/BiliSubStudio.Core/Timeline/TimelineAudio.cs` | 628 |
| `OcrLiveTailReconciler` | Class | `csharp/src/BiliSubStudio.Core/Ocr/OcrLiveTailReconciler.cs` | 11 |
| `ProductionPixelComparer` | Class | `csharp/src/BiliSubStudio.Core/Ocr/OcrPixelComparer.cs` | 21 |
| `OwnedProcessGroup` | Class | `csharp/src/BiliSubStudio.Core/Processes/OwnedProcessGroup.cs` | 5 |
| `ProcessRunner` | Class | `csharp/src/BiliSubStudio.Core/Processes/ProcessRunner.cs` | 7 |
| `HardwareService` | Class | `csharp/src/BiliSubStudio.Core/Hardware/HardwareService.cs` | 11 |
| `TimelineFfmpegFinalMixer` | Class | `csharp/src/BiliSubStudio.Core/Timeline/TimelineFidelityMux.cs` | 400 |
| `NullFinalMixer` | Class | `csharp/tests/BiliSubStudio.Core.ContractTests/TimelineSubtitleVoiceLinkContract.cs` | 445 |
| `OcrLiveCueAccumulator` | Class | `csharp/src/BiliSubStudio.Core/Ocr/OcrLiveCueAccumulator.cs` | 4 |
| `IPixelComparer` | Interface | `csharp/src/BiliSubStudio.Core/Ocr/OcrPixelComparer.cs` | 9 |
| `ITimelineFinalMixer` | Interface | `csharp/src/BiliSubStudio.Core/Timeline/TimelineFidelityMux.cs` | 390 |
| `DeleteEditorProjectAsync` | Method | `csharp/src/BiliSubStudio.Core/Application/BiliSubApplication.cs` | 488 |
| `GetEditorProjectPath` | Method | `csharp/src/BiliSubStudio.Core/Application/BiliSubApplication.cs` | 482 |
| `LoadEditorCueSpeechTimingAsync` | Method | `csharp/src/BiliSubStudio.Core/Application/BiliSubApplication.cs` | 565 |
| `RelinkEditorProjectSourceAsync` | Method | `csharp/src/BiliSubStudio.Core/Application/BiliSubApplication.cs` | 491 |
| `EnsureBootstrapDirectories` | Method | `csharp/src/BiliSubStudio.Core/Configuration/AppPaths.cs` | 44 |
| `FromExecutableDirectory` | Method | `csharp/src/BiliSubStudio.Core/Configuration/AppPaths.cs` | 27 |

## Execution Flows

| Flow | Type | Steps |
|------|------|-------|
| `GenerateSampleAsync → Kill` | cross_community | 10 |
| `GenerateCuesAsync → Lease` | cross_community | 9 |
| `InspectAsync → TryDelete` | cross_community | 9 |
| `DeleteTextItem → ValidateId` | cross_community | 9 |
| `DeleteTextItem → IsId` | cross_community | 9 |
| `UpsertTextItem → ValidateId` | cross_community | 9 |
| `UpsertTextItem → IsId` | cross_community | 9 |
| `GenerateTts_Click → QuerySecret` | cross_community | 8 |
| `GenerateTts_Click → SecretKeyValue` | cross_community | 8 |
| `GenerateTts_Click → UserPath` | cross_community | 8 |

## How to Explore

1. `context({name: "EditorPreviewRequestCoordinator"})` — see callers and callees
2. `query({search_query: "bilisubstudio.core.contracttests"})` — find related execution flows
3. Read key files listed above for implementation details
4. `explain({target: "<file or symbol>"})` — persisted taint findings (source→sink data flows), when indexed with `--pdg`
