# Codex handoff — BiliSub Studio

- Base/current upstream main: `origin/main@8eb2a8a2600b37d29bfd0deaae9eeb94b3cda635`
- Current local branch: `main`
- Local base before this task: `ecde0d57ece79fe335373c5d7403902a34ea2cf4`
- Pull request: none; local Preview/Blur task commits are not pushed or merged
- Last completed before Blur phase: `PREVIEW-15 — Cleanup preview cache`
- Task completed in this handoff: `BLUR-16 — Reopen project giữ region`
- Task result: `PASS`
- Task currently running: none
- Exact next task after this task passes: `BLUR-17 — Preview và Export geometry giống nhau`

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
- `BLUR-15`: `ecde0d57ece79fe335373c5d7403902a34ea2cf4` — retained pixel-valid Blur/Mosaic presets with one tested Core/UI policy owner.
- `BLUR-16`: the commit containing this handoff; resolve its exact SHA with `git rev-parse HEAD` after checkout. The exact SHA is also reported in the task completion message because a commit cannot contain its own cryptographic ID.

## Files changed by BLUR-16

- `csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs`
- `csharp/scripts/validate_csharp_migration.py`
- `docs/handoff/CODEX_HANDOFF.md`

## Root cause

Production already loaded and normalized the candidate project before mutating current Editor state, assigned `candidateProject`, hydrated `_document` from `_project.Regions`, restored selected inputs, rendered the document/Preview and autosaved the exact document regions. The gap was proof: the broad project contract spot-checked only a few fields while reusing the same store instance. It did not simulate a new process/store/document, prove exact order and every Blur/Mosaic/Cover field across repeated save/reopen cycles, prove fresh history state, or verify that project persistence never changes source bytes.

## Implementation

- Kept production code unchanged because the existing store/document/UI owners already satisfy BLUR-16; no repair layer or duplicate hydration method was introduced.
- Added a BLUR-16 static contract pinning load-before-mutate, exact `_document.Reset(_project.Regions)`, no re-add/re-ID path, draft/input reset, document/Preview render and exact `ProjectSnapshot` region autosave.
- Pinned Core normalization on both save and load so stored region order/identity/geometry/time policy cannot silently bypass the canonical project owner.
- Added a dedicated async Core regression that builds a document containing timed Blur, whole-video Mosaic and timed Cover; saves it, reopens through new store/document instances twice and compares every record in order.
- The regression also proves a reopened document selects the first region with fresh Undo/Redo history and that source media bytes remain identical across both save/reopen cycles.

## Tests and results

- Fail-first `python csharp/scripts/validate_csharp_migration.py` — expected FAIL before the dedicated regression existed: `BLUR-16 project reopen must hydrate the exact persisted region document and prove stable non-destructive roundtrip`.
- `python csharp/scripts/validate_csharp_migration.py` — PASS (`4.0.0-beta.42-csharp-p5`).
- `python csharp/scripts/generate_csharp_code_map.py --check` — PASS.
- First Core run after adding the regression — FAIL at test compile with `CS4007` because `SequenceEqual(await ...)` preserved a span across an await; fixed in the test by reading bytes before comparing, with no production change.
- Core contract runner with exact SDK `10.0.400` after the test fix — PASS, 70/70.
- Targeted Windows x64 Release solution build with cached restore — PASS, 0 warnings and 0 errors.
- Git diff whitespace check — PASS.
- Full clean-checkout `csharp/scripts/verify.ps1` on the commit containing this handoff — PASS: Windows compile, 70/70 Core contracts, global-log/shell and OCR contracts, range/short-read regression, self-contained WinUI x64 publish, real startup smoke, worker identity, PE32+ x64 and checksum readback.
- No FFmpeg probe was needed because BLUR-16 changes persistence regression/static coverage only, not geometry or filter generation.
- No CI workflow was dispatched; no installer/package/release was built or published.

## Verification level

- Core persistence contract PASS: exact ordered Blur/Mosaic/Cover records survive two new-store reopen cycles; fresh document selection/history is deterministic; source bytes remain unchanged.
- Static UI hydration PASS: project load completes before current state mutation; regions hydrate without re-add/re-ID; selected inputs, document render, Preview frame and autosave all use the reopened document.
- Compile PASS: Windows x64 Release solution build, 0 warnings/errors.
- Functional WinUI startup/layout on the commit containing this handoff: PASS via clean-checkout real startup smoke.
- Real close-tab/close-app/relaunch and visual verification of reopened regions on a physical Windows desktop: not run; still required before release and later PROJECT/field-test tasks.

## Constraints to preserve

- Windows desktop app: C# + .NET 10 + WinUI 3; no web/demo replacement.
- Never overwrite source media.
- Translation, ASR and TTS remain local; Chinese → Vietnamese uses the project translation skill.
- Keep the three-column Editor and contextual tool scope; do not turn it into a full NLE or add a large multi-track timeline.
- Keep one event/owner, one button/handler, no handler-calls-handler and the single `EditorPlaybackController` playback owner.
- `OpenVideoAsync` must continue loading the candidate project before mutating current state and hydrate regions only through `_document.Reset(_project.Regions)`.
- `ProjectSnapshot` and `EditorProjectStore.NormalizeRegions` remain the save/load owners; do not add a second region sidecar or re-add regions one by one on reopen.
- `EditorRegionDocument` owns bounded snapshots, no-op suppression and divergent-branch invalidation.
- `EditorRegionTimeScope` continues to own whole/timed invariants; current-position buttons retain direct XAML ownership.
- Do not add Repair/Fix/Parity layers when the real owner can be corrected; remove dead/superseded logic.
- One small task and targeted regression at a time; do not start BLUR-17 until BLUR-16 has final clean verification and its own commit.
- Do not reopen already-passed Subtitle work without a demonstrated regression.
- No version bump, release, push, PR merge or merge without explicit authorization and passing gates.
