namespace BiliSubStudio.Core.Ocr;

// Exclusion filter when exporting SRT: remove reviewer-approved junk cues (danmaku, on-scene
// text, watermark...) from the SRT file WITHOUT touching the checkpoint (resume/re-export stays intact).
// EXACT Start (±1ms) + Text match — no fuzzy match, no spillover: dialogue cues with the
// same timing but different text are kept; if the file's timing changes, old entries miss (harmless).
// Deleting the .exclude.json file is the undo.
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
        // Normalize each entry once (fossilized-match fallback): old entries stored raw
        // text (with space/diacritics/fullwidth) miss the cue normalized at export
        // (18/09: 8 entries including notebook 729.333 nearly became invisible). Entries that
        // cannot be normalized only match absolutely.
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
                // Fallback: same time + same text after normalization (no timing widening,
                // no fuzzy match — cues with different text remain safe).
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
