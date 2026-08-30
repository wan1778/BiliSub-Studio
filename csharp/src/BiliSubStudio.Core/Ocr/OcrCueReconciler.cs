using System.Text;

namespace BiliSubStudio.Core.Ocr;

public static class OcrCueReconciler
{
    // Recovered full/short/full readings may leave separate touching cues with
    // the same final spelling. Never bridge a real blank or merge similar words.
    public static IReadOnlyList<OcrCue> MergeTouchingIdentical(IEnumerable<OcrCue> cues)
    {
        var output = new List<OcrCue>();
        foreach (var rawCue in cues.OrderBy(cue => cue.Start))
        {
            if (!ChineseSubtitleNormalizer.TryNormalize(rawCue.Text, out var normalized)) continue;
            var cue = rawCue with { Text = normalized };
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
