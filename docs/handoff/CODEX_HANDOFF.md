# Codex handoff — BiliSub Studio

- Base/current upstream main: `origin/main@8eb2a8a2600b37d29bfd0deaae9eeb94b3cda635`
- Current local branch: `main`
- Local base before this task: `d455a4c31af771e5134aa4628535c6b303629b4a`
- Pull request: none; local Preview/Blur task commits are not pushed or merged
- Last completed before Blur phase: `PREVIEW-15 — Cleanup preview cache`
- Task completed in this handoff: `BLUR-10 — Whole video`
- Task result: `PASS`
- Task currently running: none
- Exact next task after this task passes: `BLUR-11 — Time range`

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
- `BLUR-10`: the commit containing this handoff; resolve its exact SHA with `git rev-parse HEAD` after checkout. The exact SHA is also reported in the task completion message because a commit cannot contain its own cryptographic ID.

## Files changed by BLUR-10

- `csharp/src/BiliSubStudio.Core/Editor/EditorRegionTimeScope.cs`
- `csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs`
- `csharp/src/BiliSubStudio.Core/Editor/VideoEditorService.cs`
- `csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml.cs`
- `csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs`
- `csharp/scripts/validate_csharp_migration.py`
- `docs/migration/CSHARP_CODE_MAP.generated.md`
- `docs/handoff/CODEX_HANDOFF.md`

## Root cause

Whole-video rendering itself already omitted FFmpeg's timed `enable=between(...)`, but the invariant was duplicated across UI construction, static Preview and processed-Preview slicing. Project normalization preserved stale `Start/End` values even when `WholeVideo=true`, so reopen could expose contradictory disabled values and later convert them into an unintended range when the toggle changed. `WholeToggle_Toggled` also requested a processed Preview after rejected/no-op input state.

## Implementation

- Added `EditorRegionTimeScope` as the Core owner of the BLUR-10 invariant: a whole-video region is canonicalized to `Start=0`, `End=source duration`.
- UI region construction delegates whole-video state to that owner; timed input behavior remains unchanged for BLUR-11.
- The one XAML `WholeToggle_Toggled` handler notifies processed Preview only when document/draft state actually changes.
- Project save and reopen normalize legacy/stale whole-video ranges against the current source duration while preserving timed regions unchanged.
- Static frame Preview and processed-Preview slicing use the same owner; processed segments normalize a whole region to the full internal segment window.
- Export retains the existing production rule that whole-video regions have no FFmpeg time guard.
- Added a BLUR-10 Core contract for canonical state, full-duration activation, project reopen, processed Preview and Export; added a static ownership contract and regenerated the C# code map.

## Tests and results

- Fail-first `python csharp/scripts/validate_csharp_migration.py` — expected FAIL before implementation: `BLUR-10 Whole video must have one canonical time-scope owner across UI persistence Preview and Export`.
- `python csharp/scripts/validate_csharp_migration.py` — PASS (`4.0.0-beta.42-csharp-p5`).
- `python csharp/scripts/generate_csharp_code_map.py --check` — PASS.
- Core contract runner with exact SDK `10.0.400` — PASS, 64/64.
- Real local application FFmpeg probe — PASS: one whole-video Cover filter produced the same black-region frame hash across all three sampled frames.
- Targeted Windows x64 Release solution build — PASS, 0 warnings and 0 errors. The first sandboxed restore attempt failed only on blocked NuGet signature access; the authorized rerun restored and built successfully.
- Git diff whitespace check — PASS.
- Full clean-checkout `csharp/scripts/verify.ps1` — PASS, including source identity, 64/64 contracts, Range regressions, PE32+ x64, embedded worker identity, self-contained publish and real WinUI startup/layout smoke.
- No CI workflow was dispatched; no installer/package/release was built or published.

## Verification level

- Whole-video Core contract PASS: canonical `0 → duration` state, full-duration activation and timed-region non-interference.
- Persistence contract PASS: stale whole-video Start/End values reopen as `0 → source duration`.
- Preview/Export graph contract PASS: whole-video regions remain present in processed segments and neither graph has a timed enable guard.
- Real FFmpeg multi-frame probe PASS.
- Static UI route PASS: one WholeToggle handler and no no-op processed-Preview request.
- Compile PASS: Windows x64 Release solution build, 0 warnings/errors.
- Functional WinUI startup/layout PASS: the published executable initialized the real Editor XAML through the startup smoke path.
- Real interactive Whole video toggle with static/playing Preview on a physical Windows desktop: not run; still required before release.

## Constraints to preserve

- Windows desktop app: C# + .NET 10 + WinUI 3; no web/demo replacement.
- Never overwrite source media.
- Translation, ASR and TTS remain local; Chinese → Vietnamese uses the project translation skill.
- Keep the three-column Editor and contextual tool scope; do not turn it into a full NLE.
- Keep one event/owner, one button/handler, no handler-calls-handler and the single `EditorPlaybackController` playback owner.
- `EditorRegionTimeScope` owns whole-video canonicalization; do not duplicate `Start=0/End=duration` in UI, persistence or Preview.
- Do not implement or change timed-range UX until BLUR-11.
- Cover remains opaque and strength-free; Blur/Mosaic strength owners remain unchanged.
- Do not add Repair/Fix/Parity layers when the real owner can be corrected; remove dead/superseded logic.
- One small task and targeted regression at a time; do not start BLUR-11 until BLUR-10 has final clean verification and its own commit.
- Do not reopen already-passed Subtitle work without a demonstrated regression.
- No version bump, release, push, PR merge or merge without explicit authorization and passing gates.
