using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

// Luminance candidate gate 2 (spec locked 16/09; lenient rule + enforcement 21/09): per-line
// bright signature (white>=200 + dark<64). Measured real dialogue: 12-19% bright; paper/blurry/
// background/scene ink: 0% bright. The lenient rule (user confirmed 21/09, "better to keep garbage
// than trim dialogue"): requires only the bright side — a bright-background subtitle with a thin
// outline is not trimmed wrongly.
// Enforcement happens right at the frame layer; every decode/box error -> untouched passthrough,
// never kills text wrongly.
[SupportedOSPlatform("windows7.0")]
internal static class OcrLuminanceGateContract
{
    public static async Task RunAsync()
    {
        // Enforce pin (21/09): proof on a composite frame + user approved.
        Check(OcrLineLuminance.EnforceDefault, "luminance phải enforce (luật nhới 21/09: chỉ đòi phe sáng)");

        ClassifyOperatingPoints();
        await SyntheticBitmapsAsync();
        PassthroughCases();
        EnforceSemantics();
        ShadowKeepsEverything();
        await NoHandleLeakAsync();
        ChromaOperatingPoints();
        await ChromaSyntheticAsync();
        ChromaApplySemantics();
    }

    private static void ClassifyOperatingPoints()
    {
        // Real measured points 16/09 (dialogue frames from the Part 1+2/0915 video).
        Check(OcrLineLuminance.KeepLine(new OcrLineLuminance.LineLuminance(0.15, 0.45)), "thoại mẫu 1 phải giữ");
        Check(OcrLineLuminance.KeepLine(new OcrLineLuminance.LineLuminance(0.12, 0.54)), "thoại mẫu 2 phải giữ");
        Check(OcrLineLuminance.KeepLine(new OcrLineLuminance.LineLuminance(0.19, 0.39)), "thoại mẫu 3 phải giữ");
        // Book paper / blurry text / background (0% bright).
        Check(!OcrLineLuminance.KeepLine(new OcrLineLuminance.LineLuminance(0.0, 0.2)), "giấy sách phải bỏ");
        Check(!OcrLineLuminance.KeepLine(new OcrLineLuminance.LineLuminance(0.0, 0.1)), "chữ mờ phải bỏ");
        Check(!OcrLineLuminance.KeepLine(new OcrLineLuminance.LineLuminance(0.0, 0.0)), "nền trơn phải bỏ");
        // Snow/glaring bright background (bright but missing the dark side) -> KEEP (lenient rule 21/09:
        // a white thin-outline subtitle on a bright background also carries this signature; trimming
        // loses real dialogue. If a phantom white blob slips through, the judge/reconcile handles it).
        Check(OcrLineLuminance.KeepLine(new OcrLineLuminance.LineLuminance(0.9, 0.0)), "tuyết/nền sáng phải giữ (nhới 21/09)");
        // Yellow text (bright + dark outline) -> kept here (the chroma gate drops yellow separately).
        Check(OcrLineLuminance.KeepLine(new OcrLineLuminance.LineLuminance(0.9, 0.4)), "chữ vàng phải giữ");
        // Threshold boundary (kept away from the dust float).
        Check(OcrLineLuminance.KeepLine(new OcrLineLuminance.LineLuminance(0.08, 0.15)), "trên ngưỡng phải giữ");
        Check(!OcrLineLuminance.KeepLine(new OcrLineLuminance.LineLuminance(0.04, 0.5)), "dưới ngưỡng sáng phải bỏ");
        Check(OcrLineLuminance.KeepLine(new OcrLineLuminance.LineLuminance(0.5, 0.05)), "sáng đủ dù thiếu tối vẫn giữ (nhới 21/09)");
    }

    private static byte[] EncodeJpeg(Action<Graphics> draw, int width = 640, int height = 80)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
            draw(graphics);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Jpeg);
        return stream.ToArray();
    }

    private static OcrLine FullBox(string text, double conf = 0.9) =>
        new(text, conf, [0, 0, 640, 80]);

    private static async Task SyntheticBitmapsAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        await Task.Yield();
        // White text on a black background: 3 horizontal strokes simulating a glyph (white + dark outline).
        var dialogue = EncodeJpeg(graphics =>
        {
            graphics.Clear(Color.Black);
            using var brush = new SolidBrush(Color.White);
            graphics.FillRectangle(brush, 60, 10, 200, 24);
            graphics.FillRectangle(brush, 300, 10, 160, 24);
            graphics.FillRectangle(brush, 120, 44, 260, 24);
        });
        var lines = new[] { FullBox("你好世界") };
        var measured = OcrLineLuminance.Measure(dialogue, lines, 640, 80);
        // 22/10 field 桃花剑神: color grading leaves the visible white caption at
        // 4-6% pixels at 224 but 16-21% pixels at 200. The old 224 cut turned those
        // frames into a blank and split one caption into 0.2s cues.
        var compressedWhite = EncodeJpeg(graphics =>
        {
            graphics.Clear(Color.Black);
            using var brush = new SolidBrush(Color.FromArgb(212, 212, 212));
            graphics.FillRectangle(brush, 60, 10, 200, 24);
            graphics.FillRectangle(brush, 300, 10, 160, 24);
            graphics.FillRectangle(brush, 120, 44, 260, 24);
        });
        var compressedMeasured = OcrLineLuminance.Measure(compressedWhite, lines, 640, 80);
        Check(compressedMeasured is not null && compressedMeasured[0] is not null,
            "decode nen trang nacn xam hong");
        Check(compressedMeasured![0]!.BrightFraction > 0.05,
            "sub trang nacn xam van phai do ra phe sang");
        Check(OcrLineLuminance.KeepLine(compressedMeasured[0]!),
            "sub trang nacn xam khong duoc giet oan");
        Check(measured is not null && measured.Count == 1 && measured[0] is not null,
            "decode hộp thoại tổng hợp hỏng");
        Check(OcrLineLuminance.KeepLine(measured![0]!), "chữ trắng nền đen phải giữ");
        Check(measured[0]!.BrightFraction > 0.05 && measured[0]!.DarkFraction > 0.3,
            "chữ ký thoại tổng hợp sai hình (sáng+tối)");

        // Grey paper with dark text (a book): grey background, dark strokes — missing the bright side.
        var paper = EncodeJpeg(graphics =>
        {
            graphics.Clear(Color.FromArgb(110, 110, 110));
            using var brush = new SolidBrush(Color.FromArgb(40, 40, 40));
            graphics.FillRectangle(brush, 60, 10, 200, 24);
            graphics.FillRectangle(brush, 300, 10, 160, 24);
        });
        var paperMeasured = OcrLineLuminance.Measure(paper, lines, 640, 80);
        Check(paperMeasured is not null && paperMeasured[0] is not null, "decode giấy hỏng");
        Check(!OcrLineLuminance.KeepLine(paperMeasured![0]!), "chữ tối trên giấy phải bỏ");

        // Flat background (no text): missing both sides.
        var blank = EncodeJpeg(graphics => graphics.Clear(Color.FromArgb(30, 30, 30)));
        var blankMeasured = OcrLineLuminance.Measure(blank, lines, 640, 80);
        Check(blankMeasured is not null && blankMeasured[0] is not null, "decode nền hỏng");
        Check(!OcrLineLuminance.KeepLine(blankMeasured![0]!), "nền trơn phải bỏ");

        // Scale box: canvas 1280x320 -> jpeg 640x160 (ratio 0.5). The box is WIDER than
        // the white stroke (like a real worker box that also includes the background around the glyph).
        var scaled = EncodeJpeg(graphics =>
        {
            graphics.Clear(Color.Black);
            using var brush = new SolidBrush(Color.White);
            graphics.FillRectangle(brush, 50, 7, 60, 8);
        }, width: 640, height: 160);
        var scaledLine = new OcrLine("t", 0.9, [60, 10, 260, 34]);
        var scaledMeasured = OcrLineLuminance.Measure(scaled, [scaledLine], 1280, 320);
        Check(scaledMeasured is not null && scaledMeasured[0] is not null, "scale box hỏng");
        Check(OcrLineLuminance.KeepLine(scaledMeasured![0]!), "scale 0.5 phải giữ chữ trắng");
    }

    private static void PassthroughCases()
    {
        var result = new OcrResult(true, true, "你好", 0.99,
            [new OcrLine("你好", 0.99, [0, 0, 100, 40])]);
        // Broken decode / empty / no Windows-guard all pass through untouched.
        foreach (var bad in new byte[][] { [], [1, 2, 3], new byte[16] })
        {
            var (kept, stats) = OcrLineLuminance.Apply(bad, result, 1280, 320, enforce: true);
            Check(ReferenceEquals(kept, result) || kept.Text == result.Text, "decode hỏng phải passthrough");
            Check(stats.DecodeFails == 1, "decode hỏng phải đếm");
        }
        var (emptyKept, emptyStats) = OcrLineLuminance.Apply(
            new byte[] { 1 }, result with { Lines = [] }, 1280, 320, enforce: true);
        Check(emptyKept.Lines.Count == 0 && emptyStats.MeasuredLines == 0, "không dòng thì không đo");
        var failed = new OcrResult(false, false, "", 0, []);
        var (failedKept, _) = OcrLineLuminance.Apply(new byte[] { 1 }, failed, 1280, 320, enforce: true);
        Check(ReferenceEquals(failedKept, failed), "result lỗi phải passthrough");
        // Degenerate box (r<=l): fail-open keeps the line.
        var weird = new OcrResult(true, true, "x", 0.5, [new OcrLine("x", 0.5, [10, 10, 10, 10])]);
        var okJpeg = EncodeJpegOrSkip();
        if (okJpeg is not null)
        {
            var measured = OcrLineLuminance.Measure(okJpeg, weird.Lines, 640, 80);
            Check(measured is not null && measured[0] is null, "box suy biến phải null (giữ dòng)");
        }
    }

    private static byte[]? EncodeJpegOrSkip()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            return EncodeJpeg(graphics => graphics.Clear(Color.Black));
        }
        catch
        {
            return null;
        }
    }

    private static void EnforceSemantics()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dialogue = EncodeJpeg(graphics =>
        {
            graphics.Clear(Color.Black);
            using var brush = new SolidBrush(Color.White);
            graphics.FillRectangle(brush, 40, 8, 220, 26);
        });
        var paper = EncodeJpeg(graphics =>
        {
            graphics.Clear(Color.FromArgb(110, 110, 110));
            using var brush = new SolidBrush(Color.FromArgb(40, 40, 40));
            graphics.FillRectangle(brush, 40, 8, 220, 26);
        });
        // Mixed: a dialogue line + a paper line in 1 result (simulating paper-dialogue pasting).
        var mixed = new OcrResult(true, true, "dialogue\npaper", 0.9,
            [new OcrLine("dialogue", 0.95, [0, 0, 640, 80]),
             new OcrLine("paper", 0.85, [0, 0, 640, 80])]);
        // Two different images cannot be measured in 1 Apply — measure them separately then assemble by hand.
        // Here the rebuild semantics are checked by calling Apply on each image:
        var (keptDialog, _) = OcrLineLuminance.Apply(dialogue,
            new OcrResult(true, true, "dialogue", 0.95, [new OcrLine("dialogue", 0.95, [0, 0, 640, 80])]),
            640, 80, enforce: true);
        Check(keptDialog.Detected && keptDialog.Text == "dialogue", "enforce phải giữ dòng thoại");
        var (keptPaper, paperStats) = OcrLineLuminance.Apply(paper,
            new OcrResult(true, true, "paper", 0.85, [new OcrLine("paper", 0.85, [0, 0, 640, 80])]),
            640, 80, enforce: true);
        Check(!keptPaper.Detected && keptPaper.Text == "" && keptPaper.Lines.Count == 0,
            "enforce bỏ hết phải thành trắng");
        Check(paperStats.WouldDropLines == 1, "đếm would-drop sai");
        Check(mixed.Lines.Count == 2, "sanity check dự phòng");
    }

    private static void ShadowKeepsEverything()
    {
        if (!OperatingSystem.IsWindows()) return;
        var paper = EncodeJpeg(graphics =>
        {
            graphics.Clear(Color.FromArgb(110, 110, 110));
            using var brush = new SolidBrush(Color.FromArgb(40, 40, 40));
            graphics.FillRectangle(brush, 40, 8, 220, 26);
        });
        var result = new OcrResult(true, true, "paper", 0.85,
            [new OcrLine("paper", 0.85, [0, 0, 640, 80])]);
        var (kept, stats) = OcrLineLuminance.Apply(paper, result, 640, 80, enforce: false);
        Check(ReferenceEquals(kept, result), "shadow phải trả nguyên result");
        Check(stats.WouldDropLines == 1 && stats.MeasuredLines == 1, "shadow phải đếm would-drop");
    }

    private static async Task NoHandleLeakAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        await Task.Yield();
        var jpeg = EncodeJpeg(graphics =>
        {
            graphics.Clear(Color.Black);
            using var brush = new SolidBrush(Color.White);
            graphics.FillRectangle(brush, 40, 8, 220, 26);
        });
        var lines = new[] { FullBox("leak") };
        for (var warmup = 0; warmup < 30; warmup++)
            OcrLineLuminance.Measure(jpeg, lines, 640, 80);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = Process.GetCurrentProcess().HandleCount;
        for (var index = 0; index < 200; index++)
            OcrLineLuminance.Measure(jpeg, lines, 640, 80);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var after = Process.GetCurrentProcess().HandleCount;
        Check(after - before < 100, $"rò handle GDI+ ({after - before}/200 decode)");
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }

    // Color chroma candidate gate (user confirmed 18/09: subtitles are always WHITE, colored text
    // is always rejected). The keep-line rule: HAS white points (bright-no-color) OR lacks colored points.
    // White text on a blue sky (a saturated color background!) is still kept thanks to the white gate —
    // the most dangerous case, pinned here.
    private static void ChromaOperatingPoints()
    {
        Check(OcrLineChroma.KeepLine(new OcrLineChroma.LineChroma(0.15, 0.02)), "chữ trắng phải giữ");
        // White 12% + color 50% (blue sky behind the text): the white gate rescues it — NOT dropped.
        Check(OcrLineChroma.KeepLine(new OcrLineChroma.LineChroma(0.12, 0.50)), "trắng trên nền trời phải giữ");
        // Blue danmaku (0% white, 60% color) -> drop.
        Check(!OcrLineChroma.KeepLine(new OcrLineChroma.LineChroma(0.0, 0.60)), "chữ xanh phải bỏ");
        // Snow/white background (lots of white, no color) -> keep.
        Check(OcrLineChroma.KeepLine(new OcrLineChroma.LineChroma(0.90, 0.0)), "nền trắng phải giữ");
        // Dark (missing both) -> keep (fail-open).
        Check(OcrLineChroma.KeepLine(new OcrLineChroma.LineChroma(0.0, 0.02)), "tối thiếu màu phải giữ");
        // Yellow text (white 0, high color) -> DROP (18/09 policy change per user
        // request: subtitles are always white; the luminance gate still keeps yellow, chroma does not).
        Check(!OcrLineChroma.KeepLine(new OcrLineChroma.LineChroma(0.0, 0.80)), "chữ vàng phải bỏ (chính sách mới)");
        // Threshold boundary (kept away from the dust float).
        Check(OcrLineChroma.KeepLine(new OcrLineChroma.LineChroma(0.04, 0.90)), "đúng ngưỡng trắng phải giữ");
        Check(!OcrLineChroma.KeepLine(new OcrLineChroma.LineChroma(0.039, 0.90)), "dưới ngưỡng trắng phải bỏ");
        Check(!OcrLineChroma.KeepLine(new OcrLineChroma.LineChroma(0.0, 0.15)), "đúng ngưỡng màu phải bỏ");
        Check(OcrLineChroma.KeepLine(new OcrLineChroma.LineChroma(0.0, 0.149)), "dưới ngưỡng màu phải giữ");
    }

    private static async Task ChromaSyntheticAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        await Task.Yield();
        void Bars(Graphics graphics, Color color)
        {
            using var brush = new SolidBrush(color);
            graphics.FillRectangle(brush, 60, 10, 200, 24);
            graphics.FillRectangle(brush, 300, 10, 160, 24);
            graphics.FillRectangle(brush, 120, 44, 260, 24);
        }
        var lines = new[] { FullBox("你好世界") };
        // White text on a black background: keep, lots of white, color ~0.
        var dialogue = EncodeJpeg(graphics => { graphics.Clear(Color.Black); Bars(graphics, Color.White); });
        var measured = OcrLineChroma.Measure(dialogue, lines, 640, 80);
        Check(measured is not null && measured[0] is not null, "decode thoại hỏng");
        Check(OcrLineChroma.KeepLine(measured![0]!), "trắng nền đen phải giữ");
        Check(measured[0]!.WhiteFraction > 0.05, "trắng thoại phải đo được");
        Check(measured[0]!.ColoredFraction < 0.15, "trắng không được đo ra màu");
        // Blue danmaku (陪一支 color block): a blue stroke on a dark background -> drop.
        var danmaku = EncodeJpeg(graphics =>
        {
            graphics.Clear(Color.FromArgb(20, 20, 20));
            Bars(graphics, Color.FromArgb(100, 180, 255));
        });
        var danmakuMeasured = OcrLineChroma.Measure(danmaku, lines, 640, 80);
        Check(danmakuMeasured is not null && danmakuMeasured[0] is not null, "decode danmaku hỏng");
        Check(!OcrLineChroma.KeepLine(danmakuMeasured![0]!), "chữ xanh phải bỏ");
        Check(danmakuMeasured[0]!.WhiteFraction < 0.04 && danmakuMeasured[0]!.ColoredFraction >= 0.15,
            "chữ ký danmaku sai hình (thiếu trắng + thừa màu)");
        // White on a BLUE SKY (saturated color background): the white gate must rescue it.
        var sky = EncodeJpeg(graphics =>
        {
            graphics.Clear(Color.FromArgb(135, 206, 235));
            Bars(graphics, Color.White);
        });
        var skyMeasured = OcrLineChroma.Measure(sky, lines, 640, 80);
        Check(skyMeasured is not null && skyMeasured[0] is not null, "decode trời hỏng");
        Check(skyMeasured![0]!.ColoredFraction > 0.2, "nền trời phải đo ra màu (test mới có ý nghĩa)");
        Check(OcrLineChroma.KeepLine(skyMeasured![0]!), "trắng trên trời xanh phải giữ (nguy hiểm nhất)");
        // Yellow text -> drop (new policy 18/09).
        var yellow = EncodeJpeg(graphics =>
        {
            graphics.Clear(Color.Black);
            Bars(graphics, Color.FromArgb(255, 220, 0));
        });
        var yellowMeasured = OcrLineChroma.Measure(yellow, lines, 640, 80);
        Check(yellowMeasured is not null && yellowMeasured[0] is not null, "decode vàng hỏng");
        Check(!OcrLineChroma.KeepLine(yellowMeasured![0]!), "vàng phải bỏ (sub luôn trắng)");
    }

    private static void ChromaApplySemantics()
    {
        if (!OperatingSystem.IsWindows()) return;
        // Mix 2 lines with different boxes: white kept, blue dropped, text stitched back together.
        var mixed = EncodeJpeg(graphics =>
        {
            graphics.Clear(Color.Black);
            using var white = new SolidBrush(Color.White);
            graphics.FillRectangle(white, 20, 10, 200, 24);
            using var blue = new SolidBrush(Color.FromArgb(100, 180, 255));
            graphics.FillRectangle(blue, 340, 10, 200, 24);
        });
        var result = new OcrResult(true, true, "sub\nspam", 0.9,
            [new OcrLine("sub", 0.95, [0, 0, 320, 80]),
             new OcrLine("spam", 0.85, [320, 0, 640, 80])]);
        var (kept, stats) = OcrLineChroma.Apply(mixed, result, 640, 80, enforce: true);
        Check(kept.Detected && kept.Text == "sub" && kept.Lines.Count == 1, "enforce giữ đúng dòng trắng");
        Check(stats.WouldDropLines == 1 && stats.MeasuredLines == 2, "đếm sai");
        // All blue -> blank result (nothing white left). 3 strokes give enough color
        // density (a single small stroke in a wide box is fail-open kept — the correct rule).
        var blue = EncodeJpeg(graphics =>
        {
            graphics.Clear(Color.Black);
            using var brush = new SolidBrush(Color.FromArgb(100, 180, 255));
            graphics.FillRectangle(brush, 60, 10, 200, 24);
            graphics.FillRectangle(brush, 300, 10, 160, 24);
            graphics.FillRectangle(brush, 120, 44, 260, 24);
        });
        var (allBlue, _) = OcrLineChroma.Apply(blue,
            new OcrResult(true, true, "spam", 0.9, [new OcrLine("spam", 0.9, [0, 0, 640, 80])]),
            640, 80, enforce: true);
        Check(!allBlue.Detected && allBlue.Text == "" && allBlue.Lines.Count == 0, "toàn xanh phải trắng");
        // Broken decode -> passthrough + count.
        var bad = new OcrResult(true, true, "x", 0.9, [new OcrLine("x", 0.9, [0, 0, 100, 40])]);
        var (keptBad, badStats) = OcrLineChroma.Apply([1, 2, 3], bad, 640, 80, enforce: true);
        Check(keptBad.Text == "x" && badStats.DecodeFails == 1, "decode hỏng phải passthrough");
    }
}
