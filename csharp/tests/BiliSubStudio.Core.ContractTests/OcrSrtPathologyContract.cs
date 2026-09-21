using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

// E.6 SRT PATHOLOGY GATE REPAIR-FIRST (OCR Gates v1.3, sua 2026-09-14):
// sua duoc thi sua + bao (trim/drop/sort), chi dung khi khong con gi cuu.
// Bai hoc vu cue 2703: chan file vi overlap 0.1s sua duoc la cuop file user.
internal static class OcrSrtPathologyContract
{
    public static Task RunAsync()
    {
        // Sach: pass im lang, giu nguyen.
        var clean = OcrSrtPathology.Repair([
            (1.0, 2.5, "你好"),
            (3.0, 4.0, "再见"),
            (5.0, 6.5, "谢谢"),
        ]);
        Check(clean.Passed && clean.Failures.Count == 0 && clean.Warnings.Count == 0, "clean cues must pass silent");
        Check(clean.Cues.Count == 3, "clean cues must survive untouched");

        // Cham nhau: pass.
        Check(OcrSrtPathology.Repair([(1.0, 2.0, "甲"), (2.0, 3.0, "乙")]).Passed, "touching cues must pass");

        // Overlap 0.5s (vu cue 2703: 0.1s): TRIM prev.End = next.Start + warn, VAN GHI.
        var overlap = OcrSrtPathology.Repair([(6996.6, 6997.5, "甲"), (6997.4, 6998.0, "乙")]);
        Check(overlap.Passed, "trimmable overlap must still release, never block the file");
        Check(overlap.Cues.Count == 2 && overlap.Cues[0].End == 6997.4, "overlap must trim prev.End to next.Start");
        Check(overlap.Warnings.Count > 0, "trim must warn");

        // Di lui: sort + warn + pass (player thich sort san).
        var backward = OcrSrtPathology.Repair([(3.0, 4.0, "甲"), (1.0, 2.0, "乙")]);
        Check(backward.Passed && backward.Cues[0].Start == 1.0 && backward.Cues[1].Start == 3.0, "backward input must sort, not fail");

        // Rac (End<=Start, text rong, manh vun): drop tung cue + liet ke; het sach -> FAIL.
        Check(!OcrSrtPathology.Repair([(1.0, 1.0, "甲")]).Passed, "sole zero-duration cue must fail (nothing salvageable)");
        Check(!OcrSrtPathology.Repair([(1.0, 2.0, "   ")]).Passed, "sole blank cue must fail (nothing salvageable)");
        Check(!OcrSrtPathology.Repair([(1.0, 1.05, "甲")]).Passed, "sole fragment must fail (nothing salvageable)");
        var mixed = OcrSrtPathology.Repair([(1.0, 2.0, "甲"), (3.0, 3.0, "x"), (4.0, 5.0, "乙")]);
        Check(mixed.Passed && mixed.Cues.Count == 2, "bad cue among good must drop only itself");
        Check(mixed.Warnings.Count > 0, "drop must warn with the listed cue");

        // Surrogate le: thay U+FFFD + warn + pass.
        var surrogate = OcrSrtPathology.Repair([(1.0, 2.0, "甲\ud800乙")]);
        Check(surrogate.Passed && surrogate.Cues.Count == 1 && surrogate.Cues[0].Text.Contains('�'), "lone surrogate must be replaced, not dropped");
        Check(OcrSrtPathology.Repair([(1.0, 2.0, "甲\ud83d\ude00")]).Passed, "valid pair must pass");

        // Cue 200s: warn + pass (player chiu duoc).
        var longCue = OcrSrtPathology.Repair([(1.0, 200.0, "甲")]);
        Check(longCue.Passed && longCue.Warnings.Count == 1, "long cue must warn, not fail");

        // Dup ke giong het cham nhau (flicker-split p02) -> WARN + van ghi.
        var dup = OcrSrtPathology.Repair([
            (26.566, 27.0, "还对付不了北凉那几千人"),
            (27.0, 27.266, "还对付不了北凉那几千人"),
        ]);
        Check(dup.Passed && dup.Warnings.Count == 1, "touching identical duplicate must warn loudly, still release");

        // Gan-dup ke (p02 凉/涼, sim 0.9) -> WARN + van ghi.
        var neardup = OcrSrtPathology.Repair([
            (326.567, 327.0, "还对付不了北凉那几千人"),
            (327.0, 327.267, "还对付不了北涼那几千人"),
        ]);
        Check(neardup.Passed && neardup.Warnings.Count == 1, "near-duplicate touching pair must warn once, still release");

        // Khac han ke nhau thi im lang.
        var distinct = OcrSrtPathology.Repair([
            (1.0, 2.0, "你好吗"),
            (2.1, 3.0, "我很好谢谢"),
        ]);
        Check(distinct.Passed && distinct.Warnings.Count == 0, "distinct neighbors must stay silent");

        // Rong + sa mac + ngoai bien -> WARN, van PASS.
        var empty = OcrSrtPathology.Repair([]);
        Check(empty.Passed && empty.Warnings.Count == 1, "empty list must warn, not fail");
        var desert = OcrSrtPathology.Repair([(1.0, 2.0, "甲"), (400.0, 401.0, "乙")]);
        Check(desert.Passed && desert.Warnings.Count == 1, "desert gap must warn, not fail");
        var odd = OcrSrtPathology.Repair([(1.0, 1.15, "甲"), (2.0, 40.0, "乙")]);
        Check(odd.Passed && odd.Warnings.Count == 2, "odd durations must warn twice, not fail");

        // Validate() cuong che cu van nhat quan voi Repair().
        var compat = OcrSrtPathology.Validate([(1.0, 2.0, "甲"), (2.0, 3.0, "乙")]);
        Check(compat.Passed, "legacy Validate must agree on clean input");

        return Task.CompletedTask;
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}

// Pipeline xuất/hiển thị DUY NHẤT (18/09): live lúc quét xong và file SRT phải
// giống y đúc — cùng 1 hàm Build cho cả 2 đường. Ghim thứ tự bước (loại trừ
// -> đắp fix -> normalize -> pathology) + mọi cú rớt đều được đếm (không vứt
// câm) + NeedsReview sống qua trim/sort.
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
        // Cue A vừa bị loại trừ vừa có fix -> rớt ở bước loại trừ, fix không
        // được tính (đúng thứ tự export cũ).
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
        // "ABC" (không chữ Hán) + "Hello 世界" (Latin đứng riêng) rớt chuẩn
        // hóa; trước đây export vứt CÂM (không đếm) còn live vẫn hiện.
        var cues = new[] { Cue(10.0, 11.0, "ABC"), Cue(12.0, 13.0, "Hello 世界"), Cue(14.0, 15.0, "你好世界") };
        var built = OcrExportPipeline.Build(cues, null, null);
        Check(built.Rows.Count == 1 && built.Rows[0].Text == "你好世界", "chỉ cue chuẩn giữ lại");
        Check(built.NormalizeDropped == 2, $"rớt chuẩn hóa phải đếm 2 (được {built.NormalizeDropped})");
        Check(built.NormalizeDroppedSamples.Count == 2, "phải có mẫu cue rớt");
    }

    private static void NeedsReviewSurvivesTrim()
    {
        // Overlap: A bị cắt End về Start của B; cờ review của A phải còn để
        // live hiện đúng như SRT (SRT cắt, live trước đây hiện giờ gốc).
        var cues = new[] { Cue(10.0, 12.0, "今天天气不错", review: true), Cue(11.0, 13.0, "我们走吧") };
        var built = OcrExportPipeline.Build(cues, null, null);
        Check(built.Rows.Count == 2, "overlap trim không được xóa cue");
        Check(Math.Abs(built.Rows[0].End - 11.0) < 0.001, $"End phải cắt về 11.0 (được {built.Rows[0].End})");
        Check(built.Rows[0].NeedsReview, "cờ review phải sống qua trim");
    }

    private static void DeterministicTwice()
    {
        // Live và export gọi cùng hàm, cùng input -> cùng output.
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
        // Giờ live và SRT dùng chung 1 hàm (làm tròn ms) — trước đây live cắt
        // ngọn (ToString fff) còn SRT làm tròn, lệch 1ms ở biên.
        Check(OcrExportPipeline.FormatSrtTime(3661.23456) == "01:01:01,235", "phải làm tròn ms");
        Check(OcrExportPipeline.FormatSrtTime(135.533) == "00:02:15,533", "giờ cue 36 mẫu");
        Check(OcrExportPipeline.FormatSrtTime(-1) == "00:00:00,000", "giờ âm kẹp 0");
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
