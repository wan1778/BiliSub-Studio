# Codex handoff — BiliSub Studio

- Current main/base SHA: `18a7e2263057b9f815ee24b884f3764466a4a0c7`.
- Current branch: `main`.
- PR: none.
- Last completed task: `OCR-ACCURACY-01` (commit pending at this handoff update).
- Task in progress: none after the targeted test gate; do not start another task until the pushed Windows CI result is known.
- Exact next task: field-test the OCR scan with the supplied video and Chinese SRT, then verify the resulting cue text and boundaries before starting another OCR task.

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

## Files changed

- `internal/ocr/worker.py`
- `csharp/src/BiliSubStudio.Core/Ocr/OcrScanner.cs`
- `csharp/src/BiliSubStudio.Core/Ocr/SubtitleTracker.cs`
- `csharp/tests/BiliSubStudio.Core.ContractTests/OcrTrackerModeRegression.cs`

## Tests and status

- Python worker syntax compilation: PASS.
- `dotnet build csharp/src/BiliSubStudio.Core/BiliSubStudio.Core.csproj -c Release --no-restore`: PASS, 0 warnings / 0 errors.
- `dotnet build csharp/src/BiliSubStudio.App/BiliSubStudio.App.csproj -c Release --no-restore`: PASS, 0 warnings / 0 errors.
- OCR worker and scanner contract scripts: PASS.
- Core contract tests: PASS, 71/71, including the new one-character recovery and midpoint-timing regression.
- Only compile/contract PASS: no complete OCR scan has yet run through the WinUI app with
  `C:\Users\Man PC\Downloads\test`; that full-video field test and Windows CI remain pending.

## Constraints to preserve

- Windows desktop app: C# + .NET 10 + WinUI 3; no web/demo replacement.
- Source media is never overwritten. Translation, ASR and TTS are local only.
- One event and one state/control owner; no event handler calls another handler.
- Fix the actual owner, not a Repair/Fix/Parity layer.
- One small task at a time. No version bump outside the release workflow.
- Commit/push each completed task; release only after the relevant Windows gate passes.
