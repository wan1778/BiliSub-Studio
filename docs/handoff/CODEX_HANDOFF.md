# Codex handoff — BiliSub Studio

- Base/current upstream main: `origin/main@8eb2a8a2600b37d29bfd0deaae9eeb94b3cda635`
- Current local branch: `main`
- Local base before this task: `38ebfcfe6a0f67964d0e3a9bfd03b567e3262aa6`
- Pull request: none; local Preview/Blur task commits are not pushed or merged
- Last completed before Blur phase: `PREVIEW-15 — Cleanup preview cache`
- Task completed in this handoff: `BLUR-07 — Blur strength`
- Task result: `PASS`
- Task currently running: none
- Exact next task after this task passes: `BLUR-08 — Mosaic`

## Recent task commits

- `BLUR-01`: `7836c5d42d86f9e331630c2908cf1c7f38cff4cc` — one reviewed event route for every blur input.
- `BLUR-02`: `45642081cb616e9fabadc3576d4694e4ac069b58` — source-pixel-valid mouse region creation.
- `BLUR-03`: `1b8b069edc168153bebcccc7e87610a24e982bac` — single region selection owner and tested topmost hit-testing.
- `BLUR-04`: `014f23de2335a986bc4fe39df032d624c408eeca` — bounded Move geometry and clean cancel transaction.
- `BLUR-05`: `48c3963e05334e267d5b46428f123348c68d9234` — eight source-pixel-valid Resize handles.
- `BLUR-06`: `38ebfcfe6a0f67964d0e3a9bfd03b567e3262aa6` — source-pixel-valid X/Y/W/H inputs without invalid/no-op refresh.
- `BLUR-07`: the commit containing this handoff; resolve its exact SHA with `git rev-parse HEAD` after checkout. The exact SHA is also reported in the task completion message because a commit cannot contain its own cryptographic ID.

## Files changed by BLUR-07

- `csharp/src/BiliSubStudio.Core/Editor/EditorBlurStrength.cs`
- `csharp/src/BiliSubStudio.Core/Editor/VideoEditorService.cs`
- `csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs`
- `csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml`
- `csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml.cs`
- `csharp/src/BiliSubStudio.App/Pages/EditorPage.ParityFixes.cs`
- `csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs`
- `csharp/scripts/validate_csharp_migration.py`
- `docs/migration/CSHARP_CODE_MAP.generated.md`
- `docs/handoff/CODEX_HANDOFF.md`

## Root cause

Blur strength had no single invariant owner. XAML declared 2–40, `EnsureEditorParityInitialized` wrote Maximum again at runtime, code-behind cast/clamped `StrengthBox.Value` directly, project normalization clamped separately and FFmpeg render clamped a fourth time. Clearing the NumberBox could therefore feed a non-finite value into integer state, while the shared generic handler still requested processed Preview even for invalid or unchanged input. Render also used the requested boxblur radius without respecting FFmpeg's strict pixel bound; a real local FFmpeg probe proved radius 1 fails on a 2×2 crop because radius must remain below half the cropped plane dimension.

## Implementation

- Added `EditorBlurStrength` as the Core owner for the 2–40 user range, default value, finite input normalization, persisted-state normalization and pixel-safe effective radius.
- Strength now has one dedicated XAML event route, integer spin step and no runtime Minimum/Maximum rewrite.
- Invalid/NaN strength leaves the selected region/draft unchanged, shows the real range and does not save or request Preview work.
- Fractional typed values normalize deterministically to the nearest integer; valid unchanged values do not rewrite the document or rebuild Preview.
- Removed the direct NumberBox-to-int cast and retained the selected/draft strength safely while an input is temporarily invalid.
- Project persistence uses the same blur-strength normalization owner.
- Static frame Preview, processed Preview and Export continue through the same `VideoEditorService.BuildFilterCore`; its boxblur luma radius now respects `(min(pixelWidth,pixelHeight)-1)/2` and its chroma expression applies the equivalent per-plane bound.
- Added a BLUR-07 Core contract covering finite/range validation, rounding, persistence clamp, effective pixel radius, selected filter strength and 2×2 safety.
- Added a static BLUR-07 ownership contract and regenerated the C# code map.

## Tests and results

- Fail-first `python csharp/scripts/validate_csharp_migration.py` — expected FAIL before implementation: `BLUR-07 blur strength must have one validated UI owner and a pixel-safe shared render radius`.
- `python csharp/scripts/validate_csharp_migration.py` — PASS (`4.0.0-beta.42-csharp-p5`).
- `python csharp/scripts/generate_csharp_code_map.py --check` — PASS; generated map is current.
- Core contract runner with exact SDK `10.0.400` — PASS, 61/61.
- Targeted Windows x64 Release solution build — PASS, 0 warnings and 0 errors.
- Real local application FFmpeg probe — initial 2×2/radius-1 probe correctly FAILed and exposed the strict runtime bound; corrected 2×2/radius-0 and 30×20/radius-9 production-shape filter probes PASS.
- Git diff whitespace check — PASS.
- Full clean-checkout `csharp/scripts/verify.ps1` — PASS, including source identity, 61/61 contracts, Range regressions, PE32+ x64, embedded worker identity, self-contained publish and real WinUI startup/layout smoke.
- No CI workflow was dispatched; no installer/package/release was built or published.

## Verification level

- Blur strength Core contract PASS: finite 2–40 input, deterministic fractional normalization, persisted clamp and pixel-safe effective radius.
- Preview/Export graph contract PASS: the chosen strength enters the common boxblur graph and tiny regions use a runtime-valid radius.
- Real FFmpeg filter probe PASS for the minimum 2×2 crop and a 30×20 crop using the same luma/chroma bounds as production.
- Static UI route PASS: one dedicated XAML handler, no runtime range owner and invalid/no-op changes do not request processed Preview.
- Compile PASS: Windows x64 Release solution build, 0 warnings/errors.
- Functional WinUI startup/layout PASS: the published executable initialized the real Editor XAML through the startup smoke path.
- Real interactive strength editing while static/playing Preview is visible on a physical Windows desktop: not run; still required before release.

## Constraints to preserve

- Windows desktop app: C# + .NET 10 + WinUI 3; no web/demo replacement.
- Never overwrite source media.
- Translation, ASR and TTS remain local; Chinese → Vietnamese uses the project translation skill.
- Keep the three-column Editor and contextual tool scope; do not turn it into a full NLE.
- Keep one event/owner, one button/handler, no handler-calls-handler and the single `EditorPlaybackController` playback owner.
- Blur strength invariants must remain in `EditorBlurStrength`; do not restore runtime range mutation or direct casts from `StrengthBox.Value`.
- All Preview and Export blur paths must retain the common pixel-safe boxblur radius policy.
- Do not add Repair/Fix/Parity layers when the real owner can be corrected; remove dead/superseded logic.
- One small task and targeted regression at a time; do not start BLUR-08 until BLUR-07 has final clean verification and its own commit.
- Do not reopen already-passed Subtitle work without a demonstrated regression.
- No version bump, release, push, PR merge or merge without explicit authorization and passing gates.
