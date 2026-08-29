using System.Reflection;
using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

internal static class OcrFragmentContract
{
    public static Task RunAsync()
    {
        OcrCue[] fragments = [new(7.8, 8.6, "一万年", .99), new(8.6, 9, "一万年", .98), new(9, 9.166667, "一万年", 1)];
        var merged = OcrCueReconciler.MergeTouchingIdentical(fragments);
        Check(merged.Count == 1 && merged[0] == new OcrCue(7.8, 9.166667, "一万年", 1), "touching duplicate fragments not merged exactly");
        Check(OcrCueReconciler.MergeTouchingIdentical(merged.Concat(fragments)).SequenceEqual(merged), "repeated live snapshots reintroduced fragments");
        Check(OcrCueReconciler.MergeTouchingIdentical([new(0, 1, "走", .99), new(1.066, 2, "走", .99)]).Count == 2,
            "real blank between repeated one-glyph captions was erased");
        Check(OcrCueReconciler.MergeTouchingIdentical([new(0, 1, "你走吧", .99), new(1, 2, "走", .99), new(2, 3, "啊", .99)]).Count == 3,
            "different or genuine short captions were merged");
        var spaced = OcrCueReconciler.MergeTouchingIdentical(
            [new(22.633, 22.7, "一 万年前", .87), new(22.7, 23.3, "一万年前", 1)]);
        Check(spaced.Count == 1 && spaced[0].Text == "一万年前" && spaced[0].Start == 22.633 && spaced[0].End == 23.3,
            "touching CJK whitespace variants were not normalized into one cue");
        var assembly = typeof(OcrResult).Assembly;
        var segmentType = assembly.GetType("BiliSubStudio.Core.Ocr.OcrScanSegment")!;
        var laneType = assembly.GetType("BiliSubStudio.Core.Ocr.OcrLaneCheckpoint")!;
        var lanes = Array.CreateInstance(laneType, 1);
        lanes.SetValue(Activator.CreateInstance(laneType,
            Activator.CreateInstance(segmentType, 0, 0d, 12d, 0d, 12d), 12d, fragments.ToList(), null, 360, 360, true), 0);
        var reconcile = typeof(OcrScanner).GetMethod("Reconcile", BindingFlags.Static | BindingFlags.NonPublic)!;
        var final = (IReadOnlyList<OcrCue>)reconcile.Invoke(null, [lanes, 0])!;
        Check(final.SequenceEqual(merged), "final same-lane SRT retains duplicated cues");
        var store = assembly.GetType("BiliSubStudio.Core.Ocr.OcrCheckpointStore")!;
        Check((int)store.GetField("Schema", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()! == 10,
            "old misrecognized checkpoint is still resume-compatible");
        return Task.CompletedTask;
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
