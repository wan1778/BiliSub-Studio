using System.Drawing;
using System.Text;
using BiliSubStudio.Core.Editor;

namespace BiliSubStudio.Core.Ocr;

public static class OcrCueReconciler
{
    // Recovered full/short/full readings may leave separate touching cues with
    // the same final spelling or one transient substitution. Never bridge a
    // real blank or merge a one-way similar caption without temporal consensus.
    private const double Epsilon = 0.001;
    // Field case (flicker-split identical caption, 0.133s phantom gap, same box):
    // a few misread frames close a cue the text never left. Byte-identical
    // readings across such a micro-gap must reach the pixel verifier; real
    // blanks (0.55s+/0.667s field evidence) stay outside. Pixel still decides:
    // Same merges, Different/Unavailable keeps separate + NeedsReview.
    private const double IdenticalTextMicroGapSeconds = 0.25;
    // Exact duplicate pairs with a gap (micro-gap, 0.5s]: the pinned spec FORBIDS pixel-verify
    // (OcrExactGapRegression#11) but silence is wrong — flag review so the user
    // can listen and merge/delete by hand (18/09: user's 关于战神篇 pair with exactly a 0.3s gap).
    // Above 0.5s treat as genuine repeated dialogue (below that boundary is the 0.55s+ blank evidence).
    private const double IdenticalReviewGapSeconds = 0.5;
    private const double SimilarVariantMinSimilarity = 0.82;
    private const double SimilarVariantGapSeconds = 0.9;

    private static double AllowedGapSeconds(OcrCue previous, OcrCue cue, double continuationWindow)
    {
        if (string.Equals(previous.Text, cue.Text, StringComparison.Ordinal))
            return Math.Max(continuationWindow, IdenticalTextMicroGapSeconds);
        if (cue.Start - previous.End > SimilarVariantGapSeconds + Epsilon)
            return continuationWindow;
        return OcrTextCleanup.TextSimilarity(previous.Text, cue.Text) >= SimilarVariantMinSimilarity
            ? Math.Max(continuationWindow, SimilarVariantGapSeconds)
            : continuationWindow;
    }

    // Legacy/no-pixel sync: no cross-cue merge, only normalize + keep separated (deterministic unit tests, no FFmpeg).
    public static IReadOnlyList<OcrCue> MergeTouchingIdentical(IEnumerable<OcrCue> cues)
    {
        var normalized = new List<OcrCue>();
        foreach (var rawCue in cues.OrderBy(cue => cue.Start))
        {
            if (ChineseSubtitleNormalizer.TryNormalize(rawCue.Text, out var text))
                normalized.Add(rawCue with { Text = text });
        }
        return normalized;
    }

    // Production async: requires videoPath + comparer + CancellationToken. Missing => Unavailable => keep separated + NeedsReview.
    // continuationWindow: visual-continuation window for EVERY boundary (default Epsilon = only touching/overlap).
    // Production always passes 3.0/nativeFps (OcrScanner), never pass a large number by hand: a real blank longer than
    // 3 native frames is a new instance (field evidence 0.667s/0.55s), must stay separated even with identical text.
    // Independent of text patterns: only temporal continuity + spatial continuity + Pixel Same merge.
    public static async Task<IReadOnlyList<OcrCue>> MergeTouchingIdenticalAsync(IEnumerable<OcrCue> cues, string videoPath, IPixelComparer comparer, CancellationToken cancellationToken, double continuationWindow = Epsilon, Action<int, int>? onProgress = null)
    {
        var normalized = new List<OcrCue>();
        foreach (var rawCue in cues.OrderBy(cue => cue.Start))
        {
            if (ChineseSubtitleNormalizer.TryNormalize(rawCue.Text, out var text))
                normalized.Add(rawCue with { Text = text });
        }
        return await MergeAdjacentAsync(normalized, videoPath, comparer, cancellationToken, continuationWindow, onProgress);
    }

    // Keep the 3-parameter sync overload for the current contract tests (will migrate to Async). Sync does not call FFmpeg, only uses a sync comparer? For now: if comparer is null or video is empty => Unavailable.
    // To avoid blocking the thread pool, this sync overload does NOT call the async comparer; it is only for tests with a sync fake (will be removed once everything migrates to Async).
    public static IReadOnlyList<OcrCue> MergeTouchingIdentical(IEnumerable<OcrCue> cues, string? videoPath, IPixelComparer? comparer) => MergeTouchingIdentical(cues);

    private static PixelComparison UnavailableComparison()
        => new(PixelCheckResult.Unavailable,
            new FrameSample(double.NaN, double.NaN, double.NaN),
            new FrameSample(double.NaN, double.NaN, double.NaN),
            System.Drawing.Rectangle.Empty, null, null, null);

    private static async Task<PixelComparison> CheckBoundaryDetailedAsync(OcrCue left, OcrCue right, string? videoPath, IPixelComparer? comparer, CancellationToken cancellationToken)
    {
        var obsA = left.LastObservation;
        var obsB = right.FirstObservation;
        if (obsA is null || obsB is null) return UnavailableComparison();
        if (comparer is null) return UnavailableComparison();
        if (string.IsNullOrWhiteSpace(videoPath)) return UnavailableComparison();
        return await comparer.CheckAsync(videoPath, obsA, obsB, cancellationToken);
    }

    // Full provenance for pixel verify: both boundary observations exist, boxes are valid,
    // SourcePts + timebase are valid, exact Geometry is already stored (no reconstruction from Start/End).
    public static bool HasPixelProvenance(OcrCue cue)
        => HasPixelProvenance(cue.FirstObservation) && HasPixelProvenance(cue.LastObservation);

    private static bool HasPixelProvenance(PixelObservation? obs)
    {
        if (obs is null || obs.SourceRoi is null || obs.Geometry is null) return false;
        if (obs.SourcePts is null) return false;
        if (obs.SourceTimeBase is not { Num: > 0, Den: > 0 }) return false;
        if (!double.IsFinite(obs.Pts)) return false;
        var box = obs.BoxInOcrCrop;
        return box.Right > box.Left && box.Bottom > box.Top;
    }

    internal sealed record BoundaryEvaluation(
        bool IsCandidate,
        string CandidateReason,
        double GapSeconds,
        double AllowedGapSeconds,
        bool GapWithinWindow,
        PixelCheckResult Pixel,
        PixelComparison? Comparison,
        string? DuplicateStableText,
        bool IsIdentical,
        bool IsDashIdentical,
        bool IsDuplicate,
        bool CanMergeFlicker,
        bool IsDashDropout,
        bool IsPunctVariant,
        bool IsPureExact);

    // Generic visual-boundary candidacy: temporal continuity (gap trong continuation window
    // measured in native frames, NOT sampling Hz) + spatial continuity (same subtitle location,
    // real provenance). Does NOT require text to be exact/substitution/punctuation/dash-like.
    // Old text helpers serve only CandidateReason diagnostics, not mandatory coverage:
    // a boundary valid in temporal+spatial terms always has a path to the pixel verifier whatever the text is.
    private sealed record VisualCandidacy(
        bool TemporalOk,
        bool Blocked,
        bool IsCandidate,
        string Reason,
        bool IsIdentical,
        bool IsDashIdentical,
        bool IsPureExact,
        bool IsDuplicate,
        bool CanMergeFlicker,
        bool IsDashDropout,
        bool IsPunctVariant);

    private static VisualCandidacy ClassifyVisualCandidate(OcrCue previous, OcrCue cue, double continuationWindow)
    {
        var gap = cue.Start - previous.End;
        // The window boundary itself is float-sensitive: a gap of exactly three
        // native frames must stay inside it, so compare with the shared 1 ms
        // epsilon instead of raw doubles. Real blanks (0.55s+) stay outside.
        // Byte-identical readings additionally qualify up to the micro-gap so a
        // flicker-split caption reaches the pixel verifier (field 0.133s case).
        if (!(gap <= AllowedGapSeconds(previous, cue, continuationWindow) + Epsilon))
            return new VisualCandidacy(false, false, false, "non-temporal",
                false, false, false, false, false, false, false);
        var obsA = previous.LastObservation;
        var obsB = cue.FirstObservation;
        if (obsA is null || obsB is null || !HasPixelProvenance(obsA) || !HasPixelProvenance(obsB)
            || !IsSpatiallyCompatible(obsA, obsB))
            return new VisualCandidacy(true, true, false, "spatial-or-provenance-blocked",
                false, false, false, false, false, false, false);
        // Diagnostic subtype: the old text pattern is only for trace/prioritization, not gating.
        var canMergeFlicker = IsShortSingleSubstitutionFlicker(previous, cue);
        var isIdentical = IsIdentical(previous, cue);
        var isDashIdentical = isIdentical && !string.Equals(previous.Text, cue.Text, StringComparison.Ordinal);
        var isPureExact = isIdentical && string.Equals(previous.Text, cue.Text, StringComparison.Ordinal);
        var isDuplicate = IsTransientAdjacentDuplicate(previous, cue, out _);
        var isDashDropout = IsDashTailDropoutCandidate(previous, cue);
        var isPunctVariant = IsPunctuationOnlyVariant(previous.Text, cue.Text);
        var reason = isPureExact ? "exact-identical"
            : isDuplicate ? "duplicate"
            : canMergeFlicker ? "flicker"
            : isDashDropout ? "dash-dropout"
            : isPunctVariant ? "punct-variant"
            : isDashIdentical ? "dash-identical"
            : "visual-continuation";
        return new VisualCandidacy(true, false, true, reason, isIdentical, isDashIdentical,
            isPureExact, isDuplicate, canMergeFlicker, isDashDropout, isPunctVariant);
    }

    // Cheap spatial filter in crop space (same Geometry is comparable now, no reconstruction).
    // Only clear mismatches (different subtitle region) are excluded; everything else is left to the pixel gate.
    // No merging based on bbox.
    internal static bool IsSpatiallyCompatible(PixelObservation left, PixelObservation right)
    {
        var geometry = left.Geometry;
        if (geometry is null || right.Geometry is null || !geometry.Equals(right.Geometry)) return false;
        var rectA = ContentClamped(left.BoxInOcrCrop, geometry);
        var rectB = ContentClamped(right.BoxInOcrCrop, geometry);
        if (rectA.IsEmpty || rectB.IsEmpty) return false;
        if (Math.Min(rectA.Right, rectB.Right) <= Math.Max(rectA.Left, rectB.Left)) return false;
        var minHeight = Math.Min(rectA.Height, rectB.Height);
        if (minHeight <= 0) return false;
        var overlap = Math.Max(0, Math.Min(rectA.Bottom, rectB.Bottom) - Math.Max(rectA.Top, rectB.Top));
        if (overlap / (double)minHeight < 0.5) return false;
        var centerDistance = Math.Abs((rectA.Top + rectA.Bottom) / 2.0 - (rectB.Top + rectB.Bottom) / 2.0);
        if (centerDistance > 0.75 * Math.Max(rectA.Height, rectB.Height)) return false;
        var ratio = rectA.Height / (double)rectB.Height;
        if (ratio >= 0.5 && ratio <= 2.0) return true;
        // A stray scene glyph can inflate the union box above the subtitle.
        // Admit bottom-aligned nested boxes to the unchanged UNION pixel check;
        // never compare only their intersection (a real shorter caption shares it).
        var larger = rectA.Height >= rectB.Height ? rectA : rectB;
        var smaller = rectA.Height >= rectB.Height ? rectB : rectA;
        return larger.Height <= smaller.Height * 4
            && Math.Abs(larger.Bottom - smaller.Bottom) <= smaller.Height * .25
            && smaller.Left >= larger.Left - 2 && smaller.Right <= larger.Right + 2
            && smaller.Top >= larger.Top;
    }

    private static System.Drawing.Rectangle ContentClamped(OcrBox box, OcrFrameGeometry geometry)
    {
        var left = Math.Max(box.Left, geometry.ContentLeft);
        var top = Math.Max(box.Top, geometry.ContentTop);
        var right = Math.Min(box.Right, geometry.ContentLeft + geometry.ContentW);
        var bottom = Math.Min(box.Bottom, geometry.ContentTop + geometry.ContentH);
        if (right <= left || bottom <= top) return System.Drawing.Rectangle.Empty;
        return System.Drawing.Rectangle.FromLTRB(left, top, right, bottom);
    }

    // Chain link generic: temporal trong window + provenance + spatial-compatible.
    // Split at a non-chain boundary for results identical to full reconcile: merge requires a
    // candidate (temporal+spatial) + Pixel Same; there is no separate A/B/A anymore so no
    // effect can cross the boundary. Independent of text.
    internal static bool IsChainBoundary(OcrCue previous, OcrCue cue, double continuationWindow = Epsilon)
    {
        if (cue.Start - previous.End > AllowedGapSeconds(previous, cue, continuationWindow) + Epsilon) return false;
        var obsA = previous.LastObservation;
        var obsB = cue.FirstObservation;
        if (obsA is null || obsB is null || !HasPixelProvenance(obsA) || !HasPixelProvenance(obsB)) return false;
        return IsSpatiallyCompatible(obsA, obsB);
    }

    // Incremental reconcile: reconciledPrefix (view already correct up to the previous offer) + new freshCommitted.
    // Only reconcile the final window (the prefix's tailWindow cues + all fresh, extended backwards through the generic chain
    // to a safe boundary); the prefix before the split stays untouched. Independent of text.
    //
    // SUA OUT-OF-ORDER (vu live-split p02): multi-lane arrival muon co the chen fresh
    // TRUOC tail (lane cham gui cue cu trong khi view da co cue tuong lai). Tinh start
    // theo index prefix nhu cu se bo sot vung fresh -> cue toi tre khong bao gio duoc
    // pixel-merge o live view. Ban nay bao quanh FRESH (min..max + tailWindow) giao voi
    // neo prefix-tail: in-order cho ket qua Y HET cu (test 6 khoa 13 calls), out-of-order
    // van bounded (khong rescan full).
    internal static async Task<IReadOnlyList<OcrCue>> ReconcileLiveTailAsync(
        IReadOnlyList<OcrCue> reconciledPrefix,
        IReadOnlyList<OcrCue> freshCommitted,
        int tailWindow,
        string videoPath,
        IPixelComparer comparer,
        CancellationToken cancellationToken,
        double continuationWindow = Epsilon)
    {
        if (freshCommitted.Count == 0) return reconciledPrefix;
        var combined = reconciledPrefix.Concat(freshCommitted)
            .OrderBy(cue => cue.Start).ThenBy(cue => cue.End).ToArray();
        var keep = Math.Min(reconciledPrefix.Count, Math.Max(0, tailWindow));
        OcrCue? anchor = keep > 0 ? reconciledPrefix[reconciledPrefix.Count - keep] : null;
        var freshSet = new HashSet<OcrCue>(freshCommitted, ReferenceEqualityComparer.Instance);
        var lo = combined.Length;
        var hi = 0;
        for (var index = 0; index < combined.Length; index++)
        {
            if (!freshSet.Contains(combined[index])) continue;
            if (index < lo) lo = index;
            if (index > hi) hi = index;
        }
        var start = lo;
        if (anchor is not null)
        {
            for (var index = 0; index < combined.Length; index++)
            {
                if (ReferenceEquals(combined[index], anchor)) { start = Math.Min(start, index); break; }
            }
        }
        hi = Math.Min(combined.Length, hi + 1 + tailWindow);
        var guard = 0;
        while (start > 0 && guard < 64)
        {
            if (!IsChainBoundary(combined[start - 1], combined[start], continuationWindow)) break;
            start--;
            guard++;
        }
        var merged = await MergeTouchingIdenticalAsync(
            combined.Skip(start).Take(hi - start).ToArray(), videoPath, comparer, cancellationToken, continuationWindow);
        if (start == 0 && hi == combined.Length) return merged;
        return combined.Take(start).Concat(merged).Concat(combined.Skip(hi)).ToArray();
    }

    // Evaluate a boundary with exactly the logic MergeAdjacentAsync uses to merge:
    // a generic visual candidate (temporal+spatial) + pixel gate. MergeAdjacentAsync calls this
    // function so trace logs and production do not drift. Independent of text patterns.
    internal static async Task<BoundaryEvaluation> EvaluateBoundaryAsync(
        OcrCue previous,
        OcrCue cue,
        string videoPath,
        IPixelComparer comparer,
        CancellationToken cancellationToken,
        double continuationWindow = Epsilon)
    {
        var gate = ClassifyVisualCandidate(previous, cue, continuationWindow);
        var gap = cue.Start - previous.End;
        string? duplicateStable = null;
        if (gate.IsDuplicate) IsTransientAdjacentDuplicate(previous, cue, out duplicateStable);
        if (!gate.TemporalOk || gate.Blocked)
            return new BoundaryEvaluation(gate.IsCandidate, gate.Reason, gap, continuationWindow, gate.TemporalOk,
                PixelCheckResult.Unavailable, null, duplicateStable,
                gate.IsIdentical, gate.IsDashIdentical, gate.IsDuplicate, gate.CanMergeFlicker,
                gate.IsDashDropout, gate.IsPunctVariant, gate.IsPureExact);
        var comparison = await CheckBoundaryDetailedAsync(previous, cue, videoPath, comparer, cancellationToken);
        return new BoundaryEvaluation(true, gate.Reason, gap, continuationWindow, true, comparison.Result, comparison,
            duplicateStable, gate.IsIdentical, gate.IsDashIdentical, gate.IsDuplicate, gate.CanMergeFlicker,
            gate.IsDashDropout, gate.IsPunctVariant, gate.IsPureExact);
    }

    private static bool IsIdentical(OcrCue a, OcrCue b) => DashNormalize(a.Text) == DashNormalize(b.Text);

    // Merge identity shared with SubtitleTracker.CanonicalTrackingRune (single source
    // of truth): dash-normalize, then per-rune canonical equality. Fixes field 672-674
    // where Paddle alternates 强/強 across frames: tracking already treats them as one
    // caption, so the merger must too. The map is 1:1, so insertions (garbage 共卡 666)
    // and substitutions (设/没) stay non-identical BY DESIGN and keep pixel + review.
    public static bool IsCanonicalIdentical(OcrCue a, OcrCue b)
    {
        var leftRunes = DashNormalize(a.Text).EnumerateRunes().ToArray();
        var rightRunes = DashNormalize(b.Text).EnumerateRunes().ToArray();
        return leftRunes.Length == rightRunes.Length
            && leftRunes.Zip(rightRunes).All(pair =>
                SubtitleTracker.CanonicalTrackingRune(pair.First.Value)
                == SubtitleTracker.CanonicalTrackingRune(pair.Second.Value));
    }

    // Merge construction shared by the pixel-gated path and the canonical-touching
    // fast path: identical output shape (span union, evidence spelling, support sums,
    // provenance widened to both ends, NeedsReview OR-preserved).
    private static OcrCue MergePair(OcrCue previous, OcrCue cue, string? stableText) =>
        previous with
        {
            End = Math.Max(previous.End, cue.End),
            Text = stableText ?? previous.Text,
            Confidence = Math.Max(previous.Confidence, cue.Confidence),
            RawSupportCount = previous.RawSupportCount + cue.RawSupportCount,
            ConfidentSupportCount = previous.ConfidentSupportCount + cue.ConfidentSupportCount,
            FirstObservation = previous.FirstObservation,
            LastObservation = cue.LastObservation,
            NeedsReview = previous.NeedsReview || cue.NeedsReview,
        };

    private static OcrCue SelectCanonical(OcrCue a, OcrCue b)
    {
        // Whole-instance evidence, text-independent: ConfidentSupportCount first,
        // then RawSupportCount, confidence, duration. Normalization is used for comparison only.
        if (a.ConfidentSupportCount != b.ConfidentSupportCount) return a.ConfidentSupportCount > b.ConfidentSupportCount ? a : b;
        if (a.RawSupportCount != b.RawSupportCount) return a.RawSupportCount > b.RawSupportCount ? a : b;
        if (Math.Abs(a.Confidence - b.Confidence) > 1e-9) return a.Confidence > b.Confidence ? a : b;
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

    private static bool IsDashRune(int value) =>
        value is '\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2014' or '\u2015' or '-' or '—';

    // 一 (U+4E00, a single horizontal stroke) is often OCR-misread as —/—— and vice versa.
    // Used only in the candidate relation below, not added to DashNormalize/IsIdentical.
    private static bool IsDashConfusableRune(int value) => IsDashRune(value) || value == '一';

    private static bool IsPureDashText(string s)
    {
        var t = s.Trim();
        if (t.Length == 0) return false;
        foreach (var r in t.EnumerateRunes())
        {
            if (!IsDashRune(r.Value)) return false;
        }
        return true;
    }

    private static string StripTrailingDashes(string s)
    {
        var runes = s.EnumerateRunes().ToArray();
        var end = runes.Length;
        while (end > 0 && IsDashRune(runes[end - 1].Value)) end--;
        return string.Concat(runes.Take(end).Select(r => r.ToString()));
    }

    private static string StripTrailingDashLikes(string s)
    {
        var runes = s.EnumerateRunes().ToArray();
        var end = runes.Length;
        while (end > 0 && IsDashConfusableRune(runes[end - 1].Value)) end--;
        return string.Concat(runes.Take(end).Select(r => r.ToString()));
    }

    private static bool IsDashTailDropoutCandidate(OcrCue left, OcrCue right)
    {
        // Real case 97-99: full caption ending dash(es) <-> pure dash fragment.
        // Only decides the candidate to call pixel; merge still has to result in Same.
        var leftPure = IsPureDashText(left.Text);
        var rightPure = IsPureDashText(right.Text);
        if (leftPure && rightPure) return true;
        if (leftPure && !rightPure)
        {
            var core = StripTrailingDashes(right.Text);
            return core.Length > 0 && right.Text.Length > core.Length;
        }
        if (!leftPure && rightPure)
        {
            var core = StripTrailingDashes(left.Text);
            return core.Length > 0 && left.Text.Length > core.Length;
        }
        return IsDashConfusableTailCandidate(left.Text, right.Text);
    }

    private static bool IsDashConfusableTailCandidate(string left, string right)
    {
        // Extended to 一 mixed with —/—— at sentence end (real case 130↔131: —— ↔ 一).
        // Lone 一 vs lone — is also a candidate (reviewed via pixel), not merged on its own.
        var leftRunes = left.EnumerateRunes().ToArray();
        var rightRunes = right.EnumerateRunes().ToArray();
        if (leftRunes.Length == 1 && rightRunes.Length == 1
            && IsDashConfusableRune(leftRunes[0].Value) && IsDashConfusableRune(rightRunes[0].Value))
            return true;
        var leftCore = StripTrailingDashLikes(left);
        var rightCore = StripTrailingDashLikes(right);
        if (leftCore.Length == 0 || !string.Equals(leftCore, rightCore, StringComparison.Ordinal)) return false;
        return left.Length > leftCore.Length || right.Length > rightCore.Length;
    }

    // Punctuation is stripped when comparing candidates (does not rewrite output): CJK U+3000-U+303F, fullwidth
    // U+FF00-U+FF0F, general punctuation U+2000-U+206F and the corresponding ASCII. No per-mark hardcoding
    // for fixtures; the canonical output is still chosen from cue evidence (SelectCanonical).
    private static bool IsPunctuationMark(int value) =>
        value is ',' or '.' or '!' or '?' or ';' or ':' or '\'' or '"' or '(' or ')' or '[' or ']' or '-' ||
        (value >= 0x2000 && value <= 0x206F) ||
        (value >= 0x3000 && value <= 0x303F) ||
        (value >= 0xFF00 && value <= 0xFF0F);

    private static string StripPunctuation(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (!IsPunctuationMark(rune.Value)) sb.Append(rune.ToString());
        }
        return sb.ToString();
    }

    private static bool IsPunctuationOnlyVariant(string left, string right)
    {
        // Equal after removing punctuation, with a total difference of ≤2 runes (small insert/delete/substitute).
        // Only PROPOSE a candidate for the pixel verifier; merge still requires PixelCheckResult.Same.
        var leftRunes = left.EnumerateRunes().ToArray();
        var rightRunes = right.EnumerateRunes().ToArray();
        if (Math.Abs(leftRunes.Length - rightRunes.Length) > 2) return false;
        var leftCore = StripPunctuation(left);
        var rightCore = StripPunctuation(right);
        if (leftCore.Length == 0) return false;
        return string.Equals(leftCore, rightCore, StringComparison.Ordinal);
    }

    // Single generic pass: temporal continuity + spatial continuity + PixelCheckResult.Same
    // merge. Chain A/B/C/... collapses throughout in a single pass (output[^1] chaining):
    // there is no separate A/B/A by text pattern anymore. Different/Unavailable/blocked/out-of-window
    // stay separated (+NeedsReview when temporally adjacent yet unmergeable). Independent of text.
    private static async Task<List<OcrCue>> MergeAdjacentAsync(IEnumerable<OcrCue> cues, string videoPath, IPixelComparer comparer, CancellationToken cancellationToken, double continuationWindow = Epsilon, Action<int, int>? onProgress = null)
    {
        var ordered = cues.ToArray();
        var totalBoundaries = Math.Max(0, ordered.Length - 1);
        var checkedBoundaries = 0;
        var output = new List<OcrCue>();
        // Chain flag (field 672-674, boundary 673→674): after the fast path merges one canonical
        // pair (强+強 → 强), the NEXT boundary is pure-exact (merged 强 + 674 强) but still in the same flicker
        // run. The flag lives WITHIN a single loop pass (no record saved, no checkpoint involvement):
        // an exact-touching boundary continuing a canonical run gets chained-merged, a standalone exact
        // boundary (timebase 一万年, replay ???) has flag=false so it still goes to pixel as the old pins require.
        var prevWasCanonicalMerge = false;
        foreach (var cue in ordered)
        {
            if (output.Count == 0) { output.Add(cue); continue; }
            var previous = output[^1];
            onProgress?.Invoke(checkedBoundaries + 1, totalBoundaries);
            checkedBoundaries++;
            // Canonical-touching fast path (field 672-674 强/強): text is equal after canonicalization
            // BUT DIFFERENT at dash level (IsIdentical false), touching/adjacent (gap<=0, no room
            // for a real blank to squeeze in) and sharing a text region with provenance, then it is one instance
            // that OCR flicker split apart — merge directly, WITHOUT pixel (do NOT call the comparer: EvaluateBoundaryAsync
            // below always calls pixel for every candidate, so this fast path must come BEFORE it).
            // Pixel compares the union-crop of boxes jittering between 2 reads and votes Different even on
            // the same caption, so it must never be the gatekeeper here. A real blank
            // (0.55s+ field evidence) always exceeds the epsilon touching so it cannot slip through. Dash-identical
            // (——/—) is DELIBERATELY EXCLUDED: OcrTimeBaseContract + OcrReplayContract pin Different→separated
            // for those (their own dash-dropout/micro-gap machinery is already tuned), do not touch. Other text
            // (junk 666, character swaps 设/没) fails IsCanonicalIdentical and takes the old pixel + review path.
            // Chain follow-on (boundary 673→674): after one fast-merge, output[^1] carries the winning spelling
            // (强) so the next boundary is pure-exact — but still in the same flicker run. prevWasCanonicalMerge
            // allows chained merging WITHOUT pixel; a standalone exact boundary (flag=false) still goes to pixel.
            var obsA = previous.LastObservation;
            var obsB = cue.FirstObservation;
            // Same-text fast path (policy 21/09, amended 22/09: ONLY micro-gap ≤0.25s):
            // byte-identical + gap ≤ IdenticalTextMicroGapSeconds (0.25s)
            // → merge directly, WITHOUT pixel + provenance. Reason: gap<0 is 2 lanes reading the
            // same caption (Ca Lop 19/09, 78 pairs); a 0..0.25s gap is one caption wrongly
            // split by the tracker — pixel on the union-crop used to vote Different wrongly
            // because of 1-2px box jitter (the comment below admits this), producing dozens
            // of missed duplicates in the SRT (touch0=12, ≤0.25s=33 across 6 exports).
            // Field-measured REAL blanks start from 0.325s+ (OcrGenericBoundaryProperty)
            // so they fall outside this range; dash-identical (——/—) is not byte-equal
            // so it still takes the old pixel path as the pinned contract requires.
            if (cue.Start - previous.End <= IdenticalTextMicroGapSeconds
                && previous.Text is { Length: > 0 }
                && string.Equals(previous.Text, cue.Text, StringComparison.Ordinal))
            {
                output[^1] = MergePair(previous, cue, previous.Text);
                prevWasCanonicalMerge = true;
                continue;
            }
            var canonicalPair = !IsIdentical(previous, cue) && IsCanonicalIdentical(previous, cue);
            var chainContinuation = prevWasCanonicalMerge
                && string.Equals(previous.Text, cue.Text, StringComparison.Ordinal);
            if (cue.Start - previous.End <= Epsilon
                && (canonicalPair || chainContinuation)
                && obsA is not null && obsB is not null
                && HasPixelProvenance(obsA) && HasPixelProvenance(obsB)
                && IsSpatiallyCompatible(obsA, obsB))
            {
                output[^1] = MergePair(previous, cue, SelectCanonical(previous, cue).Text);
                prevWasCanonicalMerge = true;
                continue;
            }
            prevWasCanonicalMerge = false;
            var eval = await EvaluateBoundaryAsync(previous, cue, videoPath, comparer, cancellationToken, continuationWindow);
            var (mergedCue, reviewBoth) = ResolveEvaluatedBoundary(previous, cue, eval);
            if (mergedCue is not null)
            {
                output[^1] = mergedCue;
            }
            else if (reviewBoth)
            {
                output[^1] = previous with { NeedsReview = true };
                output.Add(cue with { NeedsReview = true });
            }
            else
            {
                output.Add(cue);
            }
        }
        onProgress?.Invoke(totalBoundaries, totalBoundaries);
        return output;
    }

    // Shared verdict for the main pass and the retroactive post-Cleanup pass (no drift):
    // outside the window -> keep silent, no review; candidate + pixel Same -> merge cues;
    // everything else -> separate + review both.
    private static (OcrCue? Merged, bool ReviewBoth) ResolveEvaluatedBoundary(
        OcrCue previous, OcrCue cue, BoundaryEvaluation eval)
    {
        // Real blank (outside the continuation window): 2 visual instances, keep as-is with no review flag.
        if (!eval.GapWithinWindow) return (null, false);
        // Temporally adjacent but blocked by spatial/provenance, or pixel Different/Unavailable:
        // keep separated + NeedsReview on both.
        if (!eval.IsCandidate || eval.Pixel != PixelCheckResult.Same) return (null, true);
        var stableText = eval.DuplicateStableText;
        if (stableText is null)
        {
            var canonical = SelectCanonical(previous, cue);
            // DashNormalize is comparison-only: does not rewrite output Text.
            // If isDashDropout, keep the full caption (the longer side), do not let a dash fragment win on support.
            if (eval.IsDashDropout && !eval.IsIdentical && !eval.IsDuplicate && !eval.CanMergeFlicker)
            {
                var leftLen = previous.Text.EnumerateRunes().Count();
                var rightLen = cue.Text.EnumerateRunes().Count();
                stableText = leftLen >= rightLen ? previous.Text : cue.Text;
            }
            else
            {
                stableText = canonical.Text;
            }
        }
        // Provenance extended at both ends: First = left-most First, Last = right-most Last (including null, no backfill).
        // Support of disjoint fragments: summed. The tracker guarantees reconciler-input shares no observation
        // (cues touching/disjoint by Start/End); if there is overlap, this is an upper bound and still safe because
        // the merge decision is already pixel-gated and does not use support to merge.
        // NeedsReview OR-preservation: Same at this boundary does not remove reviews from other boundaries.
        return (MergePair(previous, cue, stableText), false);
    }

    // Retroactive post-Cleanup pass (18/09): Cleanup removes fragments/phantoms in the middle (or merges
    // 2 adjacent cues into 1), producing NEWLY adjacent cue pairs that pixel never verified —
    // field: 27 exact duplicate pairs with 0.067-0.3s gaps in the user's video (review=False,
    // pixel never ran). Only pixel-check the NEWLY adjacent pairs (compared against pre-cleanup adjacent pairs by
    // absolute (Start,Text) — Start is stable through Cleanup because merge keeps the left
    // Start and drop removes the whole cue); already-adjacent pairs do NOT get pixel re-checked (to avoid
    // overturning the main pass's decision). Only the review signal for gapped duplicate
    // pairs (micro-gap, 0.5s] applies to EVERY adjacent pair (including ones already adjacent before — the main
    // pass deliberately stays silent per spec; here we only add the flag so the user can listen and decide,
    // and no pixel call is made so the pinned spec is not violated).
    public static async Task<(IReadOnlyList<OcrCue> Cues, int Merges)> MergeCleanupAdjacentsAsync(
        IReadOnlyList<OcrCue> preCleanup,
        IReadOnlyList<OcrCue> postCleanup,
        string videoPath,
        IPixelComparer comparer,
        CancellationToken cancellationToken,
        double continuationWindow = Epsilon)
    {
        var prePairs = new HashSet<(double Start, string Text, double NextStart, string NextText)>();
        for (var index = 1; index < preCleanup.Count; index++)
        {
            var left = preCleanup[index - 1];
            var right = preCleanup[index];
            if (left is null || right is null) continue;
            prePairs.Add((left.Start, left.Text, right.Start, right.Text));
        }
        var output = new List<OcrCue>();
        var merges = 0;
        foreach (var cue in postCleanup)
        {
            if (cue is null) continue;
            if (output.Count == 0) { output.Add(cue); continue; }
            var previous = output[^1];
            // Exact duplicate pairs with a gap (micro-gap, 0.5s]: same text + a suspicious gap
            // (flicker or a genuine repeated line?) for which the spec forbids pixel-verify — flag review
// on both so the user can listen and decide (no blind merging, no silence).
            // Above 0.5s: genuine repeated dialogue, stay silent. Within the micro-gap: the main pass
            // already handled it (pixel/review), do not touch.
            // 21/09: the main pass already merged ≤0.25s. The (0.25s, 0.5s] range is review ONLY,
            // NOT merged — evidence of a real 0.325s blank (OcrGenericBoundaryProperty).
            var identicalGap = cue.Start - previous.End;
            if (string.Equals(previous.Text, cue.Text, StringComparison.Ordinal)
                && identicalGap > IdenticalTextMicroGapSeconds
                && identicalGap < IdenticalReviewGapSeconds)
            {
                output[^1] = previous with { NeedsReview = true };
                output.Add(cue with { NeedsReview = true });
                continue;
            }
            // Already adjacent before (same Start+Text next to each other) -> the main pass already handled it.
            if (prePairs.Contains((previous.Start, previous.Text, cue.Start, cue.Text)))
            {
                output.Add(cue);
                continue;
            }
            var eval = await EvaluateBoundaryAsync(previous, cue, videoPath, comparer, cancellationToken, continuationWindow);
            var (mergedCue, reviewBoth) = ResolveEvaluatedBoundary(previous, cue, eval);
            if (mergedCue is not null)
            {
                output[^1] = mergedCue;
                merges++;
            }
            else if (reviewBoth)
            {
                output[^1] = previous with { NeedsReview = true };
                output.Add(cue with { NeedsReview = true });
            }
            else
            {
                output.Add(cue);
            }
        }
        return (output, merges);
    }

    // Ear adjudicates WIDE-gap duplicate pairs (21/09, user's SRT duplicate report): the main
    // pass deliberately keeps identical-text pairs with gaps > 0.5s because they may be genuine repeated dialogue.
    // But static captions wrongly split by the tracker when characters were missed with gaps > 0.5s also live in that range.
    // Adjudication: an inner gap (0.5s, 8s] + text matching after normalization + ONE Whisper speech
    // segment covering the gap continuously (never any silence between the two text
    // appearances) → one caption displayed throughout, merge the span. A real silence gap (a long gap proving
    // the text turned off then on again — genuine repeated dialogue) → keep. No ear available → keep unchanged.
    public static IReadOnlyList<OcrCue> MergeSpeechCoveredIdentical(
        IReadOnlyList<OcrCue> cues,
        IReadOnlyList<EditorSpeechSegment>? speechSegments)
    {
        if (cues.Count < 2 || speechSegments is null || speechSegments.Count == 0)
            return cues;
        var output = new List<OcrCue>(cues.Count);
        var changed = false;
        foreach (var cue in cues)
        {
            if (output.Count > 0)
            {
                var previous = output[^1];
                var gap = cue.Start - previous.End;
                if (gap > IdenticalReviewGapSeconds && gap <= 8.0
                    && NormalizedEquals(previous.Text, cue.Text)
                    && SpeechCovers(previous.End, cue.Start))
                {
                    output[^1] = MergePair(previous, cue, previous.Text);
                    changed = true;
                    continue;
                }
            }
            output.Add(cue);
        }
        return changed ? output : cues;

        bool SpeechCovers(double from, double to) =>
            speechSegments.Any(segment => segment.Start <= from + 0.05 && segment.End >= to - 0.05);

        static bool NormalizedEquals(string left, string right) =>
            ChineseSubtitleNormalizer.TryNormalize(left, out var a)
            && ChineseSubtitleNormalizer.TryNormalize(right, out var b)
            && string.Equals(a, b, StringComparison.Ordinal);
    }

    private static bool IsShortSingleSubstitutionFlicker(OcrCue? left, OcrCue right)
    {
        if (left is null) return false;
        var leftDuration = left.End - left.Start;
        var rightDuration = right.End - right.Start;
        var touching = right.Start - left.End;
        // Cung window voi micro-gap identical (0.25s, vu p06 镇/鎮 khe 0.1s): vuot qua
        // thi pixel van gate nhu cu — chi mo rong vung candidate, khong tu merge.
        // Pixel tren union-crop loai nen (chi so sanh vung chu) nen cat canh + doi bien
        // the 1-rune van Same; khac that thi Different + review nhu cu.
        if (touching < -0.001 || touching > IdenticalTextMicroGapSeconds) return false;
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
