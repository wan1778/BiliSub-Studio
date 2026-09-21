namespace BiliSubStudio.Core.Ocr;

// Đắp chữ sửa của AI lúc xuất SRT: câu nào khớp Start (±1ms) + chữ cũ từng chữ
// thì thay bằng chữ đúng. Chạy SAU loại trừ (câu đã xóa thì khỏi sửa), TRƯỚC
// normalize/sửa pathology — câu còn lại đi đường cũ y hệt.
// Không khớp mờ, không lan: chữ/giờ đổi thì entry cũ khớp hụt (vô hại).
// Pure, test trực tiếp. Xóa file .fix.json là hoàn tác.
public static class OcrExportFixes
{
    public const double StartToleranceSeconds = 0.001;

    public sealed record FixResult(
        IReadOnlyList<OcrCue> Cues,
        int Fixed,
        IReadOnlyList<string> FixedSamples);

    public static FixResult Apply(
        IEnumerable<OcrCue> cues,
        IReadOnlyList<OcrCueFix>? fixes)
    {
        var list = cues.ToList();
        if (list.Count == 0) return new FixResult(list, 0, []);
        if (fixes is null || fixes.Count == 0)
            return new FixResult(list, 0, []);
        var output = new List<OcrCue>(list.Count);
        var samples = new List<string>();
        var fixedCount = 0;
        // Chuẩn hóa 1 lần mỗi entry (fallback khớp hóa thạch — cùng lý do
        // OcrExportExclusions: chữ cũ thô khớp hụt chữ đã normalize).
        var fixNorms = new string?[fixes.Count];
        for (var f = 0; f < fixes.Count; f++)
        {
            var fix = fixes[f];
            fixNorms[f] = fix?.OldText is not null
                && ChineseSubtitleNormalizer.TryNormalize(fix.OldText, out var normalized)
                ? normalized : null;
        }
        foreach (var cue in list)
        {
            if (cue is null)
            {
                continue;
            }
            string? replacement = null;
            OcrCueFix? matched = null;
            var cueNorm = ChineseSubtitleNormalizer.TryNormalize(cue.Text, out var normalizedCue)
                ? normalizedCue : null;
            for (var f = 0; f < fixes.Count; f++)
            {
                var fix = fixes[f];
                if (fix is null || fix.OldText is null || fix.NewText is null) continue;
                if (fix.NewText.Length == 0) continue;
                if (Math.Abs(cue.Start - fix.Start) > StartToleranceSeconds) continue;
                if (string.Equals(cue.Text, fix.OldText, StringComparison.Ordinal)
                    || (cueNorm is not null && fixNorms[f] is not null
                        && string.Equals(cueNorm, fixNorms[f], StringComparison.Ordinal)))
                {
                    replacement = fix.NewText;
                    matched = fix;
                    break;
                }
            }
            if (replacement is null)
            {
                output.Add(cue);
            }
            else
            {
                fixedCount++;
                // Fix giữ-nguyên-chữ (gắn cờ review) thì khỏi ghi mẫu "A→A".
                if (samples.Count < 5 && !string.Equals(replacement, cue.Text, StringComparison.Ordinal))
                    samples.Add($"{cue.Start:0.000}s “{Truncate(cue.Text, 18)}”→“{Truncate(replacement, 18)}”");
                // Fix tai-sửa-chữ mang cờ review (OcrCueFix.FlagReview) để list
                // hiện ⚠️ cho người duyệt kiểm tra chữ máy sửa.
                output.Add(matched is not null && matched.FlagReview
                    ? cue with { Text = replacement, NeedsReview = true }
                    : cue with { Text = replacement });
            }
        }
        return new FixResult(output, fixedCount, samples);
    }

    private static string Truncate(string? text, int maxRunes)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var runes = text.EnumerateRunes().Take(maxRunes).ToArray();
        var kept = string.Concat(runes);
        return kept.Length < text.Length ? kept + "…" : kept;
    }
}
