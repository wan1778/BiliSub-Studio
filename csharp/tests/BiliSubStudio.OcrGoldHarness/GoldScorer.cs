using System.Text;
using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.OcrGoldHarness;

internal sealed record ResolvedGoldCue(string Id, long OnsetPts, long OffsetPts, int OnsetFrame, int OffsetFrame, double OnsetSeconds, double OffsetSeconds, string Text);
internal sealed record ResolvedCandidateCue(int Index, long OnsetPts, long OffsetPts, int OnsetFrame, int OffsetFrame, double OnsetSeconds, double OffsetSeconds, OcrCue Cue);
internal sealed record CueMatch(
    string GoldId,
    int CandidateIndex,
    long GoldOnsetPts,
    long CandidateOnsetPts,
    long GoldOffsetPts,
    long CandidateOffsetPts,
    double OnsetErrorSeconds,
    double OffsetErrorSeconds,
    int OnsetErrorFrames,
    int OffsetErrorFrames,
    string GoldText,
    string CandidateText,
    string NormalizedGoldText,
    string NormalizedCandidateText,
    int RuneEdits,
    int GoldRunes);

internal sealed record ScoreMetrics(
    int ExpectedCueCount,
    int CandidateCueCount,
    int MatchedCueCount,
    double CueRecall,
    double CuePrecision,
    double Cer,
    int RuneEdits,
    int GoldRunes,
    double? MeanOnsetErrorSeconds,
    double? P95OnsetErrorSeconds,
    double? MeanOffsetErrorSeconds,
    double? P95OffsetErrorSeconds,
    int? MaximumOnsetErrorFrames,
    int? MaximumOffsetErrorFrames,
    bool Passed,
    IReadOnlyList<string> Failures);

internal sealed record ScoreResult(
    ScoreMetrics Metrics,
    IReadOnlyList<CueMatch> Matches,
    IReadOnlyList<string> MissedGoldCueIds,
    IReadOnlyList<int> UnmatchedCandidateIndexes);

internal static class GoldScorer
{
    public static ScoreResult Score(
        GoldCase goldCase,
        IReadOnlyList<OcrCue> candidates,
        FrameInventory inventory,
        GoldThresholds thresholds)
    {
        var gold = goldCase.ExpectedCues.Select(cue => ResolveGold(cue, inventory, thresholds.PtsEpsilonSeconds)).ToArray();
        var predicted = candidates.Select((cue, index) => ResolveCandidate(cue, index, inventory, thresholds.PtsEpsilonSeconds))
            .OrderBy(cue => cue.OnsetSeconds).ThenBy(cue => cue.OffsetSeconds).ToArray();
        var pairs = MatchByTiming(gold, predicted, thresholds.CueMatchToleranceSeconds);
        var matchedGold = pairs.Select(pair => pair.Gold).ToHashSet();
        var matchedCandidates = pairs.Select(pair => pair.Candidate).ToHashSet();
        var matches = new List<CueMatch>();
        var edits = 0;
        var goldRunes = 0;
        foreach (var pair in pairs.OrderBy(pair => pair.Gold))
        {
            var expected = gold[pair.Gold];
            var actual = predicted[pair.Candidate];
            var normalizedGold = Normalize(expected.Text);
            var normalizedCandidate = Normalize(actual.Cue.Text);
            var distance = RuneEditDistance(normalizedGold, normalizedCandidate);
            var count = normalizedGold.EnumerateRunes().Count();
            edits += distance;
            goldRunes += count;
            matches.Add(new CueMatch(
                expected.Id, actual.Index, expected.OnsetPts, actual.OnsetPts, expected.OffsetPts, actual.OffsetPts,
                Math.Abs(expected.OnsetSeconds - actual.OnsetSeconds),
                Math.Abs(expected.OffsetSeconds - actual.OffsetSeconds),
                Math.Abs(expected.OnsetFrame - actual.OnsetFrame),
                Math.Abs(expected.OffsetFrame - actual.OffsetFrame),
                expected.Text, actual.Cue.Text, normalizedGold, normalizedCandidate, distance, count));
        }

        foreach (var index in Enumerable.Range(0, gold.Length).Where(index => !matchedGold.Contains(index)))
        {
            var missedRunes = Normalize(gold[index].Text).EnumerateRunes().Count();
            edits += missedRunes;
            goldRunes += missedRunes;
        }
        foreach (var index in Enumerable.Range(0, predicted.Length).Where(index => !matchedCandidates.Contains(index)))
            edits += Normalize(predicted[index].Cue.Text).EnumerateRunes().Count();

        var recall = gold.Length == 0 ? 0 : pairs.Count / (double)gold.Length;
        var precision = predicted.Length == 0 ? 0 : pairs.Count / (double)predicted.Length;
        var cer = goldRunes == 0 ? (edits == 0 ? 0 : 1) : edits / (double)goldRunes;
        var onsetErrors = matches.Select(match => match.OnsetErrorSeconds).ToArray();
        var offsetErrors = matches.Select(match => match.OffsetErrorSeconds).ToArray();
        int? maximumOnsetFrames = matches.Count == 0 ? null : matches.Max(match => match.OnsetErrorFrames);
        int? maximumOffsetFrames = matches.Count == 0 ? null : matches.Max(match => match.OffsetErrorFrames);
        var meanOnset = Mean(onsetErrors);
        var meanOffset = Mean(offsetErrors);
        var failures = new List<string>();
        if (recall + 1e-12 < thresholds.MinimumCueRecall)
            failures.Add($"cue recall {recall:0.####} < {thresholds.MinimumCueRecall:0.####}");
        if (precision + 1e-12 < thresholds.MinimumCuePrecision)
            failures.Add($"cue precision {precision:0.####} < {thresholds.MinimumCuePrecision:0.####}");
        if (cer - 1e-12 > thresholds.MaximumCer)
            failures.Add($"rune CER {cer:0.####} > {thresholds.MaximumCer:0.####}");
        if (meanOnset is null || meanOnset.Value - 1e-12 > thresholds.MaximumMeanOnsetErrorSeconds)
            failures.Add(meanOnset is null ? "onset error is unavailable because no cues matched"
                : $"mean onset error {meanOnset:0.####}s > {thresholds.MaximumMeanOnsetErrorSeconds:0.####}s");
        if (meanOffset is null || meanOffset.Value - 1e-12 > thresholds.MaximumMeanOffsetErrorSeconds)
            failures.Add(meanOffset is null ? "offset error is unavailable because no cues matched"
                : $"mean offset error {meanOffset:0.####}s > {thresholds.MaximumMeanOffsetErrorSeconds:0.####}s");
        if (maximumOnsetFrames is null || maximumOnsetFrames > thresholds.MaximumOnsetErrorFrames)
            failures.Add(maximumOnsetFrames is null ? "onset frame error is unavailable because no cues matched"
                : $"maximum onset error {maximumOnsetFrames} frames > {thresholds.MaximumOnsetErrorFrames}");
        if (maximumOffsetFrames is null || maximumOffsetFrames > thresholds.MaximumOffsetErrorFrames)
            failures.Add(maximumOffsetFrames is null ? "offset frame error is unavailable because no cues matched"
                : $"maximum offset error {maximumOffsetFrames} frames > {thresholds.MaximumOffsetErrorFrames}");

        return new ScoreResult(
            new ScoreMetrics(gold.Length, predicted.Length, pairs.Count, recall, precision, cer, edits, goldRunes,
                meanOnset, Percentile95(onsetErrors), meanOffset, Percentile95(offsetErrors),
                maximumOnsetFrames, maximumOffsetFrames, failures.Count == 0, failures),
            matches,
            gold.Where((_, index) => !matchedGold.Contains(index)).Select(cue => cue.Id).ToArray(),
            predicted.Where((_, index) => !matchedCandidates.Contains(index)).Select(cue => cue.Index).Order().ToArray());
    }

    internal static int RuneEditDistance(string expected, string actual)
    {
        var left = expected.EnumerateRunes().Select(rune => rune.Value).ToArray();
        var right = actual.EnumerateRunes().Select(rune => rune.Value).ToArray();
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        for (var row = 1; row <= left.Length; row++)
        {
            var current = new int[right.Length + 1];
            current[0] = row;
            for (var column = 1; column <= right.Length; column++)
            {
                current[column] = Math.Min(
                    Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + (left[row - 1] == right[column - 1] ? 0 : 1));
            }
            previous = current;
        }
        return previous[^1];
    }

    internal static IReadOnlyList<(int Gold, int Candidate)> MatchByTiming(
        IReadOnlyList<ResolvedGoldCue> gold,
        IReadOnlyList<ResolvedCandidateCue> candidates,
        double tolerance)
    {
        var table = new MatchCell[gold.Count + 1, candidates.Count + 1];
        table[gold.Count, candidates.Count] = new MatchCell();
        for (var goldIndex = gold.Count; goldIndex >= 0; goldIndex--)
        {
            for (var candidateIndex = candidates.Count; candidateIndex >= 0; candidateIndex--)
            {
                if (goldIndex == gold.Count && candidateIndex == candidates.Count) continue;
                MatchCell? best = null;
                if (goldIndex < gold.Count) best = table[goldIndex + 1, candidateIndex] with { Action = MatchAction.SkipGold };
                if (candidateIndex < candidates.Count)
                    best = Better(best, table[goldIndex, candidateIndex + 1] with { Action = MatchAction.SkipCandidate });
                if (goldIndex < gold.Count && candidateIndex < candidates.Count)
                {
                    var onset = Math.Abs(gold[goldIndex].OnsetSeconds - candidates[candidateIndex].OnsetSeconds);
                    var offset = Math.Abs(gold[goldIndex].OffsetSeconds - candidates[candidateIndex].OffsetSeconds);
                    if (onset <= tolerance + 1e-12 && offset <= tolerance + 1e-12)
                    {
                        var tail = table[goldIndex + 1, candidateIndex + 1];
                        best = Better(best, new MatchCell(tail.Matches + 1, tail.Cost + onset + offset, MatchAction.Match));
                    }
                }
                table[goldIndex, candidateIndex] = best ?? new MatchCell(0, 0, MatchAction.None);
            }
        }

        var result = new List<(int Gold, int Candidate)>();
        var i = 0;
        var j = 0;
        while (i < gold.Count || j < candidates.Count)
        {
            switch (table[i, j].Action)
            {
                case MatchAction.Match:
                    result.Add((i++, j++));
                    break;
                case MatchAction.SkipGold:
                    i++;
                    break;
                case MatchAction.SkipCandidate:
                    j++;
                    break;
                default:
                    i = gold.Count;
                    j = candidates.Count;
                    break;
            }
        }
        return result;
    }

    private static MatchCell Better(MatchCell? current, MatchCell candidate)
    {
        if (current is null || candidate.Matches > current.Matches) return candidate;
        if (candidate.Matches < current.Matches) return current;
        if (candidate.Cost < current.Cost - 1e-12) return candidate;
        if (candidate.Cost > current.Cost + 1e-12) return current;
        return candidate.Action == MatchAction.Match && current.Action != MatchAction.Match ? candidate : current;
    }

    private static ResolvedGoldCue ResolveGold(GoldCue cue, FrameInventory inventory, double epsilon)
    {
        var onsetIndex = inventory.ResolveIndex(cue.OnsetPts);
        var offsetIndex = inventory.ResolveIndex(cue.OffsetPts);
        var onset = inventory.Resolve(cue.OnsetPts, epsilon);
        var offset = inventory.Resolve(cue.OffsetPts, epsilon);
        return new ResolvedGoldCue(cue.Id, cue.OnsetPts, cue.OffsetPts, onsetIndex, offsetIndex,
            onset.PtsTime, offset.PtsTime, cue.Text);
    }

    private static ResolvedCandidateCue ResolveCandidate(OcrCue cue, int index, FrameInventory inventory, double epsilon)
    {
        if (cue.FirstObservation is not { SourcePts: { } onsetPts, SourceTimeBase: { } onsetTimeBase }
            || cue.LastObservation is not { SourcePts: { } offsetPts, SourceTimeBase: { } offsetTimeBase })
            throw new InvalidDataException($"Candidate cue {index} lacks first/last source-frame PTS provenance.");
        if (onsetTimeBase.Num != inventory.TimeBaseNum || onsetTimeBase.Den != inventory.TimeBaseDen
            || offsetTimeBase.Num != inventory.TimeBaseNum || offsetTimeBase.Den != inventory.TimeBaseDen)
            throw new InvalidDataException($"Candidate cue {index} source time base differs from ffprobe.");
        var onsetIndex = inventory.ResolveIndex(onsetPts);
        var lastVisibleIndex = inventory.ResolveIndex(offsetPts);
        var onset = inventory.Resolve(onsetPts, epsilon);
        var lastVisible = inventory.Resolve(offsetPts, epsilon);
        if (Math.Abs(cue.FirstObservation.Pts - onset.PtsTime) > epsilon
            || Math.Abs(cue.LastObservation.Pts - lastVisible.PtsTime) > epsilon)
            throw new InvalidDataException($"Candidate cue {index} observation pts_time differs from its ffprobe source PTS.");
        var absoluteCueEnd = inventory.StreamStartTime + cue.End;
        var boundaryIndex = inventory.NearestIndex(absoluteCueEnd);
        var boundary = inventory.Frames[boundaryIndex];
        if (Math.Abs(boundary.PtsTime - absoluteCueEnd) > epsilon)
            throw new InvalidDataException($"Candidate cue {index} end does not land on a native-frame PTS boundary.");
        if (boundaryIndex < lastVisibleIndex || boundary.PtsTime < onset.PtsTime)
            throw new InvalidDataException($"Candidate cue {index} has reversed end/PTS provenance.");
        return new ResolvedCandidateCue(index, onsetPts, boundary.Pts, onsetIndex, boundaryIndex,
            onset.PtsTime, boundary.PtsTime, cue);
    }

    private static string Normalize(string value) =>
        ChineseSubtitleNormalizer.TryNormalize(value, out var normalized) ? normalized : string.Empty;

    private static double? Mean(IReadOnlyList<double> values) => values.Count == 0 ? null : values.Average();

    private static double? Percentile95(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return null;
        var ordered = values.Order().ToArray();
        return ordered[(int)Math.Ceiling(ordered.Length * .95) - 1];
    }

    private enum MatchAction { None, Match, SkipGold, SkipCandidate }
    private sealed record MatchCell(int Matches, double Cost, MatchAction Action)
    {
        public MatchCell() : this(0, 0, MatchAction.None) { }
    }
}

internal static class ScorerSelfTests
{
    public static void Run()
    {
        Equal(1, GoldScorer.RuneEditDistance("汉𠀀", "汉字"), "CER must count Unicode runes, not UTF-16 code units");
        Equal(0, GoldScorer.RuneEditDistance("少主……是不死丹帝", "少主……是不死丹帝"), "equal rune text");

        var gold = new[]
        {
            new ResolvedGoldCue("a", 0, 10, 0, 10, 0, 1, "甲"),
            new ResolvedGoldCue("b", 20, 30, 20, 30, 2, 3, "乙"),
        };
        var region = new OcrRegion(0, 0, 1, 1);
        var box = new OcrBox(0, 0, 10, 10);
        var timeBase = new RationalTimeBase(1, 10);
        OcrCue Cue(string text, long start, long end) => new(start / 10d, end / 10d, text, 1, FirstObservation:
            new PixelObservation(start / 10d, box, region, SourcePts: start, SourceTimeBase: timeBase), LastObservation:
            new PixelObservation(end / 10d, box, region, SourcePts: end, SourceTimeBase: timeBase));
        var candidates = new[]
        {
            new ResolvedCandidateCue(0, 0, 10, 0, 10, 0, 1, Cue("乙", 0, 10)),
            new ResolvedCandidateCue(1, 20, 30, 20, 30, 2, 3, Cue("甲", 20, 30)),
        };
        var pairs = GoldScorer.MatchByTiming(gold, candidates, .1);
        Equal(2, pairs.Count, "timing matcher recall");
        Equal((0, 0), pairs[0], "matching must ignore swapped text");
        Equal((1, 1), pairs[1], "matching must preserve temporal pairing");

        var ambiguousCandidates = new[]
        {
            new ResolvedCandidateCue(0, 0, 9, 0, 9, 0, .9, Cue("甲", 0, 9)),
            new ResolvedCandidateCue(1, 1, 10, 1, 10, .1, 1, Cue("乙", 1, 10)),
        };
        var oneGold = new[] { gold[0] };
        var closest = GoldScorer.MatchByTiming(oneGold, ambiguousCandidates, .2);
        Equal(1, closest.Count, "one-to-one matching");
        Equal(0, closest[0].Candidate, "minimum timing error must win without consulting text");

        var inventory = new FrameInventory(1, 10, 0,
            Enumerable.Range(0, 61).Select(index => new ProbeFrame(index, index / 10d, .1)).ToArray(),
            "fixture", []);
        var goldCase = new GoldCase("cer", "fixture", new string('0', 64),
            new GoldProvenance("private", "fixture", "test", "test", null),
            new GoldScan(0, 6, region, "accurate", "cpu", "1", 1),
            [new GoldCue("a", 0, 10, "甲"), new GoldCue("b", 20, 30, "乙")]);
        var scored = GoldScorer.Score(goldCase, [Cue("甲", 0, 10), Cue("丙", 40, 50)], inventory,
            new GoldThresholds(.1, 1e-9, 0, 0, 2, 2, 2, 10, 10));
        Equal(2, scored.Metrics.RuneEdits, "CER must include missed reference deletions and unmatched candidate insertions");
        Equal(2, scored.Metrics.GoldRunes, "CER denominator must include every reference rune");
        Equal(1d, scored.Metrics.Cer, "CER must score unmatched cues");
    }

    private static void Equal<T>(T expected, T actual, string label) where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Self-test failed ({label}): expected {expected}, got {actual}.");
    }
}
