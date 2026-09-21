namespace BiliSubStudio.Core.Ocr;

// Cảnh báo đoạn có tiếng mà thiếu sub (18/09): CHỈ BÁO, KHÔNG TỰ SINH sub
// (tai không bao giờ được đẻ chữ mới — nguyên tắc user chốt). Chạy ở finish
// sau Reconcile, mỗi đoạn thoại không có cue nào chồng lấn là 1 cảnh báo để
// người duyệt kiểm tra tay. Thuần logic, fail-open: thiếu dữ liệu là im.
public static class OcrMissingSubWarning
{
    // Đoạn thoại ngắn hơn ngưỡng này thì bỏ qua (tiếng ừm/hắng giọng).
    public const double MinSpeechSeconds = 0.5;
    // Số cảnh báo tối đa mỗi video (tránh spam log khi tai điếc hàng loạt).
    public const int MaxWarnings = 20;

    public static IReadOnlyList<string> Find(
        IReadOnlyList<BiliSubStudio.Core.Editor.EditorSpeechSegment>? speech,
        IEnumerable<OcrCue>? cues,
        double durationSeconds)
    {
        var warnings = new List<string>();
        if (speech is null || speech.Count == 0) return warnings;
        if (!(durationSeconds > 0)) return warnings;
        var cueSpans = (cues ?? Enumerable.Empty<OcrCue>())
            .Where(cue => cue is not null && cue.End > cue.Start
                && double.IsFinite(cue.Start) && double.IsFinite(cue.End))
            .Select(cue => (cue.Start, cue.End))
            .OrderBy(span => span.Start)
            .ToArray();
        foreach (var segment in speech)
        {
            if (warnings.Count >= MaxWarnings) break;
            if (segment is null || string.IsNullOrWhiteSpace(segment.Text)) continue;
            if (!double.IsFinite(segment.Start) || !double.IsFinite(segment.End)) continue;
            var from = Math.Max(0, segment.Start - OcrSpeechMatch.OverlapPadSeconds);
            var to = Math.Min(durationSeconds, segment.End + OcrSpeechMatch.OverlapPadSeconds);
            if (!(to - from >= MinSpeechSeconds)) continue;
            var covered = false;
            foreach (var (cueStart, cueEnd) in cueSpans)
            {
                if (cueStart > to) break;
                if (cueEnd >= from) { covered = true; break; }
            }
            if (!covered)
                warnings.Add(FormattableString.Invariant(
                    $"đoạn {from:0.0}s–{to:0.0}s có tiếng mà thiếu sub (“{Truncate(segment.Text, 20)}”) — kiểm tra tay"));
        }
        return warnings;
    }

    private static string Truncate(string? text, int maxRunes)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var runes = text.EnumerateRunes().Take(maxRunes).ToArray();
        var kept = string.Concat(runes);
        return kept.Length < text.Length ? kept + "…" : kept;
    }
}
