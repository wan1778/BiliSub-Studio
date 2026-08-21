# RC13-HF3 short-Latin subtitle correctness hotfix

## Field symptom
HF2 Auto runs, but live/recorded OCR cues can contain standalone `A`, `N`, and similar Latin fragments.

## Root cause
The inherited RC12 tracker treated 1-3 ASCII alphanumeric fragments as higher-confirmation candidates. It did not reject them. A persistent false detection could therefore reach the required hit count and become a cue. Parallel segment lanes increase independent tracker warm-up opportunities, so the weakness is more visible.

## Call path
`PaddleOCR -> scan lane -> subtitleTracker.Observe -> promoteCandidate -> lane cues -> cueOwnedBySegment -> reconcileSegmentCues -> live/final SRT`.

## Fix
- ignore standalone 1-3 Latin letters as inconclusive OCR observations;
- do not treat them as empty observations;
- reject the same shape on active cue commit/restore;
- filter stale lane cues in the reconciler;
- preserve numbers, mixed CJK+Latin, and Latin strings >3 letters.

## Regression
- repeated high-confidence A/N/W/OV never becomes a cue;
- short-Latin noise does not split/close a real active Chinese cue;
- numeric-only, mixed CJK+Latin and long Latin text remain valid;
- legacy checkpoint restore removes stale short-Latin garbage;
- reconciler removes stale short-Latin lane cues;
- full test/vet/race/UI maps/browser E2E/Windows build/release validation required.
