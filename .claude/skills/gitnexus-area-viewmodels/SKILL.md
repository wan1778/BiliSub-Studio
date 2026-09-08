---
name: gitnexus-area-viewmodels
description: "Skill for the ViewModels area of BiliSub-Studio. 21 symbols across 10 files."
---

# ViewModels

21 symbols | 10 files | Cohesion: 86%

## When to Use

- Working with code in `csharp/`
- Understanding how BindableBase, EditorProjectListItem, MediaQueueItem work
- Modifying viewmodels-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `csharp/src/BiliSubStudio.App/ViewModels/SettingsViewModel.cs` | Apply, ChooseOutputDirectoryAsync, ExecuteUpdateAsync, FormatBytes, OpenOutputDirectoryAsync (+4) |
| `csharp/src/BiliSubStudio.App/ViewModels/AsyncCommand.cs` | CanExecute, Execute, RaiseCanExecuteChanged |
| `csharp/src/BiliSubStudio.Core/Application/ISettingsApplicationService.cs` | RefreshAsync, SetOutputDirectoryAsync |
| `csharp/src/BiliSubStudio.App/Pages/EditorPage.ParityFixes.cs` | EditorOpenOutput_Click |
| `csharp/src/BiliSubStudio.App/Services/FolderPickerService.cs` | OpenFolderAsync |
| `csharp/src/BiliSubStudio.App/Services/IFolderPickerService.cs` | OpenFolderAsync |
| `csharp/src/BiliSubStudio.Core/Application/SettingsApplicationService.cs` | RefreshAsync |
| `csharp/src/BiliSubStudio.App/ViewModels/BindableBase.cs` | BindableBase |
| `csharp/src/BiliSubStudio.App/ViewModels/EditorProjectListItem.cs` | EditorProjectListItem |
| `csharp/src/BiliSubStudio.App/ViewModels/MediaQueueItem.cs` | MediaQueueItem |

## Entry Points

Start here when exploring this area:

- **`BindableBase`** (Class) — `csharp/src/BiliSubStudio.App/ViewModels/BindableBase.cs:5`
- **`EditorProjectListItem`** (Class) — `csharp/src/BiliSubStudio.App/ViewModels/EditorProjectListItem.cs:6`
- **`MediaQueueItem`** (Class) — `csharp/src/BiliSubStudio.App/ViewModels/MediaQueueItem.cs:4`
- **`SettingsViewModel`** (Class) — `csharp/src/BiliSubStudio.App/ViewModels/SettingsViewModel.cs:6`
- **`OpenFolderAsync`** (Method) — `csharp/src/BiliSubStudio.App/Services/FolderPickerService.cs:23`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `BindableBase` | Class | `csharp/src/BiliSubStudio.App/ViewModels/BindableBase.cs` | 5 |
| `EditorProjectListItem` | Class | `csharp/src/BiliSubStudio.App/ViewModels/EditorProjectListItem.cs` | 6 |
| `MediaQueueItem` | Class | `csharp/src/BiliSubStudio.App/ViewModels/MediaQueueItem.cs` | 4 |
| `SettingsViewModel` | Class | `csharp/src/BiliSubStudio.App/ViewModels/SettingsViewModel.cs` | 6 |
| `OpenFolderAsync` | Method | `csharp/src/BiliSubStudio.App/Services/FolderPickerService.cs` | 23 |
| `RefreshAsync` | Method | `csharp/src/BiliSubStudio.Core/Application/SettingsApplicationService.cs` | 15 |
| `CanExecute` | Method | `csharp/src/BiliSubStudio.App/ViewModels/AsyncCommand.cs` | 10 |
| `Execute` | Method | `csharp/src/BiliSubStudio.App/ViewModels/AsyncCommand.cs` | 12 |
| `RaiseCanExecuteChanged` | Method | `csharp/src/BiliSubStudio.App/ViewModels/AsyncCommand.cs` | 32 |
| `EditorOpenOutput_Click` | Method | `csharp/src/BiliSubStudio.App/Pages/EditorPage.ParityFixes.cs` | 36 |
| `OpenFolderAsync` | Method | `csharp/src/BiliSubStudio.App/Services/IFolderPickerService.cs` | 6 |
| `Apply` | Method | `csharp/src/BiliSubStudio.App/ViewModels/SettingsViewModel.cs` | 185 |
| `ChooseOutputDirectoryAsync` | Method | `csharp/src/BiliSubStudio.App/ViewModels/SettingsViewModel.cs` | 112 |
| `ExecuteUpdateAsync` | Method | `csharp/src/BiliSubStudio.App/ViewModels/SettingsViewModel.cs` | 162 |
| `FormatBytes` | Method | `csharp/src/BiliSubStudio.App/ViewModels/SettingsViewModel.cs` | 229 |
| `OpenOutputDirectoryAsync` | Method | `csharp/src/BiliSubStudio.App/ViewModels/SettingsViewModel.cs` | 132 |
| `RefreshAsync` | Method | `csharp/src/BiliSubStudio.App/ViewModels/SettingsViewModel.cs` | 144 |
| `SetStatus` | Method | `csharp/src/BiliSubStudio.App/ViewModels/SettingsViewModel.cs` | 215 |
| `RefreshAsync` | Method | `csharp/src/BiliSubStudio.Core/Application/ISettingsApplicationService.cs` | 6 |
| `SetOutputDirectoryAsync` | Method | `csharp/src/BiliSubStudio.Core/Application/ISettingsApplicationService.cs` | 8 |

## Execution Flows

| Flow | Type | Steps |
|------|------|-------|
| `Cleanup_Click → FormatBytes` | cross_community | 4 |
| `SetThemeAsync → FormatBytes` | cross_community | 4 |
| `SetUpdateCheckAsync → FormatBytes` | cross_community | 4 |
| `Cleanup_Click → SetStatus` | cross_community | 3 |
| `SetThemeAsync → SetStatus` | cross_community | 3 |
| `SetUpdateCheckAsync → SetStatus` | cross_community | 3 |
| `RefreshAsync → FormatBytes` | intra_community | 3 |
| `ChooseOutputDirectoryAsync → ValidateWritableDirectoryAsync` | cross_community | 3 |
| `ChooseOutputDirectoryAsync → UpdateAsync` | cross_community | 3 |

## How to Explore

1. `context({name: "BindableBase"})` — see callers and callees
2. `query({search_query: "viewmodels"})` — find related execution flows
3. Read key files listed above for implementation details
4. `explain({target: "<file or symbol>"})` — persisted taint findings (source→sink data flows), when indexed with `--pdg`
