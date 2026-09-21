using System.Reflection;
using System.Runtime.CompilerServices;
using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

internal static class OcrAdaptiveConfirmationRegression
{
    [ModuleInitializer]
    internal static void Verify()
    {
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
        var scanner = typeof(OcrScanner);
        var select = scanner.GetMethod("SelectAdaptiveRefinementFrames", statics)!;
        var selectTransition = scanner.GetMethod("SelectTransitionRefinementFrames", statics)!;
        var cap = scanner.GetMethod("CapRefinementIndexes", statics)!;
        var confirm = scanner.GetMethod("BoundedTransitionConfirmationFrames", statics)!;
        var denseNoise = scanner.GetMethod("IsDenseAdaptiveVisualNoise", statics)!;
        var needs = scanner.GetMethod("NeedsAdaptiveRefinement", statics)!;
        int[] Cap(int windowLength, int[] visualSelected, int maximum = 8) =>
            ((IReadOnlyList<int>)cap.Invoke(null, [visualSelected, windowLength, maximum])!).ToArray();
        int[] Confirm(int windowLength, int maximum = 5) =>
            ((IReadOnlyList<int>)confirm.Invoke(null, [windowLength, maximum])!).ToArray();
        bool Dense(double[] scores) => (bool)denseNoise.Invoke(null, [scores])!;
        var trackerType = scanner.Assembly.GetType("BiliSubStudio.Core.Ocr.SubtitleTracker")!;
        var constructor = trackerType.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
            null, [typeof(double), typeof(double), typeof(bool)], null)!;
        var observe = trackerType.GetMethod("Observe", [typeof(double), typeof(double), typeof(OcrResult)])!;
        var blank = new OcrResult(true, false, string.Empty, 0, []);
        OcrResult Text(string text) => new(true, true, text, .99, []);
        bool Needs(OcrResult left, OcrResult right) => (bool)needs.Invoke(null, [left, right])!;
        int[] Select(double[] scores, ref int pending)
        {
            object[] args = [scores, pending];
            var selected = ((IReadOnlyList<int>)select.Invoke(null, args)!).ToArray();
            pending = (int)args[1];
            Check(selected.SequenceEqual(selected.Distinct().Order()), "refinement must be unique and ordered");
            Check(selected.All(index => index >= 0 && index < scores.Length - 1), "baseline sample must not be recognized twice");
            return selected;
        }

        var pending = 0;
        var isolated = new double[12];
        isolated[0] = isolated[10] = .03;
        Check(Select(isolated, ref pending).SequenceEqual([0, 1, 2, 9, 10]),
            "short caption needs late evidence before disappearance, not just three onset reads");
        Check(pending == 1, "final baseline must count toward visual followups");
        Check(Select([0d], ref pending).Length == 0 && pending == 0,
            "one-frame window must consume its already-recognized followup");
        Check(Select([.03], ref pending).Length == 0 && pending == 2,
            "change at final sample must carry two confirmations into next window");
        Check(Select(new double[12], ref pending).SequenceEqual([0, 1]) && pending == 0,
            "quiet next window must honor outstanding confirmation");
        Check(Select(new double[64], ref pending).Length == 0,
            "stable window must not become every-frame inference");
        Check(Select([double.NaN, double.PositiveInfinity, .0199, 0], ref pending).Length == 0,
            "invalid/subthreshold visual scores must not create candidates");
        int[] SelectTransition(double[] scores, ref int transitionPending)
        {
            object[] args = [scores, transitionPending];
            var selected = ((IReadOnlyList<int>)selectTransition.Invoke(null, args)!).ToArray();
            transitionPending = (int)args[1];
            Check(selected.SequenceEqual(selected.Distinct().Order()), "transition refinement must be unique and ordered");
            Check(selected.All(index => index >= 0 && index < scores.Length - 1), "baseline sample must not be recognized twice");
            return selected;
        }
        // Trace-proven on H.264: a one-rune boundary scores ~0.019, below the
        // steady 0.02 bar but above the transition 0.015 bar, so a sampled text
        // change must still nominate its neighborhood for confirmation.
        pending = 0;
        var nearMiss = new double[12];
        nearMiss[8] = .019;
        Check(SelectTransition(nearMiss, ref pending).SequenceEqual([7, 8, 9, 10]),
            "transition tier must confirm a near-miss visual boundary the steady tier skips");
        Check(Select(new double[12], ref pending).Length == 0,
            "steady tier must keep skipping the same near-miss scores");
        Check(Select(Enumerable.Repeat(.03, 64).ToArray(), ref pending).Length == 63,
            "dense transitions must remain bounded to the buffered window");
        Check(Confirm(11).SequenceEqual([0, 2, 5, 7, 10]),
            "quiet transition must confirm with five spread frames, not the whole window");
        Check(Confirm(11, 2).SequenceEqual([0, 10]),
            "Accurate quiet transition must retain both interval ends");
        Check(Confirm(1).SequenceEqual([0]), "one-frame window confirmation must stay in range");
        Check(Cap(12, Enumerable.Range(0, 12).ToArray()).SequenceEqual([0, 1, 3, 4, 6, 7, 9, 11]),
            "dense visual window must be capped with earliest/latest kept");
        Check(Cap(15, Enumerable.Range(0, 15).ToArray(), 4).SequenceEqual([0, 4, 9, 14]),
            "Accurate sparse boundary windows must retain four spread representatives");
        Check(Dense([.03, .03, 0, .03, 0, .03]),
            "dense visual changes must be classified as animation noise");
        Check(!Dense([0, .03, 0, 0, 0, .03]),
            "a sparse appearance/disappearance pair must remain eligible for OCR");

        var first = Text("\u6211\u4e00\u5b9a\u4f1a\u6210\u529f");
        var second = Text("\u4f60\u4e00\u5b9a\u4f1a\u6210\u529f");
        Check(Needs(first, second), "one substituted glyph above 0.82 similarity still needs confirmation");
        Check(!Needs(first, first with { Confidence = .60 }), "confidence-only change must not force a full window");
        Check(!Needs(blank, blank), "stable blank must stay selective");

        // Scripted visual/OCR observations drive the production selector and
        // tracker. This tests scheduling/evidence, not Paddle or video accuracy.
        (IReadOnlyList<OcrCue> Cues, int Images) Replay(
            double baselineFps, int nativeFps, OcrResult[] results, bool visual = true)
        {
            var tracker = constructor.Invoke([baselineFps, .62, true]);
            var frame = 1d / nativeFps;
            var lastSample = 0;
            var nextSample = 1 / baselineFps;
            var followups = 0;
            var images = 1;
            observe.Invoke(tracker, [0d, frame, results[0]]);
            if (trackerType.GetProperty("CanCheckpoint")!.GetValue(tracker) is false) followups = 2;
            for (var at = 1; at < results.Length; at++)
            {
                if (at * frame + frame / 2 < nextSample && at - lastSample < 64 && at < results.Length - 1) continue;
                int[] selected;
                if (Needs(results[lastSample], results[at]))
                {
                    // Mirror production: a new transition discards stale
                    // followups, gates on the visual probe, confirms with a
                    // bounded subset when quiet, and caps dense neighborhoods.
                    // One score per anchor/window pair, so the baseline sample
                    // itself is never selected.
                    followups = 0;
                    var probe = Enumerable.Range(0, at - lastSample)
                        .Select(offset => visual && results[lastSample + offset].Text != results[lastSample + offset + 1].Text ? .03 : 0).ToArray();
                    var visualSelected = Select(probe, ref followups);
                    followups = 0;
                    selected = visualSelected.Length > 0
                        ? Cap(probe.Length - 1, visualSelected, baselineFps >= 4 ? 4 : 8)
                        : Confirm(at - lastSample - 1, baselineFps >= 4 ? 2 : 5);
                }
                else
                {
                    var scores = Enumerable.Range(lastSample + 1, at - lastSample)
                        .Select(index => visual && results[index].Text != results[index - 1].Text ? .03 : 0).ToArray();
                    var candidates = Select(scores, ref followups);
                    selected = baselineFps >= 4 && results[at].Detected && Dense(scores)
                        ? []
                        : baselineFps >= 4
                        ? Cap(scores.Length - 1, candidates, 4)
                        : candidates;
                }
                foreach (var index in selected.Append(at - lastSample - 1))
                {
                    var sourceIndex = lastSample + 1 + index;
                    observe.Invoke(tracker, [sourceIndex * frame, frame, results[sourceIndex]]);
                    images++;
                }
                if (trackerType.GetProperty("CanCheckpoint")!.GetValue(tracker) is false)
                    followups = Math.Max(followups, 2);
                lastSample = at;
                do nextSample += 1 / baselineFps;
                while (nextSample <= at * frame + .000001);
            }
            trackerType.GetMethod("Finish")!.Invoke(tracker, [results.Length * frame]);
            return ((IReadOnlyList<OcrCue>)trackerType.GetProperty("Cues")!.GetValue(tracker)!, images);
        }

        foreach (var baseline in new[] { 4d, 2.5, 1.5 })
        foreach (var fps in new[] { 24, 30, 60 })
        {
            var rune = Text("\u8d70");
            var durationFrames = (int)Math.Ceiling(.30 * fps);
            var results = Enumerable.Repeat(blank, fps * 2).ToArray();
            Array.Fill(results, rune, 1, durationFrames);
            var replay = Replay(baseline, fps, results);
            Check(replay.Cues.Count == 1 && replay.Cues[0].Text == rune.Text,
                $"{baseline}Hz/{fps}fps lost a short real rune between baseline samples");
            Check(Math.Abs(replay.Cues[0].Start - 1d / fps) < .000001 &&
                Math.Abs(replay.Cues[0].End - (1d + durationFrames) / fps) < .000001,
                $"{baseline}Hz/{fps}fps short-rune timing must use actual boundary PTS; "
                + $"got {replay.Cues[0].Start:0.######}-{replay.Cues[0].End:0.######}");
            Check(replay.Images < results.Length / 2, "isolated caption should not require exhaustive recognition");

            // Real repeated text separated by a blank must remain two instances.
            Array.Fill(results, rune, fps + 1, durationFrames);
            replay = Replay(baseline, fps, results);
            Check(replay.Cues.Count == 2 && replay.Cues[0].End < replay.Cues[1].Start,
                "confirmation must not bridge a real blank between identical captions");

            var stable = Replay(baseline, fps, Enumerable.Repeat(first, fps * 4).ToArray());
            Check(stable.Cues.Count == 1 && stable.Images < fps * 2,
                "stable confirmed caption must stay one cue with sparse recognition");
        }

        var rapid = Enumerable.Range(0, 36).Select(index => index is >= 6 and < 15 ? second : first).ToArray();
        var similar = Replay(2.5, 30, rapid, visual: false);
        Check(similar.Cues.Select(cue => cue.Text).SequenceEqual([first.Text, second.Text, first.Text]),
            "quiet visual probe must not hide a sampled one-glyph substitution");
        Check(Math.Abs(similar.Cues[1].Start - .2) < .000001 && Math.Abs(similar.Cues[1].End - .5) < .000001,
            "similar-caption refinement must recover native transition boundaries");

        var shortText = Enumerable.Repeat(blank, 30).ToArray();
        Array.Fill(shortText, Text("\u4f60\u597d"), 1, 6);
        Check(Replay(2.5, 30, shortText).Cues.Count == 1, "200ms multi-rune caption must survive selective confirmation");
        foreach (var fps in new[] { 24, 30, 60 })
        {
            var accurateShort = Enumerable.Repeat(blank, fps).ToArray();
            Array.Fill(accurateShort, Text("\u4f60\u597d"), 1, (int)Math.Ceiling(.20 * fps));
            var accurateReplay = Replay(4, fps, accurateShort);
            Check(accurateReplay.Cues.Count == 1 && accurateReplay.Cues[0].Text == "\u4f60\u597d",
                $"Accurate 4Hz/{fps}fps cap lost a minimum-duration caption");
        }
        shortText = Enumerable.Repeat(blank, 30).ToArray();
        shortText[12] = first;
        Check(Replay(2.5, 30, shortText).Cues.Count == 0, "one-frame sampled hallucination must not become a cue");
        var duplicate = Enumerable.Repeat(first, 36).ToArray();
        duplicate[12] = Text("\u6211\u4e00\u5b9a\u4f1a\u6210\u529f\u529f");
        var deduplicated = Replay(2.5, 30, duplicate);
        Check(deduplicated.Cues.Count == 1 && deduplicated.Cues[0].Text == first.Text,
            "one-frame fuller hallucination must not replace or split stable text");
        duplicate[12] = blank;
        var dropout = Replay(2.5, 30, duplicate);
        Check(dropout.Cues.Count == 1 && dropout.Cues[0].Text == first.Text,
            "one sampled blank dropout must not split stable text");
        var finalRune = Replay(2.5, 30, Enumerable.Repeat(Text("\u8d70"), 9).ToArray());
        Check(finalRune.Cues.Count == 1 && Math.Abs(finalRune.Cues[0].End - .30) < .000001,
            "partial EOF window must confirm a real 300ms rune without inventing extra duration");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
