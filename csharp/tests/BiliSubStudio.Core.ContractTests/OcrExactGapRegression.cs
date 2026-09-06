using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

// Regression từ field-test: cue 7↔8 (exact + touching) và cue 9↔10 (exact + gap 0.667s).
// Field evidence (6 OCR blank + mắt thường 27.35-27.9): gap 0.667 là blank THẬT => 2 instances,
// KHÔNG merge. Window production = 3.0/nativeFps (~0.1s ở 30fps) cho mọi mode, không truyền tay 0.75.
internal static class OcrExactGapRegression
{
    private static readonly OcrRegion TestRoi = new(0, 0, 1, 1);
    private static readonly RationalTimeBase TestTb = new(1, 90000);
    private static readonly OcrFrameGeometry TestGeo = new(0, 0, 1920, 1080, 1280, 320, 0, 0, 1280, 320);
    private static long _spts = 3000000;

    private static OcrCue Prov(double s, double e, string t, double c)
    {
        var s1 = System.Threading.Interlocked.Increment(ref _spts);
        var s2 = System.Threading.Interlocked.Increment(ref _spts);
        return new OcrCue(s, e, t, c, 0, 0, false,
            new PixelObservation(s, new OcrBox(10, 10, 200, 40), TestRoi, TestGeo, s1, TestTb),
            new PixelObservation(e, new OcrBox(10, 10, 200, 40), TestRoi, TestGeo, s2, TestTb));
    }

    public static async Task RunAsync()
    {
        var same = new FakePixelComparer(PixelCheckResult.Same);
        var ct = CancellationToken.None;

        // 1. Field 7↔8: exact + touching => 1 cue.
        var touch = new[] {
            Prov(21.600, 22.266, "一开始，只是觉得不顺眼", 0.99),
            Prov(22.266, 24.400, "一开始，只是觉得不顺眼", 0.98),
        };
        var touchMerged = await OcrCueReconciler.MergeTouchingIdenticalAsync(touch, "v.mp4", same, ct);
        Check(touchMerged.Count == 1 && touchMerged[0].Start == 21.600 && touchMerged[0].End == 24.400,
            "field 7-8 exact touching must merge to 1 cue");

        // 2. Field 9↔10: exact + gap 0.667s nhưng là blank THẬT (field evidence) => tách kể cả
        // production Exhaustive window (3.0/30 ≈ 0.1s). Preview hiện 2 cue là đúng visual-instance semantics.
        // Không truyền tay 0.75s: số đó sẽ merge nhầm 2 instances đã chứng minh khác nhau.
        const double productionExhaustiveWindow = 3.0 / 30.0;
        var gapped = new[] {
            Prov(24.933, 27.333, "直到烈战狂提到龙帝宫", 0.99),
            Prov(28.000, 29.966, "直到烈战狂提到龙帝宫", 0.98),
        };
        var countingGap = new FakePixelComparer(PixelCheckResult.Same);
        var gappedMerged = await OcrCueReconciler.MergeTouchingIdenticalAsync(gapped, "v.mp4", countingGap, ct, continuationWindow: productionExhaustiveWindow);
        Check(gappedMerged.Count == 2, "field 9-10 real 0.667 blank must stay 2 cues with production window");
        Check(countingGap.Calls == 0, "gap beyond production window must not reach pixel comparer");

        // 3. Cùng pair nhưng window mặc định (Epsilon) => tách, không gọi comparer.
        var countingDefault = new FakePixelComparer(PixelCheckResult.Same);
        var defaultMerged = await OcrCueReconciler.MergeTouchingIdenticalAsync(gapped, "v.mp4", countingDefault, ct);
        Check(defaultMerged.Count == 2, "default window must keep 0.667 gap separate");
        Check(countingDefault.Calls == 0, "gap beyond window must not reach pixel comparer");

        // 4. Blank thật 1.0s > window 0.75 => tách, không gọi comparer.
        var realBlank = new[] {
            Prov(40.000, 41.000, "龙无期", 0.99),
            Prov(42.000, 43.000, "龙无期", 0.98),
        };
        var countingBlank = new FakePixelComparer(PixelCheckResult.Same);
        var blankMerged = await OcrCueReconciler.MergeTouchingIdenticalAsync(realBlank, "v.mp4", countingBlank, ct, continuationWindow: 0.75);
        Check(blankMerged.Count == 2, "real 1.0s blank between same-text instances must stay separate");
        Check(countingBlank.Calls == 0, "real blank must not reach pixel comparer");

        // 5. Gap 0.667 + Different => tách + NeedsReview cả hai.
        var diff = new FakePixelComparer(PixelCheckResult.Different);
        var diffMerged = await OcrCueReconciler.MergeTouchingIdenticalAsync(gapped, "v.mp4", diff, ct, continuationWindow: 0.75);
        Check(diffMerged.Count == 2 && diffMerged.All(c => c.NeedsReview),
            "Different pixel must keep 0.667 gap separate with NeedsReview");

        // 6. Gap 0.667 + Unavailable => tách + NeedsReview cả hai.
        var unav = new FakePixelComparer(PixelCheckResult.Unavailable);
        var unavMerged = await OcrCueReconciler.MergeTouchingIdenticalAsync(gapped, "v.mp4", unav, ct, continuationWindow: 0.75);
        Check(unavMerged.Count == 2 && unavMerged.All(c => c.NeedsReview),
            "Unavailable pixel must keep 0.667 gap separate with NeedsReview");

        // 7. Hai câu thật khác nhau đứng sát (field 19↔20): temporal+spatial dính nên là
        // generic candidate, pixel quyết định. Different => tách + NeedsReview, gọi đúng 1 lần.
        // (Gate mới không lọc bằng text: an toàn đến từ pixel, không phải text pattern.)
        var countingDistinct = new FakePixelComparer(PixelCheckResult.Different);
        var distinct = new[] {
            Prov(50.700, 52.733, "这种人，不像人", 0.99),
            Prov(52.733, 54.333, "像被擦过的棋子", 0.98),
        };
        var distinctMerged = await OcrCueReconciler.MergeTouchingIdenticalAsync(distinct, "v.mp4", countingDistinct, ct, continuationWindow: 0.75);
        Check(distinctMerged.Count == 2 && distinctMerged.All(c => c.NeedsReview),
            "two truly different touching captions must stay separate with NeedsReview on Different pixel");
        Check(countingDistinct.Calls == 1, "touching distinct pair must reach pixel verifier once");

        // 8. Field V01-I07: gap đúng 3 native frame nhưng float noise ở magnitude PTS lớn
        // (13633.600 - 13633.500 = 0.10000000000036 > 3.0/30) từng làm MỘT câu bị tách
        // thành hai cue mà không có cờ review => lặp trong SRT. Epsilon window phải giữ
        // nó là candidate và merge khi pixel Same.
        var floatEdge = new[] {
            Prov(13633.333, 13633.500, "我为什么要知道", 1.0),
            Prov(13633.600, 13634.600, "我为什么要知道", 1.0),
        };
        var edgeComparer = new FakePixelComparer(PixelCheckResult.Same);
        var edgeMerged = await OcrCueReconciler.MergeTouchingIdenticalAsync(floatEdge, "v.mp4", edgeComparer, ct, continuationWindow: productionExhaustiveWindow);
        Check(edgeMerged.Count == 1, "three-frame gap at large PTS magnitude must stay inside the continuation window");
        Check(edgeComparer.Calls == 1, "three-frame float-edge gap must reach the pixel comparer");

        // Inflated scene-text unions must reach the pixel gate in BOTH directions.
        // These are the recorded boxes from the remaining V04 field boundaries.
        foreach (var (inflated, subtitle) in new[]
        {
            (new OcrBox(94, 37, 930, 284), new OcrBox(449, 199, 928, 283)),
            (new OcrBox(154, 116, 739, 271), new OcrBox(538, 209, 739, 271)),
        })
        foreach (var reverse in new[] { false, true })
        foreach (var verdict in new[] { PixelCheckResult.Same, PixelCheckResult.Different, PixelCheckResult.Unavailable })
        {
            var a = Prov(60, 61, "自刺探情报", .9);
            var b = Prov(61, 62, "刺探情报", .99);
            a = a with { LastObservation = a.LastObservation! with { BoxInOcrCrop = reverse ? subtitle : inflated } };
            b = b with { FirstObservation = b.FirstObservation! with { BoxInOcrCrop = reverse ? inflated : subtitle } };
            var verifier = new FakePixelComparer(verdict);
            var result = await OcrCueReconciler.MergeTouchingIdenticalAsync([a, b], "v.mp4", verifier, ct, productionExhaustiveWindow);
            Check(verifier.Calls == 1, "nested baseline boxes must reach union pixel verification");
            Check(result.Count == (verdict == PixelCheckResult.Same ? 1 : 2), "nested boxes must never bypass the pixel verdict");
            if (verdict == PixelCheckResult.Same)
                Check(result[0].Start == 60 && result[0].End == 62 && result[0].Text == b.Text,
                    "nested-box merge must preserve instance span and evidence-selected spelling");

            foreach (var blocked in new[]
            {
                b with { Start = 61.667 },
                b with { FirstObservation = b.FirstObservation! with { SourcePts = null } },
                b with { FirstObservation = b.FirstObservation! with { Geometry = TestGeo with { SourceCropX = 1 } } },
                b with { FirstObservation = b.FirstObservation! with { BoxInOcrCrop = new OcrBox(100, 0, 200, 25) } },
                b with { FirstObservation = b.FirstObservation! with { BoxInOcrCrop = new OcrBox(1000, 209, 1200, 271) } },
            })
            {
                var guard = new FakePixelComparer(PixelCheckResult.Same);
                var guarded = await OcrCueReconciler.MergeTouchingIdenticalAsync([a, blocked], "v.mp4", guard, ct, productionExhaustiveWindow);
                Check(guarded.Count == 2 && guard.Calls == 0,
                    "inflated boxes must not bypass blank/provenance/location guards");
            }
        }
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }

    private sealed class FakePixelComparer : IPixelComparer
    {
        private readonly PixelCheckResult _result;
        public int Calls;
        public FakePixelComparer(PixelCheckResult result) => _result = result;
        public Task<PixelComparison> CheckAsync(string videoPath, PixelObservation left, PixelObservation right, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new PixelComparison(_result,
                new FrameSample(left.Pts, left.Pts, 0),
                new FrameSample(right.Pts, right.Pts, 0),
                System.Drawing.Rectangle.Empty, null, null, null));
        }
    }
}
