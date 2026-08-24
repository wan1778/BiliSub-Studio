# Codex handoff — BiliSub Studio

- Base/current upstream main: `origin/main@8eb2a8a2600b37d29bfd0deaae9eeb94b3cda635`
- Current local branch: `main`
- Pull request: none; local Preview task commits are not pushed or merged
- Last completed before Preview phase: `SUB-18` (Subtitle/SRT source, Windows CI and contract gates already merged)
- Task completed in this handoff: `PREVIEW-04 — Play from start`
- Task result: `PASS`
- Task currently running: none
- Exact next task: `PREVIEW-05 — Pause holds frame`

## Preview task commits

- `PREVIEW-01`: `96cc8b357de420392a11046b662165e2a7894372` — remove user-facing 12s preview concept.
- `PREVIEW-02`: `540648c173e9345232e5b7221ca99878e7834408` — remove stale 12s preview contract.
- `PREVIEW-03`: `4bae288e03732a171d09d5361be58d039aee7728` — consolidate playback ownership.
- `PREVIEW-04`: the commit containing this handoff and the play-from-start change; resolve its exact SHA with `git rev-parse HEAD` after checkout. The exact literal SHA is also reported in the task completion message because a commit cannot contain its own cryptographic ID.

## Files changed by PREVIEW-04

- `csharp/src/BiliSubStudio.App/Pages/EditorPage.Playback.cs`
- `csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs`
- `csharp/scripts/validate_csharp_migration.py`
- `docs/migration/CSHARP_CODE_MAP.generated.md`
- `docs/handoff/CODEX_HANDOFF.md`

## Root cause

The first Play click followed `ToggleAsync → SetModeAsync(true, true) → LoadSegmentAsync(Timeline.Value, true)`. `Timeline.Value` is also the current edit/cue position, so selecting a cue or inspecting a later frame before pressing Play caused processed playback to start in the middle instead of at source time zero.

## Implementation

- Added the explicit controller business entry `PlayFromStartAsync()`.
- The initial/non-preview branch of `ToggleAsync` now forwards to `PlayFromStartAsync`, which requests `LoadSegmentAsync(0, play: true)`.
- Kept `SetModeAsync` using the current timeline position for existing fullscreen and auto-composite rebuild paths; did not change Pause, Resume or Seek behavior reserved for PREVIEW-05+.
- Added a fail-first static call-path contract that forbids initial Play from reading `Timeline.Value` or routing through the generic `SetModeAsync(true, true)` path.
- Extended the processed-preview Core contract to prove that `VideoEditorService.PreviewWindow` preserves a requested initial start of zero and returns a playable window.
- Regenerated the code map.

## Tests and results

- PREVIEW-04 static call-path contract before implementation — expected FAIL, reproduced the `Timeline.Value` coupling.
- `python csharp/scripts/validate_csharp_migration.py` after implementation — PASS (`4.0.0-beta.42-csharp-p5`).
- `python csharp/scripts/generate_csharp_code_map.py --check` — PASS; generated map is current.
- `dotnet run --project csharp/tests/BiliSubStudio.Core.ContractTests/BiliSubStudio.Core.ContractTests.csproj -c Release` with exact local SDK `10.0.400` — PASS, 52/52; initial preview window starts at `0` and is playable.
- `dotnet build csharp/BiliSubStudio.sln -c Release -p:Platform=x64 -p:UseSharedCompilation=false -p:ContinuousIntegrationBuild=true --no-restore` — PASS, 0 warnings and 0 errors.
- Self-contained `win-x64` App publish — PASS; local verification artifact only, not a release/package publication.
- Published `BiliSubStudio.exe --startup-smoke-test=...` — PASS, including Editor layout smoke at 800×600, 1000×700 and 1500×900.
- Git diff whitespace check — PASS.
- No CI workflow was dispatched; no installer/package/release was built or published.

## Verification level

- Source/call-path PASS: the first Play action requests processed preview at exact source time zero, independently of the edit/cue timeline position.
- Core service contract PASS: requested start zero remains start zero through preview-window selection.
- Compile PASS: full Windows x64 Release solution build, 0 warnings/errors.
- Functional WinUI startup/layout PASS: published executable initialized and exercised the real Editor XAML at all smoke viewports.
- Real-media field playback: not run. The exact first decoded/rendered frame and audible playback from `00:00` still need confirmation with representative media on a user machine.
- Pause/Resume/Seek/end/replay/fullscreen behavior is not claimed by PREVIEW-04 and remains assigned to PREVIEW-05+.

## Constraints to preserve

- Windows desktop app: C# + .NET 10 + WinUI 3; no web/demo replacement.
- Never overwrite source media.
- Translation, ASR and TTS remain local; Chinese → Vietnamese uses the project translation skill.
- Keep the three-column Editor and contextual tool scope; do not turn it into a full NLE.
- Keep one event/owner, one button/handler, no handler-calls-handler and the single `EditorPlaybackController` playback owner.
- Initial Play must stay independent of the edit/cue timeline position and request source time zero.
- Do not add Repair/Fix/Parity layers when the real owner can be corrected; remove dead/superseded logic.
- One small task and targeted regression at a time; do not start PREVIEW-05 until requested/continued after this PREVIEW-04 commit.
- Do not reopen already-passed Subtitle work without a demonstrated regression.
- No version bump, release, push, PR merge or merge without explicit authorization and passing gates.
