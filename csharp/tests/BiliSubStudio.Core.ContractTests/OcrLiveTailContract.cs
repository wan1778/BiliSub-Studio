using System.Drawing;
using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

// LIVE INCREMENTAL RECONCILE regression (field 109-113):
// committed boundary mới -> tail reconcile pixel-gated ngay, preview sạch không đợi Pause.
// Fixture: A/B exact touching, C dash-dropout (—), D exact, E câu khác hẳn.
internal static class OcrLiveTailContract
{
    private static readonly OcrRegion TestRoi = new(0.05, 0.65, 0.90, 0.29);
    private static readonly RationalTimeBase TestTb = new(1, 90000);
    private static readonly OcrFrameGeometry TestGeo = new(0, 0, 1920, 1080, 1280, 320, 0, 0, 1280, 320);
    private static long _spts = 20000000;
    private const double Window = 0.1;
    private const string ChainText = "殿主，有没有一种可能——";

    private static OcrCue Prov(double s, double e, string t, double c, double? firstPtsOverride = null)
    {
        var s1 = System.Threading.Interlocked.Increment(ref _spts);
        var s2 = System.Threading.Interlocked.Increment(ref _spts);
        return new OcrCue(s, e, t, c, 0, 0, false,
            new PixelObservation(firstPtsOverride ?? s, new OcrBox(10, 10, 200, 40), TestRoi, TestGeo, s1, TestTb),
            new PixelObservation(e, new OcrBox(10, 10, 200, 40), TestRoi, TestGeo, s2, TestTb));
    }

    private static OcrCue CueA() => Prov(46.633, 47.733, ChainText, 0.99);
    private static OcrCue CueB() => Prov(47.733, 48.333, ChainText, 0.98);
    private static OcrCue CueC() => Prov(48.333, 48.800, "殿主，有没有一种可能—", 0.97);
    private static OcrCue CueD() => Prov(48.800, 49.400, ChainText, 0.99);
    // E là instance thật mới: frame đầu tiên của nó KHÔNG được trùng Pts với frame cuối của D
    // (pxi D chốt ở 49.400, handoff đổi chữ ngay frame sau => E.First = 49.433 = 1 native frame).
    // Verdict Same iff 2 observation cùng Pts (frame vật lý) => D↔E Different tự nhiên.
    private static OcrCue CueE() => Prov(49.400, 51.133, "陈长安硬打天罚", 0.99, firstPtsOverride: 49.433);

    // Verdict theo boundary: Same trong instance, Different qua handoff (mô phỏng pixel thật).
    // So theo Pts (media time của frame quan sát) vì đó là giá trị comparer thật nhận;
    // SourcePts luôn distinct per observation (không dùng được để so boundary).
    private static PixelCheckResult Verdict(PixelObservation left, PixelObservation right) =>
        Math.Abs(left.Pts - right.Pts) < 1e-9 ? PixelCheckResult.Same : PixelCheckResult.Different;

    public static async Task RunAsync()
    {
        // 1: live exact duplicate merge ngay sau committed boundary.
        using (var live = new OcrLiveTailReconciler())
        {
            var same = new ScriptedPixelComparer(Verdict);
            live.Start("v.mp4", same, Window);
            live.AppendCommitted([CueA(), CueB()]);
            var view = await WaitIdleAsync(live);
            Check(view.Count == 1, $"live exact pair must merge right after boundary (got {view.Count})");
            Check(view[0].Start == 46.633 && view[0].End == 48.333 && view[0].Text == ChainText && !view[0].NeedsReview,
                "live merged cue must span 46.633-48.333 without review flag");
            Check(same.Calls == 1, $"exact pair must cost one pixel check (calls={same.Calls})");
            await live.StopAsync();
        }

        // 2+3: dash chain 109-112 collapse qua từng incremental offer, E giữ riêng, không Pause.
        // Generic gate: D-E (handoff) VẪN tới verifier, Different => tách + NeedsReview.
        using (var live = new OcrLiveTailReconciler())
        {
            var same = new ScriptedPixelComparer(Verdict);
            live.Start("v.mp4", same, Window);
            live.AppendCommitted([CueA(), CueB()]);
            var afterAB = await WaitIdleAsync(live);
            Check(afterAB.Count == 1 && afterAB[0].End == 48.333, "live tail must merge A+B when B commits");
            live.AppendCommitted([CueC()]);
            var afterC = await WaitIdleAsync(live);
            Check(afterC.Count == 1 && afterC[0].Start == 46.633 && afterC[0].End == 48.800 && afterC[0].Text == ChainText,
                $"live tail must absorb dash-dropout C (got {afterC.Count} cues)");
            live.AppendCommitted([CueD()]);
            var afterD = await WaitIdleAsync(live);
            Check(afterD.Count == 1 && afterD[0].Start == 46.633 && afterD[0].End == 49.400 && afterD[0].Text == ChainText,
                "live tail must collapse 109-112 into 46.633-49.400 without Pause");
            Check(same.Calls == 3, $"109-112 chain must cost 3 pixel checks (calls={same.Calls})");
            live.AppendCommitted([CueE()]);
            var afterE = await WaitIdleAsync(live);
            Check(afterE.Count == 2, $"distinct cue 113 must stay separate (got {afterE.Count})");
            Check(afterE[0].Start == 46.633 && afterE[0].End == 49.400 && afterE[0].Text == ChainText && afterE[0].NeedsReview,
                "chain cue must survive E arrival with unchanged span/text; NeedsReview flags the rejected handoff");
            Check(afterE[1].Start == 49.400 && afterE[1].End == 51.133 && afterE[1].Text == "陈长安硬打天罚" && afterE[1].NeedsReview,
                "cue 113 must stay separate with NeedsReview (Different handoff verified, not skipped)");
            Check(same.Calls == 4, $"D-E handoff must be verified once and rejected by Different (calls={same.Calls})");
            await live.StopAsync();
        }

        // 4: stale reconcile result không overwrite tail mới hơn.
        using (var live = new OcrLiveTailReconciler())
        {
            var blocking = new BlockingPixelComparer();
            live.Start("v.mp4", blocking, Window);
            live.AppendCommitted([CueA(), CueB()]);
            blocking.WaitEntered(TimeSpan.FromSeconds(15));
            live.AppendCommitted([CueC(), CueD(), CueE()]);
            blocking.Release();
            // Blocking comparer trả Same mọi boundary trong window => chain A-E collapse thành 1 cue.
            var view = await WaitIdleAsync(live);
            Check(view.Count == 1 && view[0].Start == 46.633 && view[0].End == 51.133 && view[0].Text == ChainText,
                "stale run must be discarded; fresh full-window run with Same must collapse A-E");
            Check(blocking.Calls == 5, $"discarded run (1) + fresh run (4 boundaries) must total 5 checks (calls={blocking.Calls})");
            Check(live.AppliedRevision == live.Revision, "applied revision must catch up to latest revision");
            await live.StopAsync();
        }

        // 5: active provisional không bao giờ thành committed.
        using (var live = new OcrLiveTailReconciler())
        {
            var same = new ScriptedPixelComparer(Verdict);
            live.Start("v.mp4", same, Window);
            await WaitIdleAsync(live);
            var active = Prov(49.400, 49.900, "陈长安硬打", 0.50);
            Check(live.CommittedView.Count == 0, "empty committed state must stay empty");
            var composed = live.ViewWithActive(active);
            Check(composed.Count == 1 && ReferenceEquals(composed[0], active), "provisional active must show for display only");
            Check(live.CommittedView.Count == 0, "display composition must not absorb active into committed");
            var committed = Prov(46.633, 47.733, ChainText, 0.99);
            live.AppendCommitted([committed]);
            await WaitIdleAsync(live);
            // Merge pipeline normalize/create bản copy nên so fields, không so reference.
            Check(live.CommittedView.Count == 1 && live.CommittedView[0].Start == 46.633 && live.CommittedView[0].End == 47.733,
                "committed view must hold only committed cues");
            var composed2 = live.ViewWithActive(active);
            Check(composed2.Count == 2 && composed2[0].Start == 46.633 && ReferenceEquals(composed2[1], active),
                "active must append after committed without mutating committed view");
            await live.StopAsync();
        }

        // 6: incremental offers == full reconcile cho cùng input, và chỉ trả chi phí tail.
        using (var live = new OcrLiveTailReconciler())
        {
            var all = Stream24();
            var same = new ScriptedPixelComparer(Verdict);
            live.Start("v.mp4", same, Window);
            live.AppendCommitted(all.Take(8).ToArray());
            await WaitIdleAsync(live);
            live.AppendCommitted(all.Skip(8).Take(12).ToArray());
            await WaitIdleAsync(live);
            live.AppendCommitted(all.Skip(20).ToArray());
            var incremental = await WaitIdleAsync(live);
            var fullScripted = new ScriptedPixelComparer(Verdict);
            var full = await OcrCueReconciler.MergeTouchingIdenticalAsync(all, "v.mp4", fullScripted, CancellationToken.None, Window);
            Check(incremental.Count == full.Count, $"incremental ({incremental.Count}) must equal full ({full.Count})");
            for (var i = 0; i < full.Count; i++)
                Check(incremental[i].Start == full[i].Start && incremental[i].End == full[i].End
                    && incremental[i].Text == full[i].Text && incremental[i].NeedsReview == full[i].NeedsReview,
                    $"incremental cue {i} differs from full reconcile");
            // Tail-only: offer1 4 (in-pair T1-T4) + offer2 6 (T5-T10) + offer3 3 (chain1-2, 2-3, 3-tail) = 13.
            // Between-instance boundaries (gap > window) không gọi comparer.
            Check(same.Calls == 13, $"incremental must pay tail-only cost, not full rescan (calls={same.Calls})");
            await live.StopAsync();
        }
    }

    // 10 exact pairs (T1-T10) + dash chain 3 + 1 distinct = 24 cues, offer 3 đợt.
    private static OcrCue[] Stream24()
    {
        var cues = new List<OcrCue>();
        for (var pair = 0; pair < 10; pair++)
        {
            var text = "文本" + (pair + 1);
            cues.Add(Prov(10 * (pair + 1), 10 * (pair + 1) + 1, text, 0.99));
            cues.Add(Prov(10 * (pair + 1) + 1, 10 * (pair + 1) + 2, text, 0.98));
        }
        cues.Add(Prov(210, 211, "链——", 0.99));
        cues.Add(Prov(211, 212, "链—", 0.97));
        cues.Add(Prov(212, 213, "链——", 0.98));
        cues.Add(Prov(213, 214, "尾句不同", 0.99));
        return cues.ToArray();
    }

    private static async Task<IReadOnlyList<OcrCue>> WaitIdleAsync(OcrLiveTailReconciler live)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (live.AppliedRevision == live.Revision && live.AppliedRevision >= 0)
            {
                // AppliedRevision bắt kịp nhưng worker có thể vừa nhận offer mới;
                // ổn định qua 2 lần đọc liên tiếp.
                await Task.Delay(50);
                if (live.AppliedRevision == live.Revision) return live.CommittedView;
            }
            await Task.Delay(20);
        }
        throw new InvalidOperationException("live tail reconciler did not go idle");
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }

    private sealed class ScriptedPixelComparer : IPixelComparer
    {
        private readonly Func<PixelObservation, PixelObservation, PixelCheckResult> _verdict;
        public int Calls;
        public ScriptedPixelComparer(Func<PixelObservation, PixelObservation, PixelCheckResult> verdict) => _verdict = verdict;
        public Task<PixelComparison> CheckAsync(string videoPath, PixelObservation left, PixelObservation right, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new PixelComparison(_verdict(left, right),
                new FrameSample(left.Pts, left.Pts, 0),
                new FrameSample(right.Pts, right.Pts, 0),
                Rectangle.Empty, null, null, null));
        }
    }

    private sealed class BlockingPixelComparer : IPixelComparer
    {
        private readonly ManualResetEventSlim _entered = new(false);
        private readonly ManualResetEventSlim _release = new(false);
        public int Calls;
        public void WaitEntered(TimeSpan timeout)
        {
            if (!_entered.Wait(timeout)) throw new InvalidOperationException("worker never reached pixel check");
        }
        public void Release() => _release.Set();
        public Task<PixelComparison> CheckAsync(string videoPath, PixelObservation left, PixelObservation right, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref Calls) == 1)
            {
                _entered.Set();
                _release.Wait(cancellationToken);
            }
            return Task.FromResult(new PixelComparison(PixelCheckResult.Same,
                new FrameSample(left.Pts, left.Pts, 0),
                new FrameSample(right.Pts, right.Pts, 0),
                Rectangle.Empty, null, null, null));
        }
    }
}
