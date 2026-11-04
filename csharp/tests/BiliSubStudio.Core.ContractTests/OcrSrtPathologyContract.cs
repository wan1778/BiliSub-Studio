using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

// E.6 SRT PATHOLOGY GATE REPAIR-FIRST (OCR Gates v1.3, fixed 2026-09-14):
// if it is repairable, repair + warn (trim/drop/sort); only block when nothing is left to save.
// Lesson from cue case 2703: blocking a file over a repairable 0.1s overlap robs the user's file.
internal static class OcrSrtPathologyContract
{
    public static Task RunAsync()
    {
        // Clean: passes silently, unchanged.
        var clean = OcrSrtPathology.Repair([
            (1.0, 2.5, "你好"),
            (3.0, 4.0, "再见"),
            (5.0, 6.5, "谢谢"),
        ]);
        Check(clean.Passed && clean.Failures.Count == 0 && clean.Warnings.Count == 0, "clean cues must pass silent");
        Check(clean.Cues.Count == 3, "clean cues must survive untouched");

        // Touching: pass.
        Check(OcrSrtPathology.Repair([(1.0, 2.0, "甲"), (2.0, 3.0, "乙")]).Passed, "touching cues must pass");

        // Overlap 0.5s (cue case 2703: 0.1s): TRIM prev.End = next.Start + warn, STILL WRITES.
        var overlap = OcrSrtPathology.Repair([(6996.6, 6997.5, "甲"), (6997.4, 6998.0, "乙")]);
        Check(overlap.Passed, "trimmable overlap must still release, never block the file");
        Check(overlap.Cues.Count == 2 && overlap.Cues[0].End == 6997.4, "overlap must trim prev.End to next.Start");
        Check(overlap.Warnings.Count > 0, "trim must warn");

        // Going backward: sort + warn + pass (players like the sort pre-applied).
        var backward = OcrSrtPathology.Repair([(3.0, 4.0, "甲"), (1.0, 2.0, "乙")]);
        Check(backward.Passed && backward.Cues[0].Start == 1.0 && backward.Cues[1].Start == 3.0, "backward input must sort, not fail");

        // Garbage (End<=Start, blank text, fragments): drop each cue + list them; nothing left -> FAIL.
        Check(!OcrSrtPathology.Repair([(1.0, 1.0, "甲")]).Passed, "sole zero-duration cue must fail (nothing salvageable)");
        Check(!OcrSrtPathology.Repair([(1.0, 2.0, "   ")]).Passed, "sole blank cue must fail (nothing salvageable)");
        Check(!OcrSrtPathology.Repair([(1.0, 1.05, "甲")]).Passed, "sole fragment must fail (nothing salvageable)");
        var mixed = OcrSrtPathology.Repair([(1.0, 2.0, "甲"), (3.0, 3.0, "x"), (4.0, 5.0, "乙")]);
        Check(mixed.Passed && mixed.Cues.Count == 2, "bad cue among good must drop only itself");
        Check(mixed.Warnings.Count > 0, "drop must warn with the listed cue");

        // Lone surrogate: replace with U+FFFD + warn + pass.
        var surrogate = OcrSrtPathology.Repair([(1.0, 2.0, "甲\ud800乙")]);
        Check(surrogate.Passed && surrogate.Cues.Count == 1 && surrogate.Cues[0].Text.Contains('�'), "lone surrogate must be replaced, not dropped");
        Check(OcrSrtPathology.Repair([(1.0, 2.0, "甲\ud83d\ude00")]).Passed, "valid pair must pass");

        // Cue 200s: warn + pass (players tolerate it).
        var longCue = OcrSrtPathology.Repair([(1.0, 200.0, "甲")]);
        Check(longCue.Passed && longCue.Warnings.Count == 1, "long cue must warn, not fail");

        // Touching identical duplicate (flicker-split p02) -> WARN + still writes.
        var dup = OcrSrtPathology.Repair([
            (26.566, 27.0, "还对付不了北凉那几千人"),
            (27.0, 27.266, "还对付不了北凉那几千人"),
        ]);
        Check(dup.Passed && dup.Warnings.Count == 1, "touching identical duplicate must warn loudly, still release");

        // Near-duplicate touching pair (p02 凉/涼, similarity 0.9) -> WARN + still writes.
        var neardup = OcrSrtPathology.Repair([
            (326.567, 327.0, "还对付不了北凉那几千人"),
            (327.0, 327.267, "还对付不了北涼那几千人"),
        ]);
        Check(neardup.Passed && neardup.Warnings.Count == 1, "near-duplicate touching pair must warn once, still release");

        // Different Han characters side by side stay silent.
        var distinct = OcrSrtPathology.Repair([
            (1.0, 2.0, "你好吗"),
            (2.1, 3.0, "我很好谢谢"),
        ]);
        Check(distinct.Passed && distinct.Warnings.Count == 0, "distinct neighbors must stay silent");

        // Empty + desert gap + out-of-range -> WARN, still PASS.
        var empty = OcrSrtPathology.Repair([]);
        Check(empty.Passed && empty.Warnings.Count == 1, "empty list must warn, not fail");
        var desert = OcrSrtPathology.Repair([(1.0, 2.0, "甲"), (400.0, 401.0, "乙")]);
        Check(desert.Passed && desert.Warnings.Count == 1, "desert gap must warn, not fail");
        var odd = OcrSrtPathology.Repair([(1.0, 1.15, "甲"), (2.0, 40.0, "乙")]);
        Check(odd.Passed && odd.Warnings.Count == 2, "odd durations must warn twice, not fail");

        // The legacy coercive Validate() stays consistent with Repair().
        var compat = OcrSrtPathology.Validate([(1.0, 2.0, "甲"), (2.0, 3.0, "乙")]);
        Check(compat.Passed, "legacy Validate must agree on clean input");

        return Task.CompletedTask;
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}

// Single export/display pipeline (18/09): live once the scan finishes and the SRT file must be
// identical — both paths share one Build function. Pin the step order (exclude
// -> apply fixes -> normalize -> pathology) + every drop is counted (never thrown away
// silently) + NeedsReview survives trim/sort.
internal static class OcrExportPipelineContract
{
    public static Task RunAsync()
    {
        ExclusionsRunBeforeFixes();
        NormalizeDropsAreCounted();
        NeedsReviewSurvivesTrim();
        DeterministicTwice();
        EmptyStaysEmpty();
        SrtTimeRoundsLikeExport();
        return Task.CompletedTask;
    }

    private static OcrCue Cue(double start, double end, string text, bool review = false) =>
        new(start, end, text, 0.9, NeedsReview: review);

    private static void ExclusionsRunBeforeFixes()
    {
        // Cue A is both excluded and has a fix -> it drops at the exclude step, the fix is not
        // counted (the old export order).
        var cues = new[] { Cue(10.0, 11.0, "水印"), Cue(20.0, 21.0, "你好世界") };
        var exclusions = new[] { new OcrCueExclusion(10.0, "水印") };
        var fixes = new[] { new OcrCueFix(10.0, "水印", "水印X") };
        var built = OcrExportPipeline.Build(cues, exclusions, fixes);
        Check(built.Rows.Count == 1 && built.Rows[0].Text == "你好世界", "loại trừ phải chạy trước fix");
        Check(built.Excluded == 1, "đếm loại trừ sai");
        Check(built.Fixed == 0, "fix trên cue đã loại không được tính");
    }

    private static void NormalizeDropsAreCounted()
    {
        // "ABC" (no Han characters) + "Hello 世界" (Latin standing alone) drop in normalization;
        // export used to toss them SILENTLY (uncounted) while live still showed them.
        var cues = new[] { Cue(10.0, 11.0, "ABC"), Cue(12.0, 13.0, "Hello 世界"), Cue(14.0, 15.0, "你好世界") };
        var built = OcrExportPipeline.Build(cues, null, null);
        Check(built.Rows.Count == 1 && built.Rows[0].Text == "你好世界", "chỉ cue chuẩn giữ lại");
        Check(built.NormalizeDropped == 2, $"rớt chuẩn hóa phải đếm 2 (được {built.NormalizeDropped})");
        Check(built.NormalizeDroppedSamples.Count == 2, "phải có mẫu cue rớt");
    }

    private static void NeedsReviewSurvivesTrim()
    {
        // Overlap: A has its End trimmed to B's Start; A's review flag must survive so
        // live shows the same as SRT (SRT trims, live used to show the original end).
        var cues = new[] { Cue(10.0, 12.0, "今天天气不错", review: true), Cue(11.0, 13.0, "我们走吧") };
        var built = OcrExportPipeline.Build(cues, null, null);
        Check(built.Rows.Count == 2, "overlap trim không được xóa cue");
        Check(Math.Abs(built.Rows[0].End - 11.0) < 0.001, $"End phải cắt về 11.0 (được {built.Rows[0].End})");
        Check(built.Rows[0].NeedsReview, "cờ review phải sống qua trim");
    }

    private static void DeterministicTwice()
    {
        // Live and export call the same function; same input -> same output.
        var cues = new[] { Cue(10.0, 11.0, "你好世界"), Cue(11.5, 12.5, "我们走吧", review: true) };
        var fixes = new[] { new OcrCueFix(11.5, "我们走吧", "我们走吧！") };
        var first = OcrExportPipeline.Build(cues, null, fixes);
        var second = OcrExportPipeline.Build(cues, null, fixes);
        Check(first.Rows.Count == second.Rows.Count, "số dòng phải ổn định");
        for (var i = 0; i < first.Rows.Count; i++)
            Check(first.Rows[i] == second.Rows[i], $"dòng {i} phải giống nhau 2 lần gọi");
        Check(first.Fixed == 1 && second.Fixed == 1, "đếm fix phải ổn định");
    }

    private static void EmptyStaysEmpty()
    {
        var built = OcrExportPipeline.Build([], null, null);
        Check(built.Rows.Count == 0, "rỗng phải rỗng");
        Check(built.Excluded == 0 && built.Fixed == 0 && built.NormalizeDropped == 0, "đếm phải 0");
        Check(built.Passed, "rỗng vẫn pass (video không sub là hợp lệ)");
    }

    private static void SrtTimeRoundsLikeExport()
    {
        // Live time and SRT share one function (rounds to ms) — live used to truncate
        // (ToString fff) while SRT rounds, a 1ms drift at the edge.
        Check(OcrExportPipeline.FormatSrtTime(3661.23456) == "01:01:01,235", "phải làm tròn ms");
        Check(OcrExportPipeline.FormatSrtTime(135.533) == "00:02:15,533", "giờ cue 36 mẫu");
        Check(OcrExportPipeline.FormatSrtTime(-1) == "00:00:00,000", "giờ âm kẹp 0");
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
