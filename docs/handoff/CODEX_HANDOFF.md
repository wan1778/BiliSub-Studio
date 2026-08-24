# Codex handoff — BiliSub Studio

- Base/current upstream main: `origin/main@8eb2a8a2600b37d29bfd0deaae9eeb94b3cda635`
- Current local branch: `main`
- Local base before this task: `f5bed9fac1810088970a33ed2e0e850db400184c`
- Pull request: none; local Preview task commits are not pushed or merged
- Last completed before Preview phase: `SUB-18` (Subtitle/SRT source, Windows CI and contract gates already merged)
- Task completed in this handoff: `PREVIEW-09 — Rapid seek no race/CTS leak`
- Task result: `PASS`
- Task currently running: none
- Exact next task: `PREVIEW-10 — Play to full video end`

## Preview task commits

- `PREVIEW-01`: `96cc8b357de420392a11046b662165e2a7894372` — remove user-facing 12s preview concept.
- `PREVIEW-02`: `540648c173e9345232e5b7221ca99878e7834408` — remove stale 12s preview contract.
- `PREVIEW-03`: `4bae288e03732a171d09d5361be58d039aee7728` — consolidate playback ownership.
- `PREVIEW-04`: `dc0d6c9f0bb7ca838d889ae7d560c871cacc1a0e` — start processed playback at source time zero.
- `PREVIEW-05`: `abca5d2b02bc9d856ca6bd609c93166e7a736cd8` — preserve the processed frame on Pause.
- `PREVIEW-06`: `5d4fa76ba24c26f8af391e4c8b6bb70c9a5fde7f` — resume retained processed playback.
- `PREVIEW-07`: `b802766619aac5be2b8fcd6bb443ddce2703eea8` — seek paused preview to the target frame.
- `PREVIEW-08`: `f5bed9fac1810088970a33ed2e0e850db400184c` — pause old playback before rendering a playing-seek target.
- `PREVIEW-09`: the commit containing this handoff and the latest-request coordinator; resolve its exact SHA with `git rev-parse HEAD` after checkout. The exact literal SHA is also reported in the task completion message because a commit cannot contain its own cryptographic ID.

## Files changed by PREVIEW-09

- `csharp/src/BiliSubStudio.Core/Editor/EditorPreviewRequestCoordinator.cs`
- `csharp/src/BiliSubStudio.App/Pages/EditorPage.Playback.cs`
- `csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs`
- `csharp/scripts/validate_csharp_migration.py`
- `docs/migration/CSHARP_CODE_MAP.generated.md`
- `docs/handoff/CODEX_HANDOFF.md`

## Root cause

Every timeline change launched a fire-and-forget `SeekAsync`. `LoadSegmentAsync` cancelled and immediately disposed the previous `_renderCancellation`, then started the next FFmpeg render without waiting for the cancelled render/process/temp-file cleanup. Older calls could therefore finish their `catch` or `finally` after a newer request had become current, overwrite MediaPlayer/presentation state, briefly run multiple FFmpeg operations, or leave CTS/process lifetime ownership implicit.

## Implementation

- Added one `EditorPreviewRequestCoordinator` as the request/CTS owner used by the single `EditorPlaybackController`.
- A new request cancels its predecessor and waits for the predecessor's complete operation/FFmpeg/temp-file cleanup before it may start.
- Intermediate queued requests cancelled by a newer request never invoke their render delegate; only the newest queued request renders.
- Each request owns and disposes its own CTS in `finally`; callers cancel but never dispose another live request's CTS.
- `CancelAsync` waits until the complete latest-request chain has cleaned up, including any predecessor chain.
- Replaced controller `_renderCancellation` and writable `IsRendering` state with coordinator ownership and `IsRendering => _previewRequests.IsActive`.
- Split `LoadSegmentAsync` into coordination plus `LoadSegmentCoreAsync`, preserving PREVIEW-04 through PREVIEW-08 play/pause/seek semantics.
- `PrepareAsync`, mode exit, source change and unload/reset now await coordinator cancellation cleanup before disposing player/cache state.
- Added a runtime concurrency contract proving serialization, latest-request-wins, skipped superseded request, cleanup-aware cancel and zero active operation after completion.
- Added a fail-first PREVIEW-09 static ownership contract and regenerated the code map.
- Did not change end-of-video/MediaEnded behavior reserved for PREVIEW-10.

## Tests and results

- PREVIEW-09 static ownership contract before implementation — expected FAIL; reproduced the missing latest-request owner.
- `python csharp/scripts/validate_csharp_migration.py` — PASS (`4.0.0-beta.42-csharp-p5`).
- `python csharp/scripts/generate_csharp_code_map.py --check` — PASS; generated map is current.
- Core contract runner with exact SDK `10.0.400` — PASS, 53/53, including `editor rapid preview requests serialize cleanup and run only the latest request`.
- Global log/UI static contract — PASS.
- OCR worker contract — PASS.
- OCR scanner/process-cleanup contract — PASS.
- Range regression runner — PASS, all Range field regressions.
- Full Windows x64 Release solution build — PASS, 0 warnings and 0 errors.
- Self-contained `win-x64` App publish — PASS; local verification artifact only, not a release/package publication.
- Published `BiliSubStudio.exe --startup-smoke-test=...` — PASS; Editor and all shell pages passed at 800×600, 1000×700 and 1500×900.
- Git diff whitespace check — PASS.
- Full `powershell -ExecutionPolicy Bypass -File csharp/scripts/verify.ps1` on a clean PREVIEW-09 commit — PASS, including source identity, PE32+ x64, embedded worker identity, self-contained publish and real WinUI startup smoke.
- No CI workflow was dispatched; no installer/package/release was built or published.

## Verification level

- Source/call-path PASS: all segment loads, including rapid Seek, share one cancellation/serialization owner.
- Runtime concurrency contract PASS: predecessor cleanup completes before successor render, a superseded queued request never runs, explicit cancel waits for cleanup, and the coordinator reaches inactive state.
- Compile PASS: full Windows x64 Release solution build, 0 warnings/errors.
- Functional WinUI startup/layout PASS: published executable initialized and exercised the real Editor XAML at all smoke viewports.
- Real-media rapid-seek field test: not run. Visual/audible confirmation while scrubbing a representative local video, plus observing FFmpeg process count on the user machine, remains required.
- Play-to-end, replay, fullscreen recovery and cache lifecycle are not claimed by PREVIEW-09 and remain assigned to PREVIEW-10+.

## Constraints to preserve

- Windows desktop app: C# + .NET 10 + WinUI 3; no web/demo replacement.
- Never overwrite source media.
- Translation, ASR and TTS remain local; Chinese → Vietnamese uses the project translation skill.
- Keep the three-column Editor and contextual tool scope; do not turn it into a full NLE.
- Keep one event/owner, one button/handler, no handler-calls-handler and the single `EditorPlaybackController` playback owner.
- Initial Play starts at zero; Pause/Resume retain source position; paused and playing Seek target full-video time with the correct final paused/playing intent.
- Rapid Seek must remain latest-request-wins and must wait for cancelled FFmpeg/temp-file cleanup before starting the successor render.
- Internal cache-window coordinates must not leak into user-visible timeline/position semantics.
- Do not add Repair/Fix/Parity layers when the real owner can be corrected; remove dead/superseded logic.
- One small task and targeted regression at a time; do not start PREVIEW-10 until requested/continued after this PREVIEW-09 commit.
- Do not reopen already-passed Subtitle work without a demonstrated regression.
- No version bump, release, push, PR merge or merge without explicit authorization and passing gates.
