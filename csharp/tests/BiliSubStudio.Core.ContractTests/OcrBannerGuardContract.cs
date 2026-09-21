using BiliSubStudio.Core.Editor;
using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

// Guard chữ nền tĩnh + cảnh báo thiếu sub (18/09, khóa plan với QA ngoài).
// Test bằng timeline dựng tay theo đúng format QA đã duyệt (mỗi case: tên,
// timeline, speech, kỳ vọng, biên ngược).
internal static class OcrBannerGuardContract
{
    public static Task RunAsync()
    {
        CaptionHeldWithOneEarMatchKeeps();
        MuteLongQuoteDrops();
        BlinkingWatermarkDrops();
        RepeatedChorusWithSingingKeeps();
        FuzzyVariantsGrouped();
        SilentDialogueKeeps();
        StaticFullVideoWatermarkDrops();
        MissingSubWarnsUncoveredSpeech();
        MissingSubSilentWhenCovered();
        return Task.CompletedTask;
    }

    private static OcrCue Cue(double start, double end, string text) =>
        new(start, end, text, 0.9);

    private static EditorSpeechSegment Seg(double start, double end, string text) =>
        new(start, end, text, -0.2, 0.0, [], "uncertain", 0, 0);

    // Test (a)-thuận QA: caption giữ 25s trong im lặng nhưng khớp tai 1 lần
    // ở đầu -> miễn vĩnh viễn -> giữ nguyên.
    private static void CaptionHeldWithOneEarMatchKeeps()
    {
        var cues = new[] { Cue(10.0, 35.0, "深海的秘密") };
        var speech = new[] { Seg(10.0, 14.0, "深海的秘密") };
        var drops = OcrBannerGuard.Evaluate(cues, speech, 180.0);
        Check(drops.Count == 0, "caption từng khớp tai phải miễn guard");
    }

    // Test (a)-ngược QA: cùng dạng caption nhưng CÂM HOÀN TOÀN, 1 mạch 50s
    // trong video 180s -> drop đúng đánh đổi + log ghi rõ lý do.
    private static void MuteLongQuoteDrops()
    {
        var cues = new[] { Cue(10.0, 60.0, "深海的秘密") };
        var drops = OcrBannerGuard.Evaluate(cues, [], 180.0);
        Check(drops.Count == 1, "quote câm giữ 50s phải drop");
        Check(drops[0].Reason.Contains("45", StringComparison.Ordinal),
            "lý do drop phải ghi rõ ngưỡng để người duyệt thấy");
    }

    // Test (b1) QA: watermark 1.8s-bật/0.2s-tắt suốt 0-120s (video 130s,
    // cộng dồn ~83%) + thoại khác hẳn -> drop TOÀN BỘ mọi đợt.
    private static void BlinkingWatermarkDrops()
    {
        var cues = new List<OcrCue>();
        for (var start = 0.0; start + 1.8 <= 120.0; start += 2.0)
            cues.Add(Cue(start, start + 1.8, "CHANNEL XYZ"));
        var speech = new[] { Seg(0.0, 130.0, "剧情对话内容完全不同没有水印") };
        var drops = OcrBannerGuard.Evaluate(cues, speech, 130.0);
        Check(drops.Count == cues.Count, $"watermark nhấp nháy phải drop hết {cues.Count} đợt");
    }

    // Test (b2) QA: chorus "啦啦啦" lặp 15 lần (pattern y hệt watermark) nhưng
    // CÓ tiếng hát khớp -> chỉ cần 1 lần khớp là miễn cả cụm -> giữ hết.
    private static void RepeatedChorusWithSingingKeeps()
    {
        var cues = new List<OcrCue>();
        var segs = new List<EditorSpeechSegment>();
        for (var index = 0; index < 15; index++)
        {
            var start = 30.0 + index * 2.0;
            cues.Add(Cue(start, start + 1.8, "啦啦啦"));
            segs.Add(Seg(start, start + 1.8, "啦啦啦"));
        }
        var drops = OcrBannerGuard.Evaluate(cues, segs, 130.0);
        Check(drops.Count == 0, "chorus có hát khớp phải giữ hết dù lặp như watermark");
    }

    // Điểm (e) QA: 3 biến thể OCR của cùng watermark phải gộp 1 nhóm (fuzzy
    // >= 0.85) rồi drop chung — không biến thể nào thoát lẻ.
    private static void FuzzyVariantsGrouped()
    {
        var variants = new[] { "CHANNEL XYZ", "CHANNEL XY", "CHANNEL  XYZ" };
        var cues = new List<OcrCue>();
        for (var index = 0; index < 60; index++)
        {
            var start = index * 2.0;
            cues.Add(Cue(start, start + 1.8, variants[index % 3]));
        }
        var drops = OcrBannerGuard.Evaluate(cues, [], 130.0);
        Check(drops.Count == cues.Count, "biến thể watermark phải gộp nhóm và drop hết");
    }

    // Fix #1 ở tầng guard: thoại ngắn trong im lặng (tỷ lệ nhỏ) -> giữ.
    private static void SilentDialogueKeeps()
    {
        var cues = new[] { Cue(5.0, 7.0, "你好吗"), Cue(20.0, 22.0, "我很好") };
        var drops = OcrBannerGuard.Evaluate(cues, [], 180.0);
        Check(drops.Count == 0, "thoại ngắn trong im lặng phải giữ");
    }

    // Watermark tĩnh full video, không tiếng -> drop (share ~100%).
    private static void StaticFullVideoWatermarkDrops()
    {
        var cues = new[] { Cue(0.0, 180.0, "摄制组出品") };
        var drops = OcrBannerGuard.Evaluate(cues, [], 180.0);
        Check(drops.Count == 1, "watermark full video phải drop");
    }

    // Việc 3: đoạn có tiếng mà không cue nào phủ -> 1 cảnh báo có timestamp.
    private static void MissingSubWarnsUncoveredSpeech()
    {
        var speech = new[] { Seg(10.0, 14.0, "台词内容缺字幕"), Seg(50.0, 54.0, "有字幕的台词") };
        var cues = new[] { Cue(49.0, 55.0, "有字幕的台词") };
        var warnings = OcrMissingSubWarning.Find(speech, cues, 180.0);
        Check(warnings.Count == 1, "đoạn thiếu sub phải báo đúng 1");
        // (Không assert số thập phân: format theo locale máy, có thể là 9,7.)
        Check(warnings[0].Contains("thiếu sub", StringComparison.Ordinal), "cảnh báo phải nêu thiếu sub");
        Check(warnings[0].Contains("台词内容缺字幕", StringComparison.Ordinal), "cảnh báo phải trích nội dung đoạn thiếu");
    }

    // Việc 3: thoại đã có cue phủ + không speech -> im (không spam).
    private static void MissingSubSilentWhenCovered()
    {
        var speech = new[] { Seg(50.0, 54.0, "有字幕的台词") };
        var cues = new[] { Cue(49.0, 55.0, "有字幕的台词") };
        Check(OcrMissingSubWarning.Find(speech, cues, 180.0).Count == 0, "đủ sub thì không báo");
        Check(OcrMissingSubWarning.Find([], cues, 180.0).Count == 0, "không tiếng thì không báo");
        Check(OcrMissingSubWarning.Find(null, cues, 180.0).Count == 0, "thiếu tai thì im");
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
