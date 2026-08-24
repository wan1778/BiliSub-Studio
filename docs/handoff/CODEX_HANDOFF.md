# Codex handoff — BiliSub Studio

- Base checkpoint requested for this task: `82bbeccd8f0458d6c74a821d33b843b042caf2ec`
- Remote continuation branch: `origin/editor-preview-blur-01-17`
- Last completed before this task: `BLUR-17 — Preview và Export geometry giống nhau`
- Task completed in this handoff: `AUDIO-01 — Một audio state owner`
- Task result: implementation + regression gate committed; Windows WinUI verification is still pending
- Task currently running: none
- Exact next task: none authorized in this handoff; stop after AUDIO-01
- Pull request: none; do not create/merge/release without explicit authorization

## AUDIO-01 commits

- `d02967f3d4285405ef7cc567cf2c3a2fa3b2a013` — production change: one playback-owned monitor audio state.
- `e5794066c37470ecbbc9261b23975b0d451cf627` — targeted AUDIO-01 regression contract and Windows verify gate integration.
- The commit containing this handoff only records continuation state; resolve its SHA with `git rev-parse HEAD`.

## AUDIO-01 files changed

- `csharp/src/BiliSubStudio.App/Pages/EditorPage.Playback.cs`
- `csharp/scripts/verify_editor_audio_state_contract.py`
- `csharp/scripts/verify.ps1`
- `docs/handoff/CODEX_HANDOFF.md`

No Blur, Subtitle, ASR, TTS, source-audio render graph, version, release, installer or packaging implementation was changed.

## AUDIO-01 root cause and ownership

The persisted source-audio policy and the local monitor controls are intentionally different state domains:

- `_audioSettings` is project-owned render policy for source `keep` / `duck` / `mute`.
- monitor mute/volume is local MediaPlayer state only and must not alter render policy.

Before AUDIO-01, `SetMuted`/`SetVolume` changed only the current `MediaPlayer`, while `CreatePlayer()` rebuilt mute/volume by reading `PreviewMuteToggle` and `PreviewVolumeSlider` directly. That made the UI controls an implicit second monitor-state source whenever the player was recreated after source changes or player failure.

AUDIO-01 makes `EditorPlaybackController` the single monitor-audio owner:

```text
PreviewMute_Toggled
  -> EditorPlaybackController.SetMuted
  -> _monitorAudio.Muted
  -> current MediaPlayer.IsMuted

PreviewVolume_ValueChanged
  -> EditorPlaybackController.SetVolume
  -> normalized _monitorAudio.Volume
  -> current MediaPlayer.Volume

CreatePlayer
  -> _monitorAudio.Muted / _monitorAudio.Volume
  -> recreated MediaPlayer
```

The controller no longer reconstructs monitor state from `PreviewMuteToggle` or `PreviewVolumeSlider`. `_audioSettings` remains outside the playback controller and continues to own persisted Keep/Duck/Mute render behavior.

## AUDIO-01 regression contract

`csharp/scripts/verify_editor_audio_state_contract.py` fails if any of these regress:

- mute or volume loses its reviewed XAML event handler;
- the handlers stop forwarding event state into `EditorPlaybackController`;
- monitor mute/volume no longer have exactly one `_monitorAudio` state owner;
- playback controller starts reading the mute/volume controls directly again;
- playback controller starts owning persisted `_audioSettings`;
- setters update the MediaPlayer without updating the owner first;
- a recreated MediaPlayer does not restore both values from `_monitorAudio`;
- persisted source Keep/Duck/Mute policy stops remaining project-owned.

`csharp/scripts/verify.ps1` now invokes this contract immediately after the existing C# migration/static contract.

## Verification performed in this handoff

- Confirmed task base branch HEAD before edits: `82bbeccd8f0458d6c74a821d33b843b042caf2ec`.
- Reviewed AUDIO ownership architecture and confirmed monitor volume/mute must remain separate from source Keep/Duck/Mute render policy.
- Reviewed production diff: production commit changes only `EditorPage.Playback.cs` with 15 changed lines (9 additions, 6 deletions).
- Reviewed total AUDIO-01 diff before this handoff update: only the production file, one dedicated contract script, and one `verify.ps1` invocation were changed.
- Python syntax check for `verify_editor_audio_state_contract.py`: PASS.
- Windows x64 solution build / WinUI XAML compile / startup smoke: not run in this environment because no Windows/.NET runner is available here.
- GitHub CI workflow: not dispatched. This continuation branch does not run the workflow on ordinary branch push, and no PR was created.

Therefore AUDIO-01 implementation is committed, but do not label the branch release-PASS until the existing Windows verification gate runs successfully on the new HEAD.

## Constraints to preserve

- Windows desktop app: C# + .NET 10 + WinUI 3; no web/demo replacement.
- Never overwrite source media.
- Translation, ASR and TTS remain local; Chinese -> Vietnamese uses the project translation skill.
- Keep one event/owner, one button/handler, no handler-calls-handler and the single `EditorPlaybackController` playback owner.
- Monitor mute/volume must remain local playback state only; it must never become persisted Keep/Duck/Mute render policy.
- Persisted source audio policy must remain project-owned and continue to drive processed Preview/final Export semantics.
- Do not reopen already-passed Blur or Subtitle work without a demonstrated regression.
- Do not add Repair/Fix/Parity layers when the real owner can be corrected; remove dead/superseded logic.
- One small task and targeted regression at a time.
- Do not begin another AUDIO task from this handoff without explicit user instruction.
- No version bump, release, PR or merge without explicit authorization and passing gates.
