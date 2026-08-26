# Codex handoff — BiliSub Studio

- Current main/base SHA: `e8a911b5242e3fc11fb4f8da29111553b1ed0241`.
- Current branch: `main`.
- PR: none. Release `v4.0.45` is published and its updater manifest is live.
- Last completed task: `TRANSLATION-POLICY-RESTORE-RELATIONS-01`.
- Task source commit: `ba93500f4eae0698f4763ee2b92a6e61ac6a6ee8`.
- Release preparation commit: `6289d27116956ce7287ac48f6d03cfab8d763674` (`4.0.0-beta.59-csharp-p5` / public `4.0.45`).
- Release manifest commit: `e8a911b5242e3fc11fb4f8da29111553b1ed0241`.
- Task in progress: none; awaiting user field test of `4.0.45`.
- Exact next task: reproduce and fix only the next confirmed Editor regression from the user; do not reopen passed Subtitle work without evidence.

## Root cause

`EditorSubtitleProject` saved AI-produced cue text but carried no translation-policy
identity. On reopen, `RestoreSubtitleAsync` copied that old text into the current
SRT before the user pressed Vietsub. Separately, optional `relations` memory from
the local model was treated as fatal when its key was not in the compact
CONTEXT/TARGET window, even when `translations` itself was valid.

## Changes made

- Added `TranslationPolicyKey` to persisted subtitle state. A project from an
  older policy now restores source Chinese only, clears the stale Vietnamese output
  and TTS track, and asks for a clean Vietsub run.
- Advanced local translation policy to `locked-memory-v3`, invalidating old
  checkpoints as well as old project AI output.
- On a successful new translation, the current policy key is persisted with the
  subtitle result; normal reopen restores it only when the key matches.
- Relation-memory keys outside the supplied context are now discarded as
  untrusted optional memory; they cannot abort a valid cue translation or poison
  later address locks.
- Tightened the local prompt so the model omits such relation entries.
- Regenerated the required C# code map and added contract coverage for policy
  persistence, context-gated relations, and the stale-restore owner.

## Files changed

- `csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs`
- `csharp/src/BiliSubStudio.Core/Editor/LocalSubtitleTranslationService.cs`
- `csharp/src/BiliSubStudio.Core/Editor/LocalSubtitleTranslationService.TranslationMemory.cs`
- `csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml.cs`
- `csharp/src/BiliSubStudio.App/Pages/EditorPage.SubtitleCueEditing.cs`
- `csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs`
- `csharp/tests/BiliSubStudio.Core.ContractTests/TranslationQualityPolicyContract.cs`
- `docs/migration/CSHARP_CODE_MAP.generated.md`
- `csharp/Directory.Build.props`
- `update/release-notes.json`

## Tests and status

- `python csharp/scripts/validate_csharp_migration.py`: PASS (`4.0.0-beta.59-csharp-p5`).
- `dotnet run --project csharp/tests/BiliSubStudio.Core.ContractTests/BiliSubStudio.Core.ContractTests.csproj --no-restore`: PASS, 71/71.
- `dotnet build csharp/BiliSubStudio.sln -c Release --no-restore`: PASS, 0 warnings / 0 errors.
- GitHub Actions Windows pipeline `#32929369239`: PASS. It ran the repository's
  Windows compile, contracts, self-contained WinUI publish, startup smoke, package
  integrity and release packaging gates.
- Functional PASS: persisted-state policy gate and relation context gate are
  covered by contracts; public updater manifest points to `v4.0.45` portable ZIP.
- Field test still required: install/update to `4.0.45`, open the supplied SRT at
  `C:\Users\Man PC\Downloads\test`, confirm no old Vietnamese cue appears before
  pressing Vietsub, then run past cue 4 with the local model. The package was not
  installed on the user's active app by this task.

## Constraints to preserve

- Windows desktop app: C# + .NET 10 + WinUI 3; no web/demo replacement.
- Source media is never overwritten. Translation, ASR and TTS remain local.
- One event and one state/control owner; no event handler calls another handler.
- Fix the actual owner, not a Repair/Fix/Parity layer.
- One small task at a time. Do not bump version except a release task.
- Every completed task gets a small commit and GitHub update; release only after
  the relevant Windows gate passes.
