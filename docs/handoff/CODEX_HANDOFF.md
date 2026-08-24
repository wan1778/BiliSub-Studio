# Codex handoff — BiliSub Studio

- Base/current upstream main: `origin/main@8eb2a8a2600b37d29bfd0deaae9eeb94b3cda635`
- Current local branch: `main`
- Pull request: none; local task commit is not pushed or merged
- Last completed before this task: `SUB-18` (Subtitle/SRT source, Windows CI and contract gates already merged)
- Task completed in this handoff: `PREVIEW-01 — Remove user-facing 12s concept`
- Task currently running: none after PREVIEW-01 verification and commit
- Exact next task: `PREVIEW-02 — Remove stale 12s test`

## Files changed by PREVIEW-01

- `csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml.cs`
- `ARCHITECTURE.md`
- `docs/migration/CSHARP_WINUI3_CALL_MAP.md`
- `docs/engineering/EDITOR_SPEAKER_TTS_AUDIO_OWNERSHIP.md`
- `docs/plans/2026-08-23-editor-all-in-one.md`
- `docs/handoff/CODEX_HANDOFF.md`

## Root cause

The runtime button already displayed `Xem bản chỉnh`, and `PlayerMediaEnded` already chained processed proxy segments until the end of the source. However, the smoke-contract error and current engineering/architecture text still described the preview as a user-visible 12-second action. That wording exposed an internal FFmpeg/cache window as the UX contract and contradicted continuous full-video playback.

## Change made

- Kept the visible action as `Xem bản chỉnh` and made the smoke failure describe the full-video contract without a 12-second concept.
- Documented continuous playback from the current playhead to source end.
- Documented short proxy/cache windows as internal segments automatically chained by `MediaEnded`.
- Did not change playback ownership, seek/pause/resume behavior, FFmpeg segment duration, cache cleanup or source-media safety.
- Did not touch the numeric `12d` processed-preview contract; that belongs to PREVIEW-02.

## Tests and CI

- `python csharp/scripts/validate_csharp_migration.py` — PASS (`4.0.0-beta.42-csharp-p5` static contract).
- `python csharp/scripts/generate_csharp_code_map.py --check` — PASS; generated map is current.
- `dotnet run --project csharp/tests/BiliSubStudio.Core.ContractTests/BiliSubStudio.Core.ContractTests.csproj -c Release` with local exact SDK `10.0.400` — PASS, 52/52; includes processed-preview slice/render-graph/audio policy.
- `dotnet restore csharp/BiliSubStudio.sln -p:Platform=x64 -p:NuGetAudit=false` — PASS.
- `dotnet build csharp/BiliSubStudio.sln -c Release -p:Platform=x64 -p:UseSharedCompilation=false -p:ContinuousIntegrationBuild=true --no-restore` — PASS, 0 warnings and 0 errors.
- `dotnet publish csharp/src/BiliSubStudio.App/BiliSubStudio.App.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:UseSharedCompilation=false -p:ContinuousIntegrationBuild=true --no-restore` — PASS.
- Published `BiliSubStudio.exe --startup-smoke-test=<sentinel>` — PASS; Editor Loaded/layout smoke ran at 800×600, 1000×700 and 1500×900 and wrote `PASS`.
- Git diff whitespace check — PASS.
- No CI workflow was dispatched from this local task.

## Verification level

- Compile PASS: full Windows x64 Release solution build and self-contained WinUI publish.
- Functional PASS: automated WinUI startup/Loaded/layout smoke, including the Editor preview action contract. This is not real-media playback validation.
- Still requires field test: real Windows playback across multiple internal segment boundaries, pause/resume/seek/fullscreen/failure recovery and cache cleanup. Those behaviors belong to later PREVIEW tasks and are not certified by PREVIEW-01.

## Constraints to preserve

- Windows desktop app: C# + .NET 10 + WinUI 3; no web/demo replacement.
- Never overwrite source media.
- Translation, ASR and TTS remain local; Chinese → Vietnamese uses the project translation skill.
- Keep the three-column Editor and contextual tool scope; do not turn it into a full NLE.
- One event/owner, no handler-calls-handler, no repair-layer files when the real owner can be corrected.
- One small task and targeted regression at a time; no opportunistic PREVIEW-02+ changes.
- Do not reopen already-passed Subtitle work without a demonstrated regression.
- No version bump, release, push, PR merge or merge without explicit authorization and passing gates.
