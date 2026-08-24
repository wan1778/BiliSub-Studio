# Codex handoff — BiliSub Studio

- Base/current upstream main: `origin/main@8eb2a8a2600b37d29bfd0deaae9eeb94b3cda635`
- Current local branch: `main`
- Local base before this task: `ab3b3625ab56e5000fdb12613e011eddb801bc4e`
- Pull request: none; local Preview/Blur task commits are not pushed or merged
- Last completed before Blur phase: `PREVIEW-15 — Cleanup preview cache`
- Task completed in this handoff: `BLUR-11 — Time range`
- Task result: `PASS`
- Task currently running: none
- Exact next task after this task passes: `BLUR-12 — Undo`

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
- `BLUR-11`: the commit containing this handoff; resolve its exact SHA with `git rev-parse HEAD` after checkout. The exact SHA is also reported in the task completion message because a commit cannot contain its own cryptographic ID.

## Files changed by BLUR-11

- `csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml`
- `csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml.cs`
- `csharp/src/BiliSubStudio.App/Pages/EditorPage.ParityBootstrap.cs`
- `csharp/src/BiliSubStudio.App/Pages/EditorPage.ParityFixes.cs`
- `csharp/src/BiliSubStudio.Core/Editor/EditorRegionTimeScope.cs`
- `csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs`
- `csharp/src/BiliSubStudio.Core/Editor/VideoEditorService.cs`
- `csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs`
- `csharp/scripts/validate_csharp_migration.py`
- `docs/migration/CSHARP_CODE_MAP.generated.md`
- `docs/handoff/CODEX_HANDOFF.md`

## Root cause

The Start/End NumberBoxes had no source-duration limits and render code silently clamped negative or over-duration values, so UI/project state could disagree with Preview/Export. The two “use current position” buttons had no XAML events; `ParityBootstrap` attached handlers with runtime `+=`, while the handlers and enable-state owner lived in `ParityFixes`. Numeric and current-position handlers also requested processed Preview after invalid or no-op changes.

## Implementation

- Start/End NumberBoxes now expose decimal 0.1-second input, minimum 0 and a dynamic maximum equal to the current source duration; no large multi-track timeline was added.
- Both current-position buttons now have one explicit XAML Click owner in `EditorPage.xaml.cs`; removed their compatibility fields, runtime `+=` bindings and handlers from Parity files.
- `RefreshEditorActions` is the sole enable-state owner for Start/End inputs and current-position buttons.
- Extended `EditorRegionTimeScope` with strict timed-range validation: `0 ≤ Start < End ≤ duration`, plus a tested current-position default range that remains valid at the source end.
- Numeric and current-position handlers update document/Preview only after a valid real change; NaN/out-of-range input leaves selected region state unchanged and reports validation text.
- Project load may migrate legacy values using the old effective clamp, but current project saves are strict and cannot persist a UI/render mismatch.
- Static frame Preview, processed-Preview slicing and Export validate the same range owner; FFmpeg no longer performs a silent clamp different from UI state.
- Added a BLUR-11 Core contract for invalid numeric input, legacy migration, current-position defaults, exact Export guard and processed-Preview shift/clip/exclusion; added static event/owner coverage and regenerated the C# code map.

## Tests and results

- Fail-first `python csharp/scripts/validate_csharp_migration.py` — expected FAIL before implementation: `BLUR-11 timed range must own numeric and current-position input without runtime handler patching or silent render clamps`.
- `python csharp/scripts/validate_csharp_migration.py` — PASS (`4.0.0-beta.42-csharp-p5`).
- `python csharp/scripts/generate_csharp_code_map.py --check` — PASS.
- Core contract runner with exact SDK `10.0.400` — PASS, 65/65.
- Real local application FFmpeg probe — PASS: effect was absent at t=0/3 and present only at t=1/2 for `between(t,1,2)`.
- Targeted Windows x64 Release solution build with cached restore — PASS, 0 warnings and 0 errors.
- Git diff whitespace check — PASS.
- Full clean-checkout `csharp/scripts/verify.ps1` — PASS, including source identity, 65/65 contracts, Range regressions, PE32+ x64, embedded worker identity, self-contained publish and real WinUI startup/layout smoke.
- No CI workflow was dispatched; no installer/package/release was built or published.

## Verification level

- Timed-range Core contract PASS: numeric validation, current-position defaults, legacy migration and source-end behavior.
- Event ownership PASS: one XAML Click per current-position button; no runtime attach or Parity handler remains.
- Preview/Export graph contract PASS: exact source times and processed-Preview shift/clip/exclusion share one policy.
- Real FFmpeg four-frame timing probe PASS.
- Compile PASS: Windows x64 Release solution build, 0 warnings/errors.
- Functional WinUI startup/layout PASS: the published executable initialized the real Editor XAML through the startup smoke path.
- Real interactive decimal entry and current-position clicks while scrubbing a physical Windows desktop: not run; still required before release.

## Constraints to preserve

- Windows desktop app: C# + .NET 10 + WinUI 3; no web/demo replacement.
- Never overwrite source media.
- Translation, ASR and TTS remain local; Chinese → Vietnamese uses the project translation skill.
- Keep the three-column Editor and contextual tool scope; do not turn it into a full NLE or add a large multi-track timeline.
- Keep one event/owner, one button/handler, no handler-calls-handler and the single `EditorPlaybackController` playback owner.
- `EditorRegionTimeScope` owns whole-video and timed-range invariants; do not restore UI/render/persistence clamp duplication.
- Current-position button Click and enable-state ownership remain in XAML/`EditorPage.xaml.cs`; do not restore Parity runtime bindings.
- Cover remains opaque and strength-free; Blur/Mosaic strength owners remain unchanged.
- Do not add Repair/Fix/Parity layers when the real owner can be corrected; remove dead/superseded logic.
- One small task and targeted regression at a time; do not start BLUR-12 until BLUR-11 has final clean verification and its own commit.
- Do not reopen already-passed Subtitle work without a demonstrated regression.
- No version bump, release, push, PR merge or merge without explicit authorization and passing gates.
