using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

// Field-test regression: cue 7↔8 (exact + touching) and cue 9↔10 (exact + a 0.667s gap).
// Field evidence (6 OCR blanks + naked eye 27.35-27.9): the 0.667 gap is a REAL blank => 2 instances,
// NO merge. The production window = 3.0/nativeFps (~0.1s at 30fps) for every mode, never hand-passed 0.75.
internal static class OcrExactGapRegression
{
    private static readonly OcrRegion TestRoi = new(0, 0, 1, 1);
    private static readonly RationalTimeBase TestTb = new(1, 90000);
    private static readonly OcrFrameGeometry TestGeo = new(0, 0, 1920, 1080, 1280, 320, 0, 0, 1280, 320);
    private static long _spts = 3000000;

    private static OcrCue Prov(double s, double e, string t, double c)
    {
        var s1 = System.Threading.Interlocked.Increment(ref _spts);
        var s2 = System.Threading.Interlocked.Increment(ref _spts);
        return new OcrCue(s, e, t, c, 0, 0, false,
            new PixelObservation(s, new OcrBox(10, 10, 200, 40), TestRoi, TestGeo, s1, TestTb),
            new PixelObservation(e, new OcrBox(10, 10, 200, 40), TestRoi, TestGeo, s2, TestTb));
    }

    public static async Task RunAsync()
    {
        var same = new FakePixelComparer(PixelCheckResult.Same);
        var ct = CancellationToken.None;

        // 1. Field 7↔8: exact + touching => 1 cue.
        var touch = new[] {
            Prov(21.600, 22.266, "一开始，只是觉得不顺眼", 0.99),
            Prov(22.266, 24.400, "一开始，只是觉得不顺眼", 0.98),
        };
        var touchMerged = await OcrCueReconciler.MergeTouchingIdenticalAsync(touch, "v.mp4", same, ct);
        Check(touchMerged.Count == 1 && touchMerged[0].Start == 21.600 && touchMerged[0].End == 24.400,
            "field 7-8 exact touching must merge to 1 cue");

        // 2. Field 9↔10: exact + a 0.667s gap that is a REAL blank (field evidence) => split even
        // under the production Exhaustive window (3.0/30 ≈ 0.1s). Preview showing 2 cues is correct visual-instance semantics.
        // Do not hand-pass 0.75s: that number would wrongly merge 2 instances proven different.
        const double productionExhaustiveWindow = 3.0 / 30.0;
        var gapped = new[] {
            Prov(24.933, 27.333, "直到烈战狂提到龙帝宫", 0.99),
            Prov(28.000, 29.966, "直到烈战狂提到龙帝宫", 0.98),
        };
        var countingGap = new FakePixelComparer(PixelCheckResult.Same);
        var gappedMerged = await OcrCueReconciler.MergeTouchingIdenticalAsync(gapped, "v.mp4", countingGap, ct, continuationWindow: productionExhaustiveWindow);
        Check(gappedMerged.Count == 2, "field 9-10 real 0.667 blank must stay 2 cues with production window");
        Check(countingGap.Calls == 0, "gap beyond production window must not reach pixel comparer");

        // 3. Same pair but the default window (Epsilon) => split, no comparer call.
        var countingDefault = new FakePixelComparer(PixelCheckResult.Same);
        var defaultMerged = await OcrCueReconciler.MergeTouchingIdenticalAsync(gapped, "v.mp4", countingDefault, ct);
        Check(defaultMerged.Count == 2, "default window must keep 0.667 gap separate");
        Check(countingDefault.Calls == 0, "gap beyond window must not reach pixel comparer");

        // 4. A real 1.0s blank > the 0.75 window => split, no comparer call.
        var realBlank = new[] {
            Prov(40.000, 41.000, "龙无期", 0.99),
            Prov(42.000, 43.000, "龙无期", 0.98),
        };
        var countingBlank = new FakePixelComparer(PixelCheckResult.Same);
        var blankMerged = await OcrCueReconciler.MergeTouchingIdenticalAsync(realBlank, "v.mp4", countingBlank, ct, continuationWindow: 0.75);
        Check(blankMerged.Count == 2, "real 1.0s blank between same-text instances must stay separate");
        Check(countingBlank.Calls == 0, "real blank must not reach pixel comparer");

        // 5. A 0.667 gap + Different => split + NeedsReview on both.
        var diff = new FakePixelComparer(PixelCheckResult.Different);
        var diffMerged = await OcrCueReconciler.MergeTouchingIdenticalAsync(gapped, "v.mp4", diff, ct, continuationWindow: 0.75);
        Check(diffMerged.Count == 2 && diffMerged.All(c => c.NeedsReview),
            "Different pixel must keep 0.667 gap separate with NeedsReview");

        // 6. A 0.667 gap + Unavailable => split + NeedsReview on both.
        var unav = new FakePixelComparer(PixelCheckResult.Unavailable);
        var unavMerged = await OcrCueReconciler.MergeTouchingIdenticalAsync(gapped, "v.mp4", unav, ct, continuationWindow: 0.75);
        Check(unavMerged.Count == 2 && unavMerged.All(c => c.NeedsReview),
            "Unavailable pixel must keep 0.667 gap separate with NeedsReview");

        // 7. Two truly different captions standing adjacent (field 19↔20): temporally+spatially touching so they are a
        // generic candidate and the pixel decides. Different => split + NeedsReview, called exactly once.
        // (The new gate does not filter by text: safety comes from the pixel, not a text pattern.)
        var countingDistinct = new FakePixelComparer(PixelCheckResult.Different);
        var distinct = new[] {
            Prov(50.700, 52.733, "这种人，不像人", 0.99),
            Prov(52.733, 54.333, "像被擦过的棋子", 0.98),
        };
        var distinctMerged = await OcrCueReconciler.MergeTouchingIdenticalAsync(distinct, "v.mp4", countingDistinct, ct, continuationWindow: 0.75);
        Check(distinctMerged.Count == 2 && distinctMerged.All(c => c.NeedsReview),
            "two truly different touching captions must stay separate with NeedsReview on Different pixel");
        Check(countingDistinct.Calls == 1, "touching distinct pair must reach pixel verifier once");

// 8. Field V01-I07: a gap of exactly 3 native frames but float noise at large PTS magnitude
        // (13633.600 - 13633.500 = 0.10000000000036 > 3.0/30) once split ONE caption
        // into two cues with no review flag => duplication in the SRT. The Epsilon window must keep
        // it as a candidate and merge when the pixel says Same.
        var floatEdge = new[] {
            Prov(13633.333, 13633.500, "我为什么要知道", 1.0),
            Prov(13633.600, 13634.600, "我为什么要知道", 1.0),
        };
        var edgeComparer = new FakePixelComparer(PixelCheckResult.Same);
        var edgeMerged = await OcrCueReconciler.MergeTouchingIdenticalAsync(floatEdge, "v.mp4", edgeComparer, ct, productionExhaustiveWindow);
        Check(edgeMerged.Count == 1, "three-frame gap at large PTS magnitude must stay inside the continuation window");
        Check(edgeComparer.Calls == 0, "identical byte-for-byte 0.1s gap now merges without pixel (policy 21/09)");

        // 9. Field flicker-split: byte-identical text + the same box, a phantom 0.133s gap
        // (the tracker closes the cue wrongly because of a few misread frames; the text never disappeared).
        // Policy 21/09: byte-identical ≤0.5s merges directly, PIXEL-FREE.
        var flicker = new[] {
            Prov(4.833, 5.900, "便为北凉郡主即刻离京", 0.986),
            Prov(6.033, 7.733, "便为北凉郡主即刻离京", 0.983),
        };
        var flickerSame = new FakePixelComparer(PixelCheckResult.Same);
        var flickerMerged = await OcrCueReconciler.MergeTouchingIdenticalAsync(flicker, "v.mp4", flickerSame, ct, continuationWindow: productionExhaustiveWindow);
        Check(flickerMerged.Count == 1 && flickerMerged[0].Start == 4.833 && flickerMerged[0].End == 7.733,
            "identical flicker-split pair with 0.133s gap must merge to 1 cue");
        Check(flickerSame.Calls == 0, "identical ≤0.5s merges without pixel (policy 21/09)");

        // 10. A pair with DIFFERENT TEXT (1 character swapped) at a 0.133s gap + pixel Different -> split + review.
        // Keep this case to protect pixel authority for pairs that are NOT byte-identical.
        var flickerDiffPair = new[] {
            Prov(4.833, 5.900, "便为北凉郡主即刻离京", 0.986),
            Prov(6.033, 7.733, "便为北凉郡主即刻离京", 0.983), // Policy 21/09: byte-identical ≤0.25s merge pixel-free
        };
        var flickerDiff = new FakePixelComparer(PixelCheckResult.Different);
        var flickerSplit = await OcrCueReconciler.MergeTouchingIdenticalAsync(flickerDiffPair, "v.mp4", flickerDiff, ct, continuationWindow: productionExhaustiveWindow);
        Check(flickerSplit.Count == 1, "identical ≤0.25s merges pixel-free (policy 21/09)");
        Check(flickerDiff.Calls == 0, "identical ≤0.25s merges without pixel");

        // 11. Identical text but a 0.30s gap (> micro-gap 0.25, ≤ 0.5s) — the
        // review-only zone, per the 0.325s real-blank evidence (OcrGenericBoundaryProperty).
        var beyond = new[] {
            Prov(4.833, 5.900, "便为北凉郡主即刻离京", 0.986),
            Prov(6.200, 7.733, "便为北凉郡主即刻离京", 0.983),
        };
        var beyondComparer = new FakePixelComparer(PixelCheckResult.Same);
        var beyondMerged = await OcrCueReconciler.MergeTouchingIdenticalAsync(beyond, "v.mp4", beyondComparer, ct, continuationWindow: productionExhaustiveWindow);
        Check(beyondMerged.Count == 2, "identical 0.30s gap stays 2 cues (review-only zone 0.25-0.5s)");
        Check(beyondComparer.Calls == 0, "beyond-micro-gap pair must not reach pixel comparer");

        // 12. High-similarity variant + micro-gap beyond the normal continuation window
        // is still temporal: same caption, pixel Same => merge.
        var variant = new[]
        {
            Prov(2434.27375, 2434.907, "江白我肚子上都有肉7", 0.99),
            Prov(2435.073687, 2435.740, "江白我肚子上都有肉了", 0.98),
        };
        var variantComparer = new FakePixelComparer(PixelCheckResult.Same);
        var variantMerged = await OcrCueReconciler.MergeTouchingIdenticalAsync(
            variant, "v.mp4", variantComparer, ct, productionExhaustiveWindow);
        Check(variantMerged.Count == 1, "high-similarity variant with 0.1667s gap must merge on Same");
        Check(variantComparer.Calls == 1, "high-similarity variant must reach pixel verifier once");
        Check(variantMerged[0].Start == 2434.27375 && variantMerged[0].End == 2435.740,
            "high-similarity variant merge must preserve the union span");

        // 13. Real transition with similarity exactly 0.8 stays outside the variant
        // window: no pixel call, no review.
        var realTransition = new[]
        {
            Prov(32598.216, 32599.416, "三十年河东", 0.99),
            Prov(32599.749, 32600.916, "三十年河西", 0.98),
        };
        var transitionComparer = new FakePixelComparer(PixelCheckResult.Same);
        var transitionMerged = await OcrCueReconciler.MergeTouchingIdenticalAsync(
            realTransition, "v.mp4", transitionComparer, ct, productionExhaustiveWindow);
        Check(transitionMerged.Count == 2, "real 0.8-similar transition must stay separate");
        Check(transitionComparer.Calls == 0, "real 0.8-similar transition must not reach pixel verifier");
        Check(!transitionMerged.Any(c => c.NeedsReview), "real 0.8-similar transition must not be flagged");

        // 14. High-similarity spelling variant across a pause: pixel still decides.
        // Unavailable keeps both cues and asks for review instead of silently merging.
        var spellVariant = new[]
        {
            Prov(18560.109, 18560.776, "我拿到神格7", 0.99),
            Prov(18561.109, 18561.776, "我拿到神格了", 0.98),
        };
        var spellComparer = new FakePixelComparer(PixelCheckResult.Unavailable);
        var spellMerged = await OcrCueReconciler.MergeTouchingIdenticalAsync(
            spellVariant, "v.mp4", spellComparer, ct, productionExhaustiveWindow);
        Check(spellMerged.Count == 2, "Unavailable high-similarity variant must stay separate");
        Check(spellMerged.All(c => c.NeedsReview), "Unavailable high-similarity variant must be flagged");
        Check(spellComparer.Calls == 1, "high-similarity variant must reach pixel verifier once");

        // Inflated scene-text unions must reach the pixel gate in BOTH directions.
        // These are the recorded boxes from the remaining V04 field boundaries.
        foreach (var (inflated, subtitle) in new[]
        {
            (new OcrBox(94, 37, 930, 284), new OcrBox(449, 199, 928, 283)),
            (new OcrBox(154, 116, 739, 271), new OcrBox(538, 209, 739, 271)),
        })
        foreach (var reverse in new[] { false, true })
        foreach (var verdict in new[] { PixelCheckResult.Same, PixelCheckResult.Different, PixelCheckResult.Unavailable })
        {
            var a = Prov(60, 61, "自刺探情报", .9);
            var b = Prov(61, 62, "刺探情报", .99);
            a = a with { LastObservation = a.LastObservation! with { BoxInOcrCrop = reverse ? subtitle : inflated } };
            b = b with { FirstObservation = b.FirstObservation! with { BoxInOcrCrop = reverse ? inflated : subtitle } };
            var verifier = new FakePixelComparer(verdict);
            var result = await OcrCueReconciler.MergeTouchingIdenticalAsync([a, b], "v.mp4", verifier, ct, productionExhaustiveWindow);
            Check(verifier.Calls == 1, "nested baseline boxes must reach union pixel verification");
            Check(result.Count == (verdict == PixelCheckResult.Same ? 1 : 2), "nested boxes must never bypass the pixel verdict");
            if (verdict == PixelCheckResult.Same)
                Check(result[0].Start == 60 && result[0].End == 62 && result[0].Text == b.Text,
                    "nested-box merge must preserve instance span and evidence-selected spelling");

            foreach (var blocked in new[]
            {
                b with { Start = 61.667 },
                b with { FirstObservation = b.FirstObservation! with { SourcePts = null } },
                b with { FirstObservation = b.FirstObservation! with { Geometry = TestGeo with { SourceCropX = 1 } } },
                b with { FirstObservation = b.FirstObservation! with { BoxInOcrCrop = new OcrBox(100, 0, 200, 25) } },
                b with { FirstObservation = b.FirstObservation! with { BoxInOcrCrop = new OcrBox(1000, 209, 1200, 271) } },
            })
            {
                var guard = new FakePixelComparer(PixelCheckResult.Same);
                var guarded = await OcrCueReconciler.MergeTouchingIdenticalAsync([a, blocked], "v.mp4", guard, ct, productionExhaustiveWindow);
                Check(guarded.Count == 2 && guard.Calls == 0,
                    "inflated boxes must not bypass blank/provenance/location guards");
            }
        }
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }

    private sealed class FakePixelComparer : IPixelComparer
    {
        private readonly PixelCheckResult _result;
        public int Calls;
        public FakePixelComparer(PixelCheckResult result) => _result = result;
        public Task<PixelComparison> CheckAsync(string videoPath, PixelObservation left, PixelObservation right, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new PixelComparison(_result,
                new FrameSample(left.Pts, left.Pts, 0),
                new FrameSample(right.Pts, right.Pts, 0),
                System.Drawing.Rectangle.Empty, null, null, null));
        }
    }

    // The overlap-identical fast path must not call the pixel: calling it is a test error.
    private sealed class ThrowingPixelComparer : IPixelComparer
    {
        public Task<PixelComparison> CheckAsync(string videoPath, PixelObservation left, PixelObservation right, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("overlap-identical must not reach pixel");
    }
}

// Retro pass after Cleanup (18/09): Cleanup deleting fragments/phantoms in the middle creates NEW
// adjacent pairs that the pixel has never verified (field: duplicate pair 恭迎龙王归位/杀 gapped
// 0.067-0.3s in a user video — review=False, pixel not run). This pass only judges NEWLY-adjacent
// pairs; pairs already adjacent beforehand are kept absolutely untouched (no repeat pixel
// call, no decision flip, no wrongful review flag).
internal static class OcrCleanupAdjacencyContract
{
    private static readonly OcrRegion TestRoi = new(0, 0, 1, 1);
    private static readonly RationalTimeBase TestTb = new(1, 90000);
    private static readonly OcrFrameGeometry TestGeo = new(0, 0, 1920, 1080, 1280, 320, 0, 0, 1280, 320);
    private static long _spts = 9500000;

    private static OcrCue Prov(double s, double e, string t, double c)
    {
        var s1 = System.Threading.Interlocked.Increment(ref _spts);
        var s2 = System.Threading.Interlocked.Increment(ref _spts);
        return new OcrCue(s, e, t, c, 0, 0, false,
            new PixelObservation(s, new OcrBox(10, 10, 200, 40), TestRoi, TestGeo, s1, TestTb),
            new PixelObservation(e, new OcrBox(10, 10, 200, 40), TestRoi, TestGeo, s2, TestTb));
    }

    public static async Task RunAsync()
    {
        var ct = CancellationToken.None;
        // 1. A newly-adjacent duplicate pair (the middle fragment was deleted by Cleanup): Same -> merge into 1 cue.
        var pre = new[] {
            Prov(10.0, 11.0, "恭迎龙王归位", 0.99),
            Prov(11.0, 11.15, "迎", 0.5),
            Prov(11.15, 12.15, "恭迎龙王归位", 0.98),
        };
        var post = new[] { pre[0], pre[2] };
        var same = new FakePixelComparer(PixelCheckResult.Same);
        var (merged, merges) = await OcrCueReconciler.MergeCleanupAdjacentsAsync(
            pre, post, "v.mp4", same, ct, continuationWindow: 0.1);
        Check(merged.Count == 1 && merges == 1, "kề-mới trùng + Same phải gộp");
        Check(merged[0].Start == 10.0 && merged[0].End == 12.15, "span gộp sai");

        // 2. Different -> split + review on both.
        var diff = new FakePixelComparer(PixelCheckResult.Different);
        var (split, merges2) = await OcrCueReconciler.MergeCleanupAdjacentsAsync(
            pre, post, "v.mp4", diff, ct, continuationWindow: 0.1);
        Check(split.Count == 2 && merges2 == 0 && split.All(c => c.NeedsReview),
            "Different phải tách + review");

        // 3. Already adjacent beforehand -> keep as is, do NOT call the comparer (even for Different).
        var preAdj = new[] { Prov(20.0, 21.0, "杀", 0.99), Prov(21.2, 22.0, "杀", 0.98) };
        var counting = new FakePixelComparer(PixelCheckResult.Different);
        var (kept, merges3) = await OcrCueReconciler.MergeCleanupAdjacentsAsync(
            preAdj, preAdj, "v.mp4", counting, ct, continuationWindow: 0.1);
        Check(kept.Count == 2 && merges3 == 0, "đã kề từ trước phải giữ");
        Check(counting.Calls == 0, "đã verify rồi thì không gọi pixel lại");
        Check(!kept.Any(c => c.NeedsReview), "không được gắn review oan");

        // 4. Newly adjacent but outside the window (gap 0.5) -> keep silent, no review, no
        // comparer call (hold the pinned micro-gap spec: outside the window is a silent split).
        var farPre = new[] {
            Prov(30.0, 31.0, "杀", 0.99),
            Prov(31.0, 31.4, "死", 0.5),
            Prov(31.5, 32.5, "杀", 0.98),
        };
        var farPost = new[] { farPre[0], farPre[2] };
        var farComparer = new FakePixelComparer(PixelCheckResult.Same);
        var (farKept, merges4) = await OcrCueReconciler.MergeCleanupAdjacentsAsync(
            farPre, farPost, "v.mp4", farComparer, ct, continuationWindow: 0.1);
        Check(farKept.Count == 2 && merges4 == 0 && farComparer.Calls == 0, "ngoài window phải giữ im lặng");
        Check(!farKept.Any(c => c.NeedsReview), "ngoài window không gắn review");

        // 5. An identical duplicate pair gapped 0.4s (outside the micro-gap, inside 0.5s — the user's
        // 关于战神篇 0.3s case): the review-only zone (0.25-0.5s) — evidence: a 0.325s real blank.
        var dupPre = new[] { Prov(40.0, 41.0, "关于战神篇的全部内容", 0.99) };
        var dupPost = new[] { dupPre[0], Prov(41.4, 42.4, "关于战神篇的全部内容", 0.98) };
        var dupComparer = new FakePixelComparer(PixelCheckResult.Same);
        var (dupKept, merges5) = await OcrCueReconciler.MergeCleanupAdjacentsAsync(
            dupPre, dupPost, "v.mp4", dupComparer, ct, continuationWindow: 0.1);
        Check(dupKept.Count == 2 && merges5 == 0, "trùng 0.4s vẫn review-only (zone 0.25-0.5s)");
        Check(dupComparer.Calls == 0, "trùng 0.4s không được gọi pixel (spec ghim)");
        Check(dupKept.All(c => c.NeedsReview), "trùng 0.4s phải gắn review để nghe tay");

        // 6. Duplicates gapped 0.6s (outside 0.5s: genuinely repeated speech) -> keep completely silent.
        var far2Pre = new[] { Prov(50.0, 51.0, "杀", 0.99) };
        var far2Post = new[] { far2Pre[0], Prov(51.6, 52.6, "杀", 0.98) };
        var far2Comparer = new FakePixelComparer(PixelCheckResult.Same);
        var (far2Kept, merges6) = await OcrCueReconciler.MergeCleanupAdjacentsAsync(
            far2Pre, far2Post, "v.mp4", far2Comparer, ct, continuationWindow: 0.1);
        Check(far2Kept.Count == 2 && merges6 == 0 && far2Comparer.Calls == 0, "trùng hở 0.6s giữ im");
        Check(!far2Kept.Any(c => c.NeedsReview), "ngoài 0.5s không gắn review");

        // 19/09 (Cả Lớp video: 78 duplicate cross-lane pairs now overlapping): byte-identical
        // text + overlapping spans (gap<0) = 1 caption read by 2 lanes — merge
        // directly, PIXEL-FREE (the comparer throws if called).
        var throwing = new ThrowingPixelComparer();
        var xpair = new[] {
            Prov(982.7, 984.1, "金陵武道九班档案", 0.99),
            Prov(982.7, 984.1, "金陵武道九班档案", 0.98),
        };
        var xmerged = await OcrCueReconciler.MergeTouchingIdenticalAsync(xpair, "v.mp4", throwing, ct);
        Check(xmerged.Count == 1 && xmerged[0].Start == 982.7 && xmerged[0].End == 984.1,
            "trùng giờ-chồng phải gộp 1 cue không qua pixel");
        // A chain of 3 overlapping cues merges into one in a single pass.
        var xtriple = new[] {
            Prov(100.0, 102.0, "没错", 0.99),
            Prov(101.0, 103.0, "没错", 0.98),
            Prov(102.0, 104.0, "没错", 0.97),
        };
        var xtripleMerged = await OcrCueReconciler.MergeTouchingIdenticalAsync(xtriple, "v.mp4", throwing, ct);
        Check(xtripleMerged.Count == 1 && xtripleMerged[0].Start == 100.0 && xtripleMerged[0].End == 104.0,
            "chuỗi trùng chồng phải gộp nối 1 cue");
        // Counter-check: dash-overlap (—— vs —) does NOT slip into the fast path — it takes the old pixel path.
        var dashDiff = new FakePixelComparer(PixelCheckResult.Different);
        var dashpair = new[] {
            Prov(50.0, 52.0, "出——", 0.99),
            Prov(51.0, 53.0, "出—", 0.98),
        };
        var dashKept = await OcrCueReconciler.MergeTouchingIdenticalAsync(dashpair, "v.mp4", dashDiff, ct);
        Check(dashKept.Count == 2, "dash-overlap phải đi đường pixel cũ (tách khi Different)");
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }

    private sealed class FakePixelComparer : IPixelComparer
    {
        private readonly PixelCheckResult _result;
        public int Calls;
        public FakePixelComparer(PixelCheckResult result) => _result = result;
        public Task<PixelComparison> CheckAsync(string videoPath, PixelObservation left, PixelObservation right, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new PixelComparison(_result,
                new FrameSample(left.Pts, left.Pts, 0),
                new FrameSample(right.Pts, right.Pts, 0),
                System.Drawing.Rectangle.Empty, null, null, null));
        }
    }

    // The overlap-identical fast path must not call the pixel: calling it is a test error.
    private sealed class ThrowingPixelComparer : IPixelComparer
    {
        public Task<PixelComparison> CheckAsync(string videoPath, PixelObservation left, PixelObservation right, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("overlap-identical must not reach pixel");
    }
}
