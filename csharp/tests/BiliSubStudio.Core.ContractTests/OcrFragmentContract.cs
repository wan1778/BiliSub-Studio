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
        var fieldFragments = OcrCueReconciler.MergeTouchingIdentical(
        [
            new(24310.8, 24310.966688, "少主……是不死丹帝， 药逆命", .99),
            new(24310.966688, 24312.133313, "少主……是不死丹帝， 药逆命", .99),
            new(24312.133313, 24312.266687, "少主……是是不死丹帝，药逆命", .999),
            new(24312.266687, 24312.533312, "少主……是不死丹帝， 药逆命", .99),
            new(24312.533312, 24313.233313, "少主……是不死丹帝，药逆命", .99),
        ]);
        Check(fieldFragments.Count == 1 && fieldFragments[0].Text == "少主……是不死丹帝，药逆命"
            && fieldFragments[0].Start == 24310.8 && fieldFragments[0].End == 24313.233313,
            "field punctuation and one-frame duplicated glyph still fragmented one rendered caption");
        var substituted = OcrCueReconciler.MergeTouchingIdentical(
        [
            new(142.967, 143.133, "万年没出门", .99),
            new(143.133, 143.967, "万年设出门", .999),
            new(143.967, 144.167, "万年没出门", .99),
        ]);
        Check(substituted.Count == 1 && substituted[0].Text == "万年没出门"
            && substituted[0].Start == 142.967 && substituted[0].End == 144.167,
            "A/B/A one-glyph temporal substitution survived into final SRT cues");
        var legitimateChange = OcrCueReconciler.MergeTouchingIdentical(
        [
            new(200, 200.5, "你要走吗", .99),
            new(200.5, 201.1, "你要来吗", .99),
            new(201.1, 202.0, "我不知道", .99),
        ]);
        Check(legitimateChange.Count == 3,
            "one-way similar real captions were erased without A/B/A consensus");
        var deliberateSandwich = OcrCueReconciler.MergeTouchingIdentical(
        [
            new(210, 210.5, "你要走吗", .99),
            new(210.5, 211.0, "你要来吗", .99),
            new(211.0, 211.5, "你要走吗", .99),
        ]);
        Check(deliberateSandwich.Count == 3,
            "three sustained real A/B/A captions were treated as a frame-edge flicker");
        var live = new OcrLiveCueAccumulator();
        live.Merge([new(24310.966688, 24311.966688, "少主……是不死丹帝， 药逆命", .99)]);
        live.Merge([
            new(24310.8, 24312.133313, "少主……是不死丹帝， 药逆命", .99),
            new(24312.133313, 24312.266687, "少主……是是不死丹帝，药逆命", .999),
            new(24312.266687, 24312.533312, "少主……是不死丹帝， 药逆命", .99),
            new(24312.533312, 24313.233313, "少主……是不死丹帝，药逆命", .99),
        ]);
        Check(live.Cues.Count == 1 && live.Cues[0].Text == "少主……是不死丹帝，药逆命"
            && live.Cues[0].Start == 24310.8 && live.Cues[0].End == 24313.233313,
            "superseded active OCR snapshot remained as an overlapping ghost row");
        var evolving = new OcrLiveCueAccumulator();
        evolving.Merge([new(10, 10.5, "整整一万年", .90)]);
        evolving.Merge([new(10, 10.8, "一万年", .99)]);
        Check(evolving.Cues.Count == 1 && evolving.Cues[0].Text == "整整一万年" && evolving.Cues[0].End == 10.8,
            "latest live timing did not replace the stale active end while preserving fuller text");
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
        Check((int)store.GetField("Schema", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()! == 14,
            "fixed-FPS checkpoint is still resume-compatible");
        return Task.CompletedTask;
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
