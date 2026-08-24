# Codex handoff — BiliSub Studio

- Base/current upstream main: `origin/main@8eb2a8a2600b37d29bfd0deaae9eeb94b3cda635`
- Current local branch: `main`
- Local base before this task: `ed94f7a911ff0f11ac537e41d51c713ee143b537`
- Current local commit: the commit containing this handoff; resolve it with `git rev-parse HEAD`
- Remote continuation branch: `origin/editor-preview-blur-01-17`
- Pull request: none; the continuation branch is pushed but not merged into `main`
- Last completed before this task: `BLUR-16 — Reopen project giữ region`
- Task completed in this handoff: `BLUR-17 — Preview và Export geometry giống nhau`
- Task result: `PASS`
- Task currently running: none
- Exact next task: `AUDIO-01 — Một audio state owner`

## Recent task commits

- `BLUR-12`: `9fb53063526a5b542356b38135c4b3a28f197c64` — guarded Undo owner and bounded exact history.
- `BLUR-13`: `f3dc6db2f2d8ec34c0d3a74f26d1fb5daf1e9868` — guarded Redo owner and divergent-branch invalidation.
- `BLUR-14`: `9f2de0056c3f77c1f007ca5226edda3205dfd80d` — one guarded Delete owner with exact selection/history restoration.
- `BLUR-15`: `ecde0d57ece79fe335373c5d7403902a34ea2cf4` — retained pixel-valid Blur/Mosaic presets with one policy owner.
- `BLUR-16`: `ed94f7a911ff0f11ac537e41d51c713ee143b537` — exact ordered region persistence across new-store reopen cycles.
- `BLUR-17`: the commit containing this handoff; its exact SHA is reported in the completion message because a commit cannot contain its own cryptographic ID.

## Files changed by BLUR-17

- `csharp/scripts/validate_csharp_migration.py`
- `csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs`
- `docs/handoff/CODEX_HANDOFF.md`

No production source file changed in BLUR-17.

## Geometry call path and root cause

Committed regions originate in the single `EditorRegionDocument` and enter both routes through `CurrentEditRequest`, which copies `_document.Regions` without a second coordinate model.

```text
EditorRegionDocument normalized X/Y/Width/Height
  ├─ still Preview frame
  │    CurrentPreviewRegions → GetPreviewFrameJpegAsync → BuildFilter → BuildFilterCore
  ├─ processed playback Preview
  │    CurrentEditRequest → CreatePreviewSegmentAsync → BuildPreviewSlice
  │    → BuildFilterCore(inputLabel: previewbase)
  └─ Export
       CurrentEditRequest → StartEditor → RunAsync → BuildFilter → BuildFilterCore

BuildFilterCore → RegionPixels → Blur/Mosaic/Cover crop/overlay/drawbox
```

Production already had the correct owner: still Preview and Export build their filters at source dimensions, while processed playback preserves the same normalized region values after scaling and then uses the same private `RegionPixels` conversion inside `BuildFilterCore`. The only expected difference is integer pixel quantization at the lower Preview resolution; every projected boundary must stay strictly below one Preview pixel from the scaled Export boundary.

The defect was missing proof. Existing effect-specific tests covered friendly geometry or one effect at a time, but there was no BLUR-17 contract proving all three effects, multiple ordered asymmetric regions, an edge-touching region, a source with exact Preview scaling, a source requiring even-dimension rounding, and the three real UI/service entry routes.

## Implementation

- Kept production code unchanged; no repair/parity layer and no second pixel geometry helper were added.
- Added a static BLUR-17 gate that pins the still-frame, processed-playback and Export routes to `BuildFilterCore`, pins both UI routes to `CurrentEditRequest`, and requires exactly one `RegionPixels(region, request.SourceWidth, request.SourceHeight)` conversion site.
- Added a dedicated Core regression for ordered Blur, Mosaic and Cover regions, including asymmetric normalized coordinates and a Cover touching the right/bottom source edges.
- Tested both 3840×2160 → 1280×720 and 4096×2160 → 1280×676 processed Preview dimensions.
- The regression proves exact normalized X/Y/Width/Height/identity preservation, exact filter crop/overlay/drawbox pixels in both graphs, and less than one Preview pixel of unavoidable quantization on left/top/right/bottom projected Export boundaries.

## Tests, CI and results

- Fail-first `python csharp/scripts/validate_csharp_migration.py` — expected FAIL before the dedicated regression existed: `BLUR-17 Preview frame playback and Export must share one tested normalized region geometry owner`.
- `python csharp/scripts/validate_csharp_migration.py` — PASS (`4.0.0-beta.42-csharp-p5`).
- First Core invocation through the system `dotnet` shim — environment FAIL before compilation because that host had no SDK; rerun with the workspace-pinned SDK `10.0.400`.
- Core contract runner with exact SDK `10.0.400` — PASS, 71/71.
- `python csharp/scripts/generate_csharp_code_map.py --check` — PASS; no generated map change was required because production symbols did not change.
- Targeted Windows x64 Release solution build — PASS, 0 warnings and 0 errors.
- Git whitespace check — PASS.
- Full clean-checkout `csharp/scripts/verify.ps1` on the commit containing this handoff — PASS: Windows compile, 71/71 Core contracts, global-log/shell and OCR contracts, range/short-read regression, self-contained WinUI x64 publish, real startup smoke, worker identity, PE32+ x64 and checksum readback.
- No CI workflow was dispatched; no installer/package/release was built or published.

## Verification level

- Functional Core/filter contract PASS: all committed Blur/Mosaic/Cover geometry preserves exact normalized values and uses the same pixel conversion/filter geometry across Preview and Export, within strictly less than one Preview pixel after resolution quantization.
- Static UI/service route PASS: still Preview, processed playback Preview and Export remain pinned to the same request/geometry owner.
- Compile PASS: Windows x64 Release solution build, 0 warnings/errors.
- Functional WinUI startup on the commit containing this handoff: PASS via clean-checkout startup smoke.
- Real visual side-by-side comparison of processed Preview versus an exported frame on a physical Windows desktop: not run; still required by later field-test/release gates.

## Constraints to preserve

- Windows desktop app: C# + .NET 10 + WinUI 3; no web/demo replacement.
- Never overwrite source media.
- Translation, ASR and TTS remain local; Chinese → Vietnamese uses the project translation skill.
- Keep the three-column Editor and contextual tool scope; do not turn it into a full NLE or add a large multi-track timeline.
- Keep one event/owner, one button/handler, no handler-calls-handler and the single `EditorPlaybackController` playback owner.
- `CurrentEditRequest` remains the committed Editor request owner; draft regions may appear only in the still editing Preview and must not enter Export before commit.
- `BuildFilterCore` and its single `RegionPixels` call remain the shared Blur/Mosaic/Cover render geometry owner.
- Processed Preview may scale resolution internally, but must preserve exact normalized geometry and keep every projected boundary within one Preview pixel of scaled Export geometry.
- Do not add Repair/Fix/Parity layers when the real owner can be corrected; remove dead/superseded logic.
- One small task and targeted regression at a time; do not start `AUDIO-01` in the BLUR-17 task.
- Do not reopen already-passed Subtitle work without a demonstrated regression.
- No version bump, release, push, PR or merge without explicit authorization and passing gates.
