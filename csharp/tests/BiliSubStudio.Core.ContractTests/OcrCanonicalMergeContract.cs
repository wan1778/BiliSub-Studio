using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

// Canonical-touching merge (field 672-674 强/強 + preview splits 679-681, 666):
// Paddle đổi giản↔phồn từng khung nên text sau DashNormalize vẫn khác nhau;
// IsCanonicalIdentical dùng chung bảng SubtitleTracker thì chúng là 1 caption.
// Cặp canonical-identical DÍNH NHAU (gap<=0) + cùng vùng chữ có provenance thì gộp
// thẳng, MIỄN pixel (pixel so crop rung và vote Different ngay trên cùng caption).
// Chữ khác (rác 666, thế chữ 设/没) KHÔNG bao giờ đi đường này: vẫn pixel + review.
internal static class OcrCanonicalMergeContract
{
    private static readonly OcrRegion TestRoi = new(0, 0, 1, 1);
    private static readonly RationalTimeBase TestTb = new(1, 90000);
    private static readonly OcrFrameGeometry TestGeo = new(0, 0, 1920, 1080, 1280, 320, 0, 0, 1280, 320);
    private static long _spts = 4000000;

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
        var ct = CancellationToken.None;

        // 0. Bảng canonical dùng chung: 强/強 + dash ——/— là identical; rác/thế chữ thì không.
        Check(OcrCueReconciler.IsCanonicalIdentical(
            Prov(0, 1, "来了李道一的最强剑", 0.99), Prov(1, 2, "来了李道一的最強剑", 0.97)),
            "canonical table must treat 强/強 as identical");
        Check(OcrCueReconciler.IsCanonicalIdentical(
            Prov(0, 1, "链——", 0.99), Prov(1, 2, "链—", 0.97)),
            "dash variants must stay identical after canonicalization");
        Check(!OcrCueReconciler.IsCanonicalIdentical(
            Prov(0, 1, "叶知秋，你确实很强共卡", 0.99), Prov(1, 2, "叶知秋，你确实很强", 0.98)),
            "garbage insertion must NOT be canonical-identical");
        Check(!OcrCueReconciler.IsCanonicalIdentical(
            Prov(0, 1, "设人会站着等你", 0.99), Prov(1, 2, "没人会站着等你", 0.98)),
            "single substitution must NOT be canonical-identical");

        // 1. Field 672-674: 强/強 touching + pixel Different mà vẫn gộp, KHÔNG gọi pixel.
        var hostile = new FakePixelComparer(PixelCheckResult.Different);
        var flicker = new[] {
            Prov(1431.300, 1432.500, "来了李道一的最强剑", 0.99),
            Prov(1432.500, 1432.967, "来了李道一的最強剑", 0.97),
            Prov(1432.967, 1433.400, "来了李道一的最强剑", 0.98),
        };
        var merged = await OcrCueReconciler.MergeTouchingIdenticalAsync(flicker, "v.mp4", hostile, ct);
        Check(merged.Count == 1 && merged[0].Start == 1431.300 && merged[0].End == 1433.400,
            $"canonical touching flicker must merge to 1 cue (got {merged.Count})");
        Check(hostile.Calls == 0, $"canonical touching must bypass pixel (calls={hostile.Calls})");
        Check(merged[0].Text == "来了李道一的最强剑",
            "merged spelling must keep evidence winner (higher confidence), never rewritten");

        // 2. Exact identical touching + pixel Different: policy 21/09 cho phép merge
        // byte-identical ≤0.5s MIỄN pixel. Dash-level (——/—) vẫn đi pixel cũ.
        var hostile2 = new FakePixelComparer(PixelCheckResult.Different);
        var exact = new[] {
            Prov(100.000, 101.000, "没人会站着等你", 0.99),
            Prov(101.000, 102.000, "没人会站着等你", 0.98),
        };
        var exactMerged = await OcrCueReconciler.MergeTouchingIdenticalAsync(exact, "v.mp4", hostile2, ct);
        Check(exactMerged.Count == 1, "exact identical touching now merges without pixel (policy 21/09)");
        Check(hostile2.Calls == 0, "byte-identical ≤0.5s merges without pixel");
        // Dash-level vẫn pixel-gated: test dash-pair ở test OcrExactGapRegression.

        // 3. Identical nhưng cách blank thật 5s: TÁCH, không gọi pixel (an toàn blank).
        var gapPx = new FakePixelComparer(PixelCheckResult.Same);
        var gapped = new[] {
            Prov(200.000, 201.000, "没人会站着等你", 0.99),
            Prov(206.000, 207.000, "没人会站着等你", 0.98),
        };
        var gappedMerged = await OcrCueReconciler.MergeTouchingIdenticalAsync(gapped, "v.mp4", gapPx, ct);
        Check(gappedMerged.Count == 2, "real 5s blank must keep identical captions separate");
        Check(gapPx.Calls == 0, "gap beyond touching must not reach pixel comparer");

        // 4. Rác 666 (dài hơn 2 chữ) touching + pixel Different: TÁCH + review cả hai.
        var garbagePx = new FakePixelComparer(PixelCheckResult.Different);
        var garbage = new[] {
            Prov(1420.967, 1421.367, "叶知秋，你确实很强共卡", 0.99),
            Prov(1421.367, 1422.267, "叶知秋，你确实很强", 0.98),
        };
        var garbageMerged = await OcrCueReconciler.MergeTouchingIdenticalAsync(garbage, "v.mp4", garbagePx, ct);
        Check(garbageMerged.Count == 2, "garbage insertion must stay split for review");
        Check(garbagePx.Calls == 1, "non-identical pair must still reach pixel gate");
        Check(garbageMerged[0].NeedsReview && garbageMerged[1].NeedsReview,
            "split pair must carry NeedsReview flags");

        // 5. Thế chữ 设/没 touching + pixel Different: TÁCH + review (không tự chọn chính tả).
        var substPx = new FakePixelComparer(PixelCheckResult.Different);
        var subst = new[] {
            Prov(1446.233, 1446.500, "设人会站着等你", 0.99),
            Prov(1446.500, 1447.000, "没人会站着等你", 0.98),
        };
        var substMerged = await OcrCueReconciler.MergeTouchingIdenticalAsync(subst, "v.mp4", substPx, ct);
        Check(substMerged.Count == 2, "single substitution must stay split for review");
        Check(substMerged[0].NeedsReview && substMerged[1].NeedsReview,
            "substitution split must carry NeedsReview flags");
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
