namespace BiliSubStudio.Core.Ocr;

// Text cleanup after reconcile (17/09): merge adjacent duplicate cues + delete flicker fragments.
// Pure text logic (NO AI, NO semantic understanding):
// - Merge: 2 adjacent cues (gap ≤ 0.3s) + text similarity ≥ 0.65 (after normalization) ->
//   1 cue (spanning both, keep the LONGER text = the more complete read). Handles flicker
//   where OCR splits one sentence into many pieces (cue 34/35: same sentence 休书, sim 0.69).
//   Threshold 0.65 + gap 0.3s: two genuinely different subs side by side rarely match
//   ≥65% within 0.3s (that is precisely the definition of flicker).
// - Fragments: cue ≤ 2 runes adjacent (gap ≤ 0.3s) to a LONGER cue containing all of its text ->
//   a flicker fragment -> delete (cue 31 "人。" next to cue 30 "人第一").
// Does NOT correct misread text (联姻对象→眼惘叶象, 两不相干, 纠缠, truncated sentence 38):
// correcting text requires semantic understanding (AI) — left unchanged for the user to fix manually.
public static class OcrTextCleanup
{
    public const double MergeGapSeconds = 0.3;
    public const double MergeSimilarity = 0.65;
    public const double FragmentGapSeconds = 0.3;
    // Typing/progressive-reveal effect (21/09, case p01 "xxx100%>xxx" waiting for string 3):
    // the later cue STARTS WITH all of the previous cue's text (normalized) and is longer -> the same
    // 1 caption growing, merge keeping the longer version. The window is wider than a normal merge
    // because a typing chain drags on. Short text <3 runes is not trusted enough to infer typing.
    public const double PrefixGrowthGapSeconds = 0.5;
    public const int PrefixGrowthMinRunes = 3;
    public const int FragmentMaxRunes = 2;
    // Phantom threshold (cue flickering from flash/effect): SHORT + TIGHTLY SQUEEZED + DIFFERENT
    // TEXT on both sides. The DIFFERENT-TEXT condition is mandatory: same-sentence flicker (cue
    // 34/35: short + squeezed but the text is ALIKE 0.69) must be left for merge to join, not
    // deleted — only a strange cue wedged in between (cue 55: sim ~0 on both sides) is junk.
    public const double PhantomMaxDuration = 0.6;
    public const double PhantomMaxGap = 0.15;
    public const double PhantomMaxSimilarity = 0.4;
    // Narrow phantom exemption (18/09): a cue read VERY confidently is not a flicker —
    // protects genuinely short dialogue ("ừm", "hả", "ĐI"). The old threshold stays unchanged for
    // blurry cues (flicker junk usually has low confidence).
    // Phantom exemption by read density (27/09, the case that filtered out 1 character): flash artifacts
    // cram dozens of characters into <0.5s (cue 55: ~30 characters/0.43s = physically impossible);
    // genuinely short dialogue ("嗯" 1 character/0.3s, fast subs ~10 characters/s) has an ordinary
    // density -> keep. Only cues with an impossible density deserve the flicker label.
    public const double PhantomMinDensityRunesPerSecond = 12.0;

    public sealed record CleanupResult(
        IReadOnlyList<OcrCue> Cues, int Merged, int FragmentsDropped, int PhantomsDropped);

    // Flickering cue? Short + tightly squeezed next to an adjacent cue + different text present on both sides.
    // No side (list start/end) is treated as an infinite gap -> keep.
    public const double PhantomExemptConfidence = 0.9;

    public static bool IsPhantom(OcrCue? cue, OcrCue? prev, OcrCue? next)
    {
        if (cue is null || cue.Text is null) return false;
        if (cue.Confidence >= PhantomExemptConfidence) return false;
        if (!(cue.End - cue.Start < PhantomMaxDuration)) return false;
        var runes = OcrTemporalFilter.Normalize(cue.Text).EnumerateRunes().Count();
        var duration = Math.Max(cue.End - cue.Start, 0.001);
        if (runes <= 0 || runes / duration < PhantomMinDensityRunesPerSecond) return false;
        var gapBefore = prev is null ? double.PositiveInfinity : cue.Start - prev.End;
        var gapAfter = next is null ? double.PositiveInfinity : next.Start - cue.End;
        // SQUEEZED ON BOTH SIDES is what makes a flicker (18/09: fixed && into || — the old code wrongly deleted
        // genuinely short dialogue that was only squeezed on one side, e.g. the reply "嗯" after a long sentence 0.1s later).
        // Room on one side is enough for a real line of dialogue.
        if (gapBefore >= PhantomMaxGap || gapAfter >= PhantomMaxGap) return false;
        if (prev?.Text is not null && TextSimilarity(prev.Text, cue.Text) >= PhantomMaxSimilarity)
            return false;
        if (next?.Text is not null && TextSimilarity(next.Text, cue.Text) >= PhantomMaxSimilarity)
            return false;
        return true;
    }

    public static CleanupResult Cleanup(IEnumerable<OcrCue> cues)
    {
        var ordered = cues.Where(cue => cue is not null && cue.Text is not null)
            .OrderBy(cue => cue.Start).ToArray();
        // Pass 0: delete flicker fragments FIRST (cue ≤2 runes next to a longer cue containing
        // all of its text) — so pass 1 (phantom) does not eat the anchor cue (e.g. "人第一"
        // next to "人。": the anchor is short+squeezed but is the anchor sentence, the "人。" fragment is the junk).
        var fragDropped = new bool[ordered.Length];
        var fragments = 0;
        for (var index = 0; index < ordered.Length; index++)
        {
            var cue = ordered[index];
            var norm = OcrTemporalFilter.Normalize(cue.Text);
            var runes = norm.EnumerateRunes().Count();
            if (runes == 0 || runes > FragmentMaxRunes) continue;
            foreach (var neighborIndex in new[] { index - 1, index + 1 })
            {
                if (neighborIndex < 0 || neighborIndex >= ordered.Length || fragDropped[neighborIndex]) continue;
                var neighbor = ordered[neighborIndex];
                var gap = neighborIndex < index
                    ? cue.Start - neighbor.End
                    : neighbor.Start - cue.End;
                if (gap < 0 || gap > FragmentGapSeconds) continue;
                var neighborNorm = OcrTemporalFilter.Normalize(neighbor.Text);
                if (neighborNorm.EnumerateRunes().Count() <= runes) continue;
                if (neighborNorm.Contains(norm, StringComparison.Ordinal))
                {
                    fragDropped[index] = true;
                    fragments++;
                    break;
                }
            }
        }
        var defragged = new List<OcrCue>(ordered.Length);
        for (var index = 0; index < ordered.Length; index++)
            if (!fragDropped[index]) defragged.Add(ordered[index]);
        // Pass 1: delete flickering cues (flash/effect): short + squeezed + different text on both sides.
        // Runs AFTER fragment (anchor intact) and BEFORE merge (same-sentence flicker
        // stays intact so merge joins it correctly).
        var alive = new List<OcrCue>(defragged.Count);
        var phantoms = 0;
        for (var index = 0; index < defragged.Count; index++)
        {
            if (IsPhantom(defragged[index],
                    index > 0 ? defragged[index - 1] : null,
                    index < defragged.Count - 1 ? defragged[index + 1] : null))
            {
                phantoms++;
                continue;
            }
            alive.Add(defragged[index]);
        }
        // Pass 2: merge adjacent duplicates (keep the longer text, sum support, OR NeedsReview).
        // EXCEPT byte-identical text (sim == 1.0): the pixel verdict already handled it (identical
        // + pixel Different = 2 real instances kept separate; identical + Same then
        // MergeTouchingIdentical already merged). Only merge flicker that reads DIFFERENTLY.
        var merged = new List<OcrCue>();
        var merges = 0;
        foreach (var cue in alive)
        {
            if (merged.Count > 0)
            {
                var prev = merged[^1];
                var gap = cue.Start - prev.End;
                var similarity = TextSimilarity(prev.Text, cue.Text);
                var prefixGrowth = gap >= 0 && gap <= PrefixGrowthGapSeconds
                    && IsPrefixGrowth(prev.Text, cue.Text);
                if (gap >= 0 && ((gap <= MergeGapSeconds
                    && similarity >= MergeSimilarity && similarity < 1.0) || prefixGrowth))
                {
                    var text = NormalizedLength(cue.Text) >= NormalizedLength(prev.Text)
                        ? cue.Text : prev.Text;
                    merged[^1] = prev with
                    {
                        End = Math.Max(prev.End, cue.End),
                        Text = text,
                        Confidence = Math.Max(prev.Confidence, cue.Confidence),
                        RawSupportCount = prev.RawSupportCount + cue.RawSupportCount,
                        ConfidentSupportCount = prev.ConfidentSupportCount + cue.ConfidentSupportCount,
                        LastObservation = cue.LastObservation ?? prev.LastObservation,
                        NeedsReview = prev.NeedsReview || cue.NeedsReview,
                    };
                    merges++;
                    continue;
                }
            }
            merged.Add(cue);
        }
        if (merges == 0 && fragments == 0 && phantoms == 0)
            return new CleanupResult(merged, 0, 0, 0);
        return new CleanupResult(merged, merges, fragments, phantoms);
    }

    private static int NormalizedLength(string text) =>
        OcrTemporalFilter.Normalize(text).EnumerateRunes().Count();

    // The later cue is the GROWING version of the previous one (typing effect): the short normalized
    // text is a strict prefix of the long one and considerably shorter. Does not demand similarity —
    // a fast-growing version may differ wildly (e.g. "…50%" → "…100% done").
    private static bool IsPrefixGrowth(string previousText, string nextText)
    {
        var a = OcrTemporalFilter.Normalize(previousText);
        var b = OcrTemporalFilter.Normalize(nextText);
        if (a.Length == 0 || b.Length == 0 || a.Length >= b.Length) return false;
        var aRunes = a.EnumerateRunes().Count();
        if (aRunes < PrefixGrowthMinRunes) return false;
        return b.StartsWith(a, StringComparison.Ordinal);
    }

    // Internal so the temporal filter can reuse it when looking for a better read (fallback).
    internal static double TextSimilarity(string left, string right)
    {
        var a = OcrTemporalFilter.Normalize(left);
        var b = OcrTemporalFilter.Normalize(right);
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 1;
        var leftRunes = a.EnumerateRunes().Select(x => x.Value).ToArray();
        var rightRunes = b.EnumerateRunes().Select(x => x.Value).ToArray();
        var previous = Enumerable.Range(0, rightRunes.Length + 1).ToArray();
        for (var i = 1; i <= leftRunes.Length; i++)
        {
            var current = new int[rightRunes.Length + 1];
            current[0] = i;
            for (var j = 1; j <= rightRunes.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + (leftRunes[i - 1] == rightRunes[j - 1] ? 0 : 1));
            previous = current;
        }
        return 1 - previous[^1] / (double)Math.Max(leftRunes.Length, rightRunes.Length);
    }
}
