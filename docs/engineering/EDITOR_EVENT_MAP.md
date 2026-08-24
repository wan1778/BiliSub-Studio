# AUDIT-01 — Editor Event Map

Status: **PASS (static event ownership audit)**
Branch checkpoint audited: `f0f6cbd468c3df6afca88182b1b1967ea54dd8e4`
Scope: inventory and regression gate only. **No Editor UI behavior or production handler was changed.**

## Result

The Editor currently uses two binding surfaces:

1. event attributes declared in `EditorPage.xaml`;
2. runtime subscriptions installed once from `BindStaticUiShell()` plus Page/player lifecycle subscriptions.

At the audited checkpoint:

- XAML event bindings: **52**;
- runtime user-UI bindings: **24**;
- user-facing `Click` bindings: **37**;
- duplicate `(control, event)` bindings: **0**;
- event handlers called from other event handlers: **0** under the AUDIT-01 static gate;
- Page lifecycle bindings: **3** (`Loaded`, `LayoutUpdated`, `Unloaded`);
- startup-smoke-only layout binding: **1** (`WorkspaceGrid.SizeChanged`);
- MediaPlayer event bindings: **3**, each paired with one unsubscribe;
- fullscreen property callback owner: **1 register + 1 unregister**;
- confirmed dead compatibility handlers: **2** — `InspectorMode_Click`, `Refresh_Click`.

The dead handlers are recorded only. Removing them is a cleanup task and is deliberately outside AUDIT-01.

## Binding ownership

```text
EditorPage.xaml
  -> direct XAML event handler
  -> one handler implementation

EditorPage constructor
  -> Loaded / LayoutUpdated / Unloaded
  -> one lifecycle handler each

EditorPage_Loaded
  -> guarded by _editorCoreInitialized
  -> BindStaticUiShell() exactly once
     -> tool-selector Click bindings
     -> image/logo bindings
     -> auto-preview/output bindings

EditorPlaybackController.CreatePlayer()
  -> PositionChanged / MediaEnded / MediaFailed
  -> DisposePlayer() unsubscribes the same three handlers

EditorPlaybackController.EnsureFullscreenTracking()
  -> RegisterPropertyChangedCallback(IsFullWindow)
  -> ClearFullscreenTracking() unregisters it
```

The important guard is `_editorCoreInitialized`: repeated `Loaded` events do not install the runtime shell bindings a second time.

## User-facing Click map

| Control | Binding | Handler | Handler count |
|---|---|---|---:|
| `OpenVideoButton` | XAML | `OpenVideo_Click` | 1 |
| `PlayerPlayPauseButton` | XAML | `PlayerPlayPause_Click` | 1 |
| `FullscreenButton` | XAML | `Fullscreen_Click` | 1 |
| `ImportSrtButton` | XAML | `ImportSubtitle_Click` | 1 |
| `PrepareAiButton` | XAML | `PrepareAi_Click` | 1 |
| `TranslateButton` | XAML | `Translate_Click` | 1 |
| `CancelTranslationButton` | XAML | `CancelTranslation_Click` | 1 |
| `OpenTranslatedSrtButton` | XAML | `OpenTranslatedSrt_Click` | 1 |
| `SaveKaraokeAssButton` | XAML | `SaveKaraokeAss_Click` | 1 |
| `SubtitleSaveCueButton` | XAML | `SubtitleSaveCue_Click` | 1 |
| `SubtitleRetranslateCueButton` | XAML | `SubtitleRetranslateCue_Click` | 1 |
| `SubtitleSaveSrtButton` | XAML | `SubtitleSaveSrt_Click` | 1 |
| `EditorUseCurrentStartButton` | XAML | `EditorUseCurrentStart_Click` | 1 |
| `EditorUseCurrentEndButton` | XAML | `EditorUseCurrentEnd_Click` | 1 |
| `AddRegionButton` | XAML | `AddRegion_Click` | 1 |
| `SubtitlePresetButton` | XAML | `SubtitlePreset_Click` | 1 |
| `WatermarkPresetButton` | XAML | `WatermarkPreset_Click` | 1 |
| `UndoButton` | XAML | `Undo_Click` | 1 |
| `RedoButton` | XAML | `Redo_Click` | 1 |
| `RemoveRegionButton` | XAML | `RemoveRegion_Click` | 1 |
| `CreateAsrButton` | XAML | `CreateAsr_Click` | 1 |
| `GenerateTtsButton` | XAML | `GenerateTts_Click` | 1 |
| `CancelVoiceButton` | XAML | `CancelVoice_Click` | 1 |
| `RenderButton` | XAML | `Render_Click` | 1 |
| `CancelButton` | XAML | `Cancel_Click` | 1 |
| `SubtitleModeButton` | runtime | `ShellTool_Click` | 1 |
| `BlurModeButton` | runtime | `ShellTool_Click` | 1 |
| `AudioModeButton` | runtime | `ShellTool_Click` | 1 |
| `VoiceModeButton` | runtime | `ShellTool_Click` | 1 |
| `ImageModeButton` | runtime | `ShellTool_Click` | 1 |
| `ExportModeButton` | runtime | `ShellTool_Click` | 1 |
| `AddImageButton` | runtime | `AddImage_Click` | 1 |
| `RemoveImageButton` | runtime | `RemoveImage_Click` | 1 |
| `ImageTopLeftButton` | runtime | `ImageTopLeft_Click` | 1 |
| `ImageTopRightButton` | runtime | `ImageTopRight_Click` | 1 |
| `EditorChooseOutputButton` | runtime | `EditorChooseOutput_Click` | 1 |
| `EditorOpenOutputButton` | runtime | `EditorOpenOutput_Click` | 1 |

Every user-facing click above has **one binding**. `ShellTool_Click` is intentionally shared by six tool-selector buttons, but each individual button is subscribed exactly once.

## Non-Click UI event map

| Controls | Event | Binding | Handler | Count per control/event |
|---|---|---|---|---:|
| `EditorPage` | `KeyDown` | XAML | `Page_KeyDown` | 1 |
| `Overlay` | pointer x4 | XAML | matching `Overlay_*` handlers | 1 |
| `Timeline` | `ValueChanged` | XAML | `Timeline_ValueChanged` | 1 |
| `PreviewMuteToggle` | `Toggled` | XAML | `PreviewMute_Toggled` | 1 |
| `PreviewVolumeSlider` | `ValueChanged` | XAML | `PreviewVolume_ValueChanged` | 1 |
| `SubtitleCueList` | `SelectionChanged` | XAML | `SubtitleCueList_SelectionChanged` | 1 |
| `SubtitleSourceEdit`, `SubtitleVietnameseEdit` | `TextChanged` | XAML | `SubtitleManualText_TextChanged` | 1 each |
| `SubtitleLockToggle` | `Toggled` | XAML | `SubtitleLock_Toggled` | 1 |
| `RegionXBox`, `RegionYBox`, `RegionWidthBox`, `RegionHeightBox` | `ValueChanged` | XAML | `RegionCoordinates_ValueChanged` | 1 each |
| `EffectBox` | `SelectionChanged` | XAML | `EffectBox_SelectionChanged` | 1 |
| `StrengthBox` | `ValueChanged` | XAML | `EffectStrength_ValueChanged` | 1 |
| `WholeToggle` | `Toggled` | XAML | `WholeToggle_Toggled` | 1 |
| `StartBox`, `EndBox` | `ValueChanged` | XAML | `EditInput_ValueChanged` | 1 each |
| `RegionList` | `SelectionChanged` | XAML | `RegionList_SelectionChanged` | 1 |
| `SourceAudioModeBox` | `SelectionChanged` | XAML | `SourceAudioMode_SelectionChanged` | 1 |
| `SourceAudioGainSlider` | `ValueChanged` | XAML | `SourceAudioGain_ValueChanged` | 1 |
| `KaraokeToggle` | `Toggled` | XAML | `Karaoke_Toggled` | 1 |
| `CurrentCueVoiceBox` | `SelectionChanged` | XAML | `CurrentCueVoice_SelectionChanged` | 1 |
| `FileNameBox` | `TextChanged` | XAML | `FileNameBox_TextChanged` | 1 |
| `ImageSourceList` | `SelectionChanged` | runtime | `ImageList_SelectionChanged` | 1 |
| `ImageOverlayCanvas` | pointer x4 + `SizeChanged` | runtime | matching `ImageOverlay_*` handlers | 1 each |
| `ImageXBox`, `ImageYBox`, `ImageWidthBox`, `ImageHeightBox` | `ValueChanged` | runtime | `ImageGeometry_ValueChanged` | 1 each |
| `ImageOpacitySlider` | `ValueChanged` | runtime | `ImageOpacity_ValueChanged` | 1 |
| `EditorAutoCompositeToggle` | `Toggled` | runtime | `EditorAutoComposite_Toggled` | 1 |

## First-level call map by feature

### Source

```text
OpenVideoButton.Click
  -> OpenVideo_Click
  -> OpenVideoAsync
  -> pick candidate
  -> probe candidate before state mutation
  -> load candidate project
  -> save old source state once
  -> dispose old preview once
  -> hydrate project/document/audio/subtitle/voice/image state
```

### Player

```text
PlayerPlayPauseButton.Click
  -> PlayerPlayPause_Click
  -> EditorPlaybackController.ToggleAsync

FullscreenButton.Click
  -> Fullscreen_Click
  -> EditorPlaybackController.ToggleFullscreenAsync

Timeline.ValueChanged
  -> Timeline_ValueChanged
  -> seek/update frame path

PreviewMuteToggle / PreviewVolumeSlider
  -> AUDIO-01 handlers
  -> EditorPlaybackController monitor-audio owner
```

### Tool selector

```text
six tool buttons
  -> ShellTool_Click
  -> SelectShellTool
  -> exactly one Details panel visible
```

`InspectorMode_Click` is an older unbound handler and is not part of the active click path.

### Subtitle

```text
Import / prepare AI / Vietsub / cancel / open output / save ASS
  -> one XAML handler each

cue list / text / lock / save / retranslate / save SRT
  -> one XAML handler each
  -> subtitle state helpers
```

### Blur / Mosaic / Cover

```text
geometry inputs
  -> RegionCoordinates_ValueChanged

effect / strength / whole-video / timed-range inputs
  -> their single XAML handlers
  -> document/geometry owner

Undo / Redo / Delete / presets
  -> one XAML Click handler each
```

### Source Audio

```text
SourceAudioModeBox.SelectionChanged
SourceAudioGainSlider.ValueChanged
  -> UpdateAudioSettingsFromUi
  -> project-owned _audioSettings
  -> preview-composite refresh / project save
```

This remains separate from AUDIO-01 monitor mute/volume.

### Voice

```text
CreateAsrButton / GenerateTtsButton / CancelVoiceButton
  -> one XAML Click handler each

KaraokeToggle / CurrentCueVoiceBox
  -> one XAML state-change handler each
```

### Image / Logo

```text
runtime bindings installed once by BindStaticUiShell
  -> Add/Remove/Corner Click handlers
  -> list selection
  -> geometry/opacity handlers
  -> pointer drag handlers
  -> image sidecar + overlay owner
```

### Export

```text
EditorChooseOutputButton / EditorOpenOutputButton
  -> runtime Click handlers installed once

RenderButton.Click
  -> Render_Click
  -> CurrentEditRequest(...)
  -> StartEditor(...)

CancelButton.Click
  -> Cancel_Click
```

## Confirmed audit findings

### PASS — no double-bound control/event

No active control currently has the same event wired both in XAML and runtime code.

### PASS — runtime shell binding is guarded

`BindStaticUiShell()` is reached through `EditorPage_Loaded`, but `_editorCoreInitialized` prevents a second subscription pass if the Page is loaded again.

### PASS — MediaPlayer handlers have disposal symmetry

`PositionChanged`, `MediaEnded`, and `MediaFailed` are subscribed by `CreatePlayer()` and unsubscribed by `DisposePlayer()`.

### ATTENTION — mixed binding style remains

The Editor still mixes XAML bindings and runtime bindings. This is not itself a duplicate-handler defect at this checkpoint, but it makes ownership harder to inspect manually. AUDIT-01 records this fact; it does **not** migrate bindings.

### ATTENTION — dead compatibility handlers remain

- `InspectorMode_Click`
- `Refresh_Click`

They are not active event owners. Removing them requires a later cleanup task.

## Regression gate

`csharp/scripts/verify_editor_event_map.py` fails when:

- the audited XAML event inventory drifts without updating the map;
- the same `(control, event)` appears twice in XAML;
- a named XAML control/event is also rebound at runtime;
- runtime shell bindings lose their one-time initialization guard;
- any audited runtime UI binding appears zero or multiple times;
- a user-facing click count changes without updating AUDIT-01;
- an event handler gains a second implementation or is called directly from another event handler;
- MediaPlayer subscribe/unsubscribe symmetry breaks;
- fullscreen callback register/unregister ownership breaks.

This gate is audit infrastructure only; it does not change Editor behavior.
