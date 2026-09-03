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

    // Legacy/no-pixel sync: không merge cross-cue, chỉ normalize + giữ tách (deterministic unit tests, không FFmpeg).
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

    // Production async: bắt buộc videoPath + comparer + CancellationToken. Thiếu => Unavailable => giữ tách + NeedsReview.
    // exactIdenticalMaxGap: window cho exact-identical fragmentation (mặc định Epsilon = chỉ touching).
    // Production luôn truyền 3.0/nativeFps (OcrScanner), không truyền tay số lớn: blank thật dài hơn
    // 3 native frame là reappearance (field evidence 0.667s), phải tách kể cả text giống hệt.
    public static async Task<IReadOnlyList<OcrCue>> MergeTouchingIdenticalAsync(IEnumerable<OcrCue> cues, string videoPath, IPixelComparer comparer, CancellationToken cancellationToken, double exactIdenticalMaxGap = Epsilon)
    {
        var normalized = new List<OcrCue>();
        foreach (var rawCue in cues.OrderBy(cue => cue.Start))
        {
            if (ChineseSubtitleNormalizer.TryNormalize(rawCue.Text, out var text))
                normalized.Add(rawCue with { Text = text });
        }
        var output = await MergeAdjacentAsync(normalized, allowFlicker: false, videoPath, comparer, cancellationToken, exactIdenticalMaxGap);
        await CollapseSandwichedSubstitutionsAsync(output, videoPath, comparer, cancellationToken);
        return await MergeAdjacentAsync(output, allowFlicker: true, videoPath, comparer, cancellationToken, exactIdenticalMaxGap);
    }

    // Giữ overload sync 3 tham số cho contract tests hiện tại (sẽ migrate sang Async). Sync không gọi FFmpeg, chỉ dùng comparer sync? Tạm thời: nếu comparer null/video rỗng => Unavailable.
    // Để không block thread pool, sync overload này KHÔNG gọi async comparer; nó chỉ dùng cho tests với fake sync (sẽ xóa sau khi migrate hết sang Async).
    public static IReadOnlyList<OcrCue> MergeTouchingIdentical(IEnumerable<OcrCue> cues, string? videoPath, IPixelComparer? comparer) => MergeTouchingIdentical(cues);

    private static async Task<PixelCheckResult> CheckBoundaryAsync(OcrCue left, OcrCue right, string? videoPath, IPixelComparer? comparer, CancellationToken cancellationToken)
        => (await CheckBoundaryDetailedAsync(left, right, videoPath, comparer, cancellationToken)).Result;

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

    // Provenance đầy đủ cho pixel verify: cả hai observation biên tồn tại, box hợp lệ,
    // SourcePts + timebase hợp lệ, Geometry exact đã lưu (không reconstruct từ Start/End).
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

    // Text + temporal candidacy, KHÔNG pixel. Dùng chung cho EvaluateBoundaryAsync (merge path)
    // và IsChainBoundary (tìm safe split cho live tail) nên rules không drift giữa 2 path.
    private sealed record TextCandidacy(
        bool IsCandidate,
        string Reason,
        double AllowedGapSeconds,
        bool IsIdentical,
        bool IsDashIdentical,
        bool IsPureExact,
        bool IsDuplicate,
        bool CanMergeFlicker,
        bool IsDashDropout,
        bool IsPunctVariant);

    private static TextCandidacy ClassifyTextCandidate(OcrCue previous, OcrCue cue, bool allowFlicker, double exactIdenticalMaxGap)
    {
        var canMergeFlicker = allowFlicker && IsShortSingleSubstitutionFlicker(previous, cue);
        var isIdentical = IsIdentical(previous, cue);
        var isDashIdentical = isIdentical && !string.Equals(previous.Text, cue.Text, StringComparison.Ordinal);
        var isPureExact = isIdentical && string.Equals(previous.Text, cue.Text, StringComparison.Ordinal);
        var isDuplicate = IsTransientAdjacentDuplicate(previous, cue, out _);
        var isDashDropout = IsDashTailDropoutCandidate(previous, cue);
        var isPunctVariant = IsPunctuationOnlyVariant(previous.Text, cue.Text);
        var isCandidate = isIdentical || isDuplicate || canMergeFlicker || isDashDropout || isPunctVariant;
        var allowedGap = isPureExact ? Math.Max(Epsilon, exactIdenticalMaxGap)
            : (canMergeFlicker || isDashIdentical || isDashDropout || isPunctVariant) ? FlickerGap
            : Epsilon;
        var reason = !isCandidate ? "none"
            : isPureExact ? "exact-identical"
            : isDuplicate ? "duplicate"
            : canMergeFlicker ? "flicker"
            : isDashDropout ? "dash-dropout"
            : isPunctVariant ? "punct-variant"
            : "dash-identical";
        return new TextCandidacy(isCandidate, reason, allowedGap, isIdentical, isDashIdentical,
            isPureExact, isDuplicate, canMergeFlicker, isDashDropout, isPunctVariant);
    }

    // Boundary là chain link theo đúng rules merge pass flicker (+ bare single-rune substitution
    // cho CollapseSandwiched A/B/A: triplet đó merge được qua boundary mà MergeAdjacent coi là
    // non-candidate khi fail flicker-duration). Live tail mở rộng ngược qua các link này để split
    // rơi vào safe boundary. Chỉ làm tail to hơn (re-reconcile thêm), không đổi quyết định merge.
    internal static bool IsChainBoundary(OcrCue previous, OcrCue cue, double exactIdenticalMaxGap = Epsilon)
    {
        var text = ClassifyTextCandidate(previous, cue, allowFlicker: true, exactIdenticalMaxGap);
        if (text.IsCandidate && cue.Start <= previous.End + text.AllowedGapSeconds) return true;
        return cue.Start <= previous.End + FlickerGap
            && IsSingleRuneSubstitution(previous.Text, cue.Text);
    }

    // Reconcile incremental: reconciledPrefix (view đã đúng tới offer trước) + freshCommitted mới.
    // Chỉ reconcile window cuối (tailWindow cue của prefix + toàn bộ fresh, mở rộng ngược qua chain
    // tới safe boundary); prefix trước split giữ nguyên. Split tại safe boundary (non-candidate)
    // cho kết quả đúng bằng full reconcile: merge chỉ xảy ra giữa 2 cue kề candidate, và
    // CollapseSandwiched cần inner candidate qua boundary (đã cover trong IsChainBoundary).
    internal static async Task<IReadOnlyList<OcrCue>> ReconcileLiveTailAsync(
        IReadOnlyList<OcrCue> reconciledPrefix,
        IReadOnlyList<OcrCue> freshCommitted,
        int tailWindow,
        string videoPath,
        IPixelComparer comparer,
        CancellationToken cancellationToken,
        double exactIdenticalMaxGap = Epsilon)
    {
        if (freshCommitted.Count == 0) return reconciledPrefix;
        var combined = reconciledPrefix.Concat(freshCommitted)
            .OrderBy(cue => cue.Start).ThenBy(cue => cue.End).ToArray();
        var keep = Math.Min(reconciledPrefix.Count, Math.Max(0, tailWindow));
        var start = reconciledPrefix.Count - keep;
        var guard = 0;
        while (start > 0 && guard < 64)
        {
            if (!IsChainBoundary(combined[start - 1], combined[start], exactIdenticalMaxGap)) break;
            start--;
            guard++;
        }
        var merged = await MergeTouchingIdenticalAsync(
            combined.Skip(start).ToArray(), videoPath, comparer, cancellationToken, exactIdenticalMaxGap);
        if (start == 0) return merged;
        return combined.Take(start).Concat(merged).ToArray();
    }

    // Đánh giá một boundary đúng logic MergeAdjacentAsync dùng để merge (candidate + gap + pixel).
    // Internal cho targeted range replay log; MergeAdjacentAsync gọi hàm này nên không drift.
    internal static async Task<BoundaryEvaluation> EvaluateBoundaryAsync(
        OcrCue previous,
        OcrCue cue,
        bool allowFlicker,
        string videoPath,
        IPixelComparer comparer,
        CancellationToken cancellationToken,
        double exactIdenticalMaxGap = Epsilon)
    {
        var text = ClassifyTextCandidate(previous, cue, allowFlicker, exactIdenticalMaxGap);
        var gap = cue.Start - previous.End;
        string? duplicateStable = null;
        var isDuplicateForText = text.IsDuplicate;
        if (isDuplicateForText) IsTransientAdjacentDuplicate(previous, cue, out duplicateStable);
        if (!text.IsCandidate || cue.Start > previous.End + text.AllowedGapSeconds)
            return new BoundaryEvaluation(text.IsCandidate, text.Reason, gap, text.AllowedGapSeconds, false, PixelCheckResult.Unavailable, null,
                duplicateStable, text.IsIdentical, text.IsDashIdentical, text.IsDuplicate, text.CanMergeFlicker, text.IsDashDropout, text.IsPunctVariant, text.IsPureExact);
        var comparison = await CheckBoundaryDetailedAsync(previous, cue, videoPath, comparer, cancellationToken);
        return new BoundaryEvaluation(true, text.Reason, gap, text.AllowedGapSeconds, true, comparison.Result, comparison,
            duplicateStable, text.IsIdentical, text.IsDashIdentical, text.IsDuplicate, text.CanMergeFlicker, text.IsDashDropout, text.IsPunctVariant, text.IsPureExact);
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

    private static bool IsDashRune(int value) =>
        value is '\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2014' or '\u2015' or '-' or '—';

    // 一 (U+4E00, một nét ngang) hay bị OCR đọc nhầm với —/—— và ngược lại.
    // Chỉ dùng trong candidate relation dưới đây, không đưa vào DashNormalize/IsIdentical.
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
        // Case thật 97-99: full caption ending dash(es) <-> pure dash fragment.
        // Chỉ quyết định candidate để gọi pixel, merge vẫn phải Same.
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
        // Mở rộng cho 一 lẫn với —/—— ở cuối câu (case thật 130↔131: —— ↔ 一).
        // Lone 一 vs lone — cũng là candidate (review qua pixel), không tự merge.
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

    // Dấu câu được strip khi so candidate (không rewrite output): CJK U+3000-U+303F, fullwidth
    // U+FF00-U+FF0F, general punctuation U+2000-U+206F và ASCII tương ứng. Không hardcode từng dấu
    // của fixture; canonical output vẫn chọn từ cue evidence (SelectCanonical).
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
        // Giống nhau sau khi bỏ punctuation, khác biệt tổng ≤2 rune (insert/delete/substitute dấu nhỏ).
        // Chỉ PROPOSE candidate cho pixel verifier; merge vẫn bắt buộc PixelCheckResult.Same.
        var leftRunes = left.EnumerateRunes().ToArray();
        var rightRunes = right.EnumerateRunes().ToArray();
        if (Math.Abs(leftRunes.Length - rightRunes.Length) > 2) return false;
        var leftCore = StripPunctuation(left);
        var rightCore = StripPunctuation(right);
        if (leftCore.Length == 0) return false;
        return string.Equals(leftCore, rightCore, StringComparison.Ordinal);
    }

    private static List<OcrCue> MergeAdjacent(IEnumerable<OcrCue> cues, bool allowFlicker = false, string? videoPath = null, IPixelComparer? comparer = null)
    {
        // Sync legacy: không gọi FFmpeg async, mọi candidate đều giữ tách + NeedsReview (safe-fail).
        var output = new List<OcrCue>();
        foreach (var cue in cues)
        {
            if (output.Count == 0) { output.Add(cue); continue; }
            var previous = output[^1];
            var canMergeFlicker = allowFlicker && IsShortSingleSubstitutionFlicker(previous, cue);
            var isIdentical = IsIdentical(previous, cue);
            var isDashIdentical = isIdentical && !string.Equals(previous.Text, cue.Text, StringComparison.Ordinal);
            string? duplicateStable = null;
            var isDuplicate = IsTransientAdjacentDuplicate(previous, cue, out duplicateStable);
            var isDashDropout = IsDashTailDropoutCandidate(previous, cue);
            var isPunctVariant = IsPunctuationOnlyVariant(previous.Text, cue.Text);
            var isCandidate = isIdentical || isDuplicate || canMergeFlicker || isDashDropout || isPunctVariant;
            if (!isCandidate) { output.Add(cue); continue; }
            var allowedGap = (canMergeFlicker || isDashIdentical || isDashDropout || isPunctVariant) ? FlickerGap : Epsilon;
            if (cue.Start > previous.End + allowedGap) { output.Add(cue); continue; }
            output[^1] = previous with { NeedsReview = true };
            output.Add(cue with { NeedsReview = true });
            continue;
        }
        return output;
    }

    private static async Task<List<OcrCue>> MergeAdjacentAsync(IEnumerable<OcrCue> cues, bool allowFlicker, string videoPath, IPixelComparer comparer, CancellationToken cancellationToken, double exactIdenticalMaxGap = Epsilon)
    {
        var output = new List<OcrCue>();
        foreach (var cue in cues)
        {
            if (output.Count == 0) { output.Add(cue); continue; }
            var previous = output[^1];
            var eval = await EvaluateBoundaryAsync(previous, cue, allowFlicker, videoPath, comparer, cancellationToken, exactIdenticalMaxGap);
            if (!eval.IsCandidate || !eval.GapWithinWindow) { output.Add(cue); continue; }
            // Mọi cross-cue merge bắt buộc PixelCheckResult.Same. Different/Unavailable giữ tách + NeedsReview.
            if (eval.Pixel != PixelCheckResult.Same)
            {
                output[^1] = previous with { NeedsReview = true };
                output.Add(cue with { NeedsReview = true });
                continue;
            }
            var stableText = eval.DuplicateStableText;
            if (stableText is null)
            {
                var canonical = SelectCanonical(previous, cue);
                // DashNormalize comparison-only: không rewrite output Text.
                // Nếu isDashDropout, giữ full caption (bên dài hơn), không để dash fragment thắng vì confidence.
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
            // Provenance mở rộng 2 đầu: First = left-most First, Last = right-most Last (kể cả null, không backfill).
            // Support disjoint fragments: cộng dồn. Tracker đảm bảo reconciler-input không share observation
            // (các cue touching/disjoint theo Start/End); nếu overlap, đây là upper-bound và vẫn an toàn vì
            // quyết định merge đã pixel-gate, không dùng support để merge.
            // NeedsReview OR-preservation: Same ở boundary này không xóa review từ boundary khác.
            output[^1] = previous with
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

    private static void CollapseSandwichedSubstitutions(List<OcrCue> cues, string? videoPath = null, IPixelComparer? comparer = null)
    {
        // Sync legacy safe-fail: mọi A/B/A candidate đều giữ tách + NeedsReview, không collapse (cần async pixel Same cả 2 boundary).
        for (var index = 1; index + 1 < cues.Count;)
        {
            var left = cues[index - 1];
            var middle = cues[index];
            var right = cues[index + 1];
            var touching = middle.Start <= left.End + .067 && right.Start <= middle.End + .067;
            var bounded = right.End - left.Start <= 2.0 && middle.End - middle.Start <= 1.25;
            var edgeFlicker = Math.Min(left.End - left.Start, right.End - right.Start) <= .35;
            var outerSame = IsIdentical(left, right) || IsPunctuationOnlyVariant(left.Text, right.Text) || (IsDashTailDropoutCandidate(left, middle) && IsDashTailDropoutCandidate(middle, right));
            var innerCandidate = IsSingleRuneSubstitution(left.Text, middle.Text) || IsDashTailDropoutCandidate(left, middle) || IsPunctuationOnlyVariant(left.Text, middle.Text);
            if (touching && bounded && edgeFlicker && outerSame && innerCandidate)
            {
                cues[index - 1] = left with { NeedsReview = true };
                cues[index] = middle with { NeedsReview = true };
                cues[index + 1] = right with { NeedsReview = true };
                index++;
                continue;
            }
            index++;
            continue;
        }
    }

    private static async Task CollapseSandwichedSubstitutionsAsync(List<OcrCue> cues, string videoPath, IPixelComparer comparer, CancellationToken cancellationToken)
    {
        for (var index = 1; index + 1 < cues.Count;)
        {
            var left = cues[index - 1];
            var middle = cues[index];
            var right = cues[index + 1];
            var touching = middle.Start <= left.End + .067 && right.Start <= middle.End + .067;
            // Không veto bằng tổng span A+B+C: cue A dài không được xóa candidate của một visual instance dài.
            // Candidate dựa vào từng boundary (touching) + middle ngắn (chống explosion) + quan hệ text.
            var middleBounded = middle.End - middle.Start <= 1.25;
            var edgeFlicker = Math.Min(left.End - left.Start, right.End - right.Start) <= .35;
            var outerSame = IsIdentical(left, right) || IsPunctuationOnlyVariant(left.Text, right.Text) || (IsDashTailDropoutCandidate(left, middle) && IsDashTailDropoutCandidate(middle, right));
            var innerCandidate = IsSingleRuneSubstitution(left.Text, middle.Text) || IsDashTailDropoutCandidate(left, middle) || IsPunctuationOnlyVariant(left.Text, middle.Text);
            if (touching && middleBounded && edgeFlicker && outerSame && innerCandidate)
            {
                var pixelAB = await CheckBoundaryAsync(left, middle, videoPath, comparer, cancellationToken);
                var pixelBC = await CheckBoundaryAsync(middle, right, videoPath, comparer, cancellationToken);
                if (pixelAB != PixelCheckResult.Same || pixelBC != PixelCheckResult.Same)
                {
                    cues[index - 1] = left with { NeedsReview = true };
                    cues[index] = middle with { NeedsReview = true };
                    cues[index + 1] = right with { NeedsReview = true };
                    index++;
                    continue;
                }
                // Giữ full caption cho dash-dropout, không để dash fragment thắng.
                string collapsedText = left.Text;
                if (IsDashTailDropoutCandidate(left, middle))
                {
                    collapsedText = left.Text.EnumerateRunes().Count() >= middle.Text.EnumerateRunes().Count() ? left.Text : middle.Text;
                    if (right.Text.EnumerateRunes().Count() > collapsedText.EnumerateRunes().Count()) collapsedText = right.Text;
                }
                cues[index - 1] = left with
                {
                    End = Math.Max(left.End, right.End),
                    Text = collapsedText,
                    Confidence = Math.Max(left.Confidence, right.Confidence),
                    RawSupportCount = left.RawSupportCount + middle.RawSupportCount + right.RawSupportCount,
                    ConfidentSupportCount = left.ConfidentSupportCount + middle.ConfidentSupportCount + right.ConfidentSupportCount,
                    FirstObservation = left.FirstObservation,
                    LastObservation = right.LastObservation,
                    NeedsReview = left.NeedsReview || middle.NeedsReview || right.NeedsReview,
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
