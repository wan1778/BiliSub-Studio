using BiliSubStudio.Core.Editor;
using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

// Guard against static background text + a missing-subtitle warning (18/09, plan locked with external QA).
// Tested with hand-built timelines in exactly the QA-approved format (each case: name,
// timeline, speech, expected result, reverse boundary).
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

    // QA test (a)-forward: a caption held for 25s in silence but matching the ear once
    // at the start -> permanent exemption -> left intact.
    private static void CaptionHeldWithOneEarMatchKeeps()
    {
        var cues = new[] { Cue(10.0, 35.0, "深海的秘密") };
        var speech = new[] { Seg(10.0, 14.0, "深海的秘密") };
        var drops = OcrBannerGuard.Evaluate(cues, speech, 180.0);
        Check(drops.Count == 0, "caption từng khớp tai phải miễn guard");
    }

    // QA test (a)-reverse: the same caption shape but COMPLETELY MUTE, a straight 50s
    // inside a 180s video -> drop at the correct trade-off + a log stating the reason clearly.
    private static void MuteLongQuoteDrops()
    {
        var cues = new[] { Cue(10.0, 60.0, "深海的秘密") };
        var drops = OcrBannerGuard.Evaluate(cues, [], 180.0);
        Check(drops.Count == 1, "quote câm giữ 50s phải drop");
        Check(drops[0].Reason.Contains("45", StringComparison.Ordinal),
            "lý do drop phải ghi rõ ngưỡng để người duyệt thấy");
    }

    // QA test (b1): a 1.8s-on/0.2s-off watermark throughout 0-120s (a 130s video,
    // accumulating ~83%) + entirely different speech -> drop EVERY batch.
    private static void BlinkingWatermarkDrops()
    {
        var cues = new List<OcrCue>();
        for (var start = 0.0; start + 1.8 <= 120.0; start += 2.0)
            cues.Add(Cue(start, start + 1.8, "CHANNEL XYZ"));
        var speech = new[] { Seg(0.0, 130.0, "剧情对话内容完全不同没有水印") };
        var drops = OcrBannerGuard.Evaluate(cues, speech, 130.0);
        Check(drops.Count == cues.Count, $"watermark nhấp nháy phải drop hết {cues.Count} đợt");
    }

    // QA test (b2): a "啦啦啦" chorus repeated 15 times (an exact watermark pattern) but
    // WITH matching singing -> one match exempts the whole cluster -> keep everything.
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

    // QA point (e): 3 OCR variants of the same watermark must group into 1 cluster (fuzzy
    // >= 0.85) then drop together — no variant escapes alone.
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

    // Fix #1 at the guard layer: short dialogue in silence (a small ratio) -> keep.
    private static void SilentDialogueKeeps()
    {
        var cues = new[] { Cue(5.0, 7.0, "你好吗"), Cue(20.0, 22.0, "我很好") };
        var drops = OcrBannerGuard.Evaluate(cues, [], 180.0);
        Check(drops.Count == 0, "thoại ngắn trong im lặng phải giữ");
    }

    // A static full-video watermark with no audio -> drop (share ~100%).
    private static void StaticFullVideoWatermarkDrops()
    {
        var cues = new[] { Cue(0.0, 180.0, "摄制组出品") };
        var drops = OcrBannerGuard.Evaluate(cues, [], 180.0);
        Check(drops.Count == 1, "watermark full video phải drop");
    }

    // Task 3: a segment with audio that no cue covers -> exactly 1 warning with a timestamp.
    private static void MissingSubWarnsUncoveredSpeech()
    {
        var speech = new[] { Seg(10.0, 14.0, "台词内容缺字幕"), Seg(50.0, 54.0, "有字幕的台词") };
        var cues = new[] { Cue(49.0, 55.0, "有字幕的台词") };
        var warnings = OcrMissingSubWarning.Find(speech, cues, 180.0);
        Check(warnings.Count == 1, "đoạn thiếu sub phải báo đúng 1");
        // (No decimal assertion: the format follows the machine locale, it may be 9,7.)
        Check(warnings[0].Contains("thiếu sub", StringComparison.Ordinal), "cảnh báo phải nêu thiếu sub");
        Check(warnings[0].Contains("台词内容缺字幕", StringComparison.Ordinal), "cảnh báo phải trích nội dung đoạn thiếu");
    }

    // Task 3: speech already covered by a cue + no speech -> silent (no spam).
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
