# Codex handoff — BiliSub Studio

- Base/current upstream main: `origin/main@8eb2a8a2600b37d29bfd0deaae9eeb94b3cda635`
- Current local branch: `main`
- Local base before this task: `8cff071ee44f9f7f8a90196577486505a33b3470`
- Pull request: none; local Preview/Blur task commits are not pushed or merged
- Last completed before Blur phase: `PREVIEW-15 — Cleanup preview cache`
- Task completed in this handoff: `BLUR-12 — Undo`
- Task result: `PASS`
- Task currently running: none
- Exact next task after this task passes: `BLUR-13 — Redo`

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
- `BLUR-12`: the commit containing this handoff; resolve its exact SHA with `git rev-parse HEAD` after checkout. The exact SHA is also reported in the task completion message because a commit cannot contain its own cryptographic ID.

## Files changed by BLUR-12

- `csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs`
- `csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml.cs`
- `csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs`
- `csharp/scripts/validate_csharp_migration.py`
- `docs/migration/CSHARP_CODE_MAP.generated.md`
- `docs/handoff/CODEX_HANDOFF.md`

## Root cause

Core Undo could restore simple region snapshots, but the UI handler did not clear stale coordinate inputs when undoing the first Add back to an empty document. It had no explicit guard for busy, processed-Preview or active drag state, and the page-level keyboard owner supported Delete/Backspace but not Ctrl+Z. `EditorRegionDocument.ReplaceSelected` could also capture a no-op snapshot when called with state identical to the selected region. Existing coverage proved only one edit and one removal, not ordered multi-step restoration, empty selection or the 50-entry bound.

## Implementation

- Added one independent `TryUndoDocument()` owner used by both `UndoButton.Click` and page Ctrl+Z; no event handler calls another handler.
- Undo is rejected while Editor is busy, processed Preview is active or a pointer drag transaction is open.
- Successful Undo clears draft state, restores selected-region inputs or clears coordinates for the exact empty snapshot, then routes through existing `DocumentChanged` to refresh list/overlay/timeline/actions, autosave and static/processed Preview.
- Ctrl+Z is handled only in Blur mode, outside text editing, and Shift+Ctrl+Z is left untouched for BLUR-13 Redo.
- `EditorRegionDocument.ReplaceSelected` now compares the identity-preserving replacement before `BeginChange`, so a no-op cannot create an Undo entry or clear Redo.
- Added a BLUR-12 Core contract covering empty Undo, ordered edit/add restoration, exact identity/selection, no-op suppression and the bounded 50-entry history; added static UI ownership/guard/save/Preview-route coverage and regenerated the C# code map.

## Tests and results

- Fail-first `python csharp/scripts/validate_csharp_migration.py` — expected FAIL before implementation: `BLUR-12 Undo must have one guarded owner that restores exact document selection inputs persistence and Preview state`.
- `python csharp/scripts/validate_csharp_migration.py` — PASS (`4.0.0-beta.42-csharp-p5`).
- `python csharp/scripts/generate_csharp_code_map.py --check` — PASS.
- Core contract runner with exact SDK `10.0.400` — PASS, 66/66.
- Targeted Windows x64 Release solution build with cached restore — PASS, 0 warnings and 0 errors; this compiled the WinUI `InputKeyboardSource` Ctrl+Z path.
- Git diff whitespace check — PASS.
- Full clean-checkout `csharp/scripts/verify.ps1` on the initial exact BLUR-12 commit — PASS: Windows compile, 66/66 Core contracts, global-log/shell and OCR contracts, range/short-read regression, self-contained WinUI x64 publish, real startup smoke, worker identity, PE32+ x64 and checksum readback.
- No FFmpeg probe was needed because BLUR-12 changes history/UI orchestration, not the filter graph.
- No CI workflow was dispatched; no installer/package/release was built or published.

## Verification level

- Undo Core contract PASS: ordered multi-step restoration, exact stable IDs/selection, empty state, no-op rejection and 50-entry bound.
- Static UI ownership PASS: one XAML Undo Click; button and Ctrl+Z call one independent guarded owner; no handler-to-handler call.
- Persistence/Preview route PASS by source contract: successful Undo calls the existing `DocumentChanged` owner, which renders, queues project save and queues Preview refresh once.
- Compile PASS: Windows x64 Release solution build, 0 warnings/errors.
- Functional WinUI startup/layout on the exact BLUR-12 commit: PASS via clean-checkout real startup smoke.
- Real interactive Undo button/Ctrl+Z after add/edit/move/resize/delete on a physical Windows desktop: not run; still required before release.

## Constraints to preserve

- Windows desktop app: C# + .NET 10 + WinUI 3; no web/demo replacement.
- Never overwrite source media.
- Translation, ASR and TTS remain local; Chinese → Vietnamese uses the project translation skill.
- Keep the three-column Editor and contextual tool scope; do not turn it into a full NLE or add a large multi-track timeline.
- Keep one event/owner, one button/handler, no handler-calls-handler and the single `EditorPlaybackController` playback owner.
- `TryUndoDocument` remains the only UI Undo owner; do not duplicate save/Preview synchronization or call `Undo_Click` from keyboard code.
- `EditorRegionDocument` owns bounded snapshot history and no-op suppression.
- Do not implement Shift+Ctrl+Z or otherwise change Redo until BLUR-13.
- `EditorRegionTimeScope` continues to own whole/timed invariants; current-position buttons retain direct XAML ownership.
- Do not add Repair/Fix/Parity layers when the real owner can be corrected; remove dead/superseded logic.
- One small task and targeted regression at a time; do not start BLUR-13 until BLUR-12 has final clean verification and its own commit.
- Do not reopen already-passed Subtitle work without a demonstrated regression.
- No version bump, release, push, PR merge or merge without explicit authorization and passing gates.
