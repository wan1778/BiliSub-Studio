# Codex handoff — BiliSub Studio

- Base/current upstream main: `origin/main@8eb2a8a2600b37d29bfd0deaae9eeb94b3cda635`
- Current local branch: `main`
- Local base before this task: `7836c5d42d86f9e331630c2908cf1c7f38cff4cc`
- Pull request: none; local Preview/Blur task commits are not pushed or merged
- Last completed before Blur phase: `PREVIEW-15 — Cleanup preview cache`
- Task completed in this handoff: `BLUR-02 — Create a region with the mouse`
- Task result: `PASS`
- Task currently running: none
- Exact next task after this task passes: `BLUR-03 — Select an existing region`

## Recent task commits

- `PREVIEW-15`: `16c7c8427646ef1f6c8988cbad368e38973ed346` — startup/dispose cleanup for active and crash preview artifacts.
- `BLUR-01`: `7836c5d42d86f9e331630c2908cf1c7f38cff4cc` — one reviewed event route for every blur input.
- `BLUR-02`: the commit containing this handoff; resolve its exact SHA with `git rev-parse HEAD` after checkout. The exact SHA is also reported in the task completion message because a commit cannot contain its own cryptographic ID.

## Files changed by BLUR-02

- `csharp/src/BiliSubStudio.Core/Editor/EditorRegionGeometry.cs`
- `csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml.cs`
- `csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs`
- `csharp/scripts/validate_csharp_migration.py`
- `docs/migration/CSHARP_CODE_MAP.generated.md`
- `docs/handoff/CODEX_HANDOFF.md`

## Root cause

The Overlay already had one pointer-event route, but mouse-region creation calculated normalized geometry inline in `Overlay_PointerMoved` and committed the draft directly in `FinishDrag`. Unlike the Add button path, the mouse path did not call `ValidateRegion`. A drag could therefore enter the document even when its source-pixel rectangle was below FFmpeg's two-pixel minimum or its current start/end settings were invalid; the defect surfaced only later during Preview or Export.

## Implementation

- Added `EditorRegionGeometry.FromNormalizedDrag` as the single tested owner for converting a bounded normalized drag into a source-pixel-valid `EditRegion`.
- Forward and reverse drags now produce the same normalized rectangle; non-finite coordinates, invalid source dimensions and rectangles below two source pixels are rejected before document mutation.
- `Overlay_PointerMoved` delegates creation geometry to the Core owner while preserving the current blur effect, strength, whole-video and time-range settings.
- `FinishDrag` delegates commit to `TryCommitCreatedRegion`, which calls `ValidateRegion` before `_document.Add` and reports validation failures without adding an invalid region.
- Existing select, move and resize paths were not changed; Subtitle behavior was not reopened.
- Added static regression coverage that pins the geometry owner and validation-before-add call path.

## Tests and results

- Fail-first `python csharp/scripts/validate_csharp_migration.py` — expected FAIL before implementation: `BLUR-02 mouse creation must use tested normalized geometry and validate before document commit`.
- `python csharp/scripts/validate_csharp_migration.py` — PASS (`4.0.0-beta.42-csharp-p5`).
- `python csharp/scripts/generate_csharp_code_map.py --check` — PASS; generated map is current.
- Core contract runner with exact SDK `10.0.400` — PASS, 56/56.
- Targeted Windows x64 Release solution build — PASS, 0 warnings and 0 errors.
- Git diff whitespace check — PASS.
- Full clean-checkout `csharp/scripts/verify.ps1` — PASS, including source identity, 56/56 contracts, Range regressions, PE32+ x64, embedded worker identity, self-contained publish and real WinUI startup/layout smoke.
- No CI workflow was dispatched; no installer/package/release was built or published.

## Verification level

- Geometry functional contract PASS: forward/reverse drag normalization, source-bound clamping, preservation of blur settings, rejection of non-finite input and rejection below the two-pixel source minimum.
- Document boundary contract PASS: mouse-created regions are validated before `_document.Add`.
- Compile PASS: Windows x64 Release solution build, 0 warnings/errors.
- Functional WinUI startup/layout PASS: the published executable initialized the real Editor XAML at the verification smoke sizes.
- Real interactive mouse create/drag field test on a physical Windows desktop: not run; still required before release.

## Constraints to preserve

- Windows desktop app: C# + .NET 10 + WinUI 3; no web/demo replacement.
- Never overwrite source media.
- Translation, ASR and TTS remain local; Chinese → Vietnamese uses the project translation skill.
- Keep the three-column Editor and contextual tool scope; do not turn it into a full NLE.
- Keep one event/owner, one button/handler, no handler-calls-handler and the single `EditorPlaybackController` playback owner.
- Mouse-region creation must keep source-pixel validation before document mutation; do not restore inline UI geometry or bypass `ValidateRegion`.
- Do not add Repair/Fix/Parity layers when the real owner can be corrected; remove dead/superseded logic.
- One small task and targeted regression at a time; do not start BLUR-03 until BLUR-02 has final clean verification and its own commit.
- Do not reopen already-passed Subtitle work without a demonstrated regression.
- No version bump, release, push, PR merge or merge without explicit authorization and passing gates.
