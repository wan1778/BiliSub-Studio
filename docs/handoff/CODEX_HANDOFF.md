# Codex handoff — BiliSub Studio

- Base/current upstream main: `origin/main@8eb2a8a2600b37d29bfd0deaae9eeb94b3cda635`
- Current local branch: `main`
- Local base before this task: `16c7c8427646ef1f6c8988cbad368e38973ed346`
- Pull request: none; local Preview task commits are not pushed or merged
- Last completed before Preview phase: `SUB-18` (Subtitle/SRT source, Windows CI and contract gates already merged)
- Task completed in this handoff: `BLUR-01 — One handler for each blur input`
- Task result: `PASS`
- Task currently running: none
- Exact next task after this task passes: `BLUR-02 — Create a region with the mouse`

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
- `PREVIEW-15`: `16c7c8427646ef1f6c8988cbad368e38973ed346` — startup/dispose cleanup for active and crash preview artifacts.

## Blur task commits

- `BLUR-01`: the commit containing this handoff and blur-input event ownership contract; resolve its exact SHA with `git rev-parse HEAD` after checkout. The exact literal SHA is also reported in the task completion message because a commit cannot contain its own cryptographic ID.

## Files changed by BLUR-01

- `csharp/scripts/validate_csharp_migration.py`
- `docs/handoff/CODEX_HANDOFF.md`

## Root cause

No production duplicate remained at the BLUR-01 baseline. The nine blur input controls already used one reviewed XAML event binding each, and none had runtime `+=`/`-=` replacement. The gap was regression protection: the generic XAML validator only checked that a named handler existed, so it would not fail if a later partial added a second runtime handler or called an event handler directly.

## Implementation

- Added a BLUR-01 event-map contract for `RegionXBox`, `RegionYBox`, `RegionWidthBox`, `RegionHeightBox`, `EffectBox`, `StrengthBox`, `WholeToggle`, `StartBox` and `EndBox`.
- The contract requires the exact reviewed XAML event/handler pair for every control, rejects runtime attach/detach for the same control/event, and requires exactly one handler implementation with no handler-to-handler call.
- Shared methods remain intentional: X/Y/W/H share `RegionCoordinates_ValueChanged`; Strength/Start/End share `EditInput_ValueChanged`. Each control still owns one event route.
- Production code was not changed because the audited source already satisfies BLUR-01. No cleanup/fix layer or duplicate handler was added.
- Did not start region creation behavior reserved for BLUR-02.

## Tests and results

- BLUR-01 baseline event-map audit — PASS; no production duplicate was present, so no artificial fail-first production edit was made.
- `python csharp/scripts/validate_csharp_migration.py` — PASS (`4.0.0-beta.42-csharp-p5`).
- `python csharp/scripts/generate_csharp_code_map.py --check` — PASS; generated map is current.
- Core contract runner with exact SDK `10.0.400` — PASS, 55/55.
- Targeted Windows x64 Release solution build — PASS, 0 warnings and 0 errors.
- Git diff whitespace check — PASS.
- Full clean-checkout `csharp/scripts/verify.ps1` — PASS, including source identity, 55/55 contracts, Range regressions, PE32+ x64, embedded worker identity, self-contained publish and real WinUI startup/layout smoke.
- No CI workflow was dispatched; no installer/package/release was built or published.

## Verification level

- Event ownership PASS: all nine blur inputs have one XAML event route and zero runtime event replacement routes.
- Handler ownership PASS: `RegionCoordinates_ValueChanged`, `EffectBox_SelectionChanged`, `EditInput_ValueChanged` and `WholeToggle_Toggled` each have one implementation and are not called by another handler.
- Compile PASS: Windows x64 Release solution build, 0 warnings/errors.
- Functional WinUI startup/layout PASS: the published executable initialized the real Editor XAML at the verification smoke sizes.
- Real interactive blur-input field test: not run. BLUR-01 changes only regression coverage; input behavior itself will be exercised in BLUR-02+ feature tasks.

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
- One small task and targeted regression at a time; do not start BLUR-02 until BLUR-01 has its final clean verification and commit.
- Do not reopen already-passed Subtitle work without a demonstrated regression.
- No version bump, release, push, PR merge or merge without explicit authorization and passing gates.
