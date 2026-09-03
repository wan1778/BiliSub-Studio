using System.Drawing;
using System.Text;
using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

// PROPERTY TEST generic boundary: thuật toán KHÔNG được phụ thuộc vocabulary.
// Sinh chuỗi rune ARBITRARY (bao gồm CJK, dash-lookalike, punctuation, number, surrogate pair),
// mutate ngẫu nhiên (sub 1-3 rune / delete / insert / punct / duplicate rune) rồi assert:
//   temporal+spatial dính + Pixel Same            => collapse bất kể ký tự cụ thể;
//   arbitrary text + Pixel Different              => không merge;
//   same text + blank > continuation window       => không merge, comparer không được hỏi;
//   relabel toàn bộ rune set (A->X bijection)     => quyết định merge/collapse KHÔNG đổi
//                                                    (structure-invariance: không lệch chữ cụ thể).
internal static class OcrGenericBoundaryProperty
{
    private static readonly OcrRegion TestRoi = new(0.05, 0.65, 0.90, 0.29);
    private static readonly RationalTimeBase TestTb = new(1, 90000);
    private static readonly OcrFrameGeometry TestGeo = new(0, 0, 1920, 1080, 1280, 320, 0, 0, 1280, 320);
    private static long _spts = 70000000;
    private const double Window = 0.1;

    // Vocab lớn, hỗn tạp: CJK, CJK punct, ASCII punct, digits, Latin, dash variants, surrogate pairs, một số rune trùng hình.
    private static readonly int[] Vocab =
    [
        0x4E00, 0x6C5F, 0x5927, 0x6A4B, 0x9AD8, 0x901F, 0x516C, 0x8DEF, 0x8F66, 0x7AD9,
        0x4EBA, 0x6C11, 0x5929, 0x5730, 0x6C34, 0x706B, 0x6728, 0x91D1, 0x571F, 0x7C7B,
        0x6CA1, 0x8BBE, 0x8D70, 0x6765, 0x554A, 0x54E6, 0x5514, 0x5440, 0x5417, 0x5462,
        0x2014, 0x2013, 0x2010, 0x2212, 0x2018, 0x2019, 0x201C, 0x201D, 0x3001, 0x3002,
        0xFF0C, 0xFF01, 0xFF1F, 0xFF1A, 0xFF1B, 0xFF08, 0xFF09, 0x0021, 0x002C, 0x002E,
        0x0030, 0x0031, 0x0032, 0x0039, 0x0041, 0x005A, 0x4E00 /*一 (dash-confusable)*/, 0x4E59,
        /* surrogate pairs test riêng qua SurrogatePairs; 0xD83D không phải scalar Rune */
    ];

    private static readonly string[] SurrogatePairs = ["\U0001F600", "\U0001F525", "\U0001F3B5"];

    private static readonly Random Rng = new(20260904); // seed cố định = reproducible

    private static string ArbitraryText(int minLen, int maxLen)
    {
        var len = Rng.Next(minLen, maxLen + 1);
        var sb = new StringBuilder();
        for (var i = 0; i < len; i++)
        {
            if (Rng.Next(8) == 0) { sb.Append(SurrogatePairs[Rng.Next(SurrogatePairs.Length)]); continue; }
            sb.Append((char)Vocab[Rng.Next(Vocab.Length)]);
        }
        return sb.ToString();
    }

    private static string Mutate(string text)
    {
        var runes = text.EnumerateRunes().ToArray();
        var kind = Rng.Next(6);
        switch (kind)
        {
            case 0: // substitute 1 rune
            {
                if (runes.Length == 0) break;
                var idx = Rng.Next(runes.Length);
                runes[idx] = Rng.Next(8) == 0 ? new Rune(SurrogatePairs[Rng.Next(3)].EnumerateRunes().First().Value)
                    : new Rune(Vocab[Rng.Next(Vocab.Length)]);
                break;
            }
            case 1: // substitute 2-3 runes
            {
                var count = Math.Min(runes.Length, 2 + Rng.Next(2));
                for (var n = 0; n < count; n++)
                {
                    var idx = Rng.Next(runes.Length);
                    runes[idx] = new Rune(Vocab[Rng.Next(Vocab.Length)]);
                }
                break;
            }
            case 2: // delete rune
            {
                if (runes.Length <= 1) break;
                var idx = Rng.Next(runes.Length);
                runes = runes.Where((_, i) => i != idx).ToArray();
                break;
            }
            case 3: // insert rune
            {
                var idx = Rng.Next(runes.Length + 1);
                runes = runes.Take(idx).Append(new Rune(Vocab[Rng.Next(Vocab.Length)])).Concat(runes.Skip(idx)).ToArray();
                break;
            }
            case 4: // punctuation swap
            {
                if (runes.Length == 0) break;
                var puncts = new[] { ',', '.', '!', '?', '，', '。', '！', '？', '：', '；' };
                runes[Rng.Next(runes.Length)] = new Rune(puncts[Rng.Next(puncts.Length)]);
                break;
            }
            default: // duplicate adjacent rune
            {
                if (runes.Length == 0) break;
                var idx = Rng.Next(runes.Length);
                runes = runes.Take(idx).Append(runes[idx]).Concat(runes.Skip(idx)).ToArray();
                break;
            }
        }
        return string.Concat(runes.Select(r => r.ToString()));
    }

    private static OcrCue Prov(double s, double e, string t, double c, int support)
    {
        var s1 = System.Threading.Interlocked.Increment(ref _spts);
        var s2 = System.Threading.Interlocked.Increment(ref _spts);
        return new OcrCue(s, e, t, c, support, support, false,
            new PixelObservation(s, new OcrBox(10, 10, 200, 40), TestRoi, TestGeo, s1, TestTb),
            new PixelObservation(e, new OcrBox(10, 10, 200, 40), TestRoi, TestGeo, s2, TestTb));
    }

    // Fragment A tại frame f (Pts=f), fragment B tại frame f (touching; B.First cùng Pts => Same per verdict).
    private static (OcrCue A, OcrCue B) Instance(double start, double mid, double end, string textA, string textB, int supA, int supB)
        => (Prov(start, mid, textA, 0.99, supA), Prov(mid, end, textB, 0.98, supB));

    public static async Task RunAsync()
    {
        var ct = CancellationToken.None;

        // P1 (50 trials): temporal+spatial+Same => collapse bất kể ký tự; canonical = evidence mạnh hơn.
        for (var trial = 0; trial < 50; trial++)
        {
            // Text phải normalize-PASS (rune ngoài contract bị production filter drop cue —
            // hành vi filter riêng, không thuộc generic boundary) và khác nhau sau normalize.
            string baseText, mutated;
            do
            {
                baseText = ArbitraryText(3, 8);
                mutated = Mutate(baseText);
            }
            while (ChineseNormalized(baseText) is null
                || ChineseNormalized(mutated) is null
                || ChineseNormalized(baseText) == ChineseNormalized(mutated));
            var supStable = 20 + Rng.Next(40);
            var supFlicker = 1 + Rng.Next(3);
            var flip = Rng.Next(2);
            var (a, b) = flip == 0
                ? Instance(10.0, 10.5, 11.0, baseText, mutated, supStable, supFlicker)
                : Instance(10.0, 10.5, 11.0, mutated, baseText, supFlicker, supStable);
            var same = new ScriptedComparer((l, r) => PixelCheckResult.Same);
            var merged = await OcrCueReconciler.MergeTouchingIdenticalAsync([a, b], "v.mp4", same, ct, Window);
            Check(merged.Count == 1, $"P1 trial {trial}: touching same-frame pair must collapse (got {merged.Count}) text='{baseText}'/'{mutated}'");
            Check(!merged[0].NeedsReview, $"P1 trial {trial}: Same merge must not flag review");
            Check(merged[0].Start == 10.0 && merged[0].End == 11.0, $"P1 trial {trial}: span must extend 10-11");
            // Canonical theo support (confident trước): fragment ổn định (support cao) luôn thắng
            // bất kể nó đứng A hay B. So qua normalized text vì pipeline normalize trước merge
            // (strip rune ngoài CJK/punct contract) — không biến text bằng DashNormalize.
            Check(merged[0].Text == ChineseNormalized(baseText),
                $"P1 trial {trial}: canonical must be the higher-support (stable) fragment");
            Check(same.Calls == 1, $"P1 trial {trial}: one boundary one pixel call (calls={same.Calls})");
        }

        // P2 (50 trials): arbitrary text + Different => giữ tách + NeedsReview cả hai.
        for (var trial = 0; trial < 50; trial++)
        {
            string tA, tB;
            do
            {
                tA = ArbitraryText(2, 9);
                tB = ArbitraryText(2, 9);
            }
            while (ChineseNormalized(tA) is null || ChineseNormalized(tB) is null);
            var (a, b) = Instance(20.0, 20.4, 20.8, tA, tB, 30, 30);
            var diff = new ScriptedComparer((l, r) => PixelCheckResult.Different);
            var merged = await OcrCueReconciler.MergeTouchingIdenticalAsync([a, b], "v.mp4", diff, ct, Window);
            Check(merged.Count == 2, $"P2 trial {trial}: Different must keep separate (got {merged.Count})");
            Check(merged.All(c => c.NeedsReview), $"P2 trial {trial}: Different must flag both");
            Check(merged[0].Text == ChineseNormalized(tA) && merged[1].Text == ChineseNormalized(tB),
                $"P2 trial {trial}: Different must not rewrite text");
        }

        // P3 (40 trials): same text, blank > window => không merge, comparer KHÔNG được hỏi.
        for (var trial = 0; trial < 40; trial++)
        {
            string t;
            do { t = ArbitraryText(2, 8); } while (ChineseNormalized(t) is null);
            var blank = 0.12 + Rng.NextDouble() * 0.8; // > window 0.1
            var a = Prov(30.0, 31.0, t, 0.99, 25);
            var b = Prov(31.0 + blank, 32.0 + blank, t, 0.98, 25);
            var counting = new ScriptedComparer((l, r) => PixelCheckResult.Same);
            var merged = await OcrCueReconciler.MergeTouchingIdenticalAsync([a, b], "v.mp4", counting, ct, Window);
            Check(merged.Count == 2, $"P3 trial {trial}: real blank {blank:0.000}s must stay 2 cues");
            Check(!merged.Any(c => c.NeedsReview), $"P3 trial {trial}: genuine blank must not flag review");
            Check(counting.Calls == 0, $"P3 trial {trial}: blank beyond window must not reach comparer (calls={counting.Calls})");
        }

        // P4 (30 trials) structure-invariance: relabel rune set bằng bijection A->X,
        // quyết định collapse/split phải y hệt (không phụ thuộc chữ cụ thể).
        for (var trial = 0; trial < 30; trial++)
        {
            string baseText, variant;
            do
            {
                baseText = ArbitraryText(3, 7);
                variant = Mutate(baseText);
            }
            while (ChineseNormalized(baseText) is null || ChineseNormalized(variant) is null);
            var chain = new[]
            {
                Prov(40.0, 40.4, baseText, 0.99, 30),
                Prov(40.4, 40.8, variant, 0.97, 2),
                Prov(40.8, 41.2, baseText, 0.99, 30),
            };
            var sameA = new ScriptedComparer((l, r) => PixelCheckResult.Same);
            var sameB = new ScriptedComparer((l, r) => PixelCheckResult.Same);
            var original = await OcrCueReconciler.MergeTouchingIdenticalAsync(chain, "v.mp4", sameA, ct, Window);
            var relabeled = await OcrCueReconciler.MergeTouchingIdenticalAsync(Relabel(chain), "v.mp4", sameB, ct, Window);
            Check(original.Count == relabeled.Count,
                $"P4 trial {trial}: relabel must not change decision count ({original.Count} vs {relabeled.Count})");
            Check(sameA.Calls == sameB.Calls,
                $"P4 trial {trial}: relabel must not change pixel call count ({sameA.Calls} vs {sameB.Calls})");
            for (var i = 0; i < Math.Min(original.Count, relabeled.Count); i++)
                Check(original[i].NeedsReview == relabeled[i].NeedsReview
                    && Math.Abs(original[i].Start - relabeled[i].Start) < 1e-9
                    && Math.Abs(original[i].End - relabeled[i].End) < 1e-9,
                    $"P4 trial {trial}: relabel must not change timings/review at cue {i}");
        }

        // P5 (30 trials): chain 3-6 fragment arbitrary mutation, Same => 1 visual instance.
        for (var trial = 0; trial < 30; trial++)
        {
            string baseText;
            do { baseText = ArbitraryText(2, 6); } while (ChineseNormalized(baseText) is null);
            var count = 3 + Rng.Next(4);
            var chain = new List<OcrCue>();
            var at = 50.0;
            for (var i = 0; i < count; i++)
            {
                string text;
                do { text = i == 0 ? baseText : Mutate(baseText); } while (ChineseNormalized(text) is null);
                var support = i == 0 ? 30 : 1 + Rng.Next(3);
                chain.Add(Prov(at, at + 0.4, text, 0.99, support));
                at += 0.4;
            }
            var same = new ScriptedComparer((l, r) => PixelCheckResult.Same);
            var merged = await OcrCueReconciler.MergeTouchingIdenticalAsync(chain, "v.mp4", same, ct, Window);
            Check(merged.Count == 1, $"P5 trial {trial}: {count}-fragment chain Same must collapse to 1 (got {merged.Count})");
            Check(merged[0].Start == 50.0 && Math.Abs(merged[0].End - (50.0 + 0.4 * count)) < 1e-9,
                $"P5 trial {trial}: chain span must cover all fragments");
            Check(same.Calls == count - 1, $"P5 trial {trial}: each boundary one call (calls={same.Calls})");
        }
    }

    private static string? ChineseNormalized(string text) =>
        BiliSubStudio.Core.Ocr.ChineseSubtitleNormalizer.TryNormalize(text, out var normalized) ? normalized : null;

    // Bijection ổn định rune->rune khác vocabulary: relabel cả text mà không đổi số rune.
    private static OcrCue[] Relabel(OcrCue[] cues)
    {
        var map = new Dictionary<int, int>();
        OcrCue RelabelCue(OcrCue cue)
        {
            var sb = new StringBuilder();
            foreach (var rune in cue.Text.EnumerateRunes())
            {
                if (!map.TryGetValue(rune.Value, out var mapped))
                {
                    // Chọn rune khác hệt trong vocab, bijection theo insertion order (deterministic).
                    mapped = Vocab[(map.Count * 7 + 13) % Vocab.Length];
                    if (mapped == rune.Value) mapped = Vocab[(map.Count * 7 + 29) % Vocab.Length];
                    map[rune.Value] = mapped;
                }
                sb.Append((char)mapped);
            }
            return cue with { Text = sb.ToString() };
        }
        return cues.Select(RelabelCue).ToArray();
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }

    private sealed class ScriptedComparer : IPixelComparer
    {
        private readonly Func<double, double, PixelCheckResult> _verdict;
        public int Calls;
        public ScriptedComparer(Func<double, double, PixelCheckResult> verdict) => _verdict = verdict;
        public Task<PixelComparison> CheckAsync(string videoPath, PixelObservation left, PixelObservation right, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new PixelComparison(_verdict(left.Pts, right.Pts),
                new FrameSample(left.Pts, left.Pts, 0),
                new FrameSample(right.Pts, right.Pts, 0),
                Rectangle.Empty, null, null, null));
        }
    }
}
