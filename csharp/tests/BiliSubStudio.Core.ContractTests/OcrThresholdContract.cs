using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

internal static class OcrThresholdContract
{
    public static async Task RunAsync()
    {
        Check(ProductionPixelComparer.Classify(.55, .80) == PixelCheckResult.Same, "SSIM=.55 Edge=.80 => Same");
        Check(ProductionPixelComparer.Classify(.99, .99) == PixelCheckResult.Same, "high/high => Same");
        Check(ProductionPixelComparer.Classify(.549, .90) == PixelCheckResult.Unavailable, "SSIM=.549 => Unavailable");
        Check(ProductionPixelComparer.Classify(.90, .799) == PixelCheckResult.Unavailable, "Edge=.799 => Unavailable");
        Check(ProductionPixelComparer.Classify(.30, .40) == PixelCheckResult.Different, "SSIM=.30 Edge=.40 => Different");
        Check(ProductionPixelComparer.Classify(.10, .10) == PixelCheckResult.Different, "low/low => Different");
        Check(ProductionPixelComparer.Classify(.301, .20) == PixelCheckResult.Unavailable, "SSIM=.301 => Unavailable");
        Check(ProductionPixelComparer.Classify(.10, .401) == PixelCheckResult.Unavailable, "Edge=.401 => Unavailable");
        Check(ProductionPixelComparer.Classify(null, .90) == PixelCheckResult.Unavailable, "null SSIM => Unavailable");
        Check(ProductionPixelComparer.Classify(.90, null) == PixelCheckResult.Unavailable, "null Edge => Unavailable");
        Check(ProductionPixelComparer.Classify(null, null) == PixelCheckResult.Unavailable, "null/null => Unavailable");
        Check(ProductionPixelComparer.Classify(.45, .65) == PixelCheckResult.Unavailable, "abstention band => Unavailable");
        // FG does not affect v1 classification (SSIM+Edge only).
        Check(ProductionPixelComparer.Classify(.99, .99) == PixelCheckResult.Same, "FG ignored in v1");

        // Repeat-instance gate: a very high simulated metric but no candidate => the comparer is NOT called.
        var roi = new OcrRegion(0, 0, 1, 1);
        var tb = new RationalTimeBase(1, 90000);
        var geo = new OcrFrameGeometry(0, 0, 1920, 1080, 1280, 320, 0, 0, 1280, 320);
        OcrCue Cue(double s, double e, string t) => new(s, e, t, 0.99, 0, 0, false,
            new PixelObservation(s, new OcrBox(10, 10, 200, 40), roi, geo, (long)(s * 90000), tb),
            new PixelObservation(e, new OcrBox(10, 10, 200, 40), roi, geo, (long)(e * 90000), tb));
        var counting = new CountingComparer();
        // Two instances of the same text separated by a different cue/blank (large gap) => no candidate => 0 calls.
        var far = new[] { Cue(10, 10.5, "了解不多"), Cue(20, 20.5, "了解不多") };
        var mergedFar = await OcrCueReconciler.MergeTouchingIdenticalAsync(far, "v.mp4", counting, CancellationToken.None);
        Check(mergedFar.Count == 2, "far repeat must stay separate");
        Check(counting.Calls == 0, "pixel comparer must not be called without temporal candidate");
        // A real gap between two identical instances => no merge even if pixel could be Same.
        var counting2 = new CountingComparer(PixelCheckResult.Same);
        var gapPair = new[] { Cue(30, 30.5, "龙无期"), Cue(31.0, 31.5, "龙无期") };
        var mergedGap = await OcrCueReconciler.MergeTouchingIdenticalAsync(gapPair, "v.mp4", counting2, CancellationToken.None);
        Check(mergedGap.Count == 2, "real-gap repeat must stay separate even with Same pixel available");
        Check(counting2.Calls == 0, "gap beyond tolerance must not reach pixel comparer");
    }

    private static void Check(bool v, string m) { if (!v) throw new InvalidOperationException(m); }

    private sealed class CountingComparer : IPixelComparer
    {
        private readonly PixelCheckResult _result;
        public int Calls;
        public CountingComparer(PixelCheckResult result = PixelCheckResult.Unavailable) => _result = result;
        public Task<PixelComparison> CheckAsync(string videoPath, PixelObservation left, PixelObservation right, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new PixelComparison(_result,
                new FrameSample(left.Pts, left.Pts, 0),
                new FrameSample(right.Pts, right.Pts, 0),
                System.Drawing.Rectangle.Empty, 0.99, 0.99, 0.99));
        }
    }
}
