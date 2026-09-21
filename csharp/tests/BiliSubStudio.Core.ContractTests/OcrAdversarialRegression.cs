using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

internal static class OcrAdversarialRegression
{
    private static readonly OcrRegion TestRoi = new(0, 0, 1, 1);
    private static readonly RationalTimeBase TestTb = new(1, 90000);
    private static readonly OcrFrameGeometry TestGeo = new(0, 0, 1920, 1080, 1280, 320, 0, 0, 1280, 320);
    private static long _spts = 1000000;

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
        var dog = new[] {
            Prov(653.4, 654.567, "哇，没毛狗", 0.99),
            Prov(654.567, 654.967, "哦，没毛狗", 0.98),
        };
        var dogMerged = await OcrCueReconciler.MergeTouchingIdenticalAsync(dog, "v.mp4", same, CancellationToken.None);
        Check(dogMerged.Count == 1 && dogMerged[0].Start == 653.4 && dogMerged[0].End == 654.967,
            "adversarial 279/280 哇/哦 flicker not merged to 1");

        var dash3 = new[] {
            Prov(50.6, 50.966, "公子，我可以——", 0.99),
            Prov(50.967, 51.033, "公子，我可以—", 0.98),
            Prov(51.033, 51.533, "公子，我可以——", 0.99),
        };
        var dash3Merged = await OcrCueReconciler.MergeTouchingIdenticalAsync(dash3, "v.mp4", same, CancellationToken.None);
        Check(dash3Merged.Count == 1,
            "adversarial dash 97-99 not merged to 1");

        var cluster7 = new[] {
            Prov(286.633, 287.466, "殿主，有没有一种可能——", 0.99),
            Prov(287.466, 287.566, "殿主，有没有一种可能—", 0.98),
            Prov(287.566, 288.466, "殿主，有没有一种可能——", 0.99),
            Prov(288.466, 288.666, "殿主，有没有一种可能—", 0.98),
            Prov(288.666, 288.733, "殿主，有没有一种可能——", 0.99),
            Prov(288.733, 288.8, "殿主，有没有一种可能—", 0.98),
            Prov(288.8, 289.166, "殿主，有没有一种可能——", 0.99),
        };
        var clusterMerged = await OcrCueReconciler.MergeTouchingIdenticalAsync(cluster7, "v.mp4", same, CancellationToken.None);
        Check(clusterMerged.Count == 1 && clusterMerged[0].Start == 286.633 && clusterMerged[0].End == 289.166,
            "adversarial cluster 112-118 not merged to 1");

        // 一 lẫn với —— ở cuối câu (case thật 130↔131): candidate qua pixel, merge khi Same.
        var confusable = new[] {
            Prov(215.77, 216.67, "任何阴谋诡计——", 0.99),
            Prov(216.67, 216.87, "任何阴谋诡计一", 0.98),
        };
        var confusableMerged = await OcrCueReconciler.MergeTouchingIdenticalAsync(confusable, "v.mp4", same, CancellationToken.None);
        Check(confusableMerged.Count == 1 && confusableMerged[0].Text == "任何阴谋诡计——",
            "dash-confusable tail 一/—— must merge with pixel Same, keeping full caption");

        // Long-A A/B/A: cue A dài 1.967s không được veto candidate (bounded tổng-span đã bỏ).
        var longTriple = new[] {
            Prov(55.133, 57.100, "那他搞血屠殿.是图什么？", 0.99),
            Prov(57.100, 57.167, "那他搞血屠殿，是图什么？", 0.98),
            Prov(57.167, 57.400, "那他搞血屠殿.是图什么？", 0.99),
        };
        var longMerged = await OcrCueReconciler.MergeTouchingIdenticalAsync(longTriple, "v.mp4", same, CancellationToken.None);
        Check(longMerged.Count == 1 && longMerged[0].Start == 55.133 && longMerged[0].End == 57.400,
            "long-A A/B/A must collapse with pixel Same on both boundaries");

        // p06 field: 1-rune variant (镇/涼) khe 0.1s (> flicker window cu 0.067):
        // candidate qua pixel (window moi 0.25), merge khi Same. Pixel Different
        // van tach + review nhu cu (test khac khoa). Window 0.1 = liveWindow cua
        // video 30fps production (3/nativeFps), khong phai Epsilon mac dinh.
        var variantFlicker = new[] {
            Prov(961.700, 962.400, "入镇休整", 0.973),
            Prov(962.500, 962.833, "入鎮休整", 0.921),
        };
        var variantMerged = await OcrCueReconciler.MergeTouchingIdenticalAsync(variantFlicker, "v.mp4", same, CancellationToken.None, 0.1);
        Check(variantMerged.Count == 1 && variantMerged[0].Start == 961.700 && variantMerged[0].End == 962.833,
            "single-rune variant across 0.1s gap must merge with pixel Same");

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

        var distinctLong = new[] {
            new OcrCue(30, 30.6, "你要走吗", 0.99),
            new OcrCue(30.6, 31.2, "你要来吗", 0.99),
        };
        Check(OcrCueReconciler.MergeTouchingIdentical(distinctLong).Count == 2,
            "invariant NeverMergeAcrossDistinctVisualInstances violated for long 走/来");
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }

    private sealed class FakePixelComparer : IPixelComparer
    {
        private readonly PixelCheckResult _result;
        public FakePixelComparer(PixelCheckResult result) => _result = result;
        public Task<PixelComparison> CheckAsync(string videoPath, PixelObservation left, PixelObservation right, CancellationToken cancellationToken)
        {
            return Task.FromResult(new PixelComparison(_result,
                new FrameSample(left.Pts, left.Pts, 0),
                new FrameSample(right.Pts, right.Pts, 0),
                System.Drawing.Rectangle.Empty, null, null, null));
        }
    }
}
