namespace BiliSubStudio.Core.Ocr;

// Apply AI text corrections when exporting SRT: any cue matching Start (±1ms) + old text
// exactly gets replaced with the corrected text. Runs AFTER exclusion (deleted cues are not
// corrected) and BEFORE normalize/pathology repair — remaining cues take the old path unchanged.
// No fuzzy match, no spillover: if text/timing changes, the old entry misses (harmless).
// Pure, directly tested. Deleting the .fix.json file is the undo.
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
        // Normalize each entry once (fossilized-match fallback — same reason as
        // OcrExportExclusions: raw old text misses the already-normalized cue).
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
                // Text-preserving fixes (flagged for review) are not written as "A→A" samples.
                if (samples.Count < 5 && !string.Equals(replacement, cue.Text, StringComparison.Ordinal))
                    samples.Add($"{cue.Start:0.000}s “{Truncate(cue.Text, 18)}”→“{Truncate(replacement, 18)}”");
                // Ear-driven corrections carry a review flag (OcrCueFix.FlagReview) so the list
                // shows ⚠️ for the reviewer to check the machine-corrected text.
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
