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
    // Cặp trùng y hệt hở (micro-gap, 0.5s]: spec ghim CẤM pixel-verify
    // (OcrExactGapRegression#11) nhưng im lặng là sai — gắn review để user
    // nghe và gộp/xóa tay (18/09: cặp 关于战神篇 hở đúng 0.3s của user).
    // Trên 0.5s coi như thoại lặp thật (dưới hẳn bằng chứng blank 0.55s+).
    private const double IdenticalReviewGapSeconds = 0.5;

    private static double AllowedGapSeconds(OcrCue previous, OcrCue cue, double continuationWindow) =>
        string.Equals(previous.Text, cue.Text, StringComparison.Ordinal)
            ? Math.Max(continuationWindow, IdenticalTextMicroGapSeconds)
            : continuationWindow;

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
    // Split tại non-chain boundary cho kết quả đúng bằng full reconcile: merge bắt buộc
    // candidate (temporal+spatial) + Pixel Same; không còn A/B/A riêng nên không có
    // hiệu ứng nào vượt qua boundary. Không phụ thuộc text.
    internal static bool IsChainBoundary(OcrCue previous, OcrCue cue, double continuationWindow = Epsilon)
    {
        if (cue.Start - previous.End > AllowedGapSeconds(previous, cue, continuationWindow) + Epsilon) return false;
        var obsA = previous.LastObservation;
        var obsB = cue.FirstObservation;
        if (obsA is null || obsB is null || !HasPixelProvenance(obsA) || !HasPixelProvenance(obsB)) return false;
        return IsSpatiallyCompatible(obsA, obsB);
    }

    // Reconcile incremental: reconciledPrefix (view đã đúng tới offer trước) + freshCommitted mới.
    // Chỉ reconcile window cuối (tailWindow cue của prefix + toàn bộ fresh, mở rộng ngược qua chain
    // generic tới safe boundary); prefix trước split giữ nguyên. Không phụ thuộc text.
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
    private static async Task<List<OcrCue>> MergeAdjacentAsync(IEnumerable<OcrCue> cues, string videoPath, IPixelComparer comparer, CancellationToken cancellationToken, double continuationWindow = Epsilon, Action<int, int>? onProgress = null)
    {
        var ordered = cues.ToArray();
        var totalBoundaries = Math.Max(0, ordered.Length - 1);
        var checkedBoundaries = 0;
        var output = new List<OcrCue>();
        // Chain flag (field 672-674, boundary 673→674): sau khi fast path gộp 1 cặp canonical
        // (强+強 → 强), boundary kế TIẾP là pure-exact (merged 强 + 674 强) nhưng vẫn cùng 1 run
        // flicker. Flag sống TRONG 1 lượt loop (không lưu record, không qua checkpoint):
        // boundary exact-touching nối tiếp run canonical thì gộp nối, boundary exact độc lập
        // (timebase 一万年, replay ???) flag=false nên vẫn đi pixel như các pin cũ ghim.
        var prevWasCanonicalMerge = false;
        foreach (var cue in ordered)
        {
            if (output.Count == 0) { output.Add(cue); continue; }
            var previous = output[^1];
            onProgress?.Invoke(checkedBoundaries + 1, totalBoundaries);
            checkedBoundaries++;
            // Canonical-touching fast path (field 672-674 强/強): chữ giống nhau sau canonical
            // NHƯNG KHÁC ở dash-level (IsIdentical false), dính/liền nhau (gap<=0, không có chỗ
            // cho blank thật chen vào) và cùng vùng chữ có provenance thì là 1 instance bị OCR
            // flicker chẻ ra — gộp thẳng, MIỄN pixel (KHÔNG gọi comparer: EvaluateBoundaryAsync
            // bên dưới luôn gọi pixel cho mọi candidate nên đặt fast path TRƯỚC nó).
            // Pixel so union-crop của box rung giữa 2 lần đọc và vote Different ngay cả trên
            // cùng caption, nên nó không bao giờ được làm người gác cửa ở đây. Blank thật
            // (0.55s+ field evidence) luôn vượt epsilon touching nên không lọt. Dash-identical
            // (——/—) CỐ Ý NGOẠI TRỪ: OcrTimeBaseContract + OcrReplayContract ghim Different→tách
            // cho chúng (machinery dash-dropout/micro-gap riêng đã tune), không đụng. Chữ khác
            // (rác 666, thế chữ 设/没) rớt IsCanonicalIdentical và đi đường pixel + review cũ.
            // Chain nối (boundary 673→674): sau 1 lần fast-merge, output[^1] mang chính tả thắng
            // (强) nên boundary kế là pure-exact — nhưng vẫn cùng run flicker. prevWasCanonicalMerge
            // cho phép gộp nối mà KHÔNG cần pixel; boundary exact độc lập (flag=false) vẫn pixel.
            var obsA = previous.LastObservation;
            var obsB = cue.FirstObservation;
            // Same-text fast path (chính sách 21/09 sửa 22/09: CHỈ micro-gap ≤0.25s):
            // byte-identical + khe ≤ IdenticalTextMicroGapSeconds (0.25s)
            // → gộp thẳng, MIỄN pixel + provenance. Lý do: gap<0 là 2 lane cùng
            // đọc 1 caption (Cả Lớp 19/09, 78 cặp); khe 0..0.25s là 1 caption bị
            // tracker chẻ oan — pixel trên union-crop từng vote Different oan
            // vì box rung 1-2px (comment dưới thừa nhận), đẻ ra hàng chục
            // cặp trùng sót trong SRT (touch0=12, ≤0.25s=33 trên 6 export).
            // Blank THẬT field đo được bắt đầu từ 0.325s+ (OcrGenericBoundaryProperty)
            // nên nằm ngoài vùng này; dash-identical (——/—) không phải byte-equal
            // nên vẫn đi đường pixel cũ như contract ghim.
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

    // Verdict dùng chung cho pass chính + pass hồi tố sau-Cleanup (không drift):
    // ngoài window -> giữ im, không review; candidate + pixel Same -> cue gộp;
    // còn lại -> tách + review cả hai.
    private static (OcrCue? Merged, bool ReviewBoth) ResolveEvaluatedBoundary(
        OcrCue previous, OcrCue cue, BoundaryEvaluation eval)
    {
        // Blank thật (ngoài continuation window): 2 visual instances, giữ nguyên không review flag.
        if (!eval.GapWithinWindow) return (null, false);
        // Temporal dính nhưng spatial/provenance block, hoặc pixel Different/Unavailable:
        // giữ tách + NeedsReview cả hai.
        if (!eval.IsCandidate || eval.Pixel != PixelCheckResult.Same) return (null, true);
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
        return (MergePair(previous, cue, stableText), false);
    }

    // Hồi tố sau-Cleanup (18/09): Cleanup xóa mảnh/phantom ở giữa (hoặc gộp
    // 2 cue kề thành 1) khiến 2 cue kề nhau MỚI mà pixel chưa từng verify —
    // field: 27 cặp trùng y hệt hở 0.067-0.3s trong video user (review=False,
    // pixel chưa chạy). Chỉ chấm pixel cặp kề-MỚI (so cặp kề pre-cleanup theo
    // (Start,Text) tuyệt đối — Start ổn định qua Cleanup vì merge giữ Start
    // trái, drop xóa nguyên cue); cặp đã kề thì KHÔNG gọi pixel lại (không
    // lật quyết định pass chính). Riêng tín hiệu review cho cặp trùng hở
    // (micro-gap, 0.5s] thì áp cho MỌI cặp kề (kể cả đã kề từ trước — pass
    // chính để im lặng theo spec, ở đây chỉ thêm cờ cho user nghe và quyết,
    // không gọi pixel nên không phạm spec đã ghim).
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
            // Cặp trùng y hệt hở (micro-gap, 0.5s]: cùng chữ + hở nghi vấn
            // (flicker hay hô lặp thật?) mà spec cấm pixel-verify — gắn review
// cả hai để user nghe và quyết (không gộp bừa, không im lặng).
            // Ngoài 0.5s: thoại lặp thật, giữ im. Trong micro-gap: pass chính
            // đã xử (pixel/review), không đụng.
            // 21/09: pass chính đã gộp ≤0.25s. Vùng (0.25s, 0.5s] CHỈ review,
            // KHÔNG gộp — evidence 0.325s blank thật (OcrGenericBoundaryProperty).
            var identicalGap = cue.Start - previous.End;
            if (string.Equals(previous.Text, cue.Text, StringComparison.Ordinal)
                && identicalGap > IdenticalTextMicroGapSeconds
                && identicalGap < IdenticalReviewGapSeconds)
            {
                output[^1] = previous with { NeedsReview = true };
                output.Add(cue with { NeedsReview = true });
                continue;
            }
            // Đã kề từ trước (cùng Start+Text kề nhau) -> pass chính đã xử.
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

    // Tai phân xử cặp trùng khe RỘNG (21/09, báo cáo lặp SRT của user): pass
    // chính cố ý giữ cặp chữ-y-hệt khe > 0.5s vì có thể là thoại lặp thật.
    // Nhưng caption tĩnh bị tracker chẻ oan khi hụt chữ > 0.5s cũng ở vùng đó.
    // Phân xử: khe trong (0.5s, 8s] + chữ khớp sau normalize + MỘT đoạn thoại
    // Whisper phủ liên tục qua khe (không từng có khoảng im giữa hai lần hiện
    // chữ) → 1 caption hiển thị suốt, gộp span. Khe im thật (kéo dài chứng tỏ
    // chữ tắt rồi bật lại — thoại lặp thật) → giữ. Không có tai → giữ nguyên.
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
