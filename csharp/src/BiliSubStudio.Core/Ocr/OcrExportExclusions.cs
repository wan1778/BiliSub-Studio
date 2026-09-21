namespace BiliSubStudio.Core.Ocr;

// Lọc loại trừ lúc xuất SRT: bỏ câu rác đã duyệt (danmaku, chữ cảnh, watermark...)
// khỏi file SRT mà KHÔNG đụng checkpoint (resume/export lại nguyên vẹn).
// Khớp CHÍNH XÁC Start (±1ms) + Text từng chữ — không khớp mờ, không lan:
// câu thoại trùng giờ khác chữ thì giữ; file đổi timing thì entry cũ khớp hụt
// (vô hại). Xóa file .exclude.json là hoàn tác.
public static class OcrExportExclusions
{
    public const double StartToleranceSeconds = 0.001;

    public sealed record ExclusionResult(
        IReadOnlyList<OcrCue> Kept,
        int Dropped,
        IReadOnlyList<string> DroppedSamples);

    public static ExclusionResult Apply(
        IEnumerable<OcrCue> cues,
        IReadOnlyList<OcrCueExclusion>? exclusions)
    {
        var list = cues.ToList();
        if (list.Count == 0) return new ExclusionResult(list, 0, []);
        if (exclusions is null || exclusions.Count == 0)
            return new ExclusionResult(list, 0, []);
        // Chuẩn hóa 1 lần mỗi entry (fallback khớp hóa thạch): entry cũ ghi chữ
        // thô (kèm space/dấu/fullwidth) khớp hụt cue đã normalize ở export
        // (18/09: 8 entry gồm notebook 729.333 suýt vô hình). Entry không chuẩn
        // hóa được thì chỉ khớp tuyệt đối.
        var entryNorms = new string?[exclusions.Count];
        for (var e = 0; e < exclusions.Count; e++)
        {
            var entry = exclusions[e];
            entryNorms[e] = entry?.Text is not null
                && ChineseSubtitleNormalizer.TryNormalize(entry.Text, out var normalized)
                ? normalized : null;
        }
        var kept = new List<OcrCue>(list.Count);
        var samples = new List<string>();
        var dropped = 0;
        foreach (var cue in list)
        {
            if (cue is null)
            {
                continue;
            }
            var hit = false;
            var cueNorm = ChineseSubtitleNormalizer.TryNormalize(cue.Text, out var normalizedCue)
                ? normalizedCue : null;
            foreach (var index in Enumerable.Range(0, exclusions.Count))
            {
                var entry = exclusions[index];
                if (entry is null || entry.Text is null) continue;
                if (Math.Abs(cue.Start - entry.Start) > StartToleranceSeconds) continue;
                if (string.Equals(cue.Text, entry.Text, StringComparison.Ordinal)) { hit = true; break; }
                // Fallback: cùng giờ + cùng chữ sau chuẩn hóa (không nới giờ,
                // không khớp mờ — câu khác chữ vẫn an toàn).
                if (cueNorm is not null && entryNorms[index] is not null
                    && string.Equals(cueNorm, entryNorms[index], StringComparison.Ordinal))
                {
                    hit = true;
                    break;
                }
            }
            if (hit)
            {
                dropped++;
                if (samples.Count < 5)
                    samples.Add($"{cue.Start:0.000}s “{Truncate(cue.Text, 24)}”");
            }
            else
            {
                kept.Add(cue);
            }
        }
        return new ExclusionResult(kept, dropped, samples);
    }

    private static string Truncate(string? text, int maxRunes)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var runes = text.EnumerateRunes().Take(maxRunes).ToArray();
        var kept = string.Concat(runes);
        return kept.Length < text.Length ? kept + "…" : kept;
    }
}
