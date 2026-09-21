using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

// Dọn chữ sau reconcile (17/09): gộp cue kề trùng lặp (flicker) + xóa mảnh vụn.
// Thuần logic chữ, KHÔNG sửa chữ đọc sai. Test bằng chữ thật từ video user.
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
        KeepFlickerPiecesForMerge();
        KeepIsolatedShortDialogue();
        KeepOneSidedShortDialogue();
        KeepHighConfidenceShortCue();
        NoMergeTraditionalSimplified();
        KeepSimultaneousVerticalChars();
        return Task.CompletedTask;
    }

    // Rác flicker ngoài đời tin THẤP (đọc bừa) — helper để 0.5 cho đúng thực
    // tế (18/09: IsPhantom miễn cue tin >= 0.9, helper cũ 0.9 đúng ngưỡng gây
    // nhiễu). Test miễn-tin-cao dùng CueConf riêng bên dưới.
    private static OcrCue Cue(double start, double end, string text) =>
        new(start, end, text, 0.5);

    private static OcrCue CueConf(double start, double end, string text, double confidence) =>
        new(start, end, text, confidence);

    private static void MergeAdjacentNearDuplicates()
    {
        // Cue 34/35 video user: cùng câu 休书, OCR đọc khác nhau (sim 0.69),
        // dính nhau (gap 0) -> gộp 1 cue, giữ chữ dài hơn.
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
        // Hai cue dài, hở nhau (không phantom), đuôi rác khác hẳn nhau
        // (sim 0.1) -> KHÔNG gộp bừa.
        var result = OcrTextCleanup.Cleanup([
            Cue(58.367, 59.367, "我们早已不是一个世界我己经确定新的眼细叶象，、资源升"),
            Cue(60.0, 61.0, "我己经确定新的限细叶案。对力定码回级回置子人应事止加特，资进跌升"),
        ]);
        Check(result.Cues.Count == 2, "chữ khác nhau không được gộp");
        Check(result.Merged == 0, "đếm gộp sai");
    }

    private static void NoMergeWhenGapBig()
    {
        // Cùng chữ nhưng cách xa 5s (2 câu thoại lặp lại thật) -> không gộp.
        var result = OcrTextCleanup.Cleanup([
            Cue(10.0, 11.0, "我们走吧"),
            Cue(16.0, 17.0, "我们走吧"),
        ]);
        Check(result.Cues.Count == 2, "cách xa không được gộp");
    }

    private static void DropFragmentSplinter()
    {
        // Cue 30/31 video user: "人。" (2 rune) kề "人第一" chứa hết chữ nó,
        // gap 0.133s -> mảnh flicker -> xóa.
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
        // Thoại ngắn thật ("好") không bị câu dài kề chứa -> giữ.
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
        // Cue 54/55/56 video user: 55 (433ms) kẹp giữa 2 cue thật, chữ không
        // liên quan bên nào -> flash artifact -> xóa. 54/56 (dài) giữ.
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

    private static void KeepFlickerPiecesForMerge()
    {
        // Cue 34/35: ngắn + kẹp NHƯNG chữ GIỐNG nhau (sim 0.69) -> KHÔNG phải
        // phantom (để merge gộp), chứ không xóa oan câu thoại thật. Rule gốc
        // không có điều kiện chữ sẽ xóa nhầm 34+35, chỉ còn cue 36 rác.
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
        // 18/09: thoại ngắn thật chỉ hẹp 1 BÊN (gap trước 0.1s, sau thoáng)
        // -> giữ. Chỉ cue KẸP CẢ 2 BÊN + khác chữ 2 bên mới là phantom
        // (đúng chữ "kẹp" trong comment đặc tả).
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
        // 18/09 (miễn phantom hẹp): thoại ngắn thật đọc RẤT chắc ("嗯", tin
        // 0.95) kẹp sát 2 câu khác chữ vẫn giữ — không phải chớp-nháy.
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
        // 18/09 (Việc 4, đặc tả): video lẫn phồn/giản — "国语" vs "國語"
        // khác rune nên sim ~0, KHÔNG gộp (không mất chữ; trùng lặp để user
        // thấy khi duyệt, không tự ý xóa 1 bản).
        var result = OcrTextCleanup.Cleanup([
            Cue(10.0, 11.0, "国语"),
            Cue(11.05, 12.0, "國語"),
        ]);
        Check(result.Cues.Count == 2, "phồn/giản không được gộp bừa");
        Check(result.Merged == 0, "không gộp phồn/giản");
    }

    private static void KeepSimultaneousVerticalChars()
    {
        // 18/09 (Việc 4, đặc tả): chữ dọc hiện CÙNG lúc (5 cue 1 chữ, cùng
        // span 10-15s) — gap âm nên fragment/merge bỏ qua, duration dài nên
        // phantom bỏ qua -> giữ hết. (Worker có gộp cột dọc được không thì
        // phải check tay trên video có chữ dọc.)
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
        // Thoại ngắn thật đứng riêng (gap 2 bên lớn) -> giữ.
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
