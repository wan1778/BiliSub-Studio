# Codex handoff — BiliSub Studio

- Base/current upstream main: `origin/main@8eb2a8a2600b37d29bfd0deaae9eeb94b3cda635`
- Current local branch: `main`
- Pull request: none; local task commits are not pushed or merged
- Last completed before Preview phase: `SUB-18` (Subtitle/SRT source, Windows CI and contract gates already merged)
- Task completed in this handoff: `PREVIEW-02 — Remove stale 12s test`
- Task result: `PASS`
- Task currently running: none
- Exact next task: `PREVIEW-03 — One playback controller`

## Preview task commits

- `PREVIEW-01`: `96cc8b357de420392a11046b662165e2a7894372` — remove user-facing 12s preview concept.
- `PREVIEW-02`: the commit containing this handoff and the test change; resolve its exact SHA with `git rev-parse HEAD` after checkout. The exact literal SHA is also reported in the task completion message because a commit cannot contain its own cryptographic ID.

## Files changed by PREVIEW-02

- `csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs`
- `docs/handoff/CODEX_HANDOFF.md`

## Root cause

The processed-preview Core contract still passed literal `12d` values into `BuildPreviewSlice` and `BuildPreviewArguments`, then required `-t 12.000`. That coupled render-graph/audio parity coverage to a stale fixed 12-second product contract even though segment duration is an internal implementation detail.

## Implementation

- Renamed the contract to state that processed preview accepts arbitrary internal windows.
- Replaced the fixed 12-second test window with an arbitrary 9.5-second window.
- Derived the expected FFmpeg `-ss`/`-t` arguments from named start/duration values using invariant culture.
- Preserved all existing assertions for region/cue/word/pause clipping, timestamp shifting, filter graph, audio policy, H.264 compatibility and faststart.
- Did not change `VideoEditorService.PreviewWindow`, `targetDuration = 12`, UI, playback state, cache behavior, source-media safety or passed Subtitle functionality.

## Tests and results

- `dotnet run --project csharp/tests/BiliSubStudio.Core.ContractTests/BiliSubStudio.Core.ContractTests.csproj -c Release` with exact local SDK `10.0.400` — PASS, 52/52.
- Updated processed-preview contract — PASS with arbitrary 9.5-second internal window.
- `python csharp/scripts/validate_csharp_migration.py` — PASS (`4.0.0-beta.42-csharp-p5`).
- `python csharp/scripts/generate_csharp_code_map.py --check` — PASS; generated map is current.
- `dotnet build csharp/BiliSubStudio.sln -c Release -p:Platform=x64 -p:UseSharedCompilation=false -p:ContinuousIntegrationBuild=true --no-restore` — PASS, 0 warnings and 0 errors.
- Git diff whitespace check — PASS.
- WinUI startup smoke was not rerun because PREVIEW-02 changes only a non-UI Core contract test.
- No CI workflow was dispatched; no package/release was built or published.

## Verification level

- Source/test architecture PASS: preview parity no longer treats 12 seconds as a test contract.
- Compile PASS: full Windows x64 Release solution build.
- Functional playback PASS: not claimed; production playback behavior was intentionally unchanged.
- Still requires field test in later tasks: real full-video playback, pause/resume, paused/playing/rapid seek, end/replay, fullscreen, player failure recovery, invisible segment boundaries and cache cleanup.

## Constraints to preserve

- Windows desktop app: C# + .NET 10 + WinUI 3; no web/demo replacement.
- Never overwrite source media.
- Translation, ASR and TTS remain local; Chinese → Vietnamese uses the project translation skill.
- Keep the three-column Editor and contextual tool scope; do not turn it into a full NLE.
- One event/owner, one button/handler, no handler-calls-handler and one playback controller.
- Do not add Repair/Fix/Parity layers when the real owner can be corrected; remove dead/superseded logic.
- One small task and targeted regression at a time; do not start PREVIEW-04 until PREVIEW-03 passes and is committed.
- Do not reopen already-passed Subtitle work without a demonstrated regression.
- No version bump, release, push, PR merge or merge without explicit authorization and passing gates.
