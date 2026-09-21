namespace BiliSubStudio.Core.Ocr;

// Dấu lọc trên bảng live: CHỈ hiện "🗑 xóa" cho câu bị loại khỏi SRT
// (để biết bộ lọc đã làm việc; câu giữ hiện nothing cho sạch bảng).
// Pure: cue + verdict khớp Start (±1ms) + Text -> hậu tố hàng. Không verdict /
// keep / unsure -> rỗng.
public static class OcrJudgeBadge
{
    public const double StartToleranceSeconds = 0.001;

    public static string Key(double start, string? text) =>
        FormattableString.Invariant($"{start:0.000}|{text}");

    public static string For(OcrCue? cue, IReadOnlyDictionary<string, OcrCueVerdict>? marks)
    {
        if (cue is null || marks is null || marks.Count == 0) return string.Empty;
        if (!marks.TryGetValue(Key(cue.Start, cue.Text), out var verdict) || verdict is null)
            return string.Empty;
        return verdict.Decision == "drop" ? "  🗑 xóa" : string.Empty;
    }

    public static Dictionary<string, OcrCueVerdict> BuildMap(IEnumerable<OcrCueVerdict>? verdicts)
    {
        var map = new Dictionary<string, OcrCueVerdict>(StringComparer.Ordinal);
        if (verdicts is null) return map;
        foreach (var verdict in verdicts)
        {
            if (verdict is null || verdict.CueText is null) continue;
            if (!double.IsFinite(verdict.CueStart) || verdict.CueStart < 0) continue;
            // Khớp ±1ms như journal: chuẩn hóa key về mili-giây để tra O(1).
        // Entry trùng (chấm lại) thì bản sau thắng.
        map[Key(verdict.CueStart, verdict.CueText)] = verdict;
        }
        return map;
    }
}
