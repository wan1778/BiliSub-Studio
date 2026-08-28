using System.Reflection;
using System.Runtime.CompilerServices;
using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

internal static class OcrLaneReconcileFullerTextRegression
{
    [ModuleInitializer]
    internal static void Verify()
    {
        var assembly = typeof(OcrResult).Assembly;
        var scannerType = assembly.GetType("BiliSubStudio.Core.Ocr.OcrScanner")
            ?? throw new InvalidOperationException("missing OcrScanner");
        var segmentType = assembly.GetType("BiliSubStudio.Core.Ocr.OcrScanSegment")
            ?? throw new InvalidOperationException("missing OcrScanSegment");
        var laneType = assembly.GetType("BiliSubStudio.Core.Ocr.OcrLaneCheckpoint")
            ?? throw new InvalidOperationException("missing OcrLaneCheckpoint");
        var reconcile = scannerType.GetMethod("Reconcile", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing OcrScanner.Reconcile");
        var segmentConstructor = segmentType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(x => x.GetParameters().Length == 5);
        var laneConstructor = laneType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(x => x.GetParameters().Length == 7);

        object Lane(int index, params OcrCue[] cues)
        {
            var segment = segmentConstructor.Invoke([index, 0d, 100d, 0d, 100d]);
            return laneConstructor.Invoke([segment, 100d, cues.ToList(), null, 0, 0, true]);
        }

        IReadOnlyList<OcrCue> Reconcile(params OcrCue[] cues)
        {
            var lanes = Array.CreateInstance(laneType, cues.Length);
            for (var index = 0; index < cues.Length; index++) lanes.SetValue(Lane(index, cues[index]), index);
            var arguments = new object?[] { lanes, 0 };
            var result = (IReadOnlyList<OcrCue>)(reconcile.Invoke(null, arguments)
                ?? throw new InvalidOperationException("lane reconcile returned null"));
            return result;
        }

        IReadOnlyList<OcrCue> ReconcileSameLane(params OcrCue[] cues)
        {
            var lanes = Array.CreateInstance(laneType, 1);
            lanes.SetValue(Lane(0, cues), 0);
            var arguments = new object?[] { lanes, 0 };
            return (IReadOnlyList<OcrCue>)(reconcile.Invoke(null, arguments)
                ?? throw new InvalidOperationException("same-lane reconcile returned null"));
        }

        var shortCue = new OcrCue(9.9, 10.3, "吃我的喝我", .98);
        var completeCue = new OcrCue(10.0, 10.4, "吃我的喝我的", .84);
        foreach (var output in new[] { Reconcile(shortCue, completeCue), Reconcile(completeCue, shortCue) })
        {
            if (output.Count != 1 || output[0].Text != "吃我的喝我的" || output[0].End != 10.4)
                throw new InvalidOperationException($"lane reconcile discarded the real fuller compatible OCR result: {string.Join(" | ", output.Select(x => $"{x.Start:0.0}-{x.End:0.0}:{x.Text}"))}");
        }

        var differentMeaning = new OcrCue(10.0, 10.4, "吃我的喝她", .99);
        var separate = Reconcile(shortCue, differentMeaning);
        if (separate.Count != 2 || separate[0].Text != shortCue.Text || separate[1].Text != differentMeaning.Text)
            throw new InvalidOperationException("lane reconcile synthesized or replaced text for a different-meaning OCR result");

        var firstRapidCue = new OcrCue(20.0, 21.0, "我一定会成功", .99);
        var secondRapidCue = new OcrCue(21.1, 22.0, "你一定会成功", .99);
        var sameLane = ReconcileSameLane(firstRapidCue, secondRapidCue);
        if (sameLane.Count != 2 || sameLane[0].Text != firstRapidCue.Text || sameLane[1].Text != secondRapidCue.Text)
            throw new InvalidOperationException("lane reconcile merged two distinct fast subtitles from the same lane");

        var fresh = Reconcile(shortCue, completeCue);
        var resumed = Reconcile(shortCue, completeCue);
        if (fresh.Count != resumed.Count || fresh[0] != resumed[0])
            throw new InvalidOperationException("reconcile output differs between equivalent fresh and resumed raw lane state");
    }
}
