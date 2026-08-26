# Codex handoff — BiliSub Studio

- Current main/base SHA: `e4a5a311235d908f29b352a20160a75a756ff5e8`
- Current branch: `main`.
- PR: none. User authorizes the normal fix → test → `main` → beta updater flow.
- Last completed task: `TRANSLATION-CULTIVATION-ADDRESS-01 — reject modern Vietnamese address in cultivation subtitles`.
- Task source commit: `09811531e7e341359382578bb4de957088e6c1e0`.
- Release preparation: pending commit for `4.0.44` / `4.0.0-beta.58-csharp-p5`.
- Task in progress: release CI and user field test.
- Exact next task: after the release is available, import the supplied SRT and verify `你走吧` is not saved as `Cậu đi nào`; the new translation policy must start without the old checkpoint.

## Root cause

The cultivation prompt discouraged modern address but the text validator still
accepted it. Qwen could therefore checkpoint a phrase such as `Cậu đi nào` for
`你走吧`, and the old policy would restore it on the next run.

## Changes made

- The local translation prompt now explicitly forbids `cậu`, `bạn`, and `tớ`
  and directs `你/您` to `ngươi`/`ngài` or the evidence-backed relation.
- `ValidateTranslationText` rejects those modern forms, so the ordinary retry
  path owns correction and no UI handler or second pipeline was added.
- `TranslationPolicyKey` moved to `locked-memory-v2`, invalidating checkpoints
  written under the permissive policy.
- Added a reflection contract: `Cậu đi nào` is rejected and `Ngươi đi đi` passes.

## Files changed

- `csharp/src/BiliSubStudio.Core/Editor/LocalSubtitleTranslationService.cs`
- `csharp/src/BiliSubStudio.Core/Editor/LocalSubtitleTranslationService.TranslationMemory.cs`
- `csharp/tests/BiliSubStudio.Core.ContractTests/TranslationJsonCompatibilityContract.cs`
- `csharp/Directory.Build.props`
- `update/release-notes.json`
- `docs/handoff/CODEX_HANDOFF.md`

## Tests and status

- `dotnet run --project csharp/tests/BiliSubStudio.Core.ContractTests/BiliSubStudio.Core.ContractTests.csproj --no-restore`: PASS, 71/71.
- Full `./csharp/scripts/verify.ps1`: pending after version bump.
- The patched build has not yet completed real local-Qwen inference against the
  supplied SRT; this remains a field test.
- GitHub release/CI for `4.0.44` is pending; no claim of update-channel readiness
  until it passes.

## Constraints to preserve

- Windows desktop app: C# + .NET 10 + WinUI 3; no web/demo replacement.
- Source media is never overwritten. Translation, ASR and TTS remain local.
- One event and one state/control owner; no event handler calls another handler.
- Fix the actual owner, not a Repair/Fix/Parity layer.
- One small task at a time. Do not reopen passed Subtitle work without a real
  regression.
- Every completed task gets a small commit and GitHub update; release only after
  the relevant Windows gate passes.
