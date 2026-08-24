# Codex handoff — BiliSub Studio

- Base/current upstream main: `origin/main@8eb2a8a2600b37d29bfd0deaae9eeb94b3cda635`
- Current local branch: `main`
- Pull request: none; local Preview task commits are not pushed or merged
- Last completed before Preview phase: `SUB-18` (Subtitle/SRT source, Windows CI and contract gates already merged)
- Task completed in this handoff: `PREVIEW-05 — Pause holds frame`
- Task result: `PASS`
- Task currently running: none
- Exact next task: `PREVIEW-06 — Resume`

## Preview task commits

- `PREVIEW-01`: `96cc8b357de420392a11046b662165e2a7894372` — remove user-facing 12s preview concept.
- `PREVIEW-02`: `540648c173e9345232e5b7221ca99878e7834408` — remove stale 12s preview contract.
- `PREVIEW-03`: `4bae288e03732a171d09d5361be58d039aee7728` — consolidate playback ownership.
- `PREVIEW-04`: `dc0d6c9f0bb7ca838d889ae7d560c871cacc1a0e` — start processed playback at source time zero.
- `PREVIEW-05`: the commit containing this handoff and the pause hold-frame contract; resolve its exact SHA with `git rev-parse HEAD` after checkout. The exact literal SHA is also reported in the task completion message because a commit cannot contain its own cryptographic ID.

## Files changed by PREVIEW-05

- `csharp/src/BiliSubStudio.App/Pages/EditorPage.Playback.cs`
- `csharp/scripts/validate_csharp_migration.py`
- `docs/migration/CSHARP_CODE_MAP.generated.md`
- `docs/handoff/CODEX_HANDOFF.md`

## Root cause

The existing inline Pause branch happened to call only `_player.Pause()`, which is the correct MediaPlayer primitive for holding the decoded frame. However, Pause had no explicit business operation or regression contract. A later change to route it through `SetModeAsync(false)` would silently clear `MediaPlayer.Source`, hide the processed player, delete the active segment and replace the held frame with a newly rendered edit-frame.

## Implementation

- Added the explicit controller operation `PauseAtCurrentFrame()`.
- The playing branch of `ToggleAsync` now delegates to that operation.
- `PauseAtCurrentFrame()` only invokes `MediaPlayer.Pause()`; it does not clear `Source`, leave preview mode, change presentation, mutate timeline/segment state, request FFmpeg/cache work or refresh the edit-frame.
- Added a fail-first PREVIEW-05 static contract requiring the explicit pause path and forbidding preview-exit/presentation-reset calls from the Toggle path.
- Did not implement Resume, Seek or other PREVIEW-06+ behavior.
- Regenerated the code map.

## Tests and results

- PREVIEW-05 static hold-frame contract before implementation — expected FAIL, confirmed Pause had no protected business operation.
- `python csharp/scripts/validate_csharp_migration.py` after implementation — PASS (`4.0.0-beta.42-csharp-p5`).
- `python csharp/scripts/generate_csharp_code_map.py --check` — PASS; generated map is current.
- `dotnet run --project csharp/tests/BiliSubStudio.Core.ContractTests/BiliSubStudio.Core.ContractTests.csproj -c Release` with exact local SDK `10.0.400` — PASS, 52/52.
- `dotnet build csharp/BiliSubStudio.sln -c Release -p:Platform=x64 -p:UseSharedCompilation=false -p:ContinuousIntegrationBuild=true --no-restore` — PASS, 0 warnings and 0 errors.
- Self-contained `win-x64` App publish — PASS; local verification artifact only, not a release/package publication.
- Published `BiliSubStudio.exe --startup-smoke-test=...` — PASS, including Editor layout smoke at 800×600, 1000×700 and 1500×900.
- Git diff whitespace check — PASS.
- No CI workflow was dispatched; no installer/package/release was built or published.

## Verification level

- Source/call-path PASS: Pause uses the single controller owner and only pauses the active MediaPlayer.
- Hold-frame invariant PASS at source-contract level: Pause retains MediaPlayer source, processed presentation, active segment and current position; it starts no render/cache operation.
- Compile PASS: full Windows x64 Release solution build, 0 warnings/errors.
- Functional WinUI startup/layout PASS: published executable initialized and exercised the real Editor XAML at all smoke viewports.
- Real-media field playback: not run. Visual confirmation that a representative decoded video frame remains unchanged while paused is still required on a user machine.
- Resume/Seek/end/replay/fullscreen behavior is not claimed by PREVIEW-05 and remains assigned to PREVIEW-06+.

## Constraints to preserve

- Windows desktop app: C# + .NET 10 + WinUI 3; no web/demo replacement.
- Never overwrite source media.
- Translation, ASR and TTS remain local; Chinese → Vietnamese uses the project translation skill.
- Keep the three-column Editor and contextual tool scope; do not turn it into a full NLE.
- Keep one event/owner, one button/handler, no handler-calls-handler and the single `EditorPlaybackController` playback owner.
- Initial Play must request source time zero; Pause must retain the active MediaPlayer source/presentation and start no render.
- Do not add Repair/Fix/Parity layers when the real owner can be corrected; remove dead/superseded logic.
- One small task and targeted regression at a time; do not start PREVIEW-06 until requested/continued after this PREVIEW-05 commit.
- Do not reopen already-passed Subtitle work without a demonstrated regression.
- No version bump, release, push, PR merge or merge without explicit authorization and passing gates.
