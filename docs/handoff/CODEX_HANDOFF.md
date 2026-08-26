# Codex handoff — BiliSub Studio

- Current main/base SHA: `ee5999ab0094c445853d5e83c128696a1e81d049`
- Current branch: `translation-json-id-recovery` (from current `origin/main`)
- PR: none. User authorizes the normal fix → test → `main` → beta updater flow.
- Last completed upstream release: `4.0.41` / `4.0.0-beta.55-csharp-p5`.
- Task source commit: `66050ebda88099f7be159093a135c3c8407a5b4c`.
- Task in progress: `RELEASE-TRANSLATION-JSON-ID-01 — publish the verified single-cue ID recovery through the beta updater`.
- Exact next task: merge the prepared beta release to `main`, wait for the Windows release workflow, then verify the installed application's updater payload.

## Root cause

The active local Vietsub path intentionally sends one cue per Qwen request, but
`ValidateMemoryBatch` still required Qwen to echo the exact opaque technical cue
ID. Qwen can instead return the visible SRT number or a context ID. Its one valid
translation was therefore rejected as `Model trả cue ID thừa, lặp hoặc sai` even
after the strict retry.

## Changes made

- Added `MatchTranslationItems` in `LocalSubtitleTranslationService`, the shared
  translation response matcher.
- For exactly one expected cue, it requires exactly one JSON translation object
  with a string `text`, then assigns it to that sole technical cue ID regardless
  of the model's ID echo. This is unambiguous and does not accept extra items.
- Multi-cue responses still require an exact, unique expected ID for every item.
- `ValidateMemoryBatch` now uses this matcher before glossary/name/relation
  validation; there is no added UI handler or second translation pipeline.
- Added a contract regression that reproduces model ID `"4"` for technical ID
  `"technical-cue-id"` and verifies the text is retained for the only cue.

## Files changed

- `csharp/src/BiliSubStudio.Core/Editor/LocalSubtitleTranslationService.cs`
- `csharp/src/BiliSubStudio.Core/Editor/LocalSubtitleTranslationService.TranslationMemory.cs`
- `csharp/tests/BiliSubStudio.Core.ContractTests/TranslationJsonCompatibilityContract.cs`
- `docs/handoff/CODEX_HANDOFF.md`

## Tests and status

- `dotnet run --project csharp/tests/BiliSubStudio.Core.ContractTests/BiliSubStudio.Core.ContractTests.csproj -c Release -p:NuGetAudit=false`: PASS, 71/71.
- `dotnet build csharp/src/BiliSubStudio.Core/BiliSubStudio.Core.csproj -c Release -p:NuGetAudit=false -v:minimal`: PASS, 0 warnings and 0 errors.
- `python csharp/scripts/verify_translation_skill_contract.py`: PASS.
- Full `./csharp/scripts/verify.ps1`: PASS. This included Windows WinUI compile
  with 0 warnings/errors, 71/71 contracts, range regression, self-contained
  publish, startup smoke, worker identity, PE x64 and checksum readback.
- The 5 GB local Qwen model was not downloaded and run against the user's SRT in
  this task. Real inference and installed-updater field tests remain required.

## Constraints to preserve

- Windows desktop app: C# + .NET 10 + WinUI 3; no web/demo replacement.
- Source media is never overwritten. Translation, ASR and TTS remain local.
- One event and one state/control owner; no event handler calls another handler.
- Fix the actual owner, not a Repair/Fix/Parity layer.
- One small task at a time. Do not reopen passed Subtitle work without a real
  regression.
- Every completed task gets a small commit and GitHub update; release only after
  the relevant Windows gate passes.
