using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

internal static class OcrPixelMetricsContract
{
    public static Task RunAsync()
    {
        const int w = 32; const int h = 32;
        var identical1 = TestBuffer(w, h, (x, y) => (byte)((x * 7 + y * 13) % 256));
        var identical2 = (byte[])identical1.Clone();
        var ssimSame = OcrPixelMetrics.Ssim(identical1, identical2, w, h);
        Check(ssimSame.HasValue && ssimSame.Value > 0.99, "SSIM identical buffers must be near 1");

        var different = TestBuffer(w, h, (x, y) => (byte)(((x < 16) ? 20 : 235)));
        var ssimDiff = OcrPixelMetrics.Ssim(identical1, different, w, h);
        Check(ssimDiff.HasValue && ssimDiff.Value < ssimSame!.Value, "SSIM clearly different buffers must be lower than identical");

        Check(OcrPixelMetrics.Ssim(new byte[10], new byte[12], 5, 2) is null, "SSIM dimensions mismatch must be invalid");

        var fgA = TestBuffer(w, h, (x, y) => (byte)((x >= 8 && x < 24 && y >= 8 && y < 24) ? (byte)255 : (byte)10));
        var fgB = (byte[])fgA.Clone();
        var fgSame = OcrPixelMetrics.ForegroundIoU(fgA, fgB, w, h);
        Check(fgSame.HasValue && Math.Abs(fgSame.Value - 1) < 1e-9, "ForegroundIoU identical masks must be 1");

        var fgC = TestBuffer(w, h, (x, y) => (byte)((x < 8 && y < 8) ? (byte)255 : (byte)10));
        var fgDisjoint = OcrPixelMetrics.ForegroundIoU(fgA, fgC, w, h);
        Check(fgDisjoint.HasValue && fgDisjoint.Value < 0.5, "ForegroundIoU disjoint foreground must be low");

        var fgHalf1 = TestBuffer(w, h, (x, y) => (byte)((x < 16) ? (byte)255 : (byte)10));
        var fgHalf2 = TestBuffer(w, h, (x, y) => (byte)((x < 24) ? (byte)255 : (byte)10));
        var fgPartial = OcrPixelMetrics.ForegroundIoU(fgHalf1, fgHalf2, w, h);
        Check(fgPartial.HasValue && fgPartial.Value > 0 && fgPartial.Value < 1, "ForegroundIoU partial overlap must be between 0 and 1");

        var empty = new byte[w * h];
        Check(OcrPixelMetrics.ForegroundIoU(empty, empty, w, h) is null, "ForegroundIoU both-empty must not infer Same");

        var edgeSame = OcrPixelMetrics.EdgeIoU(fgA, fgB, w, h);
        Check(!edgeSame.HasValue || (edgeSame.Value >= 0 && edgeSame.Value <= 1), "EdgeIoU identical must be valid range or no-evidence");
        if (edgeSame.HasValue) Check(Math.Abs(edgeSame.Value - 1) < 1e-9, "EdgeIoU identical edges must be 1");

        var shifted = TestBuffer(w, h, (x, y) => (byte)(((x + 4) >= 8 && (x + 4) < 24 && y >= 8 && y < 24) ? (byte)255 : (byte)10));
        var edgeShifted = OcrPixelMetrics.EdgeIoU(fgA, shifted, w, h);
        if (edgeSame.HasValue && edgeShifted.HasValue)
            Check(edgeShifted.Value < edgeSame.Value, "EdgeIoU shifted edges must be lower than identical");

        var flat1 = TestBuffer(8, 8, (_, _) => (byte)128);
        var flat2 = (byte[])flat1.Clone();
        var edgeFlat = OcrPixelMetrics.EdgeIoU(flat1, flat2, 8, 8);
        Check(edgeFlat is null, "EdgeIoU no-evidence flat frames must be safe null");

        Check(OcrPixelMetrics.EdgeIoU(new byte[10], new byte[12], 5, 2) is null, "EdgeIoU dimensions mismatch must be invalid");

        var comparer = new ProductionPixelComparer();
        var noVideo = comparer.CheckAsync("missing-video.mp4",
            new PixelObservation(1, new OcrBox(0, 0, 10, 10), new OcrRegion(0, 0, 1, 1), null, 1, new RationalTimeBase(1, 90000)),
            new PixelObservation(1, new OcrBox(0, 0, 10, 10), new OcrRegion(0, 0, 1, 1), null, 1, new RationalTimeBase(1, 90000)),
            CancellationToken.None).GetAwaiter().GetResult();
        Check(noVideo.Result == PixelCheckResult.Unavailable, "CheckAsync without video must stay Unavailable even when metrics path exists");
        return Task.CompletedTask;
    }

    private static byte[] TestBuffer(int w, int h, Func<int, int, byte> fn)
    {
        var buf = new byte[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                buf[y * w + x] = fn(x, y);
        return buf;
    }

    private static void Check(bool v, string m) { if (!v) throw new InvalidOperationException(m); }
}
