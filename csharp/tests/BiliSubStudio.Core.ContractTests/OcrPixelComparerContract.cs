using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

internal static class OcrPixelComparerContract
{
    public static async Task RunAsync()
    {
        var roi = new OcrRegion(0, 0, 1, 1);
        var geo = new OcrFrameGeometry(0, 0, 1920, 1080, 1280, 320, 0, 0, 1280, 320);
        var tb = new RationalTimeBase(1, 90000);
        long spts = 5000000;
        // Full provenance so the pair passes the generic gate (temporal+spatial) to the comparer.
        // Missing provenance => blocked, split + NeedsReview, do NOT call the comparer (test 1).
        OcrCue Cue(double s, double e, string t, double c, double pts) => new(s, e, t, c, 0, 0, false,
            new PixelObservation(pts, new OcrBox(10, 10, 200, 40), roi, geo, Interlocked.Increment(ref spts), tb),
            new PixelObservation(pts, new OcrBox(10, 10, 200, 40), roi, geo, Interlocked.Increment(ref spts), tb));
        var unavailableComparer = new UnavailablePixelComparer();
        var cues = new[] { Cue(10, 10.4, "哇，没毛狗", 0.99, 10.4), Cue(10.4, 11.1, "哦，没毛狗", 0.98, 10.4) };
        var mergedUnavailable = await OcrCueReconciler.MergeTouchingIdenticalAsync(cues, videoPath: "missing.mp4", comparer: unavailableComparer, cancellationToken: CancellationToken.None);
        Check(mergedUnavailable.Count == 2 && mergedUnavailable.All(c => c.NeedsReview),
            "ProductionUnavailableDoesNotMerge: Unavailable should keep 2 + NeedsReview");

        var union = OcrCropPlanner.PlanSharedCrop(new System.Drawing.Rectangle(0, 0, 100, 20), new System.Drawing.Rectangle(2, 1, 100, 20), 1920, 1080);
        Check(union.Left == 0 && union.Top == 0 && union.Right == 104 && union.Bottom == 23,
            "DifferentBboxesUseSharedUnionCrop: union not computed");

        var alwaysSame = new AlwaysSamePixelComparer();
        var testMerged = await OcrCueReconciler.MergeTouchingIdenticalAsync(cues, videoPath: "test.mp4", comparer: alwaysSame, cancellationToken: CancellationToken.None);
        Check(testMerged.Count == 1, "AlwaysSame test comparer should merge");
        var prodMerged = await OcrCueReconciler.MergeTouchingIdenticalAsync(cues, videoPath: "missing.mp4", comparer: unavailableComparer, cancellationToken: CancellationToken.None);
        Check(prodMerged.Count == 2, "Production comparer must not be AlwaysSame");
    }

    private static void Check(bool v, string m){ if(!v) throw new InvalidOperationException(m); }

    private sealed class UnavailablePixelComparer : IPixelComparer
    {
        public Task<PixelComparison> CheckAsync(string video, PixelObservation left, PixelObservation right, CancellationToken ct)
        {
            var l = new FrameSample(left.Pts, double.NaN, double.NaN);
            var r = new FrameSample(right.Pts, double.NaN, double.NaN);
            return Task.FromResult(new PixelComparison(PixelCheckResult.Unavailable, l, r, System.Drawing.Rectangle.Empty, null, null, null));
        }
    }
    private sealed class AlwaysSamePixelComparer : IPixelComparer
    {
        public Task<PixelComparison> CheckAsync(string video, PixelObservation left, PixelObservation right, CancellationToken ct)
        {
            var l = new FrameSample(left.Pts, left.Pts, 0);
            var r = new FrameSample(right.Pts, right.Pts, 0);
            var union = OcrCropPlanner.PlanSharedCrop(
                new System.Drawing.Rectangle(left.BoxInOcrCrop.Left, left.BoxInOcrCrop.Top, 10, 10),
                new System.Drawing.Rectangle(right.BoxInOcrCrop.Left, right.BoxInOcrCrop.Top, 10, 10), 1920, 1080);
            return Task.FromResult(new PixelComparison(PixelCheckResult.Same, l, r, union, 0.99, 0.95, 0.9));
        }
    }
}
