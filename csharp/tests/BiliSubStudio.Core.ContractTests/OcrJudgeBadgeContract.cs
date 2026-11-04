using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

// AI badge on the live table: a verdict matching Start (±1ms) + Text -> a row suffix.
// No verdict/unsure -> empty. Pure, tested directly.
internal static class OcrJudgeBadgeContract
{
    public static Task RunAsync()
    {
        KeepShowsNothing();
        DropMark();
        UnsureAndMissingBlank();
        MapDedupesAndSkipsBadEntries();
        return Task.CompletedTask;
    }

    private static OcrCue Cue(double start, string text) =>
        new(start, start + 1.0, text, 0.99);

    private static OcrCueVerdict Verdict(double start, string text, string decision, string? fixedText = null) =>
        new(start, text, decision, "test", "m", "2026-09-16", FixedText: fixedText);

    private static void KeepShowsNothing()
    {
        // A keep cue shows nothing to keep the table clean (if every row had ✓, showing it would be pointless).
        var map = OcrJudgeBadge.BuildMap([Verdict(26.233, "店", "keep")]);
        Check(OcrJudgeBadge.For(Cue(26.233, "店"), map) == string.Empty, "keep phải rỗng");
        var fixMap = OcrJudgeBadge.BuildMap([Verdict(10.0, "变", "fix", "遍")]);
        Check(OcrJudgeBadge.For(Cue(10.0, "变"), fixMap) == string.Empty, "fix phải rỗng");
    }

    private static void DropMark()
    {
        // Only a drop shows a badge (so the user knows which row will be removed from the SRT).
        var map = OcrJudgeBadge.BuildMap([Verdict(26.233, "店", "drop")]);
        Check(OcrJudgeBadge.For(Cue(26.233, "店"), map) == "  🗑 xóa", "drop phải dấu xóa");
        // No AI wording remains in the badge (AI is no longer used).
        foreach (var text in new[] { OcrJudgeBadge.For(Cue(26.233, "店"), map) })
            Check(!text.Contains("AI", StringComparison.Ordinal), "badge không được nhắc AI");
    }

    private static void UnsureAndMissingBlank()
    {
        var map = OcrJudgeBadge.BuildMap([Verdict(1.0, "啊", "unsure")]);
        Check(OcrJudgeBadge.For(Cue(1.0, "啊"), map) == string.Empty, "unsure phải rỗng");
        Check(OcrJudgeBadge.For(Cue(2.0, "嗯"), map) == string.Empty, "chưa chấm phải rỗng");
        Check(OcrJudgeBadge.For(Cue(2.0, "嗯"), null) == string.Empty, "map null phải rỗng");
        Check(OcrJudgeBadge.For(null, map) == string.Empty, "cue null phải rỗng");
        // Different text / time drift beyond 1ms -> no match.
        Check(OcrJudgeBadge.For(Cue(1.0, "嗯"), map) == string.Empty, "khác chữ phải rỗng");
        var far = OcrJudgeBadge.BuildMap([Verdict(1.002, "啊", "keep")]);
        Check(OcrJudgeBadge.For(Cue(1.0, "啊"), far) == string.Empty, "lệch giờ phải rỗng");
    }

    private static void MapDedupesAndSkipsBadEntries()
    {
        var map = OcrJudgeBadge.BuildMap([
            Verdict(5.0, "好", "keep"),
            Verdict(5.0, "好", "drop"), // re-marked: the later entry wins
            new OcrCueVerdict(-1.0, "x", "keep", "t", "m", "d"),
            new OcrCueVerdict(6.0, null!, "keep", "t", "m", "d"),
        ]);
        Check(map.Count == 1, "entry hỏng phải bỏ, trùng phải gộp");
        Check(OcrJudgeBadge.For(Cue(5.0, "好"), map) == "  🗑 xóa", "bản sau phải thắng");
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
