using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

// Text cleanup after reconcile (17/09): merge adjacent duplicates (flicker) + delete fragments.
// Pure text logic, does NOT fix misread characters. Tested with real text from user video.
internal static class OcrTextCleanupContract
{
    public static Task RunAsync()
    {
        MergeAdjacentNearDuplicates();
        NoMergeWhenDifferent();
        NoMergeWhenGapBig();
        DropFragmentSplinter();
        KeepRealShortDialogue();
        EmptyStaysEmpty();
        DropPhantomFlashCue();
        KeepTightSingleCharDialogue();
        KeepFlickerPiecesForMerge();
        KeepIsolatedShortDialogue();
        KeepOneSidedShortDialogue();
        KeepHighConfidenceShortCue();
        NoMergeTraditionalSimplified();
        KeepSimultaneousVerticalChars();
        return Task.CompletedTask;
    }

    // Real-world flicker garbage is LOW confidence (read at random) — the helper pins 0.5 to match
    // reality (18/09: IsPhantom exempts cues with confidence >= 0.9; the old helper's 0.9 at the exact threshold caused
    // noise). The high-confidence-exemption test uses its own CueConf below.
    private static OcrCue Cue(double start, double end, string text) =>
        new(start, end, text, 0.5);

    private static OcrCue CueConf(double start, double end, string text, double confidence) =>
        new(start, end, text, confidence);

    private static void MergeAdjacentNearDuplicates()
    {
        // User video cues 34/35: the same line 休书, OCR read differently (sim 0.69),
        // touching (gap 0) -> merge into 1 cue, keep the longer text.
        var result = OcrTextCleanup.Cleanup([
            Cue(58.133, 58.367, "我们早已不是一个世界的人，我己经确定新的眼惘叶象。同洗四堂了人"),
            Cue(58.367, 58.633, "我们早已不是一个世界我己经确定新的眼细叶象，、资源升"),
        ]);
        Check(result.Cues.Count == 1, "flicker kề trùng phải gộp 1");
        Check(result.Merged == 1, "đếm gộp sai");
        Check(Math.Abs(result.Cues[0].Start - 58.133) < 0.001
            && Math.Abs(result.Cues[0].End - 58.633) < 0.001, "span gộp sai");
    }

    private static void NoMergeWhenDifferent()
    {
        // Two long cues, gapped (not a phantom), with entirely different garbage tails
        // (sim 0.1) -> must NOT merge indiscriminately.
        var result = OcrTextCleanup.Cleanup([
            Cue(58.367, 59.367, "我们早已不是一个世界我己经确定新的眼细叶象，、资源升"),
            Cue(60.0, 61.0, "我己经确定新的限细叶案。对力定码回级回置子人应事止加特，资进跌升"),
        ]);
        Check(result.Cues.Count == 2, "chữ khác nhau không được gộp");
        Check(result.Merged == 0, "đếm gộp sai");
    }

    private static void NoMergeWhenGapBig()
    {
        // The same text but 5s apart (2 genuinely repeated lines of dialogue) -> no merge.
        var result = OcrTextCleanup.Cleanup([
            Cue(10.0, 11.0, "我们走吧"),
            Cue(16.0, 17.0, "我们走吧"),
        ]);
        Check(result.Cues.Count == 2, "cách xa không được gộp");
    }

    private static void DropFragmentSplinter()
    {
        // User video cues 30/31: "人。" (2 runes) adjacent to "人第一" which contains all of its
        // characters, gap 0.133s -> a flicker fragment -> delete.
        var result = OcrTextCleanup.Cleanup([
            Cue(52.800, 53.100, "人第一"),
            Cue(53.233, 53.567, "人。"),
        ]);
        Check(result.Cues.Count == 1, "mảnh vụn phải xóa");
        Check(result.Cues[0].Text == "人第一", "phải giữ câu dài");
        Check(result.FragmentsDropped == 1, "đếm mảnh sai");
    }

    private static void KeepRealShortDialogue()
    {
        // Genuine short dialogue ("好") not contained by an adjacent long line -> keep.
        var result = OcrTextCleanup.Cleanup([
            Cue(20.0, 21.0, "今天天气不错"),
            Cue(21.5, 22.0, "好"),
        ]);
        Check(result.Cues.Count == 2, "thoại ngắn thật phải giữ");
    }

    private static void EmptyStaysEmpty()
    {
        var result = OcrTextCleanup.Cleanup([]);
        Check(result.Cues.Count == 0 && result.Merged == 0 && result.FragmentsDropped == 0,
            "rỗng phải rỗng");
    }

    private static void DropPhantomFlashCue()
    {
        // User video cues 54/55/56: 55 (433ms) pinched between 2 real cues, its text related to
        // neither side -> a flash artifact -> delete. 54/56 (long) are kept.
        var result = OcrTextCleanup.Cleanup([
            Cue(91.0, 92.366, "患有严重胃疾"),
            Cue(92.500, 92.933, "这三年事业，资与眼养已经我们早已不是一个世界的人。确定新的联对象。"),
            Cue(92.933, 94.133, "龙王天天逃课"),
        ]);
        Check(result.Cues.Count == 2, "phantom phải biến mất");
        Check(result.PhantomsDropped == 1, "đếm phantom sai");
        Check(result.Cues[0].Text.StartsWith("患有", StringComparison.Ordinal)
            && result.Cues[1].Text.StartsWith("龙王", StringComparison.Ordinal),
            "2 cue thật phải còn nguyên");
    }

    private static void KeepTightSingleCharDialogue()
    {
        // 27/09 (the case where filtering lost 1 character): fast single-character dialogue ("嗯", pinched on
        // both sides by a 0.05s gap, different text, confidence 0.5) is NOT a flash — a real flash (cue 55)
        // stuffs ~30 characters into 0.43s (physically impossible), whereas 1 character/0.3s here is
        // a normal dialogue density -> keep.
        var result = OcrTextCleanup.Cleanup([
            Cue(10.0, 11.0, "今天天气不错"),
            Cue(11.05, 11.35, "嗯"),
            Cue(11.40, 12.40, "我们走吧"),
        ]);
        Check(result.Cues.Count == 3, "thoại 1 chữ kẹp sát phải giữ");
        Check(result.PhantomsDropped == 0, "không được nuốt thoại 1 chữ mật độ thường");
    }

    private static void KeepFlickerPiecesForMerge()
    {
        // Cues 34/35: short + pinched BUT with IDENTICAL text (sim 0.69) -> NOT a
        // phantom (left for the merge), rather than wrongly deleted as real dialogue. The original rule
        // without the text condition would wrongly delete 34+35, leaving only the garbage cue 36.
        var result = OcrTextCleanup.Cleanup([
            Cue(58.133, 58.367, "我们早已不是一个世界的人，我己经确定新的眼惘叶象。同洗四堂了人"),
            Cue(58.367, 58.633, "我们早已不是一个世界我己经确定新的眼细叶象，、资源升"),
        ]);
        Check(result.Cues.Count == 1, "flicker cùng câu phải gộp chứ không xóa");
        Check(result.PhantomsDropped == 0, "không được xóa flicker");
        Check(result.Merged == 1, "phải gộp");
    }

    private static void KeepOneSidedShortDialogue()
    {
        // 18/09: genuine short dialogue pinched on ONE SIDE ONLY (0.1s gap before, open after)
        // -> keep. Only a cue PINCHED ON BOTH SIDES + different from both sides is a phantom
        // (matching the word "pinched" in the spec comment).
        var result = OcrTextCleanup.Cleanup([
            Cue(10.0, 11.0, "今天天气不错"),
            Cue(11.1, 11.4, "嗯"),
            Cue(16.0, 17.0, "我们走吧"),
        ]);
        Check(result.Cues.Count == 3, "thoại ngắn hẹp 1 bên phải giữ");
        Check(result.PhantomsDropped == 0, "không xóa oan thoại ngắn 1 bên thoáng");
    }

    private static void KeepHighConfidenceShortCue()
    {
        // 18/09 (pinched-phantom exemption): genuine short dialogue read VERY confidently ("嗯",
        // confidence 0.95) pinched against 2 different-text lines is still kept — it is not a blink.
        var result = OcrTextCleanup.Cleanup([
            CueConf(10.0, 11.0, "今天天气不错", 0.5),
            CueConf(11.05, 11.45, "嗯", 0.95),
            CueConf(11.50, 12.50, "我们走吧", 0.5),
        ]);
        Check(result.Cues.Count == 3, "cue tin cao phải miễn phantom");
        Check(result.PhantomsDropped == 0, "không xóa oan thoại ngắn chắc chắn");
    }

    private static void NoMergeTraditionalSimplified()
    {
        // 18/09 (Task 4, spec): a video mixing traditional/simplified — "国语" vs "國語"
        // have different runes so sim ~0, do NOT merge (no text is lost; the duplicate is left for the user
        // to see when reviewing, never unilaterally delete one version).
        var result = OcrTextCleanup.Cleanup([
            Cue(10.0, 11.0, "国语"),
            Cue(11.05, 12.0, "國語"),
        ]);
        Check(result.Cues.Count == 2, "phồn/giản không được gộp bừa");
        Check(result.Merged == 0, "không gộp phồn/giản");
    }

    private static void KeepSimultaneousVerticalChars()
    {
        // 18/09 (Task 4, spec): vertical text appearing AT THE SAME TIME (5 single-character cues, the same
        // 10-15s span) — a negative gap makes fragment/merge skip them, the long duration makes
        // phantom skip them -> keep everything. (Whether the worker can merge vertical columns must
        // be checked by hand on a video with vertical text.)
        var result = OcrTextCleanup.Cleanup([
            Cue(10.0, 15.0, "深"),
            Cue(10.0, 15.0, "海"),
            Cue(10.0, 15.0, "的"),
            Cue(10.0, 15.0, "秘"),
            Cue(10.0, 15.0, "密"),
        ]);
        Check(result.Cues.Count == 5, "chữ dọc cùng lúc phải giữ hết");
        Check(result.PhantomsDropped == 0 && result.Merged == 0 && result.FragmentsDropped == 0,
            "không được đụng chữ dọc");
    }

    private static void KeepIsolatedShortDialogue()
    {
        // Genuine short dialogue standing alone (large gaps on both sides) -> keep.
        var result = OcrTextCleanup.Cleanup([
            Cue(10.0, 11.0, "今天天气不错"),
            Cue(15.0, 15.4, "好"),
            Cue(20.0, 21.0, "我们走吧"),
        ]);
        Check(result.Cues.Count == 3, "thoại ngắn đứng riêng phải giữ");
        Check(result.PhantomsDropped == 0, "không xóa oan");
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
