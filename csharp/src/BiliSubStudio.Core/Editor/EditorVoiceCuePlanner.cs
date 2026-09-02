using System.Text;

namespace BiliSubStudio.Core.Editor;

internal static class EditorVoiceCuePlanner
{
    private const double TouchToleranceSeconds = .05;
    // A stable caption can remain on screen for several seconds before one bad
    // OCR frame flashes at its tail. Cue count and text similarity are the real
    // safety bounds; eight seconds still prevents unrelated scene-wide merges.
    private const double FlickerMaximumSeconds = 8;
    private const double FlickerCueMaximumSeconds = .25;
    private const int FlickerMaximumCues = 8;

    public static IReadOnlyList<EditorSubtitleCue> Build(IReadOnlyList<EditorSubtitleCue> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Count < 2) return source.ToArray();
        var result = new List<EditorSubtitleCue>(source.Count);
        for (var first = 0; first < source.Count;)
        {
            var last = first;
            var maximumEnd = source[first].End;
            while (last + 1 < source.Count && last - first + 1 < FlickerMaximumCues)
            {
                var candidate = source[last + 1];
                if (candidate.Start > maximumEnd + TouchToleranceSeconds) break;
                maximumEnd = Math.Max(maximumEnd, candidate.End);
                if (maximumEnd - source[first].Start > FlickerMaximumSeconds) break;
                last++;
            }

            var collapseEnd = FindFlickerEnd(source, first, last);
            if (collapseEnd <= first)
            {
                result.Add(source[first]);
                first++;
                continue;
            }

            var cluster = source.Skip(first).Take(collapseEnd - first + 1).ToArray();
            var representative = cluster
                .OrderByDescending(cue => cue.End - cue.Start)
                .ThenBy(cue => Normalize(cue.VietnameseText).Length)
                .ThenBy(cue => cue.Start)
                .First();
            result.Add(representative with
            {
                Start = cluster.Min(cue => cue.Start),
                End = cluster.Max(cue => cue.End),
            });
            first = collapseEnd + 1;
        }
        return result;
    }

    public static IReadOnlyList<EditorSubtitleCue> RemoveUnspeakable(IReadOnlyList<EditorSubtitleCue> cues)
    {
        ArgumentNullException.ThrowIfNull(cues);
        return cues.Where(cue => VietnameseTtsTextNormalizer.HasSpeakableUnits(cue.VietnameseText)).ToArray();
    }

    private static int FindFlickerEnd(IReadOnlyList<EditorSubtitleCue> cues, int first, int last)
    {
        if (last <= first) return first;
        for (var right = last; right > first; right--)
        {
            if (cues.Skip(first).Take(right - first + 1).All(cue => cue.End - cue.Start > FlickerCueMaximumSeconds))
                continue;
            for (var index = first; index <= right; index++)
            {
                for (var previous = Math.Max(first, index - 2); previous < index; previous++)
                    if (LooksLikeSameCaption(cues[previous].VietnameseText, cues[index].VietnameseText))
                        return right;
            }
        }
        return first;
    }

    private static bool LooksLikeSameCaption(string left, string right)
    {
        var first = Normalize(left);
        var second = Normalize(right);
        if (first.Length < 2 || second.Length < 2) return false;
        if (string.Equals(first, second, StringComparison.Ordinal)) return true;
        var shorter = first.Length <= second.Length ? first : second;
        var longer = first.Length <= second.Length ? second : first;
        if (shorter.Length >= 6 && shorter.Length * 100 >= longer.Length * 65
            && longer.Contains(shorter, StringComparison.Ordinal))
            return true;

        var firstTokens = Tokens(left);
        var secondTokens = Tokens(right);
        var minimum = Math.Min(firstTokens.Count, secondTokens.Count);
        return minimum >= 3 && firstTokens.Intersect(secondTokens).Count() * 100 >= minimum * 60;
    }

    private static HashSet<string> Tokens(string value) => value
        .Normalize(NormalizationForm.FormKC)
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(Normalize)
        .Where(token => token.Length > 0)
        .ToHashSet(StringComparer.Ordinal);

    private static string Normalize(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (var character in value.Normalize(NormalizationForm.FormKC))
            if (char.IsLetterOrDigit(character)) result.Append(char.ToLowerInvariant(character));
        return result.ToString();
    }
}
