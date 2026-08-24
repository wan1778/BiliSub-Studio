# Codex handoff — BiliSub Studio

- Base/current upstream main: `origin/main@8eb2a8a2600b37d29bfd0deaae9eeb94b3cda635`
- Current local branch: `main`
- Local base before this task: `6d85d7c7c662c095eb9915f635e977360541d8c0`
- Pull request: none; local Preview task commits are not pushed or merged
- Last completed before Preview phase: `SUB-18` (Subtitle/SRT source, Windows CI and contract gates already merged)
- Task completed in this handoff: `PREVIEW-11 — Replay after end`
- Task result: `PASS`
- Task currently running: none
- Exact next task: `PREVIEW-12 — Fullscreen roundtrip`

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
- `PREVIEW-11`: the commit containing this handoff and explicit replay end-state; resolve its exact SHA with `git rev-parse HEAD` after checkout. The exact literal SHA is also reported in the task completion message because a commit cannot contain its own cryptographic ID.

## Files changed by PREVIEW-11

- `csharp/src/BiliSubStudio.App/Pages/EditorPage.Playback.cs`
- `csharp/scripts/validate_csharp_migration.py`
- `docs/migration/CSHARP_CODE_MAP.generated.md`
- `docs/handoff/CODEX_HANDOFF.md`

## Root cause

Replay happened only implicitly: final `MediaEnded` exited processed mode, so the next Play happened to fall through the same initial-play branch and render source time zero. The controller had no explicit ended state, completion business logic lived inside the dispatcher callback, and no contract protected end → replay semantics. That made replay behavior fragile even though the current happy path could appear to work.

## Implementation

- Added one controller-owned `HasEnded` state.
- `ToggleAsync` now handles `HasEnded` first and routes to `ReplayFromStartAsync`.
- `ReplayFromStartAsync` delegates to the existing PREVIEW-04 `PlayFromStartAsync` source-zero primitive; replay cannot inherit the end timeline position.
- Extracted `ContinueAfterSegmentAsync` and `CompletePlaybackAsync`; `PlayerMediaEnded` now only dispatches to the controller operation.
- Completion exits processed mode, cleans the completed segment, marks `HasEnded`, pins the visible timeline/clock to media end without triggering a duplicate frame refresh, and tells the user Play will replay from the beginning.
- A successful new segment load clears `HasEnded`; failed replay leaves it set so the user can retry.
- Prepare/source change/unload reset the ended state, while unrelated no-op mode exits do not erase it.
- Added a fail-first PREVIEW-11 static contract requiring one ended owner, explicit replay routing, source-zero delegation, dispatcher forwarding and successful-load reset.
- Regenerated the code map.
- Did not change fullscreen behavior reserved for PREVIEW-12.

## Tests and results

- PREVIEW-11 static contract before implementation — expected FAIL; reproduced the missing explicit ended/replay path.
- First Windows x64 build — FAIL with `CS0407`: a `Task` method group cannot be passed directly to the void `DispatcherQueue` delegate.
- Fixed the UI adapter to enqueue a non-async callback that starts the fully exception-contained controller task; no async-void helper was added.
- `python csharp/scripts/validate_csharp_migration.py` — PASS (`4.0.0-beta.42-csharp-p5`).
- `python csharp/scripts/generate_csharp_code_map.py --check` — PASS; generated map is current.
- Core contract runner with exact SDK `10.0.400` — PASS, 54/54.
- Final Windows x64 Release solution build — PASS, 0 warnings and 0 errors.
- Git diff whitespace check — PASS.
- Full clean-checkout `powershell -ExecutionPolicy Bypass -File csharp/scripts/verify.ps1` — PASS, including source identity, PE32+ x64, embedded worker identity, self-contained publish and real WinUI startup/layout smoke.
- No CI workflow was dispatched; no installer/package/release was built or published.

## Verification level

- Source/call-path PASS: `MediaEnded → CompletePlaybackAsync → HasEnded → Play → ReplayFromStartAsync → PlayFromStartAsync → source zero`.
- Static ownership contract PASS: exactly one ended state exists in the playback controller, and it is cleared only after a replacement segment loads successfully or lifecycle reset occurs.
- Compile PASS: full Windows x64 Release solution build, 0 warnings/errors.
- Functional WinUI startup/layout PASS: the published executable initialized the real Editor XAML at 800×600, 1000×700 and 1500×900.
- Real-media replay field test: not run. A representative local video must still be played to the real end and replayed visually/audibly on the user machine.
- Fullscreen recovery and cache lifecycle are not claimed by PREVIEW-11 and remain assigned to PREVIEW-12+.

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
- One small task and targeted regression at a time; do not start PREVIEW-12 until requested/continued after this PREVIEW-11 commit.
- Do not reopen already-passed Subtitle work without a demonstrated regression.
- No version bump, release, push, PR merge or merge without explicit authorization and passing gates.
