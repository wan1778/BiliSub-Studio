---
name: gitnexus-area-pages
description: "Skill for the Pages area of BiliSub-Studio. 559 symbols across 54 files."
---

# Pages

559 symbols | 54 files | Cohesion: 74%

## When to Use

- Working with code in `csharp/`
- Understanding how PickVideoAsync, LoadEditorProjectAsync, HasTrim work
- Modifying pages-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml.cs` | ApplyAudioSettingsToUi, AttachSubtitleToProject, CommitSubtitleStyle, CurrentTrimRange, FileNameBox_TextChanged (+140) |
| `csharp/src/BiliSubStudio.App/Pages/TimelineEditorPage.xaml.cs` | CopyClassicProject_Click, RefreshAsync, RefreshList_Click, RelinkMedia_Click, AddTimelineHandle (+53) |
| `csharp/src/BiliSubStudio.App/Pages/OcrPage.xaml.cs` | ApplyAuthoritativeCues, ApplyCheckpointUi, ApplyRecoverableCheckpointCues, BuildRequest, Cancel_Click (+50) |
| `csharp/src/BiliSubStudio.App/Pages/EditorPage.Playback.cs` | CompletePlaybackAsync, ContinueAfterSegmentAsync, ContinueWithPrefetchedSegmentAsync, EnsureFullscreenTracking, EnterFullscreenAsync (+31) |
| `csharp/src/BiliSubStudio.App/Pages/EditorPage.Images.cs` | AddImageAsync, AddImage_Click, ArchiveImageSidecarForSourceChange, EnsureBitmapLoadedAsync, EnsureImageProjectLoadedAsync (+27) |
| `csharp/src/BiliSubStudio.App/Pages/EditorPage.ProjectList.cs` | OpenProjectItemAsync, OpenProjectMenu_Click, ProjectGridView_ItemClick, RelinkProjectItemAsync, BackToProjects_Click (+15) |
| `csharp/src/BiliSubStudio.Core/Application/BiliSubApplication.cs` | LoadEditorProjectAsync, CleanupStorage, ResetToolsAsync, CancelOcrScanAsync, InspectOcrCheckpointAsync (+11) |
| `csharp/src/BiliSubStudio.App/Pages/EditorPage.SubtitleCueEditing.cs` | LoadSelectedSubtitleCue, MarkTranslatedOutputStale, RefreshSubtitleCueEditorControls, RenderSubtitleCueList, SaveCurrentSubtitleCueAsync (+8) |
| `csharp/src/BiliSubStudio.App/Pages/SettingsPage.xaml.cs` | Cleanup_Click, ConfirmAsync, RemoveOcr_Click, ResetTools_Click, ShowErrorAsync (+7) |
| `csharp/src/BiliSubStudio.App/Pages/TimelineEditorPage.Playback.cs` | AbortAppClose, FlushForAppCloseAsync, InitializeTimelinePlayback, RefreshTimelinePreviewControls, TimelineEditorPage_Loaded (+7) |

## Entry Points

Start here when exploring this area:

- **`PickVideoAsync`** (Method) — `csharp/src/BiliSubStudio.App/Services/FilePickerService.cs:19`
- **`LoadEditorProjectAsync`** (Method) — `csharp/src/BiliSubStudio.Core/Application/BiliSubApplication.cs:467`
- **`HasTrim`** (Method) — `csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs:619`
- **`NormalizeTrim`** (Method) — `csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs:605`
- **`IsSameSource`** (Method) — `csharp/src/BiliSubStudio.Core/Editor/EditorSourceSelection.cs:12`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `PickVideoAsync` | Method | `csharp/src/BiliSubStudio.App/Services/FilePickerService.cs` | 19 |
| `LoadEditorProjectAsync` | Method | `csharp/src/BiliSubStudio.Core/Application/BiliSubApplication.cs` | 467 |
| `HasTrim` | Method | `csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs` | 619 |
| `NormalizeTrim` | Method | `csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs` | 605 |
| `IsSameSource` | Method | `csharp/src/BiliSubStudio.Core/Editor/EditorSourceSelection.cs` | 12 |
| `NormalizeCandidatePath` | Method | `csharp/src/BiliSubStudio.Core/Editor/EditorSourceSelection.cs` | 4 |
| `VoiceBlockReason` | Method | `csharp/src/BiliSubStudio.Core/Editor/EditorVietnameseSubtitleWorkflow.cs` | 10 |
| `GetFrameJpegAsync` | Method | `csharp/src/BiliSubStudio.Core/Media/MediaPreviewService.cs` | 65 |
| `ParseProbe` | Method | `csharp/src/BiliSubStudio.Core/Media/MediaPreviewService.cs` | 78 |
| `ProbeAsync` | Method | `csharp/src/BiliSubStudio.Core/Media/MediaPreviewService.cs` | 26 |
| `ProbeHasAudioAsync` | Method | `csharp/src/BiliSubStudio.Core/Media/MediaPreviewService.cs` | 46 |
| `RunAsync` | Method | `csharp/tests/BiliSubStudio.Core.ContractTests/EditorSourceSelectionContract.cs` | 6 |
| `ApplyUpdateError` | Method | `csharp/src/BiliSubStudio.App/Pages/SupportPage.xaml.cs` | 57 |
| `ApplyUpdateInfo` | Method | `csharp/src/BiliSubStudio.App/Pages/SupportPage.xaml.cs` | 42 |
| `InitializeAsync` | Method | `csharp/src/BiliSubStudio.App/ViewModels/SettingsViewModel.cs` | 67 |
| `CleanupStorage` | Method | `csharp/src/BiliSubStudio.Core/Application/BiliSubApplication.cs` | 848 |
| `ResetToolsAsync` | Method | `csharp/src/BiliSubStudio.Core/Application/BiliSubApplication.cs` | 858 |
| `At` | Method | `csharp/src/BiliSubStudio.Core/Authentication/QrMatrixEncoder.cs` | 6 |
| `Error` | Method | `csharp/src/BiliSubStudio.Core/Diagnostics/ApplicationLog.cs` | 55 |
| `Error` | Method | `csharp/src/BiliSubStudio.Core/Diagnostics/ApplicationLog.cs` | 58 |

## Execution Flows

| Flow | Type | Steps |
|------|------|-------|
| `DeleteTextItem → ValidateId` | cross_community | 9 |
| `DeleteTextItem → IsId` | cross_community | 9 |
| `ToggleAsync → RequestCancellation` | cross_community | 8 |
| `GenerateTts_Click → QuerySecret` | cross_community | 8 |
| `GenerateTts_Click → SecretKeyValue` | cross_community | 8 |
| `GenerateTts_Click → UserPath` | cross_community | 8 |
| `Cancel_Click → IsChinesePunctuation` | cross_community | 8 |
| `Cancel_Click → IsHan` | cross_community | 8 |
| `DeleteTextItem → ValidateSha256` | cross_community | 8 |
| `DeleteTextItem → ComputeSpeechRevision` | cross_community | 8 |

## How to Explore

1. `context({name: "PickVideoAsync"})` — see callers and callees
2. `query({search_query: "pages"})` — find related execution flows
3. Read key files listed above for implementation details
4. `explain({target: "<file or symbol>"})` — persisted taint findings (source→sink data flows), when indexed with `--pdg`
