using System.Text;

namespace BiliSubStudio.Core.Ocr;

public static class OcrCueReconciler
{
    // Recovered full/short/full readings may leave separate touching cues with
    // the same final spelling or one transient substitution. Never bridge a
    // real blank or merge a one-way similar caption without temporal consensus.
    public static IReadOnlyList<OcrCue> MergeTouchingIdentical(IEnumerable<OcrCue> cues)
    {
        var normalized = new List<OcrCue>();
        foreach (var rawCue in cues.OrderBy(cue => cue.Start))
        {
            if (ChineseSubtitleNormalizer.TryNormalize(rawCue.Text, out var text))
                normalized.Add(rawCue with { Text = text });
        }
        var output = MergeAdjacent(normalized);
        CollapseSandwichedSubstitutions(output);
        return MergeAdjacent(output);
    }

    private static List<OcrCue> MergeAdjacent(IEnumerable<OcrCue> cues)
    {
        var output = new List<OcrCue>();
        foreach (var cue in cues)
        {
            if (output.Count > 0 && cue.Start <= output[^1].End + .001
                && (string.Equals(output[^1].Text, cue.Text, StringComparison.Ordinal)
                    || IsTransientAdjacentDuplicate(output[^1], cue, out _)))
            {
                var previous = output[^1];
                _ = IsTransientAdjacentDuplicate(previous, cue, out var stableText);
                output[^1] = previous with
                {
                    End = Math.Max(previous.End, cue.End),
                    Text = stableText ?? previous.Text,
                    Confidence = Math.Max(previous.Confidence, cue.Confidence),
                };
            }
            else output.Add(cue);
        }
        return output;
    }

    private static void CollapseSandwichedSubstitutions(List<OcrCue> cues)
    {
        // A/B/A over consecutive frames is strong temporal evidence that B is a
        // transient one-glyph OCR substitution. Preserve the spelling actually
        // observed on both sides and merge timing; do not fuzzy-merge A/B alone.
        for (var index = 1; index + 1 < cues.Count;)
        {
            var left = cues[index - 1];
            var middle = cues[index];
            var right = cues[index + 1];
            var touching = middle.Start <= left.End + .067 && right.Start <= middle.End + .067;
            var bounded = right.End - left.Start <= 2.0 && middle.End - middle.Start <= 1.25;
            var edgeFlicker = Math.Min(left.End - left.Start, right.End - right.Start) <= .35;
            if (touching && bounded && edgeFlicker
                && string.Equals(left.Text, right.Text, StringComparison.Ordinal)
                && IsSingleRuneSubstitution(left.Text, middle.Text))
            {
                cues[index - 1] = left with
                {
                    End = Math.Max(left.End, right.End),
                    Confidence = Math.Max(left.Confidence, right.Confidence),
                };
                cues.RemoveRange(index, 2);
                index = Math.Max(1, index - 1);
                continue;
            }
            index++;
        }
    }

    private static bool IsSingleRuneSubstitution(string stable, string variant)
    {
        var stableRunes = stable.EnumerateRunes().ToArray();
        var variantRunes = variant.EnumerateRunes().ToArray();
        return stableRunes.Length == variantRunes.Length
            && stableRunes.Zip(variantRunes).Count(pair => pair.First != pair.Second) == 1;
    }

    private static bool IsTransientAdjacentDuplicate(OcrCue left, OcrCue right, out string? stableText)
    {
        stableText = null;
        var leftRunes = left.Text.EnumerateRunes().ToArray();
        var rightRunes = right.Text.EnumerateRunes().ToArray();
        var longer = leftRunes.Length > rightRunes.Length ? leftRunes : rightRunes;
        var shorter = leftRunes.Length > rightRunes.Length ? rightRunes : leftRunes;
        if (longer.Length != shorter.Length + 1) return false;

        var longerCue = leftRunes.Length > rightRunes.Length ? left : right;
        var shorterCue = leftRunes.Length > rightRunes.Length ? right : left;
        if (longerCue.End - longerCue.Start > .20 || shorterCue.End - shorterCue.Start < .25) return false;

        for (var removed = 0; removed < longer.Length; removed++)
        {
            var adjacentDuplicate = removed > 0 && longer[removed] == longer[removed - 1]
                || removed + 1 < longer.Length && longer[removed] == longer[removed + 1];
            if (!adjacentDuplicate) continue;
            var candidate = longer.Where((_, index) => index != removed).ToArray();
            if (!candidate.SequenceEqual(shorter)) continue;
            stableText = shorterCue.Text;
            return true;
        }
        return false;
    }
}
