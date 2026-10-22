using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace BiliSubStudio.Core.Ocr;

// Đo chữ ký sáng từng dòng OCR (ứng viên 2, spec khóa 16/09; luật nhới 21/09).
// Thoại đo thật trên video: ~15% điểm trắng (luma>=224). Giấy sách / chữ mờ /
// nền / chữ mực-đen trong cảnh (thư, biển hiệu): 0% điểm trắng → loại ở đây,
// trước tracker (vụ bức thư "休书" bị đọc lẫn vào thoại, 21/09).
// Luật giữ dòng (nhới, user chốt 21/09 "thà giữ rác hơn xén thoại"): CHỈ đòi
// phe SÁNG. Không đòi phe tối vì sub trắng trên nền sáng chói (tuyết) viền
// mỏng thiếu phe tối mà vẫn là thoại thật; blob trắng ma nếu lọt thì tầng
// judge/reconcile phía sau lo tiếp. Phe tối vẫn đo để so telemetry/đối chứng.
// Enforce TRỰC TIẾP (21/09, sau proof khung tổng hợp ghim contract + user
// duyệt). Mọi lỗi (không Windows, decode hỏng, box lạ, stride âm) → passthrough
// nguyên (fail-open): đo là diagnostic, không bao giờ được giết chữ oan.
public static class OcrLineLuminance
{
    // 22/10 field: the 桃花剑神 white caption measures only 4-6% at luma 224
    // (color grading + JPEG), but 16-21% at 200 with zero colored pixels. The
    // old 224 cut dropped those readable frames as blank and split one caption
    // into 0.2s fragments. Match the chroma gate's white definition instead of
    // treating compressed white as scene ink.
    public const int BrightLumaMin = 200;
    public const int DarkLumaMax = 64;

    // Ngưỡng phe sáng (đo 16/09: thoại sáng 12-19%; giấy/mực/nền 0%). Biên an
    // toàn ~2× dưới min quan sát. Đổi ngưỡng = code + test + duyệt.
    public const double KeepBrightMin = 0.06;
    // Phe tối vẫn đo (telemetry/đối chứng) nhưng KHÔNG còn trong luật giữ
    // (nhới 21/09: đòi phe tối sẽ xén oan sub trắng nền sáng viền mỏng).
    public const double KeepDarkMin = 0.10;

    // Enforce trực tiếp (user chốt 21/09 sau proof khung tổng hợp ghim contract).
    public const bool EnforceDefault = true;

    public sealed record LineLuminance(double BrightFraction, double DarkFraction);

    public sealed record GateStats(int MeasuredLines, int WouldDropLines, int DecodeFails);

    public static bool KeepLine(LineLuminance measurement) =>
        measurement.BrightFraction >= KeepBrightMin;

    public static (OcrResult Result, GateStats Stats) Apply(
        byte[]? jpeg,
        OcrResult result,
        int canvasWidth,
        int canvasHeight,
        bool enforce)
    {
        if (!result.Ok || result.Lines.Count == 0)
            return (result, new GateStats(0, 0, 0));
        var perLine = Measure(jpeg, result.Lines, canvasWidth, canvasHeight);
        if (perLine is null)
            return (result, new GateStats(0, 0, 1));
        var measured = 0;
        var wouldDrop = 0;
        var kept = new List<OcrLine>(result.Lines.Count);
        for (var index = 0; index < result.Lines.Count; index++)
        {
            var measurement = perLine[index];
            if (measurement is null)
            {
                kept.Add(result.Lines[index]);
                continue;
            }
            measured++;
            if (KeepLine(measurement))
            {
                kept.Add(result.Lines[index]);
            }
            else
            {
                wouldDrop++;
            }
        }
        var stats = new GateStats(measured, wouldDrop, 0);
        if (!enforce || wouldDrop == 0)
            return (result, stats);
        if (kept.Count == 0)
            return (result with { Detected = false, Text = string.Empty, Confidence = 0, Lines = [] }, stats);
        return (result with
        {
            Text = string.Join("\n", kept.Select(line => line.Text)),
            Confidence = kept.Average(line => line.Confidence),
            Lines = kept,
        }, stats);
    }

    public static IReadOnlyList<LineLuminance?>? Measure(
        byte[]? jpeg,
        IReadOnlyList<OcrLine> lines,
        int canvasWidth,
        int canvasHeight)
    {
        if (lines.Count == 0) return [];
        if (!OperatingSystem.IsWindows()) return null;
        if (jpeg is null || jpeg.Length == 0 || jpeg.Length > 8 * 1024 * 1024) return null;
        try
        {
            using var stream = new MemoryStream(jpeg, writable: false);
            using var bitmap = new Bitmap(stream);
            var width = bitmap.Width;
            var height = bitmap.Height;
            if (width <= 0 || height <= 0 || width > 4096 || height > 4096) return null;
            var scaleX = canvasWidth > 0 ? (double)width / canvasWidth : 1.0;
            var scaleY = canvasHeight > 0 ? (double)height / canvasHeight : 1.0;
            if (!(scaleX > 0) || !(scaleY > 0) || scaleX > 8 || scaleY > 8) return null;
            Bitmap? owned = null;
            try
            {
                var rgb = bitmap;
                if (bitmap.PixelFormat != PixelFormat.Format24bppRgb)
                    rgb = owned = bitmap.Clone(new Rectangle(0, 0, width, height), PixelFormat.Format24bppRgb);
                return MeasureLocked(rgb, lines, scaleX, scaleY);
            }
            finally
            {
                // bitmap do using ngoài dispose đúng 1 lần; clone (nếu có) dispose ở đây.
                owned?.Dispose();
            }
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<LineLuminance?>? MeasureLocked(
        Bitmap rgb, IReadOnlyList<OcrLine> lines, double scaleX, double scaleY)
    {
        var width = rgb.Width;
        var height = rgb.Height;
        var rect = new Rectangle(0, 0, width, height);
        BitmapData? data = null;
        try
        {
            data = rgb.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            if (data.Stride <= 0) return null;
            var stride = data.Stride;
            var buffer = new byte[stride * height];
            Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
            var output = new LineLuminance?[lines.Count];
            for (var index = 0; index < lines.Count; index++)
                output[index] = MeasureBox(buffer, stride, width, height, lines[index].Box, scaleX, scaleY);
            return output;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (data is not null)
            {
                try { rgb.UnlockBits(data); } catch { }
            }
        }
    }

    private static LineLuminance? MeasureBox(
        byte[] pixels, int stride, int width, int height, int[] box, double scaleX, double scaleY)
    {
        if (box.Length < 4) return null;
        var x0 = Math.Clamp((int)Math.Floor(Math.Min(box[0], box[2]) * scaleX), 0, width);
        var x1 = Math.Clamp((int)Math.Ceiling(Math.Max(box[0], box[2]) * scaleX), 0, width);
        var y0 = Math.Clamp((int)Math.Floor(Math.Min(box[1], box[3]) * scaleY), 0, height);
        var y1 = Math.Clamp((int)Math.Ceiling(Math.Max(box[1], box[3]) * scaleY), 0, height);
        if (x1 <= x0 || y1 <= y0) return null;
        long total = 0;
        long bright = 0;
        long dark = 0;
        for (var y = y0; y < y1; y++)
        {
            var row = y * stride;
            for (var x = x0; x < x1; x++)
            {
                var offset = row + x * 3;
                var luma = (pixels[offset + 2] * 19595 + pixels[offset + 1] * 38470 + pixels[offset] * 7471) >> 16;
                total++;
                if (luma >= BrightLumaMin) bright++;
                else if (luma < DarkLumaMax) dark++;
            }
        }
        if (total == 0) return null;
        return new LineLuminance((double)bright / total, (double)dark / total);
    }
}

// =====================================================================
// CỔNG MÀU CHỮ (user chốt 18/09): sub luôn TRẮNG, chữ màu (danmaku xanh như
// vụ tường 陪一支) loại luôn ở tầng frame — chữ màu không bao giờ vào tracker.
// Quy tắc giữ dòng: CÓ điểm trắng (sáng-không-màu) HOẶC thiếu điểm màu.
// Cổng trắng đứng trước nên chữ trắng trên nền màu (trời xanh!) vẫn giữ —
// nền bão hòa không giết được sub trắng. Chữ vàng cũng BỎ (đổi chính sách so
// với cổng luminance 16/09 "không kén màu" — theo yêu cầu user sub luôn trắng).
// Enforce TRỰC TIẾP (không shadow): fail-open mọi lỗi (không Windows, decode
// hỏng, box lạ) -> giữ dòng. Đổi ngưỡng = code + test + duyệt.
// (Nằm chung file vì cùng đường đo pixel/box với luminance; tách file riêng
// khi được phép tạo file mới.)
public static class OcrLineChroma
{
    // Trắng: sáng + gần như không màu (JPEG ringing quanh glyph trắng ~20-40).
    public const int WhiteLumaMin = 200;
    public const int WhiteSatMax = 48;
    // Màu rõ: bão hòa cao hẳn (trên hẳn noise nén) — xanh/vàng/đỏ glyph.
    public const int ColoredSatMin = 64;

    // Ngưỡng drop: thiếu trắng (<4%) + thừa màu (≥15%). Thoại trắng đo thật
    // 12-19% điểm sáng (spec 16/09) nên biên an toàn ~3× dưới min quan sát;
    // dòng danmaku ôm sát glyph màu 40-70%. Đổi ngưỡng = code + test + duyệt.
    public const double DropWhiteMax = 0.04;
    public const double DropColoredMin = 0.15;

    // User chốt 18/09: sub luôn trắng — enforce ngay, không shadow.
    public const bool EnforceDefault = true;

    public sealed record LineChroma(double WhiteFraction, double ColoredFraction);

    public sealed record GateStats(int MeasuredLines, int WouldDropLines, int DecodeFails);

    public static bool KeepLine(LineChroma measurement) =>
        !(measurement.WhiteFraction < DropWhiteMax
            && measurement.ColoredFraction >= DropColoredMin);

    public static (OcrResult Result, GateStats Stats) Apply(
        byte[]? jpeg,
        OcrResult result,
        int canvasWidth,
        int canvasHeight,
        bool enforce)
    {
        if (!result.Ok || result.Lines.Count == 0)
            return (result, new GateStats(0, 0, 0));
        var perLine = Measure(jpeg, result.Lines, canvasWidth, canvasHeight);
        if (perLine is null)
            return (result, new GateStats(0, 0, 1));
        var measured = 0;
        var wouldDrop = 0;
        var kept = new List<OcrLine>(result.Lines.Count);
        for (var index = 0; index < result.Lines.Count; index++)
        {
            var measurement = perLine[index];
            if (measurement is null)
            {
                kept.Add(result.Lines[index]);
                continue;
            }
            measured++;
            if (KeepLine(measurement))
            {
                kept.Add(result.Lines[index]);
            }
            else
            {
                wouldDrop++;
            }
        }
        var stats = new GateStats(measured, wouldDrop, 0);
        if (!enforce || wouldDrop == 0)
            return (result, stats);
        if (kept.Count == 0)
            return (result with { Detected = false, Text = string.Empty, Confidence = 0, Lines = [] }, stats);
        return (result with
        {
            Text = string.Join("\n", kept.Select(line => line.Text)),
            Confidence = kept.Average(line => line.Confidence),
            Lines = kept,
        }, stats);
    }

    public static IReadOnlyList<LineChroma?>? Measure(
        byte[]? jpeg,
        IReadOnlyList<OcrLine> lines,
        int canvasWidth,
        int canvasHeight)
    {
        if (lines.Count == 0) return [];
        if (!OperatingSystem.IsWindows()) return null;
        if (jpeg is null || jpeg.Length == 0 || jpeg.Length > 8 * 1024 * 1024) return null;
        try
        {
            using var stream = new MemoryStream(jpeg, writable: false);
            using var bitmap = new Bitmap(stream);
            var width = bitmap.Width;
            var height = bitmap.Height;
            if (width <= 0 || height <= 0 || width > 4096 || height > 4096) return null;
            var scaleX = canvasWidth > 0 ? (double)width / canvasWidth : 1.0;
            var scaleY = canvasHeight > 0 ? (double)height / canvasHeight : 1.0;
            if (!(scaleX > 0) || !(scaleY > 0) || scaleX > 8 || scaleY > 8) return null;
            Bitmap? owned = null;
            try
            {
                var rgb = bitmap;
                if (bitmap.PixelFormat != PixelFormat.Format24bppRgb)
                    rgb = owned = bitmap.Clone(new Rectangle(0, 0, width, height), PixelFormat.Format24bppRgb);
                return MeasureLocked(rgb, lines, scaleX, scaleY);
            }
            finally
            {
                owned?.Dispose();
            }
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<LineChroma?>? MeasureLocked(
        Bitmap rgb, IReadOnlyList<OcrLine> lines, double scaleX, double scaleY)
    {
        var width = rgb.Width;
        var height = rgb.Height;
        var rect = new Rectangle(0, 0, width, height);
        BitmapData? data = null;
        try
        {
            data = rgb.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            if (data.Stride <= 0) return null;
            var stride = data.Stride;
            var buffer = new byte[stride * height];
            Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
            var output = new LineChroma?[lines.Count];
            for (var index = 0; index < lines.Count; index++)
                output[index] = MeasureBox(buffer, stride, width, height, lines[index].Box, scaleX, scaleY);
            return output;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (data is not null)
            {
                try { rgb.UnlockBits(data); } catch { }
            }
        }
    }

    private static LineChroma? MeasureBox(
        byte[] pixels, int stride, int width, int height, int[] box, double scaleX, double scaleY)
    {
        if (box.Length < 4) return null;
        var x0 = Math.Clamp((int)Math.Floor(Math.Min(box[0], box[2]) * scaleX), 0, width);
        var x1 = Math.Clamp((int)Math.Ceiling(Math.Max(box[0], box[2]) * scaleX), 0, width);
        var y0 = Math.Clamp((int)Math.Floor(Math.Min(box[1], box[3]) * scaleY), 0, height);
        var y1 = Math.Clamp((int)Math.Ceiling(Math.Max(box[1], box[3]) * scaleY), 0, height);
        if (x1 <= x0 || y1 <= y0) return null;
        long total = 0;
        long white = 0;
        long colored = 0;
        for (var y = y0; y < y1; y++)
        {
            var row = y * stride;
            for (var x = x0; x < x1; x++)
            {
                var offset = row + x * 3;
                var b = pixels[offset];
                var g = pixels[offset + 1];
                var r = pixels[offset + 2];
                var luma = (r * 19595 + g * 38470 + b * 7471) >> 16;
                var brightest = r > g ? (r > b ? r : b) : (g > b ? g : b);
                var darkest = r < g ? (r < b ? r : b) : (g < b ? g : b);
                var saturation = brightest - darkest;
                total++;
                if (luma >= WhiteLumaMin && saturation <= WhiteSatMax) white++;
                if (saturation >= ColoredSatMin) colored++;
            }
        }
        if (total == 0) return null;
        return new LineChroma((double)white / total, (double)colored / total);
    }
}
