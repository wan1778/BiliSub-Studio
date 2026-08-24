# Codex handoff — BiliSub Studio

- Base/current upstream main: `origin/main@8eb2a8a2600b37d29bfd0deaae9eeb94b3cda635`
- Current local branch: `main`
- Local base before this task: `81d37c5b51e35e8a4fb3f40643e6ee4d4fefa8ec`
- Pull request: none; local Preview task commits are not pushed or merged
- Last completed before Preview phase: `SUB-18` (Subtitle/SRT source, Windows CI and contract gates already merged)
- Task completed in this handoff: `PREVIEW-14 — Keep preview segmentation invisible to the user`
- Task result: `PASS`
- Task currently running: none
- Exact next task after this task passes: `PREVIEW-15 — Cleanup preview cache`

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
- `PREVIEW-14`: the commit containing this handoff and invisible segment prefetch; resolve its exact SHA with `git rev-parse HEAD` after checkout. The exact literal SHA is also reported in the task completion message because a commit cannot contain its own cryptographic ID.

## Files changed by PREVIEW-14

- `csharp/src/BiliSubStudio.App/Pages/EditorPage.Playback.cs`
- `csharp/scripts/validate_csharp_migration.py`
- `docs/migration/CSHARP_CODE_MAP.generated.md`
- `docs/handoff/CODEX_HANDOFF.md`

## Root cause

The next processed window was not rendered until the current `MediaPlayer` raised `MediaEnded`. Every internal 12-second boundary therefore incurred a full FFmpeg render wait before playback could resume. The same request activity flag represented both user-requested rendering and internal work, so background rendering would disable Editor controls and expose segmentation as UX state. Boundary loads also reused visible preparation/playback status messages.

## Implementation

- Added a one-slot next-window prefetch owned by `EditorPlaybackController`; each successful foreground load renders the exact `NextPreviewStart` window while the current window plays.
- Kept prefetch on the existing latest-request coordinator, so Seek/source change/unload cancels it and waits for FFmpeg cleanup before replacement work starts.
- Split user-visible foreground rendering from internal coordinator activity. Prefetch and its internal fallback no longer disable Timeline/Play/Fullscreen or expose background work as Editor-busy state.
- Added a controller revision guard so rapid Seek/source changes supersede an in-flight prefetch or boundary continuation; an older continuation cannot replace the newer requested position.
- Extracted segment activation from rendering. At `MediaEnded`, a ready prefetched file is attached, mapped to full-source timeline time, the previous file is deleted, and the following window starts prefetching.
- If prefetch failed or was unavailable, the same render pipeline runs without boundary preparation/playback status messages.
- Prepare, mode exit, source change, unload and player-failure recovery cancel background preview work and discard the one-slot prefetched file.
- Added a fail-first PREVIEW-14 static contract for prefetch ownership, foreground-state separation, prefetched continuation and hidden boundary status.
- Updated PREVIEW-03/10 contracts to recognize the new foreground owner and prefetched full-video continuation without weakening rapid-seek serialization.
- Regenerated the code map.
- Did not broaden cleanup to stale/crash-startup cache sweeping reserved for PREVIEW-15.

## Tests and results

- PREVIEW-14 static contract before implementation — expected FAIL; reproduced post-`MediaEnded` rendering and the missing background prefetch owner.
- `python csharp/scripts/validate_csharp_migration.py` — PASS (`4.0.0-beta.42-csharp-p5`).
- `python csharp/scripts/generate_csharp_code_map.py --check` — PASS; generated map is current.
- Core contract runner with exact SDK `10.0.400` — PASS, 54/54.
- Targeted Windows x64 Release solution build — PASS, 0 warnings and 0 errors.
- Git diff whitespace check — PASS.
- One intermediate static-validator invocation after build hit the known generated `Microsoft.UI.Xaml` directory/glob issue; after deleting only `csharp/src/BiliSubStudio.App/bin`, the same validator PASSed. This was an artifact-discovery issue, not a source failure.
- Full clean-checkout `csharp/scripts/verify.ps1` — PASS, including source identity, 54/54 contracts, Range regressions, PE32+ x64, embedded worker identity, self-contained publish and real WinUI startup/layout smoke.
- No CI workflow was dispatched; no installer/package/release was built or published.

## Verification level

- Source/call-path PASS: `foreground load → background next-window prefetch → MediaEnded → revision guard → prefetched activation → next prefetch`, with an internal-status-free fallback.
- Static ownership contract PASS: one prefetch slot, one request coordinator and one controller revision owner exist inside `EditorPlaybackController`.
- Compile PASS: Windows x64 Release solution build, 0 warnings/errors.
- Functional WinUI startup/layout PASS: the published executable initialized the real Editor XAML at the verification smoke sizes.
- Real-media boundary field test: not run. Play a video longer than 24 seconds through at least two internal boundaries and confirm no preparation message/control lock; observe whether the MediaPlayer source swap causes a perceptible A/V hiccup on representative hardware.
- Crash/startup stale-cache cleanup is not claimed by PREVIEW-14 and remains assigned to PREVIEW-15.

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
- One small task and targeted regression at a time; do not start PREVIEW-15 until PREVIEW-14 has its final clean verification and commit.
- Do not reopen already-passed Subtitle work without a demonstrated regression.
- No version bump, release, push, PR merge or merge without explicit authorization and passing gates.
