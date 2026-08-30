using System.Text;

namespace BiliSubStudio.Core.Ocr;

public sealed class OcrLiveCueAccumulator
{
    private readonly SortedDictionary<long, OcrCue> _byStart = [];

    public IReadOnlyList<OcrCue> Cues => _byStart.Values.ToArray();

    public void Clear() => _byStart.Clear();

    public bool Merge(IReadOnlyList<OcrCue> incoming)
    {
        var before = _byStart.Values.ToArray();
        foreach (var rawCue in incoming)
        {
            if (!ChineseSubtitleNormalizer.TryNormalize(rawCue.Text, out var text)) continue;
            var cue = rawCue with { Text = text };
            var key = StartKey(cue.Start);
            _byStart[key] = _byStart.TryGetValue(key, out var existing)
                ? MergeSameStart(existing, cue)
                : cue;
        }

        // A later job snapshot can replace several provisional active cues with
        // one reconciled committed cue. Rebuild the keyed state itself so those
        // superseded starts cannot remain as ghost rows in the next UI refresh.
        var reconciled = OcrCueReconciler.MergeTouchingIdentical(_byStart.Values);
        _byStart.Clear();
        foreach (var cue in reconciled) _byStart[StartKey(cue.Start)] = cue;

        return !before.SequenceEqual(_byStart.Values);
    }

    private static OcrCue MergeSameStart(OcrCue current, OcrCue candidate)
    {
        var preferredText = PreferText(current, candidate);
        // The incoming cue is the newer timing snapshot. Keep the best observed
        // spelling, but never retain an obsolete active end time.
        return candidate with
        {
            Text = preferredText,
            Confidence = Math.Max(current.Confidence, candidate.Confidence),
        };
    }

    private static string PreferText(OcrCue current, OcrCue candidate)
    {
        if (IsSingleAdjacentDuplicateInsertion(current.Text, candidate.Text)) return current.Text;
        if (IsSingleAdjacentDuplicateInsertion(candidate.Text, current.Text)) return candidate.Text;
        var currentRunes = current.Text.EnumerateRunes().Count();
        var candidateRunes = candidate.Text.EnumerateRunes().Count();
        if (candidateRunes != currentRunes) return candidateRunes > currentRunes ? candidate.Text : current.Text;
        if (Math.Abs(candidate.Confidence - current.Confidence) > .000001)
            return candidate.Confidence > current.Confidence ? candidate.Text : current.Text;
        return candidate.Text;
    }

    private static bool IsSingleAdjacentDuplicateInsertion(string shorter, string longer)
    {
        var shorterRunes = shorter.EnumerateRunes().ToArray();
        var longerRunes = longer.EnumerateRunes().ToArray();
        if (longerRunes.Length != shorterRunes.Length + 1) return false;
        for (var removed = 0; removed < longerRunes.Length; removed++)
        {
            var adjacentDuplicate = removed > 0 && longerRunes[removed] == longerRunes[removed - 1]
                || removed + 1 < longerRunes.Length && longerRunes[removed] == longerRunes[removed + 1];
            if (!adjacentDuplicate) continue;
            if (longerRunes.Where((_, index) => index != removed).SequenceEqual(shorterRunes)) return true;
        }
        return false;
    }

    private static long StartKey(double start) =>
        checked((long)Math.Round(start * 1000, MidpointRounding.AwayFromZero));
}
