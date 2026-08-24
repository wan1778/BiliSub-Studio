# Codex handoff — BiliSub Studio

- Base/current upstream main: `origin/main@8eb2a8a2600b37d29bfd0deaae9eeb94b3cda635`
- Current local branch: `main`
- Local base before this task: `45642081cb616e9fabadc3576d4694e4ac069b58`
- Pull request: none; local Preview/Blur task commits are not pushed or merged
- Last completed before Blur phase: `PREVIEW-15 — Cleanup preview cache`
- Task completed in this handoff: `BLUR-03 — Select an existing region`
- Task result: `PASS`
- Task currently running: none
- Exact next task after this task passes: `BLUR-04 — Move a region`

## Recent task commits

- `PREVIEW-15`: `16c7c8427646ef1f6c8988cbad368e38973ed346` — startup/dispose cleanup for active and crash preview artifacts.
- `BLUR-01`: `7836c5d42d86f9e331630c2908cf1c7f38cff4cc` — one reviewed event route for every blur input.
- `BLUR-02`: `45642081cb616e9fabadc3576d4694e4ac069b58` — source-pixel-valid mouse region creation.
- `BLUR-03`: the commit containing this handoff; resolve its exact SHA with `git rev-parse HEAD` after checkout. The exact SHA is also reported in the task completion message because a commit cannot contain its own cryptographic ID.

## Files changed by BLUR-03

- `csharp/src/BiliSubStudio.Core/Editor/EditorRegionGeometry.cs`
- `csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml.cs`
- `csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs`
- `csharp/scripts/validate_csharp_migration.py`
- `docs/migration/CSHARP_CODE_MAP.generated.md`
- `docs/handoff/CODEX_HANDOFF.md`

## Root cause

Region selection behavior existed, but it had no single state owner or functional contract. `RegionList_SelectionChanged`, Blur Overlay selection, empty-canvas deselection and Subtitle interaction called `_document.Select(...)` directly from separate branches. Overlay hit-testing was inline WinUI code and untested, including the required topmost rule for overlapping regions. Compile/startup gates therefore could not detect a selection regression before Move/Resize depended on it.

## Implementation

- Added `EditorRegionGeometry.FindTopmostContaining` as the tested normalized hit-test owner. It walks in reverse draw order so the visually topmost overlapping region wins.
- Invalid/out-of-source pointer coordinates and invalid region geometry do not produce a selection.
- Added `SelectRegion(int index)` as the only Editor UI owner allowed to call `_document.Select`; it clears draft state and synchronizes the selected region into Details inputs.
- Routed Region List selection, Blur Overlay hit selection, empty-canvas deselection and Subtitle-region deselection through that owner.
- Removed redundant input/render refreshes from selection handlers while preserving existing pointer capture and drag setup for later BLUR-04/05 tasks.
- Existing Move and Resize geometry was not changed; Subtitle feature logic was not reopened.
- Added a static BLUR-03 contract requiring the one selection owner, reviewed ListView event route and tested Core hit-test call path.

## Tests and results

- Fail-first `python csharp/scripts/validate_csharp_migration.py` — expected FAIL before implementation: `BLUR-03 region selection must have one state owner and tested topmost hit-testing`.
- `python csharp/scripts/validate_csharp_migration.py` — PASS (`4.0.0-beta.42-csharp-p5`).
- `python csharp/scripts/generate_csharp_code_map.py --check` — PASS; generated map is current.
- Core contract runner with exact SDK `10.0.400` — PASS, 57/57.
- Targeted Windows x64 Release solution build — PASS, 0 warnings and 0 errors.
- Git diff whitespace check — PASS.
- Full clean-checkout `csharp/scripts/verify.ps1` — PASS, including source identity, 57/57 contracts, Range regressions, PE32+ x64, embedded worker identity, self-contained publish and real WinUI startup/layout smoke.
- No CI workflow was dispatched; no installer/package/release was built or published.

## Verification level

- Selection functional contract PASS: topmost overlap selection, lower-only selection, inclusive boundary, outside/NaN rejection, document selection and clean deselection.
- State ownership PASS: all Editor UI calls to `_document.Select` route through one `SelectRegion` method.
- Compile PASS: Windows x64 Release solution build, 0 warnings/errors.
- Functional WinUI startup/layout PASS: the published executable initialized the real Editor XAML at the verification smoke sizes.
- Real interactive region selection via Overlay and Region List on a physical Windows desktop: not run; still required before release.

## Constraints to preserve

- Windows desktop app: C# + .NET 10 + WinUI 3; no web/demo replacement.
- Never overwrite source media.
- Translation, ASR and TTS remain local; Chinese → Vietnamese uses the project translation skill.
- Keep the three-column Editor and contextual tool scope; do not turn it into a full NLE.
- Keep one event/owner, one button/handler, no handler-calls-handler and the single `EditorPlaybackController` playback owner.
- Region selection must route through `SelectRegion`; overlapping Overlay hits must keep reverse draw-order/topmost semantics.
- Do not add Repair/Fix/Parity layers when the real owner can be corrected; remove dead/superseded logic.
- One small task and targeted regression at a time; do not start BLUR-04 until BLUR-03 has final clean verification and its own commit.
- Do not reopen already-passed Subtitle work without a demonstrated regression.
- No version bump, release, push, PR merge or merge without explicit authorization and passing gates.
