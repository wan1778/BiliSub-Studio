# Codex handoff — BiliSub Studio

- Base/current upstream main: `origin/main@8eb2a8a2600b37d29bfd0deaae9eeb94b3cda635`
- Current local branch: `main`
- Pull request: none; local Preview task commits are not pushed or merged
- Last completed before Preview phase: `SUB-18` (Subtitle/SRT source, Windows CI and contract gates already merged)
- Task completed in this handoff: `PREVIEW-06 — Resume`
- Task result: `PASS`
- Task currently running: none
- Exact next task: `PREVIEW-07 — Seek paused`

## Preview task commits

- `PREVIEW-01`: `96cc8b357de420392a11046b662165e2a7894372` — remove user-facing 12s preview concept.
- `PREVIEW-02`: `540648c173e9345232e5b7221ca99878e7834408` — remove stale 12s preview contract.
- `PREVIEW-03`: `4bae288e03732a171d09d5361be58d039aee7728` — consolidate playback ownership.
- `PREVIEW-04`: `dc0d6c9f0bb7ca838d889ae7d560c871cacc1a0e` — start processed playback at source time zero.
- `PREVIEW-05`: `abca5d2b02bc9d856ca6bd609c93166e7a736cd8` — preserve the processed frame on Pause.
- `PREVIEW-06`: the commit containing this handoff and the resume current-frame contract; resolve its exact SHA with `git rev-parse HEAD` after checkout. The exact literal SHA is also reported in the task completion message because a commit cannot contain its own cryptographic ID.

## Files changed by PREVIEW-06

- `csharp/src/BiliSubStudio.App/Pages/EditorPage.Playback.cs`
- `csharp/scripts/validate_csharp_migration.py`
- `docs/migration/CSHARP_CODE_MAP.generated.md`
- `docs/handoff/CODEX_HANDOFF.md`

## Root cause

The existing inline Resume branch happened to call `_player.Play()`, which resumes the retained MediaPlayer source and position correctly. However, Resume had no explicit controller operation or regression contract. It could later be confused with initial Play and routed through `PlayFromStartAsync`/`LoadSegmentAsync`, which would reset position and create a new FFmpeg/cache segment instead of continuing the paused frame.

## Implementation

- Added the explicit controller operation `ResumeFromCurrentFrame()`.
- The paused branch of `ToggleAsync` now delegates to that operation.
- The existing-active-preview `SetModeAsync(..., play: true)` branch uses the same operation, leaving one resume behavior owner.
- `ResumeFromCurrentFrame()` only invokes `MediaPlayer.Play()` on the retained source/position; it does not reset `PlaybackSession.Position`, request a segment, replace source, mutate timeline or start FFmpeg/cache work.
- Added a fail-first PREVIEW-06 static contract requiring both resume call sites to use the one operation.
- Did not implement paused/playing Seek or other PREVIEW-07+ behavior.
- Regenerated the code map.

## Tests and results

- PREVIEW-06 static current-position resume contract before implementation — expected FAIL, confirmed Resume had no protected business operation.
- `python csharp/scripts/validate_csharp_migration.py` after implementation — PASS (`4.0.0-beta.42-csharp-p5`).
- `python csharp/scripts/generate_csharp_code_map.py --check` — PASS; generated map is current.
- `dotnet run --project csharp/tests/BiliSubStudio.Core.ContractTests/BiliSubStudio.Core.ContractTests.csproj -c Release` with exact local SDK `10.0.400` — PASS, 52/52.
- `dotnet build csharp/BiliSubStudio.sln -c Release -p:Platform=x64 -p:UseSharedCompilation=false -p:ContinuousIntegrationBuild=true --no-restore` — PASS, 0 warnings and 0 errors.
- Self-contained `win-x64` App publish — PASS; local verification artifact only, not a release/package publication.
- Published `BiliSubStudio.exe --startup-smoke-test=...` — PASS, including Editor layout smoke at 800×600, 1000×700 and 1500×900.
- Git diff whitespace check — PASS.
- No CI workflow was dispatched; no installer/package/release was built or published.

## Verification level

- Source/call-path PASS: Resume uses one controller operation and reuses the paused MediaPlayer source/position.
- No-reload invariant PASS at source-contract level: Resume starts no FFmpeg/cache work and does not mutate source, segment or timeline state.
- Compile PASS: full Windows x64 Release solution build, 0 warnings/errors.
- Functional WinUI startup/layout PASS: published executable initialized and exercised the real Editor XAML at all smoke viewports.
- Real-media field playback: not run. Audible/visual confirmation that playback continues from the exact paused frame still requires representative media on a user machine.
- Paused/playing Seek, end/replay and fullscreen behavior is not claimed by PREVIEW-06 and remains assigned to PREVIEW-07+.

## Constraints to preserve

- Windows desktop app: C# + .NET 10 + WinUI 3; no web/demo replacement.
- Never overwrite source media.
- Translation, ASR and TTS remain local; Chinese → Vietnamese uses the project translation skill.
- Keep the three-column Editor and contextual tool scope; do not turn it into a full NLE.
- Keep one event/owner, one button/handler, no handler-calls-handler and the single `EditorPlaybackController` playback owner.
- Initial Play requests source time zero; Pause retains the active source/frame; Resume reuses that exact source/position without a render.
- Do not add Repair/Fix/Parity layers when the real owner can be corrected; remove dead/superseded logic.
- One small task and targeted regression at a time; do not start PREVIEW-07 until requested/continued after this PREVIEW-06 commit.
- Do not reopen already-passed Subtitle work without a demonstrated regression.
- No version bump, release, push, PR merge or merge without explicit authorization and passing gates.
