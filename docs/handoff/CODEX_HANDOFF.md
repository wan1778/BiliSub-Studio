# Codex handoff — BiliSub Studio

- Base/current upstream main: `origin/main@8eb2a8a2600b37d29bfd0deaae9eeb94b3cda635`
- Current local branch: `main`
- Local base before this task: `9f2de0056c3f77c1f007ca5226edda3205dfd80d`
- Pull request: none; local Preview/Blur task commits are not pushed or merged
- Last completed before Blur phase: `PREVIEW-15 — Cleanup preview cache`
- Task completed in this handoff: `BLUR-15 — Preset nếu còn giữ`
- Task result: `PASS`
- Task currently running: none
- Exact next task after this task passes: `BLUR-16 — Reopen project giữ region`

## Recent task commits

- `BLUR-01`: `7836c5d42d86f9e331630c2908cf1c7f38cff4cc` — one reviewed event route for every blur input.
- `BLUR-02`: `45642081cb616e9fabadc3576d4694e4ac069b58` — source-pixel-valid mouse region creation.
- `BLUR-03`: `1b8b069edc168153bebcccc7e87610a24e982bac` — single region selection owner and tested topmost hit-testing.
- `BLUR-04`: `014f23de2335a986bc4fe39df032d624c408eeca` — bounded Move geometry and clean cancel transaction.
- `BLUR-05`: `48c3963e05334e267d5b46428f123348c68d9234` — eight source-pixel-valid Resize handles.
- `BLUR-06`: `38ebfcfe6a0f67964d0e3a9bfd03b567e3262aa6` — source-pixel-valid X/Y/W/H inputs without invalid/no-op refresh.
- `BLUR-07`: `61448498d365dacf771ca225b33680a346e74f80` — one Blur strength owner and pixel-safe boxblur Preview/Export policy.
- `BLUR-08`: `06b3b18e863285093fcec41111bf3f9da627dab2` — Mosaic strength owner and matched processed-Preview/Export grid density.
- `BLUR-09`: `d455a4c31af771e5134aa4628535c6b303629b4a` — opaque strength-free Cover with normalized Preview/Export geometry.
- `BLUR-10`: `ab3b3625ab56e5000fdb12613e011eddb801bc4e` — canonical whole-video time scope across UI, project, Preview and Export.
- `BLUR-11`: `8cff071ee44f9f7f8a90196577486505a33b3470` — strict numeric/current-position timed ranges with direct XAML event ownership.
- `BLUR-12`: `9fb53063526a5b542356b38135c4b3a28f197c64` — guarded Undo button/Ctrl+Z owner with exact input restoration and bounded history.
- `BLUR-13`: `f3dc6db2f2d8ec34c0d3a74f26d1fb5daf1e9868` — guarded Redo button/Ctrl+Y/Shift+Ctrl+Z owner with exact history restoration.
- `BLUR-14`: `9f2de0056c3f77c1f007ca5226edda3205dfd80d` — one guarded Delete owner with exact neighboring selection/history/input restoration.
- `BLUR-15`: the commit containing this handoff; resolve its exact SHA with `git rev-parse HEAD` after checkout. The exact SHA is also reported in the task completion message because a commit cannot contain its own cryptographic ID.

## Files changed by BLUR-15

- `csharp/src/BiliSubStudio.Core/Editor/EditorRegionGeometry.cs`
- `csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml`
- `csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml.cs`
- `csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs`
- `csharp/scripts/validate_csharp_migration.py`
- `docs/migration/CSHARP_CODE_MAP.generated.md`
- `docs/handoff/CODEX_HANDOFF.md`

## Root cause

The current release acceptance explicitly requires Editor multi-region presets, so removing them would regress the required product scope. The two retained handlers nevertheless embedded untested geometry/effect/strength/time literals directly in WinUI, bypassed source-pixel validation and lacked the active-drag guard used by other region actions. The labels `Preset sub` and especially `Preset logo` were ambiguous because the latter creates a Mosaic region to hide a logo rather than adding an Image/Logo overlay.

## Implementation

- Retained both domain-specific presets to satisfy release acceptance: bottom-subtitle Blur and top-right-logo Mosaic.
- Added `EditorRegionPresetKind` and `EditorRegionGeometry.CreatePreset` as the tested Core policy owner for normalized geometry, effect, default strength, whole-video time scope and two-source-pixel validity.
- Both direct-XAML handlers now call one independent `TryAddRegionPreset` owner; it rejects missing/invalid media, busy, processed Preview and active drag states, then adds exactly one history entry, clears draft state, loads inputs and calls `DocumentChanged` once.
- Renamed the visible buttons to `Mờ sub dưới` and `Mosaic logo`, with explicit accessibility names, so the Mosaic action is not confused with adding an Image/Logo overlay.
- Added a BLUR-15 Core contract covering exact geometry/effect/default strength/time, invalid source/duration/kind rejection, filter acceptance, unique document identity and Undo/Redo.
- Added static label/direct-XAML/action-owner/guard/save/Preview-route coverage and regenerated the C# code map.

## Tests and results

- Fail-first `python csharp/scripts/validate_csharp_migration.py` — expected FAIL before implementation: `BLUR-15 retained presets must have clear labels one guarded owner and tested source-pixel-valid whole-video policy`.
- `python csharp/scripts/validate_csharp_migration.py` — PASS (`4.0.0-beta.42-csharp-p5`).
- `python csharp/scripts/generate_csharp_code_map.py --check` — PASS.
- Core contract runner with exact SDK `10.0.400` — PASS, 69/69.
- Targeted Windows x64 Release solution build with cached restore — PASS, 0 warnings and 0 errors; this compiled the WinUI preset owner, labels and Core preset policy.
- Git diff whitespace check — PASS.
- Full clean-checkout `csharp/scripts/verify.ps1` on the commit containing this handoff — PASS: Windows compile, 69/69 Core contracts, global-log/shell and OCR contracts, range/short-read regression, self-contained WinUI x64 publish, real startup smoke, worker identity, PE32+ x64 and checksum readback.
- No standalone FFmpeg probe was needed because the BLUR-15 Core contract passes both preset regions through the production filter builder without changing the filter graph implementation.
- No CI workflow was dispatched; no installer/package/release was built or published.

## Verification level

- Preset Core contract PASS: exact normalized geometry, expected Blur/Mosaic defaults, canonical whole-video range, pixel validity, filter acceptance, unique identity and Undo/Redo.
- Static UI ownership PASS: two distinct direct-XAML Click handlers call one independent guarded preset action owner; no runtime attachment or handler-to-handler call.
- Persistence/Preview route PASS by source contract: successful preset Add calls the existing `DocumentChanged` owner, which renders, queues project save and queues Preview refresh once.
- Compile PASS: Windows x64 Release solution build, 0 warnings/errors.
- Functional WinUI startup/layout on the commit containing this handoff: PASS via clean-checkout real startup smoke.
- Real interactive preset clicks, label fit at 800×600 and visual placement on varied aspect ratios on a physical Windows desktop: not run; still required before release.

## Constraints to preserve

- Windows desktop app: C# + .NET 10 + WinUI 3; no web/demo replacement.
- Never overwrite source media.
- Translation, ASR and TTS remain local; Chinese → Vietnamese uses the project translation skill.
- Keep the three-column Editor and contextual tool scope; do not turn it into a full NLE or add a large multi-track timeline.
- Keep one event/owner, one button/handler, no handler-calls-handler and the single `EditorPlaybackController` playback owner.
- `TryUndoDocument`, `TryRedoDocument`, `TryDeleteSelectedRegion` and `TryAddRegionPreset` remain the only UI history action owners for their operations; do not duplicate save/Preview synchronization or call event handlers from other handlers.
- `EditorRegionGeometry.CreatePreset` owns retained preset geometry/effect/default strength/time/pixel validity; do not reintroduce preset literals in WinUI.
- `EditorRegionDocument` owns bounded snapshots, no-op suppression and divergent-branch invalidation.
- `EditorRegionTimeScope` continues to own whole/timed invariants; current-position buttons retain direct XAML ownership.
- Do not add Repair/Fix/Parity layers when the real owner can be corrected; remove dead/superseded logic.
- One small task and targeted regression at a time; do not start BLUR-16 until BLUR-15 has final clean verification and its own commit.
- Do not reopen already-passed Subtitle work without a demonstrated regression.
- No version bump, release, push, PR merge or merge without explicit authorization and passing gates.
