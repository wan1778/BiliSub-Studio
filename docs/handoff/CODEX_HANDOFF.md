# Codex handoff — BiliSub Studio

- Base/current upstream main: `origin/main@8eb2a8a2600b37d29bfd0deaae9eeb94b3cda635`
- Current local branch: `main`
- Local base before this task: `1b8b069edc168153bebcccc7e87610a24e982bac`
- Pull request: none; local Preview/Blur task commits are not pushed or merged
- Last completed before Blur phase: `PREVIEW-15 — Cleanup preview cache`
- Task completed in this handoff: `BLUR-04 — Move a region`
- Task result: `PASS`
- Task currently running: none
- Exact next task after this task passes: `BLUR-05 — Resize a region`

## Recent task commits

- `PREVIEW-15`: `16c7c8427646ef1f6c8988cbad368e38973ed346` — startup/dispose cleanup for active and crash preview artifacts.
- `BLUR-01`: `7836c5d42d86f9e331630c2908cf1c7f38cff4cc` — one reviewed event route for every blur input.
- `BLUR-02`: `45642081cb616e9fabadc3576d4694e4ac069b58` — source-pixel-valid mouse region creation.
- `BLUR-03`: `1b8b069edc168153bebcccc7e87610a24e982bac` — single region selection owner and tested topmost hit-testing.
- `BLUR-04`: the commit containing this handoff; resolve its exact SHA with `git rev-parse HEAD` after checkout. The exact SHA is also reported in the task completion message because a commit cannot contain its own cryptographic ID.

## Files changed by BLUR-04

- `csharp/src/BiliSubStudio.Core/Editor/EditorRegionGeometry.cs`
- `csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs`
- `csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml.cs`
- `csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs`
- `csharp/scripts/validate_csharp_migration.py`
- `docs/migration/CSHARP_CODE_MAP.generated.md`
- `docs/handoff/CODEX_HANDOFF.md`

## Root cause

Region Move was mixed into the same inline code-behind method as Resize and had no functional geometry contract. Pointer capture continued outside the Overlay, but `TryNormalize` rejected positions outside the displayed video, so the region stopped at the last in-bounds pointer event instead of clamping cleanly to the source edge. Pointer cancellation restored through public Undo, which put a canceled drag into Redo history. A zero-delta/clamped event could also start an unnecessary history transaction.

## Implementation

- Added `EditorRegionGeometry.MoveBy` as the tested Move geometry owner. It preserves size, effect, strength, whole-video/time settings and stable region identity while clamping X/Y to source bounds.
- Non-finite deltas or invalid source geometry leave the original region unchanged.
- Region Move now uses a clamped normalized pointer only after pointer capture leaves the video; Create, Resize and Subtitle behavior retain their existing normalization rules.
- Move events that do not change X/Y return before capturing history or rewriting the document.
- Removed the superseded EditRegion Move branch from code-behind; the remaining `ResizeRegion` method contains only Resize behavior reserved for BLUR-05.
- Added `EditorRegionDocument.CancelChange` and routed pointer cancellation through it, restoring the pre-drag snapshot without creating a Redo entry for the canceled move.
- Committed Move still creates one history snapshot and remains Undo/Redo capable.
- Subtitle and Image placement behavior were not changed.
- Added a static BLUR-04 contract requiring the bounded Core owner, clamped pointer path, separated Resize method and clean cancel transaction.

## Tests and results

- Fail-first `python csharp/scripts/validate_csharp_migration.py` — expected FAIL before implementation: `BLUR-04 move must use tested bounded geometry and cancel its history transaction`.
- `python csharp/scripts/validate_csharp_migration.py` — PASS (`4.0.0-beta.42-csharp-p5`).
- `python csharp/scripts/generate_csharp_code_map.py --check` — PASS; generated map is current.
- Core contract runner with exact SDK `10.0.400` — PASS, 58/58.
- Targeted Windows x64 Release solution build — PASS, 0 warnings and 0 errors.
- Git diff whitespace check — PASS.
- Full clean-checkout `csharp/scripts/verify.ps1` — PASS, including source identity, 58/58 contracts, Range regressions, PE32+ x64, embedded worker identity, self-contained publish and real WinUI startup/layout smoke.
- No CI workflow was dispatched; no installer/package/release was built or published.

## Verification level

- Move geometry contract PASS: normal delta, four-edge clamping, full metadata preservation and non-finite rejection.
- Move history contract PASS: canceled drag restores state with no Undo/Redo artifact; committed drag can Undo and Redo.
- Static UI route PASS: region Move uses the Core owner, captured out-of-video pointer is clamped and no-op Move events do not start history.
- Compile PASS: Windows x64 Release solution build, 0 warnings/errors.
- Functional WinUI startup/layout PASS: the published executable initialized the real Editor XAML at the verification smoke sizes.
- Real interactive region Move/cancel at all four edges on a physical Windows desktop: not run; still required before release.

## Constraints to preserve

- Windows desktop app: C# + .NET 10 + WinUI 3; no web/demo replacement.
- Never overwrite source media.
- Translation, ASR and TTS remain local; Chinese → Vietnamese uses the project translation skill.
- Keep the three-column Editor and contextual tool scope; do not turn it into a full NLE.
- Keep one event/owner, one button/handler, no handler-calls-handler and the single `EditorPlaybackController` playback owner.
- Region Move must route through `EditorRegionGeometry.MoveBy`; pointer cancellation must not become a user-visible Undo/Redo action.
- Do not add Repair/Fix/Parity layers when the real owner can be corrected; remove dead/superseded logic.
- One small task and targeted regression at a time; do not start BLUR-05 until BLUR-04 has final clean verification and its own commit.
- Do not reopen already-passed Subtitle work without a demonstrated regression.
- No version bump, release, push, PR merge or merge without explicit authorization and passing gates.
