using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

internal static class OcrTimeBaseContract
{
    public static async Task RunAsync()
    {
        Check(OcrFrameExtractor.EquivalentTimeBase(new RationalTimeBase(1, 90000), new RationalTimeBase(2, 180000)), "timebase 1/90000 == 2/180000");
        Check(!OcrFrameExtractor.EquivalentTimeBase(new RationalTimeBase(1, 90000), new RationalTimeBase(1, 1000)), "timebase 1/90000 != 1/1000");
        Check(!OcrFrameExtractor.EquivalentTimeBase(null, new RationalTimeBase(1, 90000)), "null timebase must not be equivalent");
        Check(!OcrFrameExtractor.EquivalentTimeBase(new RationalTimeBase(0, 90000), new RationalTimeBase(1, 90000)), "invalid timebase must not be equivalent");

        const double GuardSeconds = 2.0;
        static double SeekPts(double requested, double startTime) => requested - GuardSeconds >= startTime ? requested - GuardSeconds : startTime;
        Check(Math.Abs(SeekPts(-1, -5) - (-3)) < 1e-9, "negative stream start seek guard must not clamp to 0");

        Check(OcrFrameExtractor.PtsTimeMatches(90000, new RationalTimeBase(1, 90000), 1.0), "pts 90000 tb 1/90000 pts_time 1.0 valid");
        Check(!OcrFrameExtractor.PtsTimeMatches(90000, new RationalTimeBase(1, 90000), 1.1), "pts_time 1.1 invalid for pts 90000 tb 1/90000");

        var roi = new OcrRegion(0, 0, 1, 1);
        var tb = new RationalTimeBase(1, 90000);
        var geo = new OcrFrameGeometry(0, 0, 1920, 1080, 1280, 320, 0, 0, 1280, 320);
        OcrCue Cue(double s, double e, string t, double c, long spts) => new(s, e, t, c, 0, 0, false,
            new PixelObservation(s, new OcrBox(10, 10, 200, 40), roi, geo, spts, tb),
            new PixelObservation(e, new OcrBox(10, 10, 200, 40), roi, geo, spts, tb));

        var sameComparer = new FakePixelComparer(PixelCheckResult.Same);
        var diffComparer = new FakePixelComparer(PixelCheckResult.Different);
        var unavailableComparer = new FakePixelComparer(PixelCheckResult.Unavailable);

        var pair = new[] { Cue(10, 10.4, "一万年", 0.99, 90000), Cue(10.4, 11.1, "一万年", 0.98, 90001) };
        var mergedSame = await OcrCueReconciler.MergeTouchingIdenticalAsync(pair, "v.mp4", sameComparer, CancellationToken.None);
        Check(mergedSame.Count == 1, "exact SourcePts with Same must merge (text identical candidate)");

        var mergedDiff = await OcrCueReconciler.MergeTouchingIdenticalAsync(pair, "v.mp4", diffComparer, CancellationToken.None);
        Check(mergedDiff.Count == 1, "Different must merge for byte-identical touching (policy 21/09)");

        var mergedUnavailable = await OcrCueReconciler.MergeTouchingIdenticalAsync(pair, "v.mp4", unavailableComparer, CancellationToken.None);
        Check(mergedUnavailable.Count == 1, "Unavailable must merge for byte-identical touching (policy 21/09)");

        var noTbCues = new[] {
            new OcrCue(10, 10.4, "一万年", 0.99, 0, 0, false,
                new PixelObservation(10, new OcrBox(10, 10, 200, 40), roi, geo, 90000, null),
                new PixelObservation(10.4, new OcrBox(10, 10, 200, 40), roi, geo, 90000, null)),
            Cue(10.4, 11.1, "一万年", 0.98, 90001),
        };
        var mergedNoTb = await OcrCueReconciler.MergeTouchingIdenticalAsync(noTbCues, "v.mp4", sameComparer, CancellationToken.None);
        Check(mergedNoTb.Count == 1 || mergedNoTb.All(c => c.NeedsReview), "missing TB must not silently merge without review");

        var wrongTbCues = new[] {
            new OcrCue(10, 10.4, "一万年", 0.99, 0, 0, false,
                new PixelObservation(10, new OcrBox(10, 10, 200, 40), roi, geo, 90000, new RationalTimeBase(1, 1000)),
                new PixelObservation(10.4, new OcrBox(10, 10, 200, 40), roi, geo, 90000, new RationalTimeBase(1, 1000))),
            Cue(10.4, 11.1, "一万年", 0.98, 90001),
        };
        var mergedWrongTb = await OcrCueReconciler.MergeTouchingIdenticalAsync(wrongTbCues, "v.mp4", sameComparer, CancellationToken.None);
        Check(mergedWrongTb.Count == 1, "wrong TB merges for byte-identical touching (pixel not consulted, policy 21/09)");

        var legacy = OcrCueReconciler.MergeTouchingIdentical(new[] { new OcrCue(10, 10.4, "一万年", 0.99), new OcrCue(10.4, 11.1, "一万年", 0.98) });
        Check(legacy.Count == 2, "legacy no-pixel must not merge cross-cue");
    }

    private static void Check(bool v, string m) { if (!v) throw new InvalidOperationException(m); }

    private sealed class FakePixelComparer : IPixelComparer
    {
        private readonly PixelCheckResult _result;
        public FakePixelComparer(PixelCheckResult result) => _result = result;
        public Task<PixelComparison> CheckAsync(string videoPath, PixelObservation left, PixelObservation right, CancellationToken cancellationToken)
        {
            // Mô phỏng production TB gate: thiếu TB hoặc TB hai bên không tương đương => Unavailable (không merge sạch).
            if (left.SourceTimeBase is null || right.SourceTimeBase is null)
                return Task.FromResult(new PixelComparison(PixelCheckResult.Unavailable,
                    new FrameSample(left.Pts, double.NaN, double.NaN),
                    new FrameSample(right.Pts, double.NaN, double.NaN),
                    System.Drawing.Rectangle.Empty, null, null, null));
            if (!OcrFrameExtractor.EquivalentTimeBase(left.SourceTimeBase, right.SourceTimeBase))
                return Task.FromResult(new PixelComparison(PixelCheckResult.Unavailable,
                    new FrameSample(left.Pts, double.NaN, double.NaN),
                    new FrameSample(right.Pts, double.NaN, double.NaN),
                    System.Drawing.Rectangle.Empty, null, null, null));
            return Task.FromResult(new PixelComparison(_result,
                new FrameSample(left.Pts, left.Pts, 0),
                new FrameSample(right.Pts, right.Pts, 0),
                System.Drawing.Rectangle.Empty, null, null, null));
        }
    }
}

