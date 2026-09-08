---
name: gitnexus-area-timeline
description: "Skill for the Timeline area of BiliSub-Studio. 230 symbols across 41 files."
---

# Timeline

230 symbols | 41 files | Cohesion: 66%

## When to Use

- Working with code in `csharp/`
- Understanding how TimelineEditHistory, TimelineFeatureFlags, TimelineFfmpegConcatenator work
- Modifying timeline-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `csharp/src/BiliSubStudio.Core/Timeline/TimelineAudio.cs` | NewId, Delete, ReconcileVoices, Reorder, Split (+26) |
| `csharp/src/BiliSubStudio.Core/Timeline/TimelineItems.cs` | AttachSubtitleToClip, TimelineEditHistory, Delete, Commit, Redo (+11) |
| `csharp/src/BiliSubStudio.Core/Timeline/TimelineProcessedPreviewService.cs` | RenderAsync, CleanupCacheAsync, DeleteAsync, DeleteManagedFileAsync, EnsureManagedPath (+9) |
| `csharp/src/BiliSubStudio.Core/Timeline/TimelineExportService.cs` | RenderSegmentAsync, Build, EnsureManagedPath, DeleteManagedFileAsync, EnsureManagedPath (+7) |
| `csharp/src/BiliSubStudio.Core/Timeline/TimelineFfmpegMux.cs` | ConcatAsync, ConcatDurationUs, AtempoChain, BuildLogoChain, BuildSegment (+7) |
| `csharp/src/BiliSubStudio.Core/Timeline/TimelineFidelityMux.cs` | BuildFinalMixArguments, MixFinalAsync, BuildAssText, BuildComposedVideoFilter, MapBurnForSegment (+6) |
| `csharp/tests/BiliSubStudio.Core.ContractTests/TimelineSubtitleVoiceLinkContract.cs` | MusicAsset, VoiceAsset, FractionalSplitClampsCueToFinalOwner, GlobalCueAndVoiceStayAbsolute, ValidationRejectsStaleOrOrphanVoice (+5) |
| `csharp/src/BiliSubStudio.Core/Timeline/TimelineProjectV2.cs` | Validate, ValidateColorGrades, ValidateKeyframeSets, ValidateSpeedEnvelopes, ValidateTransitions (+4) |
| `csharp/src/BiliSubStudio.Core/Timeline/TimelineSpeedMapping.cs` | Nested, BuildAudioChain, BuildPiecewiseBody, BuildSetptsExpression, Seconds (+3) |
| `csharp/src/BiliSubStudio.Core/Timeline/TimelineProjectStoreV2.cs` | CleanupAbandonedTemporaries, IsId, ListAsync, ProjectPath, ReadStoredFileAsync (+2) |

## Entry Points

Start here when exploring this area:

- **`TimelineEditHistory`** (Class) — `csharp/src/BiliSubStudio.Core/Timeline/TimelineItems.cs:356`
- **`TimelineFeatureFlags`** (Class) — `csharp/src/BiliSubStudio.Core/Timeline/TimelineFeatureFlags.cs:8`
- **`TimelineFfmpegConcatenator`** (Class) — `csharp/src/BiliSubStudio.Core/Timeline/TimelineFfmpegMux.cs:433`
- **`NullConcatenator`** (Class) — `csharp/tests/BiliSubStudio.Core.ContractTests/TimelineSubtitleVoiceLinkContract.cs:431`
- **`TimelineFfmpegSegmentRenderer`** (Class) — `csharp/src/BiliSubStudio.Core/Timeline/TimelineFfmpegMux.cs:393`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `TimelineEditHistory` | Class | `csharp/src/BiliSubStudio.Core/Timeline/TimelineItems.cs` | 356 |
| `TimelineFeatureFlags` | Class | `csharp/src/BiliSubStudio.Core/Timeline/TimelineFeatureFlags.cs` | 8 |
| `TimelineFfmpegConcatenator` | Class | `csharp/src/BiliSubStudio.Core/Timeline/TimelineFfmpegMux.cs` | 433 |
| `NullConcatenator` | Class | `csharp/tests/BiliSubStudio.Core.ContractTests/TimelineSubtitleVoiceLinkContract.cs` | 431 |
| `TimelineFfmpegSegmentRenderer` | Class | `csharp/src/BiliSubStudio.Core/Timeline/TimelineFfmpegMux.cs` | 393 |
| `NullSegmentRenderer` | Class | `csharp/tests/BiliSubStudio.Core.ContractTests/TimelineSubtitleVoiceLinkContract.cs` | 418 |
| `TimelineProcessedPreviewService` | Class | `csharp/src/BiliSubStudio.Core/Timeline/TimelineProcessedPreviewService.cs` | 45 |
| `TimelineFfmpegProcessedPreviewRenderer` | Class | `csharp/src/BiliSubStudio.Core/Timeline/TimelineFfmpegProcessedPreviewRenderer.cs` | 9 |
| `ITimelineExportConcatenator` | Interface | `csharp/src/BiliSubStudio.Core/Timeline/TimelineFfmpegMux.cs` | 423 |
| `ITimelineExportSegmentRenderer` | Interface | `csharp/src/BiliSubStudio.Core/Timeline/TimelineExportService.cs` | 21 |
| `ITimelineProcessedPreviewGenerator` | Interface | `csharp/src/BiliSubStudio.Core/Timeline/TimelineProcessedPreviewService.cs` | 28 |
| `ITimelineProcessedPreviewRenderer` | Interface | `csharp/src/BiliSubStudio.Core/Timeline/TimelineProcessedPreviewService.cs` | 19 |
| `NewId` | Method | `csharp/src/BiliSubStudio.Core/Timeline/TimelineAudio.cs` | 84 |
| `Delete` | Method | `csharp/src/BiliSubStudio.Core/Timeline/TimelineAudio.cs` | 586 |
| `Reorder` | Method | `csharp/src/BiliSubStudio.Core/Timeline/TimelineAudio.cs` | 506 |
| `Split` | Method | `csharp/src/BiliSubStudio.Core/Timeline/TimelineAudio.cs` | 544 |
| `Trim` | Method | `csharp/src/BiliSubStudio.Core/Timeline/TimelineAudio.cs` | 523 |
| `UpdateSubtitleCue` | Method | `csharp/src/BiliSubStudio.Core/Timeline/TimelineAudio.cs` | 486 |
| `Create` | Method | `csharp/src/BiliSubStudio.Core/Timeline/TimelineAudio.cs` | 452 |
| `FramesToMicroseconds` | Method | `csharp/src/BiliSubStudio.Core/Timeline/TimelineAudio.cs` | 21 |

## Execution Flows

| Flow | Type | Steps |
|------|------|-------|
| `DeleteTextItem → ValidateId` | cross_community | 9 |
| `DeleteTextItem → IsId` | cross_community | 9 |
| `UpsertTextItem → ValidateId` | cross_community | 9 |
| `UpsertTextItem → IsId` | cross_community | 9 |
| `CreateTestProject_Click → ValidateShape` | cross_community | 8 |
| `CreateTestProject_Click → Validate` | cross_community | 8 |
| `DeleteTextItem → ValidateSha256` | cross_community | 8 |
| `DeleteTextItem → ComputeSpeechRevision` | cross_community | 8 |
| `DeleteTextItem → Validate` | cross_community | 8 |
| `ExportProject_Click → ValidateShape` | cross_community | 8 |

## How to Explore

1. `context({name: "TimelineEditHistory"})` — see callers and callees
2. `query({search_query: "timeline"})` — find related execution flows
3. Read key files listed above for implementation details
4. `explain({target: "<file or symbol>"})` — persisted taint findings (source→sink data flows), when indexed with `--pdg`
