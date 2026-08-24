# Codex handoff — BiliSub Studio

- Base/current upstream main: `origin/main@8eb2a8a2600b37d29bfd0deaae9eeb94b3cda635`
- Current local branch: `main`
- Local base before this task: `014f23de2335a986bc4fe39df032d624c408eeca`
- Pull request: none; local Preview/Blur task commits are not pushed or merged
- Last completed before Blur phase: `PREVIEW-15 — Cleanup preview cache`
- Task completed in this handoff: `BLUR-05 — Resize a region`
- Task result: `PASS`
- Task currently running: none
- Exact next task after this task passes: `BLUR-06 — X/Y/W/H numeric inputs`

## Recent task commits

- `BLUR-01`: `7836c5d42d86f9e331630c2908cf1c7f38cff4cc` — one reviewed event route for every blur input.
- `BLUR-02`: `45642081cb616e9fabadc3576d4694e4ac069b58` — source-pixel-valid mouse region creation.
- `BLUR-03`: `1b8b069edc168153bebcccc7e87610a24e982bac` — single region selection owner and tested topmost hit-testing.
- `BLUR-04`: `014f23de2335a986bc4fe39df032d624c408eeca` — bounded Move geometry and clean cancel transaction.
- `BLUR-05`: the commit containing this handoff; resolve its exact SHA with `git rev-parse HEAD` after checkout. The exact SHA is also reported in the task completion message because a commit cannot contain its own cryptographic ID.

## Files changed by BLUR-05

- `csharp/src/BiliSubStudio.Core/Editor/EditorRegionGeometry.cs`
- `csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml.cs`
- `csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs`
- `csharp/scripts/validate_csharp_migration.py`
- `docs/migration/CSHARP_CODE_MAP.generated.md`
- `docs/handoff/CODEX_HANDOFF.md`

## Root cause

All eight visual handles and hit-test directions existed, but Resize geometry was still calculated inline in WinUI code-behind with a fixed normalized minimum of `.002`. That minimum is not equivalent to FFmpeg's two-source-pixel requirement and could create a region later rejected by Preview/Export, especially on smaller sources. Pointer capture also stopped updating Resize when the pointer left the displayed video instead of clamping the active edge to the source boundary. No functional test covered the eight directions, source-pixel minimum or metadata preservation.

## Implementation

- Added the explicit eight-value `EditorRegionResizeHandle` enum and `EditorRegionGeometry.ResizeBy` as the tested Resize geometry owner.
- North, South, East, West and all four corners resize only their owned edges while preserving effect, strength, whole-video/time settings and stable region identity.
- Minimum width/height is calculated against actual source pixels using the same integer edge semantics as `VideoEditorService`; every output remains at least two source pixels.
- Resize clamps at all four source boundaries. Once pointer capture leaves the displayed video, region Resize uses the same bounded normalized pointer policy as Move; Create and Subtitle behavior remain unchanged.
- Removed the superseded `ResizeRegion` code-behind geometry and replaced it with a pure DragKind-to-Core-handle mapping.
- Move and Resize no-op pointer events now return before creating history or rewriting the document.
- Existing one-snapshot commit and clean pointer-cancel transaction from BLUR-04 remain the shared history owner.
- Updated the BLUR-04 regression contract to retain its real invariant without requiring the now-deleted temporary UI Resize method.
- Added a static BLUR-05 contract requiring all eight UI/Core mappings and the source-pixel geometry owner.

## Tests and results

- Fail-first `python csharp/scripts/validate_csharp_migration.py` — expected FAIL before implementation: `BLUR-05 resize must route all eight handles through tested source-pixel geometry`.
- `python csharp/scripts/validate_csharp_migration.py` — PASS (`4.0.0-beta.42-csharp-p5`).
- `python csharp/scripts/generate_csharp_code_map.py --check` — PASS; generated map is current.
- Core contract runner with exact SDK `10.0.400` — PASS, 59/59.
- Targeted Windows x64 Release solution build — PASS, 0 warnings and 0 errors.
- Git diff whitespace check — PASS.
- Full clean-checkout `csharp/scripts/verify.ps1` — PASS, including source identity, 59/59 contracts, Range regressions, PE32+ x64, embedded worker identity, self-contained publish and real WinUI startup/layout smoke.
- No CI workflow was dispatched; no installer/package/release was built or published.

## Verification level

- Resize geometry contract PASS: all eight handles, expected edge ownership, four-boundary clamping, metadata preservation and non-finite rejection.
- Source-pixel minimum PASS: every shrinking handle/corner stops at 2–3 pixels on a 640×360 source and is accepted by the production FFmpeg filter builder.
- Static UI route PASS: eight rendered/hit-tested DragKinds map to eight Core handles; no EditRegion Resize geometry remains in code-behind.
- History path PASS by shared BLUR-04 contract: cancel restores without Undo/Redo artifact; commit keeps one reversible change.
- Compile PASS: Windows x64 Release solution build, 0 warnings/errors.
- Functional WinUI startup/layout PASS: the published executable initialized the real Editor XAML at the verification smoke sizes.
- Real interactive eight-handle Resize/cancel at all four edges on a physical Windows desktop: not run; still required before release.

## Constraints to preserve

- Windows desktop app: C# + .NET 10 + WinUI 3; no web/demo replacement.
- Never overwrite source media.
- Translation, ASR and TTS remain local; Chinese → Vietnamese uses the project translation skill.
- Keep the three-column Editor and contextual tool scope; do not turn it into a full NLE.
- Keep one event/owner, one button/handler, no handler-calls-handler and the single `EditorPlaybackController` playback owner.
- Region Resize must route through `EditorRegionGeometry.ResizeBy`; do not restore normalized magic minimums or inline EditRegion Resize geometry.
- Do not add Repair/Fix/Parity layers when the real owner can be corrected; remove dead/superseded logic.
- One small task and targeted regression at a time; do not start BLUR-06 until BLUR-05 has final clean verification and its own commit.
- Do not reopen already-passed Subtitle work without a demonstrated regression.
- No version bump, release, push, PR merge or merge without explicit authorization and passing gates.
