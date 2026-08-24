# Codex handoff — BiliSub Studio

- Base/current upstream main: `origin/main@8eb2a8a2600b37d29bfd0deaae9eeb94b3cda635`
- Current local branch: `main`
- Local base before this task: `7bbbd3f8b7da6e5707ced44098923b5f44a90bda`
- Pull request: none; local Preview task commits are not pushed or merged
- Last completed before Preview phase: `SUB-18` (Subtitle/SRT source, Windows CI and contract gates already merged)
- Task completed in this handoff: `PREVIEW-10 — Play to full video end`
- Task result: `PASS`
- Task currently running: none
- Exact next task: `PREVIEW-11 — Replay after end`

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
- `PREVIEW-10`: the commit containing this handoff and full-video segment progression contract; resolve its exact SHA with `git rev-parse HEAD` after checkout. The exact literal SHA is also reported in the task completion message because a commit cannot contain its own cryptographic ID.

## Files changed by PREVIEW-10

- `csharp/src/BiliSubStudio.Core/Editor/VideoEditorService.cs`
- `csharp/src/BiliSubStudio.App/Pages/EditorPage.Playback.cs`
- `csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs`
- `csharp/scripts/validate_csharp_migration.py`
- `docs/migration/CSHARP_CODE_MAP.generated.md`
- `docs/handoff/CODEX_HANDOFF.md`

## Root cause

The current controller already attempted to continue from `MediaEnded`, so no confirmed production regression that always stopped at 12 seconds was found. The gap was architectural and test coverage: the advance-versus-end calculation lived inline in the WinUI callback and no runtime contract walked multiple internal windows, including the shifted final-window case. A later edit could therefore silently turn the internal 12-second cache duration into a user-visible playback limit.

## Implementation

- Added `VideoEditorService.NextPreviewStart(sourceStart, segmentDuration, sourceDuration)` as the validated owner of segment advance/end calculation.
- The primitive returns the next full-source position while media remains, or `null` only when the current segment reaches the source end within final-frame tolerance.
- `PlayerMediaEnded` now uses that primitive and loads `nextStart.Value` with `play: true`; it enters the completed state only when the primitive returns `null`.
- Added `EditorFullVideoPlaybackContractAsync`, which walks every generated preview window for source durations from 0.04 seconds through 3600 seconds.
- The contract covers exact 12-second boundaries, just-over-boundary sources, shifted near-end windows, short media and long media; sources longer than one cache window must traverse multiple windows.
- Added a fail-first PREVIEW-10 static contract tying `MediaEnded` to the tested boundary owner.
- Regenerated the code map.
- Did not change replay/end-state behavior reserved for PREVIEW-11 or segment prefetch/seamlessness reserved for PREVIEW-14.

## Tests and results

- PREVIEW-10 static contract before implementation — expected FAIL; reproduced the missing tested boundary owner.
- `python csharp/scripts/validate_csharp_migration.py` — PASS (`4.0.0-beta.42-csharp-p5`).
- `python csharp/scripts/generate_csharp_code_map.py --check` — PASS; generated map is current.
- Core contract runner with exact SDK `10.0.400` — PASS, 54/54, including `editor processed preview advances every segment to the full source end`.
- Full Windows x64 Release solution build — PASS, 0 warnings and 0 errors.
- Git diff whitespace check — PASS.
- Full clean-checkout `powershell -ExecutionPolicy Bypass -File csharp/scripts/verify.ps1` — PASS, including source identity, PE32+ x64, embedded worker identity, self-contained publish and real WinUI startup/layout smoke.
- No CI workflow was dispatched; no installer/package/release was built or published.

## Verification level

- Source/call-path PASS: `MediaEnded → NextPreviewStart → LoadSegmentAsync(..., play: true)` repeats until the full source end.
- Runtime service contract PASS: short, boundary, near-end-shifted, 299/300-second and one-hour sequences all terminate at the full source end without stopping after their first internal segment.
- Compile PASS: full Windows x64 Release solution build, 0 warnings/errors.
- Functional WinUI startup/layout PASS: the published executable initialized the real Editor XAML at 800×600, 1000×700 and 1500×900.
- Real-media play-to-end field test: not run. Continuous visual/audio playback across actual FFmpeg segment boundaries still requires a representative local video on the user machine.
- Replay, fullscreen recovery and cache lifecycle are not claimed by PREVIEW-10 and remain assigned to PREVIEW-11+.

## Constraints to preserve

- Windows desktop app: C# + .NET 10 + WinUI 3; no web/demo replacement.
- Never overwrite source media.
- Translation, ASR and TTS remain local; Chinese → Vietnamese uses the project translation skill.
- Keep the three-column Editor and contextual tool scope; do not turn it into a full NLE.
- Keep one event/owner, one button/handler, no handler-calls-handler and the single `EditorPlaybackController` playback owner.
- Initial Play starts at zero; Pause/Resume retain source position; paused and playing Seek target full-video time with the correct final paused/playing intent.
- Rapid Seek remains latest-request-wins and waits for cancelled FFmpeg/temp-file cleanup.
- Internal segment duration must remain an implementation detail; `MediaEnded` must continue until the real source end.
- Do not add Repair/Fix/Parity layers when the real owner can be corrected; remove dead/superseded logic.
- One small task and targeted regression at a time; do not start PREVIEW-11 until requested/continued after this PREVIEW-10 commit.
- Do not reopen already-passed Subtitle work without a demonstrated regression.
- No version bump, release, push, PR merge or merge without explicit authorization and passing gates.
