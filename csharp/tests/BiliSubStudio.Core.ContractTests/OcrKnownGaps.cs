using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

// Known gaps pending pixel-diff - run separately, not counted in 90/90
internal static class OcrKnownGaps
{
    public static Task RunAsync()
    {
        // legit_dash_handoff: two speakers, dash handoff must stay 2, not merge to 1
        var handoff = new[] {
            new OcrCue(100, 100.36, "你别说了—", 0.99),
            new OcrCue(100.36, 100.42, "—", 0.98),
            new OcrCue(100.42, 101.0, "—你听我说", 0.99),
        };
        var merged = OcrCueReconciler.MergeTouchingIdentical(handoff);
        // With current heuristic (dash via identical after DashNormalize) this will incorrectly merge to 1 or 2 - should be 3
        // This test documents the gap and will PASS only after pixel-diff (stroke IoU) is implemented
        if (merged.Count != 3)
            throw new InvalidOperationException($"Known gap: legit dash handoff merged to {merged.Count} instead of 3 - needs pixel diff");
        return Task.CompletedTask;
    }
}
