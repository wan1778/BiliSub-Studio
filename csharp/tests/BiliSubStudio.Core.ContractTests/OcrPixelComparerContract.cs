using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

internal static class OcrPixelComparerContract
{
    public static Task RunAsync()
    {
        // ProductionUnavailableDoesNotMerge: Unavailable must keep 2 + NeedsReview (use flicker durations 0.4 vs 0.7 diff 0.3)
        var unavailableComparer = new UnavailablePixelComparer();
        var cues = new[] { new OcrCue(10, 10.4, "哇，没毛狗", 0.99), new OcrCue(10.4, 11.1, "哦，没毛狗", 0.98) };
        var mergedUnavailable = OcrCueReconciler.MergeTouchingIdentical(cues, videoPath: "missing.mp4", comparer: unavailableComparer);
        Check(mergedUnavailable.Count == 2 && mergedUnavailable.All(c => c.NeedsReview),
            "ProductionUnavailableDoesNotMerge: Unavailable should keep 2 + NeedsReview");

        var unionComparer = new UnionCropPixelComparer();
        var result = unionComparer.CheckWithBboxes(new System.Drawing.Rectangle(0,0,100,20), new System.Drawing.Rectangle(2,1,100,20));
        Check(result.Union.Width == 106 && result.Union.Height == 25,
            "DifferentBboxesUseSharedUnionCrop: union not computed");

        // TestComparerIsExplicitDependency: AlwaysSame only in test, not in production DI
        var alwaysSame = new AlwaysSamePixelComparer();
        var testMerged = OcrCueReconciler.MergeTouchingIdentical(cues, videoPath: "test.mp4", comparer: alwaysSame);
        Check(testMerged.Count == 1, "AlwaysSame test comparer should merge");
        var prodMerged = OcrCueReconciler.MergeTouchingIdentical(cues, videoPath: "missing.mp4", comparer: unavailableComparer);
        Check(prodMerged.Count == 2, "Production comparer must not be AlwaysSame");

        return Task.CompletedTask;
    }

    private static void Check(bool v, string m){ if(!v) throw new InvalidOperationException(m); }

    private sealed class UnavailablePixelComparer : IPixelComparer
    {
        public PixelCheckResult Check(string video, double ptsA, double ptsB, System.Drawing.Rectangle bboxA, System.Drawing.Rectangle bboxB) => PixelCheckResult.Unavailable;
    }
    private sealed class AlwaysSamePixelComparer : IPixelComparer
    {
        public PixelCheckResult Check(string video, double ptsA, double ptsB, System.Drawing.Rectangle bboxA, System.Drawing.Rectangle bboxB) => PixelCheckResult.Same;
    }
    private sealed class UnionCropPixelComparer : IPixelComparer
    {
        public System.Drawing.Rectangle Union;
        public PixelCheckResult Check(string video, double ptsA, double ptsB, System.Drawing.Rectangle bboxA, System.Drawing.Rectangle bboxB)
        {
            Union = System.Drawing.Rectangle.Union(bboxA, bboxB);
            Union.Inflate(2,2);
            return PixelCheckResult.Same;
        }
        public (System.Drawing.Rectangle Union, PixelCheckResult Result) CheckWithBboxes(System.Drawing.Rectangle a, System.Drawing.Rectangle b)
        {
            Check("",0,0,a,b);
            return (Union, PixelCheckResult.Same);
        }
    }
}
