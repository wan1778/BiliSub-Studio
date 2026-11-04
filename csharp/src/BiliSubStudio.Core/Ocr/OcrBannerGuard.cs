namespace BiliSubStudio.Core.Ocr;

// Guard for static background text (banner/watermark/logo with text), running at FINISH after
// Reconcile (18/09, plan lock): fix #1 removed drop-on-silence, so static background text standing
// still through a silent stretch would slip through (drift never catches it, the ear is silent). This guard fills
// exactly that hole — and only inspects cues the eye kept during silence:
// - Any cue the ear ever matched (MaxSimilarity >= 0.3 on any instance in
//   the group) is EXEMPT FOREVER — long dialogue, captions read aloud, chorus... all
//   escape. Interacts with every ear-eye fix = 0 by structure.
// - Groups by FUZZY match (>= 0.85, reusing TextSimilarity): a flickering watermark read by OCR as
//   2-3 variants still merges together, it cannot dodge.
// - TIERED thresholds by video length (patches short videos): video >= 60s needs a share
//   >= 50% AND cumulative >= 20s; video < 60s needs a share >= 85%; any length: 1
//   continuous run >= 45s is a banner (no dialogue stays unchanged for 45s).
// Pure logic, fail-open: missing data means keep everything. Drops record a reasoned, numbered cause so
// the reviewer can see it (not deleted silently). Written to .exclude.json like a judge drop,
// so the final list = the SRT via exactly the old path.
public static class OcrBannerGuard
{
    // Only long videos consider a 50% share (short videos at 50% easily catch a real caption).
    public const double LongVideoSeconds = 60;
    public const double ShareThresholdLong = 0.5;
    public const double MinCumulativeSecondsLong = 20;
    // Short video: only a banner covering almost the whole thing gets caught.
    public const double ShareThresholdShort = 0.85;
    // A continuous run this long cannot be dialogue.
    public const double MaxContinuousSeconds = 45;
    // Groups OCR variants of the same background text (one character missing/extra).
    public const double GroupSimilarity = 0.85;
    // Guard exemption: the ear once heard something close on any instance (SAFETY-oriented —
    // exempt only means this guard skips it, the cue still goes through every other filter).
    public const double EarExemptSimilarity = 0.3;
    // Joins display runs: a watermark flickering off for 0.2s still counts as one run.
    public const double RunJoinGapSeconds = 1.0;

    public sealed record BannerDrop(double Start, string Text, string Reason);

    public static IReadOnlyList<BannerDrop> Evaluate(
        IEnumerable<OcrCue> cues,
        IReadOnlyList<BiliSubStudio.Core.Editor.EditorSpeechSegment>? speech,
        double durationSeconds)
    {
        var list = cues
            .Where(cue => cue is not null && cue.Text is not null
                && cue.End > cue.Start
                && double.IsFinite(cue.Start) && double.IsFinite(cue.End))
            .OrderBy(cue => cue.Start)
            .ToArray();
        if (list.Length == 0 || !(durationSeconds > 0) || !double.IsFinite(durationSeconds))
            return [];
        // Fuzzy grouping by normalized text (union-find, threshold 0.85).
        var parent = new int[list.Length];
        for (var index = 0; index < parent.Length; index++) parent[index] = index;
        int Find(int value)
        {
            while (parent[value] != value)
            {
                parent[value] = parent[parent[value]];
                value = parent[value];
            }
            return value;
        }
        void Union(int left, int right)
        {
            left = Find(left);
            right = Find(right);
            if (left != right) parent[left] = right;
        }
        for (var a = 0; a < list.Length; a++)
        {
            var textA = list[a].Text;
            if (string.IsNullOrWhiteSpace(textA)) continue;
            for (var b = a + 1; b < list.Length; b++)
            {
                var textB = list[b].Text;
                if (string.IsNullOrWhiteSpace(textB)) continue;
                if (Find(a) == Find(b)) continue;
                if (OcrTextCleanup.TextSimilarity(textA, textB) >= GroupSimilarity)
                    Union(a, b);
            }
        }
        var groups = new Dictionary<int, List<OcrCue>>();
        for (var index = 0; index < list.Length; index++)
        {
            var root = Find(index);
            if (!groups.TryGetValue(root, out var members))
            {
                members = new List<OcrCue>();
                groups[root] = members;
            }
            members.Add(list[index]);
        }
        var drops = new List<BannerDrop>();
        foreach (var members in groups.Values)
        {
            // Permanently exempt: the ear once matched something close on any instance.
            if (EarHeard(speech, members)) continue;
            var (cumulative, maxRun) = Measure(members);
            var share = cumulative / durationSeconds;
            string? rule = null;
            if (maxRun >= MaxContinuousSeconds)
                rule = FormattableString.Invariant(
                    $"1 mạch liên tục {maxRun:0}s (≥ {MaxContinuousSeconds:0}s)");
            else if (durationSeconds >= LongVideoSeconds)
            {
                if (share >= ShareThresholdLong && cumulative >= MinCumulativeSecondsLong)
                    rule = FormattableString.Invariant(
                        $"chiếm {share:P0} video ({cumulative:0}s cộng dồn)");
            }
            else if (share >= ShareThresholdShort)
            {
                rule = FormattableString.Invariant(
                    $"chiếm {share:P0} video ngắn ({cumulative:0}s cộng dồn)");
            }
            if (rule is null) continue;
            var sample = members
                .OrderByDescending(cue => OcrTemporalFilter.Normalize(cue.Text).Length)
                .First().Text;
            foreach (var cue in members)
                drops.Add(new BannerDrop(cue.Start, cue.Text,
                    $"chữ nền tĩnh “{Truncate(sample, 20)}” ({rule}), không lần nào khớp tiếng"));
        }
        return drops;
    }

    // Did the ear ever hear something close for this group? (any cue in the group matching >= 0.3
    // with the speech segment overlapping it). No speech = no exemption.
    internal static bool EarHeard(
        IReadOnlyList<BiliSubStudio.Core.Editor.EditorSpeechSegment>? speech,
        IReadOnlyList<OcrCue> members)
    {
        if (speech is null || speech.Count == 0) return false;
        foreach (var cue in members)
        {
            if (cue?.Text is null) continue;
            var overlapping = OcrSpeechMatch.OverlappingSegments(speech, cue.Start, cue.End);
            if (overlapping.Count == 0) continue;
            if (OcrSpeechMatch.MaxSimilarity(cue.Text, overlapping) >= EarExemptSimilarity)
                return true;
        }
        return false;
    }

    // Cumulative (union of overlapping spans) + longest run (joining gaps <= 1s to catch a
    // flickering watermark).
    internal static (double Cumulative, double MaxRun) Measure(IReadOnlyList<OcrCue> members)
    {
        var spans = members
            .Where(cue => cue is not null && cue.End > cue.Start)
            .Select(cue => (cue.Start, cue.End))
            .OrderBy(span => span.Start)
            .ToArray();
        if (spans.Length == 0) return (0, 0);
        var cumulative = 0.0;
        var maxRun = 0.0;
        var runStart = spans[0].Start;
        var runEnd = spans[0].End;
        var unionStart = spans[0].Start;
        var unionEnd = spans[0].End;
        for (var index = 1; index < spans.Length; index++)
        {
            var (start, end) = spans[index];
            if (start <= unionEnd)
            {
                if (end > unionEnd) unionEnd = end;
            }
            else
            {
                cumulative += unionEnd - unionStart;
                unionStart = start;
                unionEnd = end;
            }
            if (start <= runEnd + RunJoinGapSeconds)
            {
                if (end > runEnd) runEnd = end;
            }
            else
            {
                if (runEnd - runStart > maxRun) maxRun = runEnd - runStart;
                runStart = start;
                runEnd = end;
            }
        }
        cumulative += unionEnd - unionStart;
        if (runEnd - runStart > maxRun) maxRun = runEnd - runStart;
        return (cumulative, maxRun);
    }

    private static string Truncate(string? text, int maxRunes)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var runes = text.EnumerateRunes().Take(maxRunes).ToArray();
        var kept = string.Concat(runes);
        return kept.Length < text.Length ? kept + "…" : kept;
    }
}
