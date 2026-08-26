# Codex handoff — BiliSub Studio

- Current main/base SHA: `31b524bb8e756458faa6fbeaa3bd124ea17039bf`
- Current branch: `main`.
- PR: none. User authorizes the normal fix → test → `main` → beta updater flow.
- Last completed task: `TRANSLATION-CONTEXT-TARGET-01 — recover a uniquely identified TARGET when Qwen echoes read-only CONTEXT`.
- Task source commit: `8e93af0119d27a427eae5d52ab24f0a21cafcea5`.
- Release preparation commit: `0633797861d8684074190bff206710fbbc689d7f`; published manifest commit: `31b524bb8e756458faa6fbeaa3bd124ea17039bf`.
- Published version: `4.0.43` / `4.0.0-beta.57-csharp-p5`.
- Task in progress: user field test only.
- Exact next task: after the release is available, verify that resuming the supplied SRT after three checkpointed cues advances past cue 4 without `Model bỏ sót hoặc trả thừa cue trong batch.`

## Root cause

When asked for one TARGET cue, Qwen can return `translations` for the adjacent
read-only CONTEXT cues too. `MatchTranslationItems` rejected the complete response
by count before it could examine the uniquely identifiable target, producing
`Model bỏ sót hoặc trả thừa cue trong batch.` after the strict retry.

## Changes made

- Extended the shared `MatchTranslationItems` single-cue recovery path. With
  multiple response items, it accepts only one item whose ID matches the target's
  technical ID or visible SRT number; otherwise it rejects the response.
- Strengthened the prompt: CONTEXT is read-only and `translations` must contain
  only TARGET items.
- Multi-cue responses still require an exact, unique expected ID for every item.
- `ValidateMemoryBatch` now uses this matcher before glossary/name/relation
  validation; there is no added UI handler or second translation pipeline.
- Added a contract regression for a three-item CONTEXT leak, plus an ambiguous
  response rejection regression.

## Files changed

- `csharp/src/BiliSubStudio.Core/Editor/LocalSubtitleTranslationService.cs`
- `csharp/src/BiliSubStudio.Core/Editor/LocalSubtitleTranslationService.TranslationMemory.cs`
- `csharp/tests/BiliSubStudio.Core.ContractTests/TranslationJsonCompatibilityContract.cs`
- `csharp/Directory.Build.props`
- `update/release-notes.json`
- `docs/handoff/CODEX_HANDOFF.md`

## Tests and status

- Direct runtime reproduction on installed `4.0.42` / build `a977c47`: FAIL.
  The supplied SRT resumed from three checkpoints, then received an error at cue
  4: `Model bỏ sót hoặc trả thừa cue trong batch.` The job ended safely with no
  source media overwrite.
- `dotnet run --project csharp/tests/BiliSubStudio.Core.ContractTests/BiliSubStudio.Core.ContractTests.csproj --no-restore`: PASS, 71/71.
- Full `./csharp/scripts/verify.ps1`: PASS. This included Windows WinUI compile
  with 0 warnings/errors, 71/71 contracts, range regression, self-contained
  publish, startup smoke, worker identity, PE x64 and checksum readback.
- The patched build has compile/startup coverage but has not yet completed real
  local-Qwen inference against the supplied SRT; this remains a field test.
- GitHub Actions Windows release run `#32927141512`: PASS for
  `0633797861d8684074190bff206710fbbc689d7f`.
- GitHub release `v4.0.43` is published. `update/beta.json` is channel-ready and
  points to the portable payload with SHA-256
  `2427b2bfb5027a9c6ed0634dc946668f4db9e5358c368c91949b6f880c358d78`.

## Constraints to preserve

- Windows desktop app: C# + .NET 10 + WinUI 3; no web/demo replacement.
- Source media is never overwritten. Translation, ASR and TTS remain local.
- One event and one state/control owner; no event handler calls another handler.
- Fix the actual owner, not a Repair/Fix/Parity layer.
- One small task at a time. Do not reopen passed Subtitle work without a real
  regression.
- Every completed task gets a small commit and GitHub update; release only after
  the relevant Windows gate passes.
