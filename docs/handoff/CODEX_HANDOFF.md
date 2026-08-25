# Codex handoff — BiliSub Studio

- Current main/base SHA: `717ad3ffa9e39fb17a80107a0c4a1c2485e9e640`
- Current branch: `editor-transport-layout` (created from `origin/main`)
- PR: none; do not create, merge, release, or bump version without explicit authorization
- Last completed task: upstream `PROJECT-10` / release `4.0.31` on `main`; this branch has no completed task yet
- Task in progress: `EDITOR-PREVIEW-UI-AND-UNLOAD — separate transport and prevent preview teardown crash`
- Exact next task: install/restore .NET SDK `10.0.400`, then run the full Windows WinUI gate and reproduce Editor preview -> leave tab -> reopen before any merge, release, or update publication

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

## Files changed

- `csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml`
- `csharp/src/BiliSubStudio.App/Pages/EditorPage.Playback.cs`
- `csharp/scripts/verify.ps1`
- `csharp/scripts/verify_editor_dead_controls.py`
- `csharp/scripts/verify_editor_preview_unload_contract.py`
- `csharp/scripts/verify_editor_project_tab_reopen_contract.py`
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
- `dotnet build csharp/src/BiliSubStudio.App/BiliSubStudio.App.csproj --no-restore -v:minimal`: BLOCKED — this machine currently exposes `C:\Program Files\dotnet\dotnet.exe`, but no installed SDK. The repository pins SDK `10.0.400`.

No Windows/WinUI compile, startup, or real-machine layout test has been run for
this branch. The installed runtime at `E:\New folder\testrc` is an older built
release and does not contain this source change; it must not be used to claim
this layout change passes.

## Constraints to preserve

- Windows desktop app: C# + .NET 10 + WinUI 3; no web/demo replacement.
- Never overwrite source media. Translation, ASR and TTS remain local.
- Keep one event and one state/control owner; no handler calls another handler.
- Do not layer Repair/Fix/Parity files when the actual owner can be changed.
- One small task at a time; do not reopen completed Subtitle work without an
  actual regression.
- No version bump, release, PR, or merge without explicit authorization and
  passing gates.
