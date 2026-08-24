# Codex handoff — BiliSub Studio

- Base/current upstream main: `origin/main@8eb2a8a2600b37d29bfd0deaae9eeb94b3cda635`
- Current local branch: `main`
- Local base before this task: `871c1055f1734ad7b48b514d34beb70fd460355e`
- Pull request: none; local Preview task commits are not pushed or merged
- Last completed before Preview phase: `SUB-18` (Subtitle/SRT source, Windows CI and contract gates already merged)
- Task completed in this handoff: `PREVIEW-12 — Fullscreen roundtrip`
- Task result: `PASS`
- Task currently running: none
- Exact next task after this task passes: `PREVIEW-13 — Recover player failure`

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
- `PREVIEW-12`: the commit containing this handoff and fullscreen roundtrip owner; resolve its exact SHA with `git rev-parse HEAD` after checkout. The exact literal SHA is also reported in the task completion message because a commit cannot contain its own cryptographic ID.

## Files changed by PREVIEW-12

- `csharp/src/BiliSubStudio.App/Pages/EditorPage.Playback.cs`
- `csharp/scripts/validate_csharp_migration.py`
- `docs/migration/CSHARP_CODE_MAP.generated.md`
- `docs/handoff/CODEX_HANDOFF.md`

## Root cause

Fullscreen had only an enter operation: the button always assigned `IsFullWindow = true`, and no owner observed the native `MediaPlayerElement` transition back to windowed mode (including Esc). Entering fullscreen from the static edit presentation also loaded a processed segment without a roundtrip contract to restore the original presentation. The controller therefore could not guarantee that exit preserved the entry play/pause intent, current position, or explicit ended/replay state.

## Implementation

- Changed the single fullscreen button handler to forward to `ToggleFullscreenAsync`; no second event handler was added.
- Added a controller-owned `FullscreenSnapshot` containing the entry presentation, play/pause intent and ended state.
- Registered one `IsFullWindow` dependency-property callback so native Esc follows the same controller-owned exit path.
- Exit from an already processed preview keeps the same `MediaPlayer` source and position, then restores the entry play/pause intent.
- Exit after entering from the static edit presentation leaves processed mode through the existing `SetModeAsync(false, false)` owner, which maps the processed position back to source time, restores the static frame/overlays and deletes that temporary segment.
- Preserved the explicit PREVIEW-11 ended/replay state across a fullscreen roundtrip.
- Prepare, source change and unload clear the snapshot and unregister the property callback; controller-driven completion/failure suppresses a stale roundtrip restore before closing full-window mode.
- Added a fail-first PREVIEW-12 static contract for one toggle route, snapshot owner, native transition registration/cleanup and presentation/play-state restoration.
- Scoped the older PREVIEW-06 contract to its `ToggleAsync` call path so the new legitimate fullscreen restore caller does not weaken Resume ownership.
- Regenerated the code map.
- Did not change player-failure recovery reserved for PREVIEW-13.

## Tests and results

- PREVIEW-12 static contract before implementation — expected FAIL; reproduced the missing fullscreen exit/restore owner.
- `python csharp/scripts/validate_csharp_migration.py` — PASS (`4.0.0-beta.42-csharp-p5`).
- `python csharp/scripts/generate_csharp_code_map.py --check` — PASS; generated map is current.
- Core contract runner with exact SDK `10.0.400` — PASS, 54/54.
- Targeted Windows x64 Release solution build — PASS, 0 warnings and 0 errors.
- Git diff whitespace check — PASS.
- Full clean-checkout `csharp/scripts/verify.ps1` — PASS, including source identity, 54/54 contracts, Range regressions, PE32+ x64, embedded worker identity, self-contained publish and real WinUI startup/layout smoke.
- No CI workflow was dispatched; no installer/package/release was built or published.

## Verification level

- Source/call-path PASS: `Fullscreen button/native Esc → EditorPlaybackController → FullscreenSnapshot → restore processed or static presentation → restore play/pause intent`.
- Static ownership contract PASS: one fullscreen snapshot and one `IsFullWindow` property callback are owned and cleaned by `EditorPlaybackController`.
- Compile PASS: Windows x64 Release solution build, 0 warnings/errors.
- Functional WinUI startup/layout PASS: the published executable initialized the real Editor XAML at the verification smoke sizes.
- Real-media fullscreen roundtrip field test: not run. A representative local video must still be checked while playing, paused, on the static edit frame and after playback end; verify visual position and audio intent before/after Esc.
- Player failure recovery and cache lifecycle are not claimed by PREVIEW-12 and remain assigned to PREVIEW-13+.

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
- One small task and targeted regression at a time; do not start PREVIEW-13 until PREVIEW-12 has its final clean verification and commit.
- Do not reopen already-passed Subtitle work without a demonstrated regression.
- No version bump, release, push, PR merge or merge without explicit authorization and passing gates.
