using System.Drawing;
using System.Text;

namespace BiliSubStudio.Core.Ocr;

public static class OcrCueReconciler
{
    // Recovered full/short/full readings may leave separate touching cues with
    // the same final spelling or one transient substitution. Never bridge a
    // real blank or merge a one-way similar caption without temporal consensus.
    private const double Epsilon = 0.001;

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
    // continuationWindow: visual-continuation window cho MỌI boundary (mặc định Epsilon = chỉ touching/overlap).
    // Production luôn truyền 3.0/nativeFps (OcrScanner), không truyền tay số lớn: blank thật dài hơn
    // 3 native frame là instance mới (field evidence 0.667s/0.55s), phải tách kể cả text giống hệt.
    // Không phụ thuộc text pattern: temporal continuity + spatial continuity + Pixel Same mới merge.
    public static async Task<IReadOnlyList<OcrCue>> MergeTouchingIdenticalAsync(IEnumerable<OcrCue> cues, string videoPath, IPixelComparer comparer, CancellationToken cancellationToken, double continuationWindow = Epsilon)
    {
        var normalized = new List<OcrCue>();
        foreach (var rawCue in cues.OrderBy(cue => cue.Start))
        {
            if (ChineseSubtitleNormalizer.TryNormalize(rawCue.Text, out var text))
                normalized.Add(rawCue with { Text = text });
        }
        return await MergeAdjacentAsync(normalized, videoPath, comparer, cancellationToken, continuationWindow);
    }

    // Giữ overload sync 3 tham số cho contract tests hiện tại (sẽ migrate sang Async). Sync không gọi FFmpeg, chỉ dùng comparer sync? Tạm thời: nếu comparer null/video rỗng => Unavailable.
    // Để không block thread pool, sync overload này KHÔNG gọi async comparer; nó chỉ dùng cho tests với fake sync (sẽ xóa sau khi migrate hết sang Async).
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

    // Generic visual-boundary candidacy: temporal continuity (gap trong continuation window
    // tính bằng native frames, KHÔNG sampling Hz) + spatial continuity (cùng subtitle location,
    // provenance thật). KHÔNG yêu cầu text phải exact/substitution/punctuation/dash-like.
    // Text helpers cũ chỉ cho CandidateReason diagnostic, không phải coverage bắt buộc:
    // boundary hợp lệ temporal+spatial luôn có đường tới pixel verifier dù text là gì.
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
        if (!(gap <= continuationWindow))
            return new VisualCandidacy(false, false, false, "non-temporal",
                false, false, false, false, false, false, false);
        var obsA = previous.LastObservation;
        var obsB = cue.FirstObservation;
        if (obsA is null || obsB is null || !HasPixelProvenance(obsA) || !HasPixelProvenance(obsB)
            || !IsSpatiallyCompatible(obsA, obsB))
            return new VisualCandidacy(true, true, false, "spatial-or-provenance-blocked",
                false, false, false, false, false, false, false);
        // Diagnostic subtype: text pattern cũ chỉ để trace/prioritize, không gate.
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

    // Cheap spatial filter trong crop space (cùng Geometry mới so được, không reconstruct).
    // Chỉ loại mismatch rõ ràng (khác vùng subtitle); mọi thứ còn lại để pixel quyết định.
    // Không merge dựa trên bbox.
    internal static bool IsSpatiallyCompatible(PixelObservation left, PixelObservation right)
    {
        var geometry = left.Geometry;
        if (geometry is null || right.Geometry is null || !geometry.Equals(right.Geometry)) return false;
        var rectA = ContentClamped(left.BoxInOcrCrop, geometry);
        var rectB = ContentClamped(right.BoxInOcrCrop, geometry);
        if (rectA.IsEmpty || rectB.IsEmpty) return false;
        var minHeight = Math.Min(rectA.Height, rectB.Height);
        if (minHeight <= 0) return false;
        var overlap = Math.Max(0, Math.Min(rectA.Bottom, rectB.Bottom) - Math.Max(rectA.Top, rectB.Top));
        if (overlap / (double)minHeight < 0.5) return false;
        var centerDistance = Math.Abs((rectA.Top + rectA.Bottom) / 2.0 - (rectB.Top + rectB.Bottom) / 2.0);
        if (centerDistance > 0.75 * Math.Max(rectA.Height, rectB.Height)) return false;
        var ratio = rectA.Height / (double)rectB.Height;
        return ratio >= 0.5 && ratio <= 2.0;
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
    // Split tại non-chain boundary cho kết quả đúng bằng full reconcile: merge bắt buộc
    // candidate (temporal+spatial) + Pixel Same; không còn A/B/A riêng nên không có
    // hiệu ứng nào vượt qua boundary. Không phụ thuộc text.
    internal static bool IsChainBoundary(OcrCue previous, OcrCue cue, double continuationWindow = Epsilon)
    {
        if (cue.Start - previous.End > continuationWindow) return false;
        var obsA = previous.LastObservation;
        var obsB = cue.FirstObservation;
        if (obsA is null || obsB is null || !HasPixelProvenance(obsA) || !HasPixelProvenance(obsB)) return false;
        return IsSpatiallyCompatible(obsA, obsB);
    }

    // Reconcile incremental: reconciledPrefix (view đã đúng tới offer trước) + freshCommitted mới.
    // Chỉ reconcile window cuối (tailWindow cue của prefix + toàn bộ fresh, mở rộng ngược qua chain
    // generic tới safe boundary); prefix trước split giữ nguyên. Không phụ thuộc text.
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
        var start = reconciledPrefix.Count - keep;
        var guard = 0;
        while (start > 0 && guard < 64)
        {
            if (!IsChainBoundary(combined[start - 1], combined[start], continuationWindow)) break;
            start--;
            guard++;
        }
        var merged = await MergeTouchingIdenticalAsync(
            combined.Skip(start).ToArray(), videoPath, comparer, cancellationToken, continuationWindow);
        if (start == 0) return merged;
        return combined.Take(start).Concat(merged).ToArray();
    }

    // Đánh giá một boundary đúng logic MergeAdjacentAsync dùng để merge:
    // generic visual candidate (temporal+spatial) + pixel gate. MergeAdjacentAsync gọi hàm này
    // nên trace log và production không drift. Không phụ thuộc text pattern.
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

    private static OcrCue SelectCanonical(OcrCue a, OcrCue b)
    {
        // Evidence toàn instance, không biến text: ConfidentSupportCount trước,
        // rồi RawSupportCount, confidence, duration. Normalization chỉ dùng comparison.
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

    // Single generic pass: temporal continuity + spatial continuity + PixelCheckResult.Same
    // mới merge. Chain A/B/C/... collapse xuyên suốt trong một pass (output[^1] chaining):
    // không còn A/B/A riêng theo text pattern. Different/Unavailable/blocked/out-of-window
    // giữ tách (+NeedsReview khi temporal dính mà không merge được). Không phụ thuộc text.
    private static async Task<List<OcrCue>> MergeAdjacentAsync(IEnumerable<OcrCue> cues, string videoPath, IPixelComparer comparer, CancellationToken cancellationToken, double continuationWindow = Epsilon)
    {
        var output = new List<OcrCue>();
        foreach (var cue in cues)
        {
            if (output.Count == 0) { output.Add(cue); continue; }
            var previous = output[^1];
            var eval = await EvaluateBoundaryAsync(previous, cue, videoPath, comparer, cancellationToken, continuationWindow);
            // Blank thật (ngoài continuation window): 2 visual instances, giữ nguyên không review flag.
            if (!eval.GapWithinWindow) { output.Add(cue); continue; }
            // Temporal dính nhưng spatial/provenance block, hoặc pixel Different/Unavailable:
            // giữ tách + NeedsReview cả hai.
            if (!eval.IsCandidate || eval.Pixel != PixelCheckResult.Same)
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
                // Nếu isDashDropout, giữ full caption (bên dài hơn), không để dash fragment thắng vì support.
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
