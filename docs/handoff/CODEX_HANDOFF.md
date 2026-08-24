# Codex handoff — BiliSub Studio

- Base/current upstream main: `origin/main@8eb2a8a2600b37d29bfd0deaae9eeb94b3cda635`
- Current local branch: `main`
- Pull request: none; local Preview task commits are not pushed or merged
- Last completed before Preview phase: `SUB-18` (Subtitle/SRT source, Windows CI and contract gates already merged)
- Task completed in this handoff: `PREVIEW-03 — One playback controller`
- Task result: `PASS`
- Task currently running: none
- Exact next task: `PREVIEW-04 — Play from start`

## Preview task commits

- `PREVIEW-01`: `96cc8b357de420392a11046b662165e2a7894372` — remove user-facing 12s preview concept.
- `PREVIEW-02`: `540648c173e9345232e5b7221ca99878e7834408` — remove stale 12s preview contract.
- `PREVIEW-03`: the commit containing this handoff and the playback ownership refactor; resolve its exact SHA with `git rev-parse HEAD` after checkout. The exact literal SHA is also reported in the task completion message because a commit cannot contain its own cryptographic ID.

## Files changed by PREVIEW-03

- `csharp/src/BiliSubStudio.App/Pages/EditorPage.Playback.cs`
- `csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml`
- `csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml.cs`
- `csharp/src/BiliSubStudio.App/Pages/EditorPage.ParityBootstrap.cs`
- `csharp/src/BiliSubStudio.App/Pages/EditorPage.ParityFixes.cs`
- `csharp/src/BiliSubStudio.App/Pages/EditorPage.Images.cs`
- `csharp/src/BiliSubStudio.App/Pages/EditorPage.SubtitleCueEditing.cs`
- `csharp/scripts/validate_csharp_migration.py`
- `docs/migration/CSHARP_CODE_MAP.generated.md`
- `docs/handoff/CODEX_HANDOFF.md`

## Root cause

Processed-preview ownership was split across the main page and a later parity bootstrap. `EditorPage.xaml.cs` directly owned `MediaPlayer`, preview-mode/render flags, segment coordinates/path and playback CTS, while `ParityBootstrap` attached the visible Play/Pause button at runtime, mirrored enable state through an invisible compatibility `PlaybackButton`, and directly played/paused the player. Source switching and Unloaded also duplicated player/segment cleanup. This left more than one playback state/event/cleanup owner and made later playback fixes race-prone.

## Implementation

- Added one cohesive nested `EditorPlaybackController` in `EditorPage.Playback.cs`.
- Moved the only `MediaPlayer`, processed-preview mode/render state, segment window/path, render CTS, MediaPlayer events, play/pause, seek, fullscreen, volume/mute and playback cleanup behind that controller.
- Bound the real `PlayerPlayPauseButton` once from XAML; its event handler only forwards to `ToggleAsync`.
- Removed the dead `Playback_Click`, runtime Play/Pause `+=`, invisible compatibility `PlaybackButton`, and page-level playback fields/methods.
- Routed timeline, Subtitle cue seek, Image/Blur edit guards and auto-composite rebuild through the controller's state/API.
- Unified source-switch and Unloaded playback disposal through the controller while preserving frame-preview cancellation and project/image saving in their existing owners.
- Preserved the current internal segmented FFmpeg/cache playback behavior. PREVIEW-04+ behavior was not implemented opportunistically.
- Updated the static ownership contract and regenerated the code map.

## Tests and results

- `python csharp/scripts/validate_csharp_migration.py` — PASS (`4.0.0-beta.42-csharp-p5`), including PREVIEW-03 single-owner/event contracts.
- `python csharp/scripts/generate_csharp_code_map.py --check` — PASS; generated map is current.
- `python csharp/scripts/verify_global_log_ui_contract.py` — PASS.
- `dotnet run --project csharp/tests/BiliSubStudio.Core.ContractTests/BiliSubStudio.Core.ContractTests.csproj -c Release` with exact local SDK `10.0.400` — PASS, 52/52.
- `dotnet build csharp/BiliSubStudio.sln -c Release -p:Platform=x64 -p:UseSharedCompilation=false -p:ContinuousIntegrationBuild=true --no-restore` — PASS, 0 warnings and 0 errors.
- `dotnet run --project csharp/tests/BiliSubStudio.RangeRegression/BiliSubStudio.RangeRegression.csproj -c Release -p:NuGetAudit=false` — PASS, all Range field regressions.
- Self-contained `win-x64` App publish — PASS; local verification artifact only, not a release/package publication.
- Published `BiliSubStudio.exe --startup-smoke-test=...` — PASS, including Editor layout smoke at 800×600, 1000×700 and 1500×900.
- Git diff whitespace check — PASS.
- No CI workflow was dispatched; no installer/package/release was built or published.

## Verification level

- Source architecture PASS: playback state, events and cleanup now have one controller owner.
- Compile PASS: full Windows x64 Release solution build, 0 warnings/errors.
- Functional WinUI startup/layout PASS: published executable initialized and exercised the real Editor XAML at all smoke viewports.
- Functional playback PASS: not claimed. No real media was played on this machine during PREVIEW-03.
- Still requires field test in later tasks: play from start, pause/resume, paused/playing/rapid seek, full-video end/replay, fullscreen roundtrip, player failure recovery, invisible segment boundaries and final cache cleanup.

## Constraints to preserve

- Windows desktop app: C# + .NET 10 + WinUI 3; no web/demo replacement.
- Never overwrite source media.
- Translation, ASR and TTS remain local; Chinese → Vietnamese uses the project translation skill.
- Keep the three-column Editor and contextual tool scope; do not turn it into a full NLE.
- Keep one event/owner, one button/handler, no handler-calls-handler and the single `EditorPlaybackController` playback owner.
- Do not add Repair/Fix/Parity layers when the real owner can be corrected; remove dead/superseded logic.
- One small task and targeted regression at a time; do not start PREVIEW-04 until requested/continued after this PREVIEW-03 commit.
- Do not reopen already-passed Subtitle work without a demonstrated regression.
- No version bump, release, push, PR merge or merge without explicit authorization and passing gates.
