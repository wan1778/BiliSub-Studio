using System.Drawing;
using System.Text;

namespace BiliSubStudio.Core.Ocr;

public static class OcrCueReconciler
{
    // Recovered full/short/full readings may leave separate touching cues with
    // the same final spelling or one transient substitution. Never bridge a
    // real blank or merge a one-way similar caption without temporal consensus.
    private const double Epsilon = 0.001;
    private const double FlickerGap = 0.067; // ~2 frames @30fps, CFR assumption - revisit for VFR

    public static IReadOnlyList<OcrCue> MergeTouchingIdentical(IEnumerable<OcrCue> cues) => MergeTouchingIdentical(cues, videoPath: null, comparer: null);
    public static IReadOnlyList<OcrCue> MergeTouchingIdentical(IEnumerable<OcrCue> cues, string? videoPath) => MergeTouchingIdentical(cues, videoPath, comparer: null);
    public static IReadOnlyList<OcrCue> MergeTouchingIdentical(IEnumerable<OcrCue> cues, string? videoPath, IPixelComparer? comparer)
    {
        var normalized = new List<OcrCue>();
        foreach (var rawCue in cues.OrderBy(cue => cue.Start))
        {
            if (ChineseSubtitleNormalizer.TryNormalize(rawCue.Text, out var text))
                normalized.Add(rawCue with { Text = text });
        }
        var output = MergeAdjacent(normalized, allowFlicker: false, videoPath, comparer);
        CollapseSandwichedSubstitutions(output);
        return MergeAdjacent(output, allowFlicker: true, videoPath, comparer);
    }

    private static bool IsIdentical(OcrCue a, OcrCue b) => DashNormalize(a.Text) == DashNormalize(b.Text);

    private static OcrCue SelectCanonical(OcrCue a, OcrCue b)
    {
        // Prefer higher confidence, tie-break by confident support then raw support then duration
        if (a.Confidence > b.Confidence + 1e-9) return a;
        if (b.Confidence > a.Confidence + 1e-9) return b;
        if (a.ConfidentSupportCount != b.ConfidentSupportCount) return a.ConfidentSupportCount > b.ConfidentSupportCount ? a : b;
        if (a.RawSupportCount != b.RawSupportCount) return a.RawSupportCount > b.RawSupportCount ? a : b;
        var durA = a.End - a.Start;
        var durB = b.End - b.Start;
        return durA >= durB ? a : b;
    }

    private static string DashNormalize(string text)
    {
        // —— / — / - variations are rendering ambiguity of the same em dash; normalize to single — before equality checks.
        // This makes 7-line ——/— flicker (112-118) collapse via identical path without duration heuristics.
        var sb = new StringBuilder(text.Length);
        bool lastWasDash = false;
        foreach (var rune in text.EnumerateRunes())
        {
            bool isDash = rune.Value is '\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2014' or '\u2015' or '-';
            if (isDash)
            {
                if (!lastWasDash) sb.Append('—');
                lastWasDash = true;
            }
            else
            {
                sb.Append(rune.ToString());
                lastWasDash = false;
            }
        }
        return sb.ToString();
    }

    private static List<OcrCue> MergeAdjacent(IEnumerable<OcrCue> cues, bool allowFlicker = false, string? videoPath = null, IPixelComparer? comparer = null)
    {
        var output = new List<OcrCue>();
        foreach (var cue in cues)
        {
            var canMergeFlicker = allowFlicker && IsShortSingleSubstitutionFlicker(output.Count > 0 ? output[^1] : null, cue);
            var isIdentical = output.Count > 0 && IsIdentical(output[^1], cue);
            var isDashIdentical = isIdentical && output.Count > 0 && !string.Equals(output[^1].Text, cue.Text, StringComparison.Ordinal);
            string? duplicateStable = null;
            var isDuplicate = output.Count > 0 && IsTransientAdjacentDuplicate(output[^1], cue, out duplicateStable);
            var allowedGap = (canMergeFlicker || isDashIdentical) ? FlickerGap : Epsilon;
            PixelCheckResult pixel = PixelCheckResult.Unavailable;
            if (canMergeFlicker && output.Count > 0)
            {
                IPixelComparer effective = comparer ?? (string.IsNullOrWhiteSpace(videoPath) ? (IPixelComparer)new AlwaysSamePixelComparer() : new ProductionPixelComparer());
                var bboxA = new System.Drawing.Rectangle(0, 0, 100, 20);
                var bboxB = new System.Drawing.Rectangle(2, 1, 100, 20);
                pixel = effective.Check(videoPath ?? "", output[^1].End, cue.Start, bboxA, bboxB);
            }
            var flickerShouldMerge = canMergeFlicker && pixel == PixelCheckResult.Same;
            var flickerNeedsReview = canMergeFlicker && pixel != PixelCheckResult.Same;
            if (flickerNeedsReview && output.Count > 0)
            {
                // FlagForReview: keep separate but mark both for UI badge / export tag
                output[^1] = output[^1] with { NeedsReview = true };
                var flaggedCue = cue with { NeedsReview = true };
                // Keep separate - do not merge, but flagged
                output.Add(flaggedCue);
                continue;
            }
            if (output.Count > 0 && cue.Start <= output[^1].End + allowedGap
                && (isIdentical
                    || isDuplicate
                    || flickerShouldMerge))
            {
                var previous = output[^1];
                var stableText = duplicateStable;
                if (stableText is null && isIdentical)
                    stableText = DashNormalize(SelectCanonical(previous, cue).Text);
                if (stableText is null && flickerShouldMerge)
                    stableText = SelectCanonical(previous, cue).Text;
                // Keep earliest Start, never take later cue's Start even if canonical is later cue
                output[^1] = previous with
                {
                    End = Math.Max(previous.End, cue.End),
                    Text = stableText ?? previous.Text,
                    Confidence = Math.Max(previous.Confidence, cue.Confidence),
                    RawSupportCount = Math.Max(previous.RawSupportCount, cue.RawSupportCount),
                    ConfidentSupportCount = Math.Max(previous.ConfidentSupportCount, cue.ConfidentSupportCount),
                };
            }
            else output.Add(cue);
        }
        return output;
    }

    private static bool IsShortSingleSubstitutionFlicker(OcrCue? left, OcrCue right)
    {
        if (left is null) return false;
        var leftDuration = left.End - left.Start;
        var rightDuration = right.End - right.Start;
        var touching = right.Start - left.End;
        if (touching < -0.001 || touching > 0.067) return false;
        var shortDuration = Math.Min(leftDuration, rightDuration);
        var longDuration = Math.Max(leftDuration, rightDuration);
        if (shortDuration > 0.45) return false;
        if (longDuration > 2.5) return false;
        if (longDuration - shortDuration < 0.20) return false;
        if (IsSingleRuneSubstitution(left.Text, right.Text)) return true;
        if (HasAdjacentDuplicateInsertion(left.Text, right.Text)) return true;
        return false;
    }

    private static bool HasAdjacentDuplicateInsertion(string a, string b)
    {
        var aRunes = a.EnumerateRunes().ToArray();
        var bRunes = b.EnumerateRunes().ToArray();
        var longer = aRunes.Length > bRunes.Length ? aRunes : bRunes;
        var shorter = aRunes.Length > bRunes.Length ? bRunes : aRunes;
        if (longer.Length != shorter.Length + 1) return false;
        for (var removed = 0; removed < longer.Length; removed++)
        {
            var adjacentDuplicate = removed > 0 && longer[removed].Value == longer[removed - 1].Value
                || removed + 1 < longer.Length && longer[removed].Value == longer[removed + 1].Value;
            if (!adjacentDuplicate) continue;
            var candidate = longer.Where((_, index) => index != removed).Select(r => r.Value).ToArray();
            if (candidate.SequenceEqual(shorter.Select(r => r.Value))) return true;
        }
        return false;
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
                && IsIdentical(left, right)
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
