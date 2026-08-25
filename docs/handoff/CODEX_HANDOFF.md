# Codex handoff — BiliSub Studio

- Current main/base SHA: `717ad3ffa9e39fb17a80107a0c4a1c2485e9e640`
- Current branch: `editor-transport-layout` (created from `origin/main`)
- Current task source commit: `0caf02e0d8dd98f43e441cc6c1e879877bb47109`
- Current verification commits: `435dc399275abeff4af0ee18ebf6b585e0aaebb9` and `b6fe1daa3b86a1768725edaf68074a4d20ac2a48`
- PR: none. User has now explicitly authorized merge to `main`, beta publication, and updater verification after the gates pass.
- Last completed task: `EDITOR-PREVIEW-UI-AND-UNLOAD — separate transport and prevent preview teardown crash`
- Task in progress: `RELEASE-EDITOR-PREVIEW-FIX — publish the verified fix through the existing beta updater`
- Exact next task: bump the immutable beta metadata to the next release, merge the verified branch to `main`, wait for the Windows release workflow, then verify the installed application's updater.

## Root cause

`PlayerControlBar` was a bottom-aligned child of `PreviewSurface` in
`csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml`. It therefore rendered as a
dark overlay inside the user-visible preview frame rather than as its own editor
control area.

The observed crash on leaving Editor while preview was active also has an unsafe
teardown path: `EditorPage_Unloaded` awaits preview cancellation while queued
MediaPlayer callbacks can still target the page, and `ResetAsync` still writes
the presentation after the page has unloaded. The player remained attached to
`PreviewPlayer` when disposed.

## Changes made

- `EditorPage.xaml`: the centre column now has two rows. `PreviewSurface` is the
  top row and contains only the player/image/direct-edit overlays. The existing
  `PlayerControlBar` is now a separate, themed 48px row below it.
- Control names and the existing event handlers are unchanged:
  `PlayerPlayPause_Click`, `Timeline_ValueChanged`, `PreviewMute_Toggled`,
  `PreviewVolume_ValueChanged`, and `Fullscreen_Click` retain their single
  owners. Native WinUI transport remains disabled.
- `csharp/scripts/verify_editor_dead_controls.py`: added the
  `PREVIEW-LAYOUT-01` regression contract requiring the transport bar to be
  outside `PreviewSurface` and in the lower row.
- `EditorPage.Playback.cs`: `UnloadAsync` now owns the unloading state before
  awaiting preview cancellation. Late position/end/failure callbacks and render
  completion UI updates are ignored during teardown; unload skips visual
  presentation writes and detaches `MediaPlayerElement` before disposing its
  `MediaPlayer`. Normal source-change reset/reopen presentation remains intact.
- Added `csharp/scripts/verify_editor_preview_unload_contract.py`, and included
  it in the Windows verification gate. Updated the existing PROJECT-03 contract
  for the explicit presentation-skip teardown parameter.
- `csharp/scripts/validate_csharp_migration.py`: source scanning now excludes
  generated `bin` and `obj` trees, so a self-contained publish does not make the
  source-only migration gate fail on generated framework XML.
- `csharp/scripts/verify.ps1`: the local startup smoke uses the system temporary
  directory when `RUNNER_TEMP` is not set outside GitHub Actions.

## Files changed

- `csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml`
- `csharp/src/BiliSubStudio.App/Pages/EditorPage.Playback.cs`
- `csharp/scripts/verify.ps1`
- `csharp/scripts/verify_editor_dead_controls.py`
- `csharp/scripts/verify_editor_preview_unload_contract.py`
- `csharp/scripts/verify_editor_project_tab_reopen_contract.py`
- `csharp/scripts/validate_csharp_migration.py`
- `docs/handoff/CODEX_HANDOFF.md`

## Tests and status

- `python csharp/scripts/verify_editor_dead_controls.py .`: PASS
- `python csharp/scripts/verify_editor_voice_preview_contract.py`: PASS
- `python csharp/scripts/verify_editor_preview_unload_contract.py .`: PASS
- `python csharp/scripts/verify_editor_project_tab_reopen_contract.py .`: PASS
- `python csharp/scripts/validate_csharp_migration.py`: PASS
- `python csharp/scripts/generate_csharp_code_map.py --check`: PASS
- `python csharp/scripts/verify_editor_event_map.py .`: FAIL on unchanged
  `origin/main` XAML event-count baseline (`expected 52, found 53`); the layout
  move did not add or remove an event binding, so this is an upstream stale test
  baseline and must be reconciled separately rather than hidden in this task.
- Full `./csharp/scripts/verify.ps1`: PASS. This included the Windows WinUI
  compile with `0 Warning(s), 0 Error(s)`, 71/71 core contracts, range
  regression, self-contained publish, real startup smoke, worker identity,
  PE x64, and checksum readback.
- Functional Windows test with the user's local fixture
  `C:\Users\Man PC\Downloads\test\万年老祖 第1~6季：弃徒被赶出师门，归来已是万年老祖！！！ p01 1-4季.mp4`:
  PASS. The transport is visibly a separate row below Preview; playback reached
  00:14; switching to `Tải media` during active Preview and reopening Editor did
  not exit the app, and the video/position remained available.
- `python csharp/scripts/verify_editor_event_map.py .`: still FAILS on the
  unchanged upstream expected XAML event count (52 expected, 53 found). This
  task did not change an event binding and `verify.ps1` does not use that stale
  script; reconcile it separately.

Only the source-built app has functional PASS. The installed runtime at
`E:\New folder\testrc` has not yet received nor tested this updater payload.
Seek races, end/replay, fullscreen, cache cleanup, subtitle/voice/image/export
combinations and prolonged playback remain field-test work outside this task.

## Constraints to preserve

- Windows desktop app: C# + .NET 10 + WinUI 3; no web/demo replacement.
- Never overwrite source media. Translation, ASR and TTS remain local.
- Keep one event and one state/control owner; no handler calls another handler.
- Do not layer Repair/Fix/Parity files when the actual owner can be changed.
- One small task at a time; do not reopen completed Subtitle work without an
  actual regression.
- No version bump, release, PR, or merge without explicit authorization and
  passing gates.
