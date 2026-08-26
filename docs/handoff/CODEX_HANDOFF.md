# Codex handoff — BiliSub Studio

- Current main/base SHA: `dd147526ae40f45a0df0b93df83d6de41bf2bd92`.
- Current branch: `main`.
- PR: none.
- Last completed task: `OCR-ACCURACY-01` (published source commit `dd147526ae40f45a0df0b93df83d6de41bf2bd92`).
- Task in progress: `RELEASE-4.0.48` — rebuild and publish only after the Windows installer gate passes.
- Exact next task: wait for the Windows installer workflow on the release-preparation commit; if it passes, the workflow creates the immutable 4.0.48 beta release and updater manifest.

## Root cause

OCR discarded a legitimate one-character recovery when the next frame had a
similar-but-slightly-lower confidence, because the prior policy required a gain
of more than two characters. It also assigned cue boundaries to the first sampled
frame, which makes timings late by roughly half the scan interval. Thin outlined
subtitle glyphs could be rejected before tracker confirmation.

## Changes made

- Lowered the Paddle OCR detection and recognition gates while retaining the
  consecutive-frame C# tracker guard against false positives.
- Retries only low-confidence detected frames with the existing FFmpeg enhanced
  frame transform and chooses it only when it is more complete or genuinely more
  confident.
- Uses midpoint cue boundaries and accepts a stable one-character CJK recovery
  when its confidence remains close to the prior frame.
- Regenerated the checked-in C# source inventory required by the Windows release
  verifier after the OCR methods were added.
- Prepared the next immutable public beta number `4.0.48` (technical
  `4.0.0-beta.62-csharp-p5`) and release notes for the completed Voice and OCR work.

## Files changed

- `internal/ocr/worker.py`
- `csharp/src/BiliSubStudio.Core/Ocr/OcrScanner.cs`
- `csharp/src/BiliSubStudio.Core/Ocr/SubtitleTracker.cs`
- `csharp/tests/BiliSubStudio.Core.ContractTests/OcrTrackerModeRegression.cs`
- `docs/migration/CSHARP_CODE_MAP.generated.md`
- `csharp/Directory.Build.props`
- `update/release-notes.json`

## Tests and status

- Python worker syntax compilation: PASS.
- `dotnet build csharp/src/BiliSubStudio.Core/BiliSubStudio.Core.csproj -c Release --no-restore`: PASS, 0 warnings / 0 errors.
- `dotnet build csharp/src/BiliSubStudio.App/BiliSubStudio.App.csproj -c Release --no-restore`: PASS, 0 warnings / 0 errors.
- OCR worker and scanner contract scripts: PASS.
- Core contract tests: PASS, 71/71, including the new one-character recovery and midpoint-timing regression.
- Only compile/contract PASS: no complete OCR scan has yet run through the WinUI app with
  `C:\Users\Man PC\Downloads\test`; that full-video field test and Windows CI remain pending.
- The first OCR CI run `#478` failed only because the generated code map was stale;
  local `csharp/scripts/verify.ps1` passes after regeneration. The next CI run is
  the release gate for 4.0.48.

## Constraints to preserve

- Windows desktop app: C# + .NET 10 + WinUI 3; no web/demo replacement.
- Source media is never overwritten. Translation, ASR and TTS are local only.
- One event and one state/control owner; no event handler calls another handler.
- Fix the actual owner, not a Repair/Fix/Parity layer.
- One small task at a time. No version bump outside the release workflow.
- Commit/push each completed task; release only after the relevant Windows gate passes.
