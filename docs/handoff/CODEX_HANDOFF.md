# Codex handoff — BiliSub Studio

- Base/current upstream main: `origin/main@8eb2a8a2600b37d29bfd0deaae9eeb94b3cda635`
- Current local branch: `main`
- Pull request: none; local Preview task commits are not pushed or merged
- Last completed before Preview phase: `SUB-18` (Subtitle/SRT source, Windows CI and contract gates already merged)
- Task completed in this handoff: `PREVIEW-08 — Seek playing`
- Task result: `PASS`
- Task currently running: none
- Exact next task: `PREVIEW-09 — Rapid seek no race/CTS leak`

## Preview task commits

- `PREVIEW-01`: `96cc8b357de420392a11046b662165e2a7894372` — remove user-facing 12s preview concept.
- `PREVIEW-02`: `540648c173e9345232e5b7221ca99878e7834408` — remove stale 12s preview contract.
- `PREVIEW-03`: `4bae288e03732a171d09d5361be58d039aee7728` — consolidate playback ownership.
- `PREVIEW-04`: `dc0d6c9f0bb7ca838d889ae7d560c871cacc1a0e` — start processed playback at source time zero.
- `PREVIEW-05`: `abca5d2b02bc9d856ca6bd609c93166e7a736cd8` — preserve the processed frame on Pause.
- `PREVIEW-06`: `5d4fa76ba24c26f8af391e4c8b6bb70c9a5fde7f` — resume retained processed playback.
- `PREVIEW-07`: `b802766619aac5be2b8fcd6bb443ddce2703eea8` — seek paused preview to the target frame.
- `PREVIEW-08`: the commit containing this handoff and the playing-seek pause/render/resume path; resolve its exact SHA with `git rev-parse HEAD` after checkout. The exact literal SHA is also reported in the task completion message because a commit cannot contain its own cryptographic ID.

## Files changed by PREVIEW-08

- `csharp/src/BiliSubStudio.App/Pages/EditorPage.Playback.cs`
- `csharp/scripts/validate_csharp_migration.py`
- `docs/migration/CSHARP_CODE_MAP.generated.md`
- `docs/handoff/CODEX_HANDOFF.md`

## Root cause

Playing seek previously passed `play: true` to `LoadSegmentAsync`, so the new target segment did autoplay once available. However, the old MediaPlayer continued advancing throughout asynchronous FFmpeg/cache generation because it was not paused until after the new file had been rendered and opened. Its clock/frame could drift far past the requested point or reach `MediaEnded` while the seek was still pending, starting another segment transition against the user action.

## Implementation

- Added the explicit `SeekPlayingAsync(sourcePosition)` controller operation.
- `SeekAsync` selects that operation when `IsPlaying` is true.
- `SeekPlayingAsync` pauses the retained old MediaPlayer immediately, before awaiting any segment render.
- It then loads the target source-time/segment offset through the PREVIEW-07 positioning primitive with `play: true`, so playback resumes at the requested frame once the target segment is ready.
- Kept paused seek routed through `SeekPausedAsync(..., play: false)`.
- Added a fail-first PREVIEW-08 static contract requiring pause-before-render ordering and target autoplay.
- Did not add request generations, serialization, rapid-seek coalescing or CTS arbitration reserved for PREVIEW-09.
- Regenerated the code map.

## Tests and results

- PREVIEW-08 playing-seek contract before implementation — expected FAIL, reproduced the missing explicit pause/render/resume path.
- `python csharp/scripts/validate_csharp_migration.py` after implementation — PASS (`4.0.0-beta.42-csharp-p5`).
- `python csharp/scripts/generate_csharp_code_map.py --check` — PASS; generated map is current.
- `dotnet run --project csharp/tests/BiliSubStudio.Core.ContractTests/BiliSubStudio.Core.ContractTests.csproj -c Release` with exact local SDK `10.0.400` — PASS, 52/52.
- `dotnet build csharp/BiliSubStudio.sln -c Release -p:Platform=x64 -p:UseSharedCompilation=false -p:ContinuousIntegrationBuild=true --no-restore` — PASS, 0 warnings and 0 errors.
- Self-contained `win-x64` App publish — PASS; local verification artifact only, not a release/package publication.
- Published `BiliSubStudio.exe --startup-smoke-test=...` — PASS, including Editor layout smoke at 800×600, 1000×700 and 1500×900.
- Git diff whitespace check — PASS.
- No CI workflow was dispatched; no installer/package/release was built or published.

## Verification level

- Source/call-path PASS: playing seek pauses old playback before render and requests autoplay at the correctly positioned target segment.
- State intent PASS at source-contract level: paused seek remains paused; a single playing seek resumes playing at target.
- Compile PASS: full Windows x64 Release solution build, 0 warnings/errors.
- Functional WinUI startup/layout PASS: published executable initialized and exercised the real Editor XAML at all smoke viewports.
- Real-media field playback: not run. Visual/audible confirmation of a single playing seek resuming at the representative target frame still requires a user-machine test.
- Rapid/multiple Seek, stale completion, CTS/process cleanup, end/replay and fullscreen behavior is not claimed by PREVIEW-08 and remains assigned to PREVIEW-09+.

## Constraints to preserve

- Windows desktop app: C# + .NET 10 + WinUI 3; no web/demo replacement.
- Never overwrite source media.
- Translation, ASR and TTS remain local; Chinese → Vietnamese uses the project translation skill.
- Keep the three-column Editor and contextual tool scope; do not turn it into a full NLE.
- Keep one event/owner, one button/handler, no handler-calls-handler and the single `EditorPlaybackController` playback owner.
- Initial Play starts at zero; Pause/Resume retain source position; paused and playing Seek target full-video time with the correct final paused/playing intent.
- Internal cache-window coordinates must not leak into user-visible timeline/position semantics.
- Do not add Repair/Fix/Parity layers when the real owner can be corrected; remove dead/superseded logic.
- One small task and targeted regression at a time; do not start PREVIEW-09 until requested/continued after this PREVIEW-08 commit.
- Do not reopen already-passed Subtitle work without a demonstrated regression.
- No version bump, release, push, PR merge or merge without explicit authorization and passing gates.
