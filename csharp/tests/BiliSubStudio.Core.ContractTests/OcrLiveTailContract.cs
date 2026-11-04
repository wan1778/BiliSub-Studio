using System.Drawing;
using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

// LIVE INCREMENTAL RECONCILE regression (field 109-113):
// a new committed boundary -> pixel-gated tail reconcile immediately, a clean preview without waiting for Pause.
// Fixture: A/B dash variant touching, C dash-dropout (—), D exact, E an entirely different cue.
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
    // Policy 21/09: A-B is not byte-identical (dash variant) so the blocking comparer gets called.
    // It still merges because the pixel says Same for a dash variant.
    private static OcrCue CueB() => Prov(47.733, 48.333, "殿主，有那些那些那些那些可能—", 0.98);
    private static OcrCue CueC() => Prov(48.333, 48.800, "殿主，有没有一种可能—", 0.97);
    private static OcrCue CueD() => Prov(48.800, 49.400, ChainText, 0.99);
    // E is a genuinely new instance: its first frame Pts must not duplicate D's last frame
    // (D's pxi locks at 49.400, the handoff changes text on the very next frame => E.First = 49.433 = 1 native frame).
    // The verdict is Same iff both observations share a Pts (the same physical frame) => D↔E is naturally Different.
    private static OcrCue CueE() => Prov(49.400, 51.133, "陈长安硬打天罚", 0.99, firstPtsOverride: 49.433);

    // Verdict per boundary: Same inside an instance, Different across a handoff (simulating a real pixel).
    // Compared by Pts (the media time of the observed frame) because that is the value the real comparer receives;
    // SourcePts is always distinct per observation (unusable for boundary comparison).
    private static PixelCheckResult Verdict(PixelObservation left, PixelObservation right) =>
        Math.Abs(left.Pts - right.Pts) < 1e-9 ? PixelCheckResult.Same : PixelCheckResult.Different;

    public static async Task RunAsync()
    {
        // 1: a live dash-variant pair merges right after the committed boundary (exactly 1 pixel check).
        using (var live = new OcrLiveTailReconciler())
        {
            var same = new ScriptedPixelComparer(Verdict);
            live.Start("v.mp4", same, Window);
            live.AppendCommitted([CueA(), CueB()]);
            var view = await WaitIdleAsync(live);
            Check(view.Count == 1, $"live exact pair must merge right after boundary (got {view.Count})");
            Check(view[0].Start == 46.633 && view[0].End == 48.333 && view[0].Text == ChainText && !view[0].NeedsReview,
                "live merged cue must span 46.633-48.333 without review flag");
            // A-B is a dash variant (not byte-identical) so it costs exactly 1 pixel check, Same → merge.
            Check(same.Calls == 1, $"dash-variant pair must cost one pixel check (calls={same.Calls})");
            await live.StopAsync();
        }

        // 2+3: the dash chain 109-112 collapses through each incremental offer, E stays separate, no Pause.
        // Generic gate: D-E (handoff) STILL reaches the verifier, Different => split + NeedsReview.
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
            // A-B dash (1) + B-C dash variant Same (1) + C-D byte-identical after merging → fast-path (0) = 2 calls.
            Check(same.Calls == 2, $"109-112 chain must cost 2 pixel checks (calls={same.Calls})");
            live.AppendCommitted([CueE()]);
            var afterE = await WaitIdleAsync(live);
            Check(afterE.Count == 2, $"distinct cue 113 must stay separate (got {afterE.Count})");
            Check(afterE[0].Start == 46.633 && afterE[0].End == 49.400 && afterE[0].Text == ChainText && afterE[0].NeedsReview,
                "chain cue must survive E arrival with unchanged span/text; NeedsReview flags the rejected handoff");
            Check(afterE[1].Start == 49.400 && afterE[1].End == 51.133 && afterE[1].Text == "陈长安硬打天罚" && afterE[1].NeedsReview,
                "cue 113 must stay separate with NeedsReview (Different handoff verified, not skipped)");
            // D-E handoff Different (1 call) + chain 2 = 3 total.
            Check(same.Calls == 3, $"D-E handoff must be verified once and rejected by Different (calls={same.Calls})");
            await live.StopAsync();
        }

        // 4: a stale reconcile result must not overwrite the newer tail.
        using (var live = new OcrLiveTailReconciler())
        {
            var blocking = new BlockingPixelComparer();
            live.Start("v.mp4", blocking, Window);
            live.AppendCommitted([CueA(), CueB()]);
            blocking.WaitEntered(TimeSpan.FromSeconds(15));
            live.AppendCommitted([CueC(), CueD(), CueE()]);
            blocking.Release();
            // The blocking comparer returns Same for every in-window boundary => the A-E chain collapses into 1 cue.
            var view = await WaitIdleAsync(live);
            Check(view.Count == 1 && view[0].Start == 46.633 && view[0].End == 51.133 && view[0].Text == ChainText,
                "stale run must be discarded; fresh full-window run with Same must collapse A-E");
            // Stale run: A-B dash variant → 1 pixel check (blocked) then cancelled when C/D/E arrive.
            // Fresh run: A-B (1) + B-C (1) + C-D identical fast-path (0) + D-E (1, blocking Same) = 3 calls.
            Check(blocking.Calls == 4, $"discarded run (1) + fresh run (3) must total 4 checks (calls={blocking.Calls})");
            Check(live.AppliedRevision == live.Revision, "applied revision must catch up to latest revision");
            await live.StopAsync();
        }

        // 5: an active provisional never becomes committed.
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
            // The merge pipeline normalizes/creates copies, so compare fields, not references.
            Check(live.CommittedView.Count == 1 && live.CommittedView[0].Start == 46.633 && live.CommittedView[0].End == 47.733,
                "committed view must hold only committed cues");
            var composed2 = live.ViewWithActive(active);
            Check(composed2.Count == 2 && composed2[0].Start == 46.633 && ReferenceEquals(composed2[1], active),
                "active must append after committed without mutating committed view");
            await live.StopAsync();
        }

        // 6: incremental offers == full reconcile for the same input, and only pay the tail cost.
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
            // Tail-only + fast path: 10 exact pairs merge pixel-free (0); offer3 chain1-2 Same (1),
            // the already-merged chain vs chain3 byte-identical → fast-path (0), chain-tail Different (1) = 2.
            // Between-instance boundaries (gap > window) do not call the comparer.
            Check(same.Calls == 2, $"incremental must pay tail-only cost, not full rescan (calls={same.Calls})");
            await live.StopAsync();
        }

        // 7: a resume seed exposes the bulk immediately, a later append only pays the tail cost.
        using (var live = new OcrLiveTailReconciler())
        {
            var bulk = Enumerable.Range(0, 10).Select(i => Prov(10 * (i + 1), 10 * (i + 1) + 1, "散句" + (i + 1), 0.99)).ToArray();
            live.ResetCommitted(bulk);
            // No Start/worker yet: the view must already hold the full normalized bulk.
            Check(live.CommittedView.Count == 10, $"resume must expose restored history immediately (got {live.CommittedView.Count})");
            Check(live.CommittedView[0].Start == 10 && live.CommittedView[9].Start == 100, "seeded view must preserve order");
            var same = new ScriptedPixelComparer(Verdict);
            live.Start("v.mp4", same, Window);
            live.AppendCommitted([Prov(200, 201, "新句不同", 0.99)]);
            var view = await WaitIdleAsync(live);
            Check(view.Count == 11 && view[0].Start == 10 && view[10].Start == 200, "resumed append lost history or the new cue");
            Check(same.Calls == 0, $"disjoint resume append must not rescan bulk (calls={same.Calls})");
            await live.StopAsync();
        }

        // 7b: resume seed cleans the field variant chain instead of exposing raw
        // near-duplicates until Pause. No worker/pixel work is needed.
        using (var live = new OcrLiveTailReconciler())
        {
            var chain = new[]
            {
                Prov(2434.27375, 2434.940437, "江白我肚子上都有肉7", 0.99),
                Prov(2434.940437, 2435.507125, "江白我肚子上都有肉了", 0.98),
                Prov(2435.507125, 2436.073812, "江白我肚子上都有肉下", 0.97),
                Prov(2436.073812, 2436.6405, "江白我肚子上都有肉了", 0.98),
                Prov(2436.6405, 2437.207187, "江白我肚子上都有肉7", 0.97),
                Prov(2437.207187, 2437.773875, "江白我肚子上都有肉了", 0.98),
            };
            live.ResetCommitted(chain);
            var view = live.CommittedView;
            Check(view.Count == 1, $"resume seed must clean field variant chain (got {view.Count})");
            Check(view[0].Start == 2434.27375 && view[0].End == 2437.773875,
                "cleaned resume seed must preserve the union span");
        }

        // 8: out-of-order multi-lane arrival (the live-split p02 case): the prefix already holds 10
        // future cues (> TailWindow) and the fresh arrival is a LATE dash-variant pair from the past.
        // Expected: merge into 1 (pixel Same) instead of living side by side.
        using (var live = new OcrLiveTailReconciler())
        {
            var futures = Enumerable.Range(0, 10).Select(i => Prov(1000 + 10 * i, 1000 + 10 * i + 1, "后来" + (i + 1), 0.99)).ToArray();
            var same = new ScriptedPixelComparer(Verdict);
            live.Start("v.mp4", same, Window);
            live.AppendCommitted(futures);
            await WaitIdleAsync(live);
            Check(live.CommittedView.Count == 10, "futures must all show before late arrival");
            var callsBefore = same.Calls;
            live.AppendCommitted([CueA(), CueB()]);
            var view = await WaitIdleAsync(live);
            var pair = view.Where(cue => cue.Start < 100).ToArray();
            Check(pair.Length == 1 && pair[0].Start == 46.633 && pair[0].End == 48.333 && pair[0].Text == ChainText,
                $"late out-of-order identical pair must merge in live view (got {pair.Length} cues)");
            Check(view.Count == 11, $"late merge must not drop futures (got {view.Count})");
            Check(same.Calls - callsBefore <= 4, $"late merge must stay bounded, not rescan bulk (extra calls={same.Calls - callsBefore})");
            await live.StopAsync();
        }
    }

    // 10 exact pairs (T1-T10) + a 3-dash chain + 1 distinct = 24 cues, delivered in 3 offers.
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
                // AppliedRevision has caught up but the worker may have just taken a new offer;
                // stabilize by reading twice in a row.
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
