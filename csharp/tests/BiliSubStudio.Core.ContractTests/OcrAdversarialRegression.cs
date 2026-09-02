using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

internal static class OcrAdversarialRegression
{
    public static Task RunAsync()
    {
        // 279/280 - 哇/哦 single substitution flicker, must merge to 1
        var dog = new[] {
            new OcrCue(653.4, 654.567, "哇，没毛狗", 0.99),
            new OcrCue(654.567, 654.967, "哦，没毛狗", 0.98),
        };
        var dogMerged = OcrCueReconciler.MergeTouchingIdentical(dog);
        Check(dogMerged.Count == 1 && dogMerged[0].Start == 653.4 && dogMerged[0].End == 654.967,
            "adversarial 279/280 哇/哦 flicker not merged to 1");

        // 97-99 dash A/B/A
        var dash3 = new[] {
            new OcrCue(50.6, 50.966, "公子，我可以——", 0.99),
            new OcrCue(50.967, 51.033, "公子，我可以—", 0.98),
            new OcrCue(51.033, 51.533, "公子，我可以——", 0.99),
        };
        var dash3Merged = OcrCueReconciler.MergeTouchingIdentical(dash3);
        Check(dash3Merged.Count == 1 && dash3Merged[0].Text == "公子，我可以—",
            "adversarial dash 97-99 not normalized to 1");

        // 112-118 7-line cluster
        var cluster7 = new[] {
            new OcrCue(286.633, 287.466, "殿主，有没有一种可能——", 0.99),
            new OcrCue(287.466, 287.566, "殿主，有没有一种可能—", 0.98),
            new OcrCue(287.566, 288.466, "殿主，有没有一种可能——", 0.99),
            new OcrCue(288.466, 288.666, "殿主，有没有一种可能—", 0.98),
            new OcrCue(288.666, 288.733, "殿主，有没有一种可能——", 0.99),
            new OcrCue(288.733, 288.8, "殿主，有没有一种可能—", 0.98),
            new OcrCue(288.8, 289.166, "殿主，有没有一种可能——", 0.99),
        };
        var clusterMerged = OcrCueReconciler.MergeTouchingIdentical(cluster7);
        Check(clusterMerged.Count == 1 && clusterMerged[0].Start == 286.633 && clusterMerged[0].End == 289.166,
            "adversarial cluster 112-118 not merged to 1");

        // Legitimate 2 real captions must stay 2 - edit=1 gap=0 duration similar
        var legit = new[] {
            new OcrCue(200, 200.5, "你要走吗", 0.99),
            new OcrCue(200.5, 201.1, "你要来吗", 0.99),
        };
        var legitMerged = OcrCueReconciler.MergeTouchingIdentical(legit);
        Check(legitMerged.Count == 2,
            "adversarial legit 走/来 was incorrectly merged");

        var legit2 = new[] {
            new OcrCue(10, 10.6, "你要走吗", 0.99),
            new OcrCue(10.6, 11.2, "你要来吗", 0.99),
            new OcrCue(11.2, 12.0, "我不知道", 0.99),
        };
        Check(OcrCueReconciler.MergeTouchingIdentical(legit2).Count == 3,
            "adversarial legit 3 distinct not kept");

        // NeverMergeAcrossDistinctVisualInstances invariant stub - pixel diff would be low for 走/来
        // For now assert that even with identical gap/duration, distinct single substitution with both >0.45s stays separate
        var distinctLong = new[] {
            new OcrCue(30, 30.6, "你要走吗", 0.99),
            new OcrCue(30.6, 31.2, "你要来吗", 0.99),
        };
        Check(OcrCueReconciler.MergeTouchingIdentical(distinctLong).Count == 2,
            "invariant NeverMergeAcrossDistinctVisualInstances violated for long 走/来");

        return Task.CompletedTask;
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
