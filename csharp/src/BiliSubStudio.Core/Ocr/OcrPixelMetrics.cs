namespace BiliSubStudio.Core.Ocr;

// Raw pixel metrics for the same SharedCrop, gray8 buffers. Deterministic, no separate resize.
// No Same/Different threshold here yet. Null = not enough evidence / invalid input.
public static class OcrPixelMetrics
{
    private const double DynamicRange = 255.0;
    private const double K1 = 0.01;
    private const double K2 = 0.03;

    // Gaussian 1D kernel sigma=1.5, radius=5 (window 11). Standard SSIM, not a merge threshold.
    private static readonly double[] GaussianKernel = CreateGaussianKernel(1.5, 5);

    private static double[] CreateGaussianKernel(double sigma, int radius)
    {
        var size = radius * 2 + 1;
        var kernel = new double[size];
        var sum = 0d;
        for (var i = 0; i < size; i++)
        {
            var x = i - radius;
            kernel[i] = Math.Exp(-(x * x) / (2 * sigma * sigma));
            sum += kernel[i];
        }
        for (var i = 0; i < size; i++) kernel[i] /= sum;
        return kernel;
    }

    public static double? Ssim(byte[] a, byte[] b, int width, int height)
    {
        if (a is null || b is null || a.Length != b.Length) return null;
        if (width <= 0 || height <= 0 || a.Length != width * height) return null;
        if (width < 11 || height < 11) return SsimSmall(a, b, width, height);
        var radius = 5;
        var kernel = GaussianKernel;
        var n = width * height;
        var muA = new double[n];
        var muB = new double[n];
        var tmp = new double[n];
        SeparableConvolve(a, width, height, kernel, radius, tmp, muA);
        SeparableConvolve(b, width, height, kernel, radius, tmp, muB);
        var c1 = (K1 * DynamicRange) * (K1 * DynamicRange);
        var c2 = (K2 * DynamicRange) * (K2 * DynamicRange);
        var sum = 0d;
        var count = 0;
        for (var y = radius; y < height - radius; y++)
        {
            for (var x = radius; x < width - radius; x++)
            {
                var sumAA = 0d; var sumBB = 0d; var sumAB = 0d;
                for (var ky = -radius; ky <= radius; ky++)
                {
                    var row = (y + ky) * width;
                    var wy = kernel[ky + radius];
                    for (var kx = -radius; kx <= radius; kx++)
                    {
                        var w = wy * kernel[kx + radius];
                        var pa = a[row + x + kx];
                        var pb = b[row + x + kx];
                        sumAA += w * pa * pa;
                        sumBB += w * pb * pb;
                        sumAB += w * pa * pb;
                    }
                }
                var idx = y * width + x;
                var mA = muA[idx]; var mB = muB[idx];
                var varA = Math.Max(0, sumAA - mA * mA);
                var varB = Math.Max(0, sumBB - mB * mB);
                var cov = sumAB - mA * mB;
                var num = (2 * mA * mB + c1) * (2 * cov + c2);
                var den = (mA * mA + mB * mB + c1) * (varA + varB + c2);
                sum += den <= 0 ? 1d : num / den;
                count++;
            }
        }
        if (count == 0) return null;
        var mean = sum / count;
        if (double.IsNaN(mean) || double.IsInfinity(mean)) return null;
        return Math.Clamp(mean, -1, 1);
    }

    private static double? SsimSmall(byte[] a, byte[] b, int width, int height)
    {
        var n = width * height;
        var mA = 0d; var mB = 0d;
        for (var i = 0; i < n; i++) { mA += a[i]; mB += b[i]; }
        mA /= n; mB /= n;
        var vA = 0d; var vB = 0d; var cov = 0d;
        for (var i = 0; i < n; i++)
        {
            var da = a[i] - mA; var db = b[i] - mB;
            vA += da * da; vB += db * db; cov += da * db;
        }
        vA /= n; vB /= n; cov /= n;
        var c1 = (K1 * DynamicRange) * (K1 * DynamicRange);
        var c2 = (K2 * DynamicRange) * (K2 * DynamicRange);
        var num = (2 * mA * mB + c1) * (2 * cov + c2);
        var den = (mA * mA + mB * mB + c1) * (vA + vB + c2);
        if (den <= 0) return 1d;
        var s = num / den;
        if (double.IsNaN(s) || double.IsInfinity(s)) return null;
        return Math.Clamp(s, -1, 1);
    }

    private static void SeparableConvolve(byte[] src, int width, int height, double[] kernel, int radius, double[] tmp, double[] dst)
    {
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                var acc = 0d;
                for (var k = -radius; k <= radius; k++)
                {
                    var xx = Math.Clamp(x + k, 0, width - 1);
                    acc += kernel[k + radius] * src[row + xx];
                }
                tmp[row + x] = acc;
            }
        }
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var acc = 0d;
                for (var k = -radius; k <= radius; k++)
                {
                    var yy = Math.Clamp(y + k, 0, height - 1);
                    acc += kernel[k + radius] * tmp[yy * width + x];
                }
                dst[y * width + x] = acc;
            }
        }
    }

    // Foreground mask via Otsu on gray (adaptive, no hardcoded threshold). Bright subtitle text on a dark background.
    public static double? ForegroundIoU(byte[] a, byte[] b, int width, int height)
    {
        if (a is null || b is null || a.Length != b.Length) return null;
        if (width <= 0 || height <= 0 || a.Length != width * height) return null;
        var maskA = OtsuMask(a);
        var maskB = OtsuMask(b);
        long inter = 0, union = 0;
        for (var i = 0; i < maskA.Length; i++)
        {
            var fa = maskA[i]; var fb = maskB[i];
            if (fa && fb) inter++;
            if (fa || fb) union++;
        }
        if (union == 0) return null;
        return inter / (double)union;
    }

    private static bool[] OtsuMask(byte[] gray)
    {
        var hist = new long[256];
        foreach (var v in gray) hist[v]++;
        var total = gray.Length;
        var sumAll = 0d;
        for (var i = 0; i < 256; i++) sumAll += i * hist[i];
        var sumB = 0d; var wB = 0L;
        var bestVar = -1d; var threshold = 127;
        for (var t = 0; t < 256; t++)
        {
            wB += hist[t];
            if (wB == 0) continue;
            var wF = total - wB;
            if (wF == 0) break;
            sumB += t * hist[t];
            var mB = sumB / wB;
            var mF = (sumAll - sumB) / wF;
            var between = wB * wF * (mB - mF) * (mB - mF);
            if (between > bestVar) { bestVar = between; threshold = t; }
        }
        var mask = new bool[total];
        for (var i = 0; i < total; i++) mask[i] = gray[i] > threshold;
        return mask;
    }

    // Edge mask via Sobel (|Gx|+|Gy|), Otsu threshold on magnitude. No separate resize/recenter.
    public static double? EdgeIoU(byte[] a, byte[] b, int width, int height)
    {
        if (a is null || b is null || a.Length != b.Length) return null;
        if (width <= 0 || height <= 0 || a.Length != width * height) return null;
        if (width < 3 || height < 3) return null;
        var edgeA = SobelMask(a, width, height);
        var edgeB = SobelMask(b, width, height);
        if (edgeA is null || edgeB is null) return null;
        long inter = 0, union = 0;
        for (var i = 0; i < edgeA.Length; i++)
        {
            if (edgeA[i] && edgeB[i]) inter++;
            if (edgeA[i] || edgeB[i]) union++;
        }
        if (union == 0) return null;
        return inter / (double)union;
    }

    private static bool[]? SobelMask(byte[] gray, int width, int height)
    {
        var n = width * height;
        var mag = new int[n];
        for (var y = 1; y < height - 1; y++)
        {
            for (var x = 1; x < width - 1; x++)
            {
                var i = y * width + x;
                var gx = -gray[i - width - 1] - 2 * gray[i - 1] - gray[i + width - 1]
                         + gray[i - width + 1] + 2 * gray[i + 1] + gray[i + width + 1];
                var gy = -gray[i - width - 1] - 2 * gray[i - width] - gray[i - width + 1]
                         + gray[i + width - 1] + 2 * gray[i + width] + gray[i + width + 1];
                mag[i] = Math.Abs(gx) + Math.Abs(gy);
            }
        }
        var max = 0;
        foreach (var v in mag) if (v > max) max = v;
        if (max == 0) return null;
        var hist = new long[max + 1];
        foreach (var v in mag) hist[v]++;
        var total = (long)n;
        var sumAll = 0d;
        for (var i = 0; i <= max; i++) sumAll += (double)i * hist[i];
        var sumB = 0d; var wB = 0L;
        var bestVar = -1d; var threshold = max;
        for (var t = 0; t <= max; t++)
        {
            wB += hist[t];
            if (wB == 0) continue;
            var wF = total - wB;
            if (wF == 0) break;
            sumB += (double)t * hist[t];
            var mB = sumB / wB;
            var mF = (sumAll - sumB) / wF;
            var between = (double)wB * wF * (mB - mF) * (mB - mF);
            if (between > bestVar) { bestVar = between; threshold = t; }
        }
        var mask = new bool[n];
        for (var i = 0; i < n; i++) mask[i] = mag[i] > threshold;
        return mask;
    }
}
