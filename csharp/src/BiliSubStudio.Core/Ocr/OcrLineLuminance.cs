using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace BiliSubStudio.Core.Ocr;

// Measures the per-line light signature of OCR (candidate 2, locked spec 16/09; relaxed rule 21/09).
// Dialogue actually measured on video: ~15% white pixels (luma>=224). Books/paper, faint text /
// backgrounds / pitch-black scene text (letters, signs): 0% white pixels -> discarded here,
// before the tracker (the case of the letter "休书" being misread into dialogue, 21/09).
// Line-keeping rule (relaxed, user locked 21/09 "better keep junk than cut dialogue"): ONLY demands
// the LIGHT side. Does not demand the dark side because white subs on a blazing bright background (snow) with a thin
// outline lack a dark side yet are still real dialogue; if a ghost white blob slips through, the
// judge/reconcile layer behind handles it. The dark side is still measured for telemetry/cross-checking.
// Enforce DIRECTLY (21/09, after the composite proof frame pinned the contract + the user
// reviewed it). Every error (not Windows, corrupt decode, odd box, negative stride) -> full passthrough
// (fail-open): measuring is diagnostic, never allowed to kill text unjustly.
public static class OcrLineLuminance
{
    // 22/10 field: the 桃花剑神 white caption measures only 4-6% at luma 224
    // (color grading + JPEG), but 16-21% at 200 with zero colored pixels. The
    // old 224 cut dropped those readable frames as blank and split one caption
    // into 0.2s fragments. Match the chroma gate's white definition instead of
    // treating compressed white as scene ink.
    public const int BrightLumaMin = 200;
    public const int DarkLumaMax = 64;

    // Light-side threshold (measured 16/09: light dialogue 12-19%; paper/ink/background 0%). A safety
    // margin ~2x below the observed minimum. Changing a threshold = code + test + review.
    public const double KeepBrightMin = 0.06;
    // The dark side is still measured (telemetry/cross-checking) but is NO LONGER part of the keep rule
    // (relaxed 21/09: demanding a dark side would wrongly cut white subs on bright thin-outline backgrounds).
    public const double KeepDarkMin = 0.10;

    // Enforce directly (user decision 21/09, after the composite-frame proof pinned the contract).
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
                // the outer using disposes the bitmap exactly once; the clone (if any) is disposed here.
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
// TEXT COLOR GATE (user locked 18/09): subs are ALWAYS WHITE, colored text (blue danmaku like
// the 陪一支 wall case) is discarded at the frame level — colored text never reaches the tracker.
// Line-keeping rule: HAS white pixels (bright-without-color) OR lacks colored pixels.
// The white gate comes first, so white text on a colored background (a blue sky!) is still kept —
// a saturated background cannot kill a white sub. Yellow text is also DISCARDED (a policy change from
// the 16/09 luminance gate "color-agnostic" — per the user requirement that subs are always white).
// Enforce DIRECTLY (not shadow): fail-open on every error (not Windows, corrupt
// decode, odd box) -> keep the line. Changing a threshold = code + test + review.
// (Shares this file because it uses the same pixel/box measurement path as the luminance gate; split into a
// separate file once creating new files is allowed.)
public static class OcrLineChroma
{
    // White: bright + almost colorless (JPEG ringing around white glyphs ~20-40).
    public const int WhiteLumaMin = 200;
    public const int WhiteSatMax = 48;
    // Clearly colored: high saturation (well above compression noise) — blue/yellow/red glyphs.
    public const int ColoredSatMin = 64;

    // Drop threshold: lacking white (<4%) + excess color (≥15%). White dialogue actually measured
    // at 12-19% bright pixels (spec 16/09), so the safety margin is ~3x below the observed minimum;
    // danmaku lines hug colored glyphs at 40-70%. Changing a threshold = code + test + review.
    public const double DropWhiteMax = 0.04;
    public const double DropColoredMin = 0.15;

    // User decision 18/09: subs are always white — enforce now, no shadow.
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
