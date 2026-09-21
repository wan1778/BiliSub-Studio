using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

// Ứng viên 2 luminance (spec khóa 16/09; luật nhới + enforce 21/09): chữ ký
// sáng từng dòng (trắng>=224 + tối<64). Thoại đo thật: sáng 12-19%; giấy/mờ/
// nền/mực-cảnh: 0% sáng. Luật nhới (user chốt 21/09 "thà giữ rác hơn xén
// thoại"): chỉ đòi phe sáng — sub nền sáng viền mỏng không bị xén oan.
// Enforce ngay ở tầng frame; mọi lỗi decode/box -> passthrough nguyên,
// không bao giờ giết chữ oan.
[SupportedOSPlatform("windows7.0")]
internal static class OcrLuminanceGateContract
{
    public static async Task RunAsync()
    {
        // Enforce pin (21/09): đã proof trên khung tổng hợp + user duyệt.
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
        // Điểm đo thật 16/09 (khung thoại video Phần 1+2/0915).
        Check(OcrLineLuminance.KeepLine(new OcrLineLuminance.LineLuminance(0.15, 0.45)), "thoại mẫu 1 phải giữ");
        Check(OcrLineLuminance.KeepLine(new OcrLineLuminance.LineLuminance(0.12, 0.54)), "thoại mẫu 2 phải giữ");
        Check(OcrLineLuminance.KeepLine(new OcrLineLuminance.LineLuminance(0.19, 0.39)), "thoại mẫu 3 phải giữ");
        // Giấy sách / chữ mờ / nền (0% sáng).
        Check(!OcrLineLuminance.KeepLine(new OcrLineLuminance.LineLuminance(0.0, 0.2)), "giấy sách phải bỏ");
        Check(!OcrLineLuminance.KeepLine(new OcrLineLuminance.LineLuminance(0.0, 0.1)), "chữ mờ phải bỏ");
        Check(!OcrLineLuminance.KeepLine(new OcrLineLuminance.LineLuminance(0.0, 0.0)), "nền trơn phải bỏ");
        // Tuyết/nền sáng chói (sáng mà thiếu phe tối) -> GIỮ (luật nhới 21/09:
        // sub trắng viền mỏng trên nền sáng cũng mang chữ ký này; xén là mất
        // thoại thật. Blob trắng ma nếu lọt thì judge/reconcile lo tiếp).
        Check(OcrLineLuminance.KeepLine(new OcrLineLuminance.LineLuminance(0.9, 0.0)), "tuyết/nền sáng phải giữ (nhới 21/09)");
        // Chữ vàng (sáng + viền tối) -> giữ ở đây (cổng chroma lo vàng riêng).
        Check(OcrLineLuminance.KeepLine(new OcrLineLuminance.LineLuminance(0.9, 0.4)), "chữ vàng phải giữ");
        // Biên ngưỡng (cách xa dust float).
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
        // Chữ trắng trên nền đen: 3 vệt ngang mô phỏng glyph (trắng+viền tối).
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
        Check(measured is not null && measured.Count == 1 && measured[0] is not null,
            "decode hộp thoại tổng hợp hỏng");
        Check(OcrLineLuminance.KeepLine(measured![0]!), "chữ trắng nền đen phải giữ");
        Check(measured[0]!.BrightFraction > 0.05 && measured[0]!.DarkFraction > 0.3,
            "chữ ký thoại tổng hợp sai hình (sáng+tối)");

        // Giấy xám chữ tối (sách): nền xám, vệt tối — thiếu phe sáng.
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

        // Nền trơn (không chữ): thiếu cả 2 phe.
        var blank = EncodeJpeg(graphics => graphics.Clear(Color.FromArgb(30, 30, 30)));
        var blankMeasured = OcrLineLuminance.Measure(blank, lines, 640, 80);
        Check(blankMeasured is not null && blankMeasured[0] is not null, "decode nền hỏng");
        Check(!OcrLineLuminance.KeepLine(blankMeasured![0]!), "nền trơn phải bỏ");

        // Scale box: canvas 1280x320 -> jpeg 640x160 (tỉ lệ 0.5). Hộp RỘNG hơn
        // vệt trắng (như box worker thật ôm cả nền quanh glyph).
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
        // Decode hỏng / rỗng / không Windows-guard đều passthrough nguyên.
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
        // Box suy biến (r<=l): fail-open giữ dòng.
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
        // Trộn: dòng thoại + dòng giấy trong 1 result (mô phỏng dán sách-thoại).
        var mixed = new OcrResult(true, true, "dialogue\npaper", 0.9,
            [new OcrLine("dialogue", 0.95, [0, 0, 640, 80]),
             new OcrLine("paper", 0.85, [0, 0, 640, 80])]);
        // Không thể đo 2 ảnh khác nhau trong 1 Apply — đo riêng rồi dựng tay.
        // Ở đây kiểm ngữ nghĩa rebuild bằng cách gọi Apply trên từng ảnh:
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

    // Ứng viên màu sắc (user chốt 18/09: sub luôn TRẮNG, chữ màu loại luôn).
    // Quy tắc giữ dòng: CÓ điểm trắng (sáng-không-màu) HOẶC thiếu điểm màu.
    // Chữ trắng trên trời xanh (nền màu bão hòa!) vẫn giữ nhờ cổng trắng —
    // case nguy hiểm nhất, ghim ở đây.
    private static void ChromaOperatingPoints()
    {
        Check(OcrLineChroma.KeepLine(new OcrLineChroma.LineChroma(0.15, 0.02)), "chữ trắng phải giữ");
        // Trắng 12% + màu 50% (trời xanh sau chữ): cổng trắng cứu — KHÔNG bỏ.
        Check(OcrLineChroma.KeepLine(new OcrLineChroma.LineChroma(0.12, 0.50)), "trắng trên nền trời phải giữ");
        // Danmaku xanh (0% trắng, 60% màu) -> bỏ.
        Check(!OcrLineChroma.KeepLine(new OcrLineChroma.LineChroma(0.0, 0.60)), "chữ xanh phải bỏ");
        // Tuyết/nền trắng (nhiều trắng, không màu) -> giữ.
        Check(OcrLineChroma.KeepLine(new OcrLineChroma.LineChroma(0.90, 0.0)), "nền trắng phải giữ");
        // Tối (thiếu cả 2) -> giữ (fail-open).
        Check(OcrLineChroma.KeepLine(new OcrLineChroma.LineChroma(0.0, 0.02)), "tối thiếu màu phải giữ");
        // Chữ vàng (trắng 0, màu cao) -> BỎ (đổi chính sách 18/09 theo yêu cầu
        // user: sub luôn trắng; cổng luminance vẫn giữ vàng, chroma thì không).
        Check(!OcrLineChroma.KeepLine(new OcrLineChroma.LineChroma(0.0, 0.80)), "chữ vàng phải bỏ (chính sách mới)");
        // Biên ngưỡng (cách xa dust float).
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
        // Chữ trắng trên nền đen: giữ, trắng nhiều, màu ~0.
        var dialogue = EncodeJpeg(graphics => { graphics.Clear(Color.Black); Bars(graphics, Color.White); });
        var measured = OcrLineChroma.Measure(dialogue, lines, 640, 80);
        Check(measured is not null && measured[0] is not null, "decode thoại hỏng");
        Check(OcrLineChroma.KeepLine(measured![0]!), "trắng nền đen phải giữ");
        Check(measured[0]!.WhiteFraction > 0.05, "trắng thoại phải đo được");
        Check(measured[0]!.ColoredFraction < 0.15, "trắng không được đo ra màu");
        // Danmaku xanh (màu vụ 陪一支): vệt xanh trên nền tối -> bỏ.
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
        // Trắng trên TRỜI XANH (nền màu bão hòa): cổng trắng phải cứu.
        var sky = EncodeJpeg(graphics =>
        {
            graphics.Clear(Color.FromArgb(135, 206, 235));
            Bars(graphics, Color.White);
        });
        var skyMeasured = OcrLineChroma.Measure(sky, lines, 640, 80);
        Check(skyMeasured is not null && skyMeasured[0] is not null, "decode trời hỏng");
        Check(skyMeasured![0]!.ColoredFraction > 0.2, "nền trời phải đo ra màu (test mới có ý nghĩa)");
        Check(OcrLineChroma.KeepLine(skyMeasured![0]!), "trắng trên trời xanh phải giữ (nguy hiểm nhất)");
        // Chữ vàng -> bỏ (chính sách mới 18/09).
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
        // Trộn 2 dòng khác box: trắng giữ, xanh bỏ, chữ ráp lại.
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
        // Toàn xanh -> trắng kết quả (không còn gì trắng). 3 vệt cho đủ mật
        // độ màu (1 vệt nhỏ trong hộp rộng thì fail-open giữ — đúng luật).
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
        // Decode hỏng -> passthrough + đếm.
        var bad = new OcrResult(true, true, "x", 0.9, [new OcrLine("x", 0.9, [0, 0, 100, 40])]);
        var (keptBad, badStats) = OcrLineChroma.Apply([1, 2, 3], bad, 640, 80, enforce: true);
        Check(keptBad.Text == "x" && badStats.DecodeFails == 1, "decode hỏng phải passthrough");
    }
}
