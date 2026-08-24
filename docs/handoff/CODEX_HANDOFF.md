# Codex handoff — BiliSub Studio

- Base/current upstream main: `origin/main@8eb2a8a2600b37d29bfd0deaae9eeb94b3cda635`
- Current local branch: `main`
- Local base before this task: `2475eb3ed23f12bf7e3afd9af575f601675f69e9`
- Pull request: none; local Preview task commits are not pushed or merged
- Last completed before Preview phase: `SUB-18` (Subtitle/SRT source, Windows CI and contract gates already merged)
- Task completed in this handoff: `PREVIEW-15 — Cleanup preview cache`
- Task result: `PASS`
- Task currently running: none
- Exact next task after this task passes: `BLUR-01 — One handler for each blur input`

## Preview task commits

- `PREVIEW-01`: `96cc8b357de420392a11046b662165e2a7894372` — remove user-facing 12s preview concept.
- `PREVIEW-02`: `540648c173e9345232e5b7221ca99878e7834408` — remove stale 12s preview contract.
- `PREVIEW-03`: `4bae288e03732a171d09d5361be58d039aee7728` — consolidate playback ownership.
- `PREVIEW-04`: `dc0d6c9f0bb7ca838d889ae7d560c871cacc1a0e` — start processed playback at source time zero.
- `PREVIEW-05`: `abca5d2b02bc9d856ca6bd609c93166e7a736cd8` — preserve the processed frame on Pause.
- `PREVIEW-06`: `5d4fa76ba24c26f8af391e4c8b6bb70c9a5fde7f` — resume retained processed playback.
- `PREVIEW-07`: `b802766619aac5be2b8fcd6bb443ddce2703eea8` — seek paused preview to the target frame.
- `PREVIEW-08`: `f5bed9fac1810088970a33ed2e0e850db400184c` — pause old playback before rendering a playing-seek target.
- `PREVIEW-09`: `7bbbd3f8b7da6e5707ced44098923b5f44a90bda` — serialize rapid seek requests and cleanup.
- `PREVIEW-10`: `6d85d7c7c662c095eb9915f635e977360541d8c0` — continue playback across internal segments to source end.
- `PREVIEW-11`: `871c1055f1734ad7b48b514d34beb70fd460355e` — explicit ended state and replay from source zero.
- `PREVIEW-12`: `6a38392d839ca862c30ac2e3b794c234f5a437f8` — controller-owned fullscreen roundtrip and native Esc restoration.
- `PREVIEW-13`: `81d37c5b51e35e8a4fb3f40643e6ee4d4fefa8ec` — stale-safe failed-player replacement and Editor recovery.
- `PREVIEW-14`: `2475eb3ed23f12bf7e3afd9af575f601675f69e9` — one-slot background prefetch with hidden segment boundaries.
- `PREVIEW-15`: the commit containing this handoff and preview-cache cleanup; resolve its exact SHA with `git rev-parse HEAD` after checkout. The exact literal SHA is also reported in the task completion message because a commit cannot contain its own cryptographic ID.

## Files changed by PREVIEW-15

- `csharp/src/BiliSubStudio.Core/Application/BiliSubApplication.cs`
- `csharp/src/BiliSubStudio.Core/Editor/VideoEditorService.cs`
- `csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs`
- `csharp/scripts/validate_csharp_migration.py`
- `docs/migration/CSHARP_CODE_MAP.generated.md`
- `docs/handoff/CODEX_HANDOFF.md`

## Root cause

Playback stop/mode exit, source change and Editor unload deleted the active controller-owned preview, and PREVIEW-14 added cleanup for the prefetched slot. Crash leftovers were different: `VideoEditorService` only scanned when another preview was created and only deleted files older than one day. A normal restart after a crash therefore retained recent `.mp4`, `.rendering.mp4` and `.ass` artifacts. Normal application disposal also had no service-level final sweep.

## Implementation

- Added `VideoEditorService.CleanupPreviewCacheAsync`, scoped to the app-owned `Temp/Editor/Preview` directory and managed `.mp4`/`.ass` artifact types; `.rendering.mp4` is covered by its final `.mp4` extension.
- Reused the existing bounded retry deletion policy for individual segments and full-cache cleanup.
- Application initialization now purges crash leftovers before settings/session/OCR initialization, without requiring Editor to open or a new preview to start.
- Normal application disposal performs a best-effort final service-level sweep after jobs/OCR shutdown; the playback controller remains the deterministic owner of active/prefetched cleanup during Editor lifecycle changes.
- Removed the superseded age-based “older than one day during next render” sweep, which left recent crash files and could eventually target a legitimately paused long-running preview.
- Added a real-filesystem contract using a temporary app root. It verifies deletion of active `.mp4`, crashed `.rendering.mp4` and `.ass`, while preserving an unrelated `.txt` file.
- Added a fail-first PREVIEW-15 static contract requiring startup/dispose cleanup plus the existing three controller lifecycle cancellation paths.
- Regenerated the code map.
- Did not start BLUR work.

## Tests and results

- PREVIEW-15 static contract before implementation — expected FAIL; reproduced missing startup/dispose crash-artifact cleanup.
- `python csharp/scripts/validate_csharp_migration.py` — PASS (`4.0.0-beta.42-csharp-p5`).
- `python csharp/scripts/generate_csharp_code_map.py --check` — PASS; generated map is current.
- Core contract runner with exact SDK `10.0.400` — PASS, 55/55, including the new real-filesystem preview-cache contract.
- Targeted Windows x64 Release solution build — PASS, 0 warnings and 0 errors.
- Git diff whitespace check — PASS.
- Full clean-checkout `csharp/scripts/verify.ps1` — PASS, including source identity, 55/55 contracts, Range regressions, PE32+ x64, embedded worker identity, self-contained publish and real WinUI startup/layout smoke.
- No CI workflow was dispatched; no installer/package/release was built or published.

## Verification level

- Source/call-path PASS: active/prefetched cleanup remains controller-owned; crash recovery is `BiliSubApplication.InitializeAsync → VideoEditorService.CleanupPreviewCacheAsync`; normal close also sweeps during application dispose.
- Filesystem functional contract PASS: all managed fixture artifacts were deleted and an unrelated extension was preserved.
- Compile PASS: Windows x64 Release solution build, 0 warnings/errors.
- Functional WinUI startup/layout PASS: the published executable initialized the real Editor XAML at the verification smoke sizes.
- Real-process crash/restart field test: not run. Kill the app during preview render/playback, reopen normally, and confirm the managed Preview directory contains no crash artifacts; filesystem behavior itself is covered by the contract test.

## Constraints to preserve

- Windows desktop app: C# + .NET 10 + WinUI 3; no web/demo replacement.
- Never overwrite source media.
- Translation, ASR and TTS remain local; Chinese → Vietnamese uses the project translation skill.
- Keep the three-column Editor and contextual tool scope; do not turn it into a full NLE.
- Keep one event/owner, one button/handler, no handler-calls-handler and the single `EditorPlaybackController` playback owner.
- Initial Play and replay both start at source zero; Pause/Resume retain source position; paused and playing Seek retain their intended state.
- Rapid Seek remains latest-request-wins and waits for cancelled FFmpeg/temp-file cleanup.
- Internal segment duration remains an implementation detail; playback continues until the real source end.
- Do not add Repair/Fix/Parity layers when the real owner can be corrected; remove dead/superseded logic.
- One small task and targeted regression at a time; do not start BLUR-01 until PREVIEW-15 has its final clean verification and commit.
- Do not reopen already-passed Subtitle work without a demonstrated regression.
- No version bump, release, push, PR merge or merge without explicit authorization and passing gates.
