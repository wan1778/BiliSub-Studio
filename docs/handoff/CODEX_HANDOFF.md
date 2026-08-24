# Codex handoff — BiliSub Studio

- Base/current upstream main: `origin/main@8eb2a8a2600b37d29bfd0deaae9eeb94b3cda635`
- Current local branch: `main`
- Local base before this task: `48c3963e05334e267d5b46428f123348c68d9234`
- Pull request: none; local Preview/Blur task commits are not pushed or merged
- Last completed before Blur phase: `PREVIEW-15 — Cleanup preview cache`
- Task completed in this handoff: `BLUR-06 — X/Y/W/H numeric inputs`
- Task result: `PASS`
- Task currently running: none
- Exact next task after this task passes: `BLUR-07 — Blur strength`

## Recent task commits

- `BLUR-01`: `7836c5d42d86f9e331630c2908cf1c7f38cff4cc` — one reviewed event route for every blur input.
- `BLUR-02`: `45642081cb616e9fabadc3576d4694e4ac069b58` — source-pixel-valid mouse region creation.
- `BLUR-03`: `1b8b069edc168153bebcccc7e87610a24e982bac` — single region selection owner and tested topmost hit-testing.
- `BLUR-04`: `014f23de2335a986bc4fe39df032d624c408eeca` — bounded Move geometry and clean cancel transaction.
- `BLUR-05`: `48c3963e05334e267d5b46428f123348c68d9234` — eight source-pixel-valid Resize handles.
- `BLUR-06`: the commit containing this handoff; resolve its exact SHA with `git rev-parse HEAD` after checkout. The exact SHA is also reported in the task completion message because a commit cannot contain its own cryptographic ID.

## Files changed by BLUR-06

- `csharp/src/BiliSubStudio.Core/Editor/EditorRegionGeometry.cs`
- `csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml.cs`
- `csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs`
- `csharp/scripts/validate_csharp_migration.py`
- `docs/migration/CSHARP_CODE_MAP.generated.md`
- `docs/handoff/CODEX_HANDOFF.md`

## Root cause

The four X/Y/W/H NumberBoxes routed through one handler, but geometry parsing and normalized-bound validation were duplicated in WinUI code-behind. That UI check accepted dimensions greater than zero even when they covered fewer than the two source pixels required by the production FFmpeg filter builder. `RegionCoordinates_ValueChanged` also requested processed-preview work after invalid input and after an unchanged value, despite no document or draft change.

## Implementation

- Added `EditorRegionGeometry.FromPercentInputs` as the source-dimension-aware owner for numeric percentage conversion and geometry validation.
- Numeric input now preserves the selected effect, strength, whole-video/time settings and stable region identity while requiring finite, in-bounds geometry of at least two source pixels in both dimensions.
- `ReadRegionFromInputs` delegates to the Core geometry owner instead of duplicating normalized validation in WinUI code-behind.
- `ApplyInputsToDocument` now reports whether the selected region or draft actually changed.
- The X/Y/W/H event route requests a processed composite refresh only for a real valid change; invalid and no-op values do not rewrite the document, save the project or rebuild Preview.
- Updated the validation message to state the real two-source-pixel requirement.
- Added a BLUR-06 Core contract covering percentage normalization, metadata preservation, source edges, invalid/non-finite/out-of-bounds values, the two-pixel minimum and production FFmpeg filter acceptance.
- Added a static BLUR-06 ownership contract and regenerated the C# code map.

## Tests and results

- Fail-first `python csharp/scripts/validate_csharp_migration.py` — expected FAIL before implementation: `BLUR-06 numeric geometry must use source-pixel validation and suppress invalid or no-op refresh`.
- `python csharp/scripts/validate_csharp_migration.py` — PASS (`4.0.0-beta.42-csharp-p5`).
- `python csharp/scripts/generate_csharp_code_map.py --check` — PASS; generated map is current.
- Core contract runner with exact SDK `10.0.400` — PASS, 60/60.
- Targeted Windows x64 Release solution build — PASS, 0 warnings and 0 errors.
- Git diff whitespace check — PASS.
- Full clean-checkout `csharp/scripts/verify.ps1` — PASS, including source identity, 60/60 contracts, Range regressions, PE32+ x64, embedded worker identity, self-contained publish and real WinUI startup/layout smoke.
- Local harness note: the runner must start with its process cwd at the clean checkout and a non-null `RUNNER_TEMP`; two setup-only attempts stopped before startup smoke until those CI assumptions were supplied. No source change was required.
- No CI workflow was dispatched; no installer/package/release was built or published.

## Verification level

- Numeric geometry contract PASS: normalization, source boundaries, non-finite/invalid rejection, metadata preservation and production filter acceptance.
- Source-pixel minimum PASS: sub-two-pixel W/H values are rejected and pixel-valid minimums are accepted at 640×360.
- Static UI route PASS: numeric parsing is owned by Core and processed Preview refresh is conditional on a real valid change.
- Compile PASS: Windows x64 Release solution build, 0 warnings/errors.
- Functional WinUI startup/layout PASS: the published executable initialized the real Editor XAML through the startup smoke path.
- Real interactive X/Y/W/H editing against visible Preview on a physical Windows desktop: not run; still required before release.

## Constraints to preserve

- Windows desktop app: C# + .NET 10 + WinUI 3; no web/demo replacement.
- Never overwrite source media.
- Translation, ASR and TTS remain local; Chinese → Vietnamese uses the project translation skill.
- Keep the three-column Editor and contextual tool scope; do not turn it into a full NLE.
- Keep one event/owner, one button/handler, no handler-calls-handler and the single `EditorPlaybackController` playback owner.
- Numeric region geometry must route through `EditorRegionGeometry.FromPercentInputs`; do not restore duplicate normalized validation in code-behind.
- Invalid/no-op X/Y/W/H input must not rewrite the document, save the project or request processed-preview work.
- Do not add Repair/Fix/Parity layers when the real owner can be corrected; remove dead/superseded logic.
- One small task and targeted regression at a time; do not start BLUR-07 until BLUR-06 has final clean verification and its own commit.
- Do not reopen already-passed Subtitle work without a demonstrated regression.
- No version bump, release, push, PR merge or merge without explicit authorization and passing gates.
