namespace BiliSubStudio.Core.Ocr;

// Filter badge on the live table: only shows "🗑 xóa" for cues removed from the SRT
// (so you can see the filter worked; kept cues show nothing to keep the table clean).
// Pure: cue + verdict matching Start (±1ms) + Text -> row suffix. No verdict /
// keep / unsure -> empty.
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
            // Match ±1ms like the journal: normalize the key to milliseconds for O(1) lookup.
        // Duplicate entries (re-judged) -> the later one wins.
        map[Key(verdict.CueStart, verdict.CueText)] = verdict;
        }
        return map;
    }
}
