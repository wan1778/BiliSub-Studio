# Codex handoff — BiliSub Studio

- Base/current upstream main: `origin/main@8eb2a8a2600b37d29bfd0deaae9eeb94b3cda635`
- Current local branch: `main`
- Pull request: none; local Preview task commits are not pushed or merged
- Last completed before Preview phase: `SUB-18` (Subtitle/SRT source, Windows CI and contract gates already merged)
- Task completed in this handoff: `PREVIEW-07 — Seek paused`
- Task result: `PASS`
- Task currently running: none
- Exact next task: `PREVIEW-08 — Seek playing`

## Preview task commits

- `PREVIEW-01`: `96cc8b357de420392a11046b662165e2a7894372` — remove user-facing 12s preview concept.
- `PREVIEW-02`: `540648c173e9345232e5b7221ca99878e7834408` — remove stale 12s preview contract.
- `PREVIEW-03`: `4bae288e03732a171d09d5361be58d039aee7728` — consolidate playback ownership.
- `PREVIEW-04`: `dc0d6c9f0bb7ca838d889ae7d560c871cacc1a0e` — start processed playback at source time zero.
- `PREVIEW-05`: `abca5d2b02bc9d856ca6bd609c93166e7a736cd8` — preserve the processed frame on Pause.
- `PREVIEW-06`: `5d4fa76ba24c26f8af391e4c8b6bb70c9a5fde7f` — resume retained processed playback.
- `PREVIEW-07`: the commit containing this handoff and the paused-seek target-frame change; resolve its exact SHA with `git rev-parse HEAD` after checkout. The exact literal SHA is also reported in the task completion message because a commit cannot contain its own cryptographic ID.

## Files changed by PREVIEW-07

- `csharp/src/BiliSubStudio.App/Pages/EditorPage.Playback.cs`
- `csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs`
- `csharp/scripts/validate_csharp_migration.py`
- `docs/migration/CSHARP_CODE_MAP.generated.md`
- `docs/handoff/CODEX_HANDOFF.md`

## Root cause

Paused seek previously reused the generic `LoadSegmentAsync(sourcePosition, resume: false)` path but always set `PlaybackSession.Position = TimeSpan.Zero`. Near the video end, `VideoEditorService.PreviewWindow` intentionally shifts the internal cache window earlier so it remains playable. Therefore a request such as source time 299s in a 300s video could load a window beginning at 288s and display its first frame instead of the requested 299s frame. The paused state was implicit rather than a protected path.

## Implementation

- Added the explicit `SeekPausedAsync(sourcePosition)` controller operation, which loads the target segment with `play: false`.
- `SeekAsync` now selects the paused path when playback is not active; it does not autoplay after loading.
- Added `PositionInSegment` to translate requested full-video source time into an offset inside the returned internal cache segment.
- `LoadSegmentAsync` now sets `PlaybackSession.Position` to that offset and keeps the visible timeline/status at the resulting full-video source time.
- Clamped an exact end-of-video request to the final playable frame (`segment.Duration - 0.05s`) instead of seeking to the media-ended boundary.
- Extended the Core processed-preview contract with a near-end shifted-window case proving the requested target remains addressable inside the returned window.
- Added a fail-first PREVIEW-07 static contract requiring the exact offset and `play: false` paused path.
- Did not implement playing-seek state preservation or rapid-seek race/CTS handling reserved for PREVIEW-08/PREVIEW-09.
- Regenerated the code map.

## Tests and results

- PREVIEW-07 paused-seek contract before implementation — expected FAIL, reproduced the missing target-offset/explicit paused path.
- `python csharp/scripts/validate_csharp_migration.py` after implementation — PASS (`4.0.0-beta.42-csharp-p5`).
- `python csharp/scripts/generate_csharp_code_map.py --check` — PASS; generated map is current.
- `dotnet run --project csharp/tests/BiliSubStudio.Core.ContractTests/BiliSubStudio.Core.ContractTests.csproj -c Release` with exact local SDK `10.0.400` — PASS, 52/52; includes the shifted near-end cache-window case.
- `dotnet build csharp/BiliSubStudio.sln -c Release -p:Platform=x64 -p:UseSharedCompilation=false -p:ContinuousIntegrationBuild=true --no-restore` — PASS, 0 warnings and 0 errors.
- Self-contained `win-x64` App publish — PASS; local verification artifact only, not a release/package publication.
- Published `BiliSubStudio.exe --startup-smoke-test=...` — PASS, including Editor layout smoke at 800×600, 1000×700 and 1500×900.
- Git diff whitespace check — PASS.
- No CI workflow was dispatched; no installer/package/release was built or published.

## Verification level

- Source/call-path PASS: paused seek uses `play: false` and targets the requested source time inside the returned cache segment.
- Near-end service/controller contract PASS: an earlier internal cache-window start no longer changes the requested displayed source frame.
- Compile PASS: full Windows x64 Release solution build, 0 warnings/errors.
- Functional WinUI startup/layout PASS: published executable initialized and exercised the real Editor XAML at all smoke viewports.
- Real-media field playback: not run. Visual confirmation that a paused seek displays the representative target frame and remains paused still requires a user-machine test.
- Playing/rapid Seek, end/replay and fullscreen behavior is not claimed by PREVIEW-07 and remains assigned to PREVIEW-08+.

## Constraints to preserve

- Windows desktop app: C# + .NET 10 + WinUI 3; no web/demo replacement.
- Never overwrite source media.
- Translation, ASR and TTS remain local; Chinese → Vietnamese uses the project translation skill.
- Keep the three-column Editor and contextual tool scope; do not turn it into a full NLE.
- Keep one event/owner, one button/handler, no handler-calls-handler and the single `EditorPlaybackController` playback owner.
- Initial Play starts at source zero; Pause retains the frame; Resume reuses the position; paused Seek targets the requested full-video frame and remains paused.
- Internal cache-window coordinates must not leak into user-visible timeline/position semantics.
- Do not add Repair/Fix/Parity layers when the real owner can be corrected; remove dead/superseded logic.
- One small task and targeted regression at a time; do not start PREVIEW-08 until requested/continued after this PREVIEW-07 commit.
- Do not reopen already-passed Subtitle work without a demonstrated regression.
- No version bump, release, push, PR merge or merge without explicit authorization and passing gates.
