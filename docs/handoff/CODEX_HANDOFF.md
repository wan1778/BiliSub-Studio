# Codex handoff — BiliSub Studio

- Base/current upstream main: `origin/main@8eb2a8a2600b37d29bfd0deaae9eeb94b3cda635`
- Current local branch: `main`
- Local base before this task: `6a38392d839ca862c30ac2e3b794c234f5a437f8`
- Pull request: none; local Preview task commits are not pushed or merged
- Last completed before Preview phase: `SUB-18` (Subtitle/SRT source, Windows CI and contract gates already merged)
- Task completed in this handoff: `PREVIEW-13 — Recover player failure`
- Task result: `PASS`
- Task currently running: none
- Exact next task after this task passes: `PREVIEW-14 — Keep preview segmentation invisible to the user`

## Preview task commits

- `PREVIEW-01`: `96cc8b357de420392a11046b662165e2a7894372` — remove user-facing 12s preview concept.
- `PREVIEW-02`: `540648c173e9345232e5b7221ca99878e7834408` — remove stale 12s preview contract.
- `PREVIEW-03`: `4bae288e03732a171d09d5361be58d039aee7728` — consolidate playback ownership.
- `PREVIEW-04`: `dc0d6c9f0bb7ca838d889ae7d560c871cacc1a0e` — start processed playback at source time zero.
- `PREVIEW-05`: `abca5d2b02bc9d856ca6bd609c93166e7a736cd8` — preserve the processed frame on Pause.
- `PREVIEW-06`: `5d4fa76ba24c26f8af391e4c8b6bb70c9a5fde7f` — resume retained processed playback.
- `PREVIEW-07`: `b802766619aac5be2b8fcd6bb443ddce2703eea8` — seek paused preview to the target frame.
- `PREVIEW-08`: `f5bed9fac1810088970a33ed2e0e850db400184c` — pause old playback before rendering a playing-seek target.
- `PREVIEW-09`: `7bbbd3f8b7da6e5707ced44098923b5f44a90bda` — serialize rapid seek requests and cleanup.
- `PREVIEW-10`: `6d85d7c7c662c095eb9915f635e977360541d8c0` — continue playback across internal segments to source end.
- `PREVIEW-11`: `871c1055f1734ad7b48b514d34beb70fd460355e` — explicit ended state and replay from source zero.
- `PREVIEW-12`: `6a38392d839ca862c30ac2e3b794c234f5a437f8` — controller-owned fullscreen roundtrip and native Esc restoration.
- `PREVIEW-13`: the commit containing this handoff and player-failure recovery; resolve its exact SHA with `git rev-parse HEAD` after checkout. The exact literal SHA is also reported in the task completion message because a commit cannot contain its own cryptographic ID.

## Files changed by PREVIEW-13

- `csharp/src/BiliSubStudio.App/Pages/EditorPage.Playback.cs`
- `csharp/scripts/validate_csharp_migration.py`
- `docs/migration/CSHARP_CODE_MAP.generated.md`
- `docs/handoff/CODEX_HANDOFF.md`

## Root cause

`MediaFailed` only exited processed mode and left the same failed `MediaPlayer` instance attached. The next Play could therefore reuse an invalid player. The dispatcher adapter was also an async lambda, had no stale-sender guard, swallowed cleanup errors, and did not explicitly refresh Editor actions after failure, so an old callback or failed cleanup could leave preview controls in an unreliable state.

## Implementation

- Extracted the existing player creation code into the controller-owned `CreatePlayer` primitive; initial source preparation and recovery now use the same event wiring, mute and volume policy.
- `PlayerMediaFailed` is now a synchronous event adapter that captures the error and dispatches to `RecoverFromPlayerFailureAsync`; no async dispatcher lambda remains.
- Recovery ignores a callback unless its sender is still the controller's current player, and checks again after asynchronous cleanup so a stale failure cannot replace a newer player.
- Recovery uses the existing lifecycle reset owner to cancel preview work, close fullscreen, dispose the failed player, restore static presentation and delete the active preview segment.
- If cleanup itself reports an error, recovery still disposes the failed instance and creates a replacement player, preserving a retry path instead of locking Play.
- Recovery refreshes Editor actions and the Play/Pause shell state in `finally`, on both success and failure.
- Added a fail-first PREVIEW-13 static contract covering synchronous dispatch, stale-sender rejection, lifecycle reset, replacement creation and action refresh.
- Regenerated the code map.
- Did not change segmentation/cache behavior reserved for PREVIEW-14/15.

## Tests and results

- PREVIEW-13 static contract before implementation — expected FAIL; reproduced reuse of the failed player and missing recovery owner.
- `python csharp/scripts/validate_csharp_migration.py` — PASS (`4.0.0-beta.42-csharp-p5`).
- `python csharp/scripts/generate_csharp_code_map.py --check` — PASS; generated map is current.
- Core contract runner with exact SDK `10.0.400` — PASS, 54/54.
- Targeted Windows x64 Release solution build — PASS, 0 warnings and 0 errors.
- Git diff whitespace check — PASS.
- Full clean-checkout `csharp/scripts/verify.ps1` — PASS, including source identity, 54/54 contracts, Range regressions, PE32+ x64, embedded worker identity, self-contained publish and real WinUI startup/layout smoke.
- No CI workflow was dispatched; no installer/package/release was built or published.

## Verification level

- Source/call-path PASS: `MediaFailed → synchronous dispatcher adapter → stale-sender guard → lifecycle reset → dispose failed player → create replacement → refresh controls`.
- Static ownership contract PASS: failure recovery remains inside the single `EditorPlaybackController`; no second handler/state owner was introduced.
- Compile PASS: Windows x64 Release solution build, 0 warnings/errors.
- Functional WinUI startup/layout PASS: the published executable initialized the real Editor XAML at the verification smoke sizes.
- Real MediaPlayer failure injection field test: not run. On the user machine, force a decoder/source failure, confirm the Editor returns to the static frame, Play is enabled, then retry preview successfully without reopening the project.
- Segmentation continuity and cache lifecycle are not claimed by PREVIEW-13 and remain assigned to PREVIEW-14/15.

## Constraints to preserve

- Windows desktop app: C# + .NET 10 + WinUI 3; no web/demo replacement.
- Never overwrite source media.
- Translation, ASR and TTS remain local; Chinese → Vietnamese uses the project translation skill.
- Keep the three-column Editor and contextual tool scope; do not turn it into a full NLE.
- Keep one event/owner, one button/handler, no handler-calls-handler and the single `EditorPlaybackController` playback owner.
- Initial Play and replay both start at source zero; Pause/Resume retain source position; paused and playing Seek retain their intended state.
- Rapid Seek remains latest-request-wins and waits for cancelled FFmpeg/temp-file cleanup.
- Internal segment duration remains an implementation detail; playback continues until the real source end.
- Do not add Repair/Fix/Parity layers when the real owner can be corrected; remove dead/superseded logic.
- One small task and targeted regression at a time; do not start PREVIEW-14 until PREVIEW-13 has its final clean verification and commit.
- Do not reopen already-passed Subtitle work without a demonstrated regression.
- No version bump, release, push, PR merge or merge without explicit authorization and passing gates.
