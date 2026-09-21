using System.Text;

namespace BiliSubStudio.Core.Ocr;

public sealed class OcrLiveCueAccumulator
{
    private readonly SortedDictionary<long, OcrCue> _byStart = [];

    public IReadOnlyList<OcrCue> Cues => _byStart.Values.ToArray();

    public void Clear() => _byStart.Clear();

    // Resume path: re-install the paused authoritative list without deleting
    // anything, so rolling snapshots reconcile forward from it instead of the
    // UI going blank until telemetry refills.
    public void Seed(IReadOnlyList<OcrCue> cues)
    {
        foreach (var cue in Normalize(cues))
        {
            var key = StartKey(cue.Start);
            _byStart[key] = _byStart.TryGetValue(key, out var existing)
                ? MergeSameStart(existing, cue)
                : cue;
        }
    }

    public bool Merge(IReadOnlyList<OcrCue> incoming)
    {
        var before = _byStart.Values.ToArray();
        var snapshot = Normalize(incoming)
            .OrderBy(cue => cue.Start)
            .ThenBy(cue => cue.End)
            .ToArray();

        // An empty telemetry snapshot carries no information (resume warm-up,
        // pre-first-callback). It must never wipe the retained history.
        if (snapshot.Length == 0) return false;

        // Scanner telemetry is the current rolling suffix, not an append-only
        // delta. Replace its overlapping tail so cues removed by reconciliation
        // or provisional-state changes cannot survive as duplicate ghost rows.
        var replaceFrom = StartKey(snapshot[0].Start);
        var currentKeys = snapshot.Select(cue => StartKey(cue.Start)).ToHashSet();
        foreach (var key in _byStart.Keys.Where(key => key >= replaceFrom && !currentKeys.Contains(key)).ToArray())
            _byStart.Remove(key);

        foreach (var cue in snapshot)
        {
            var key = StartKey(cue.Start);
            _byStart[key] = _byStart.TryGetValue(key, out var existing)
                ? MergeSameStart(existing, cue)
                : cue;
        }

        return !before.SequenceEqual(_byStart.Values);
    }

    private static IReadOnlyList<OcrCue> Normalize(IEnumerable<OcrCue> cues)
    {
        var output = new List<OcrCue>();
        foreach (var rawCue in cues)
        {
            if (ChineseSubtitleNormalizer.TryNormalize(rawCue.Text, out var text))
                output.Add(rawCue with { Text = text });
        }
        return output;
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
