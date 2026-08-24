# Codex handoff — BiliSub Studio

- Base/current upstream main: `origin/main@8eb2a8a2600b37d29bfd0deaae9eeb94b3cda635`
- Current local branch: `main`
- Local base before this task: `06b3b18e863285093fcec41111bf3f9da627dab2`
- Pull request: none; local Preview/Blur task commits are not pushed or merged
- Last completed before Blur phase: `PREVIEW-15 — Cleanup preview cache`
- Task completed in this handoff: `BLUR-09 — Cover`
- Task result: `PASS`
- Task currently running: none
- Exact next task after this task passes: `BLUR-10 — Whole video`

## Recent task commits

- `BLUR-01`: `7836c5d42d86f9e331630c2908cf1c7f38cff4cc` — one reviewed event route for every blur input.
- `BLUR-02`: `45642081cb616e9fabadc3576d4694e4ac069b58` — source-pixel-valid mouse region creation.
- `BLUR-03`: `1b8b069edc168153bebcccc7e87610a24e982bac` — single region selection owner and tested topmost hit-testing.
- `BLUR-04`: `014f23de2335a986bc4fe39df032d624c408eeca` — bounded Move geometry and clean cancel transaction.
- `BLUR-05`: `48c3963e05334e267d5b46428f123348c68d9234` — eight source-pixel-valid Resize handles.
- `BLUR-06`: `38ebfcfe6a0f67964d0e3a9bfd03b567e3262aa6` — source-pixel-valid X/Y/W/H inputs without invalid/no-op refresh.
- `BLUR-07`: `61448498d365dacf771ca225b33680a346e74f80` — one Blur strength owner and pixel-safe boxblur Preview/Export policy.
- `BLUR-08`: `06b3b18e863285093fcec41111bf3f9da627dab2` — Mosaic strength owner and matched processed-Preview/Export grid density.
- `BLUR-09`: the commit containing this handoff; resolve its exact SHA with `git rev-parse HEAD` after checkout. The exact SHA is also reported in the task completion message because a commit cannot contain its own cryptographic ID.

## Files changed by BLUR-09

- `csharp/src/BiliSubStudio.Core/Editor/EditorCoverEffect.cs`
- `csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs`
- `csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml.cs`
- `csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs`
- `csharp/scripts/validate_csharp_migration.py`
- `docs/migration/CSHARP_CODE_MAP.generated.md`
- `docs/handoff/CODEX_HANDOFF.md`

## Root cause

Cover already rendered through FFmpeg as an opaque black `drawbox`, but the Editor still treated it as a Blur-strength effect. The shared StrengthBox remained enabled, its handler wrote a meaningless value into region state, switching effects normalized that value with Blur rules, and project reopen retained a meaningless 2–40 strength. Cover Preview/Export geometry relied on the shared normalized-region graph but had no dedicated regression contract.

## Implementation

- Added `EditorCoverEffect` as the Core owner of the compatibility-only stored strength, canonicalized to `0` because Cover has no user-adjustable strength.
- Cover selection disables StrengthBox; the single strength handler exits before touching document, save or Preview state when Cover is active.
- Creating or editing a Cover region writes only `EditorCoverEffect.StoredStrength`; loading a Cover region does not push the compatibility value into the disabled NumberBox.
- Project normalization converts legacy Cover strength values to the canonical value while leaving Blur and Mosaic policies unchanged.
- Kept the existing production `drawbox` implementation: opaque `black@1`, normalized region geometry, shared by static Preview, processed Preview and Export.
- Added a BLUR-09 Core contract for legacy persistence normalization, strength-independent rendering, opaque black fill and exact normalized geometry at 3840×2160 Export versus 1280×720 processed Preview.
- Added a static BLUR-09 ownership contract and regenerated the C# code map.

## Tests and results

- Fail-first `python csharp/scripts/validate_csharp_migration.py` — expected FAIL before implementation: `BLUR-09 Cover must be opaque, strength-free and share normalized Preview Export geometry`.
- `python csharp/scripts/validate_csharp_migration.py` — PASS (`4.0.0-beta.42-csharp-p5`).
- `python csharp/scripts/generate_csharp_code_map.py --check` — PASS.
- Core contract runner with exact SDK `10.0.400` — PASS, 63/63.
- Real local application FFmpeg probes — PASS for opaque Cover at 3840×2160 Export geometry and 1280×720 processed-Preview geometry.
- Targeted Windows x64 Release solution build — PASS, 0 warnings and 0 errors. The first sandboxed attempt failed only because NuGet network access was blocked; the authorized rerun restored and built successfully.
- Git diff whitespace check — PASS.
- Full clean-checkout `csharp/scripts/verify.ps1` — PASS, including source identity, 63/63 contracts, Range regressions, PE32+ x64, embedded worker identity, self-contained publish and real WinUI startup/layout smoke.
- No CI workflow was dispatched; no installer/package/release was built or published.

## Verification level

- Cover Core contract PASS: compatibility state canonicalization and strength-independent filter output.
- Preview/Export graph contract PASS: both paths use opaque black fill and the same normalized rectangle.
- Real FFmpeg graph probes PASS at source and proxy dimensions.
- Static UI route PASS: Cover disables strength and the one strength event owner cannot mutate Cover state.
- Compile PASS: Windows x64 Release solution build, 0 warnings/errors.
- Functional WinUI startup/layout PASS: the published executable initialized the real Editor XAML through the startup smoke path.
- Real interactive Cover create/select/move/resize while static/playing Preview is visible on a physical Windows desktop: not run; still required before release.

## Constraints to preserve

- Windows desktop app: C# + .NET 10 + WinUI 3; no web/demo replacement.
- Never overwrite source media.
- Translation, ASR and TTS remain local; Chinese → Vietnamese uses the project translation skill.
- Keep the three-column Editor and contextual tool scope; do not turn it into a full NLE.
- Keep one event/owner, one button/handler, no handler-calls-handler and the single `EditorPlaybackController` playback owner.
- Cover remains opaque black and strength-free; `EditorCoverEffect` owns its compatibility-only stored value.
- Blur strength remains owned by `EditorBlurStrength`; Mosaic strength/grid geometry remains owned by `EditorMosaicStrength`.
- Do not add Repair/Fix/Parity layers when the real owner can be corrected; remove dead/superseded logic.
- One small task and targeted regression at a time; do not start BLUR-10 until BLUR-09 has final clean verification and its own commit.
- Do not reopen already-passed Subtitle work without a demonstrated regression.
- No version bump, release, push, PR merge or merge without explicit authorization and passing gates.
