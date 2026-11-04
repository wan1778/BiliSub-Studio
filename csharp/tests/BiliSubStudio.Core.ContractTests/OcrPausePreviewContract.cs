using System.Reflection;
using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

// Fast-pause regression (the "loading 2529 frames in parallel" wait, 15/9): the pause preview MUST be
// the pure function BuildPausedPreview (lane-owned cues trimmed by the frontier, sorted) — NO FFmpeg,
// NO pixel, NO worker. Pixel merging is reserved for finish/export/open-checkpoint.
// Bound ownership copies Reconcile exactly: a normal lane has Start < CoreEnd, the last lane Start <= CoreEnd,
// Start >= CoreStart; trim Start > frontier.
internal static class OcrPausePreviewContract
{
    public static Task RunAsync()
    {
        var assembly = typeof(OcrManager).Assembly;
        var preview = assembly.GetType("BiliSubStudio.Core.Ocr.OcrScanner")!
            .GetMethod("BuildPausedPreview", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException("missing BuildPausedPreview");
        var segType = assembly.GetType("BiliSubStudio.Core.Ocr.OcrScanSegment")
            ?? throw new InvalidOperationException("missing OcrScanSegment");
        var laneType = assembly.GetType("BiliSubStudio.Core.Ocr.OcrLaneCheckpoint")
            ?? throw new InvalidOperationException("missing OcrLaneCheckpoint");
        var segCtor = segType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .First(c => c.GetParameters().Length == 5);
        var laneCtor = laneType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .First(c => c.GetParameters().Length == 7);
        var listType = typeof(List<>).MakeGenericType(laneType);

        object Seg(int index, double coreStart, double coreEnd)
            => segCtor.Invoke([index, coreStart, coreEnd, Math.Max(0, coreStart - 5), coreEnd + 5]);
        object Lane(object seg, double media, List<OcrCue> cues)
            => laneCtor.Invoke([seg, media, cues, null, 0, 0, false])!;
        IReadOnlyList<OcrCue> Preview(object lanes, double frontier)
        {
            var raw = (System.Collections.IList)preview.Invoke(null, [lanes, frontier])!;
            return raw.Cast<OcrCue>().ToArray();
        }
        object Lanes(params object[] lanes)
        {
            var list = Activator.CreateInstance(listType)!;
            var add = listType.GetMethod("Add")!;
            foreach (var lane in lanes) add.Invoke(list, [lane]);
            return list;
        }

        // 1. Two lanes, frontier 150: keep owned, drop pre-core/over-guard/beyond-frontier, sort.
        var lanes = Lanes(
            Lane(Seg(0, 0, 100), 60, [
                new OcrCue(-10, -5, "pre", 0.9),
                new OcrCue(10, 20, "a", 0.9),
                new OcrCue(99, 101, "guard", 0.9),
                new OcrCue(100, 110, "next-lane", 0.9),
            ]),
            Lane(Seg(1, 100, 200), 150, [
                new OcrCue(100, 110, "b", 0.9),
                new OcrCue(160, 170, "future", 0.9),
                new OcrCue(200, 210, "last-edge", 0.9),
                new OcrCue(200.5, 210, "past-end", 0.9),
            ]));
        var view = Preview(lanes, 150);
        Check(view.Count == 3, $"pause preview must keep 3 owned cues (got {view.Count})");
        Check(view[0].Text == "a" && view[1].Text == "guard" && view[2].Text == "b",
            "preview must be ordered a/guard/b with pre/next-lane/future cut");
        Check(view.All(c => c.Start <= 150.001), "preview must cut cues beyond frontier");

        // 2. Frontier 250: the last lane includes Start == CoreEnd (<=), drops Start > CoreEnd.
        var wide = Preview(lanes, 250);
        Check(wide.Count == 5, $"wide frontier must keep 5 cues (got {wide.Count})");
        Check(wide[3].Text == "future" && wide[4].Text == "last-edge",
            "last lane must include Start == CoreEnd but exclude Start > CoreEnd");

        // 3. Empty lane + frontier 0: empty, no throw, no FFmpeg/worker touched (pure function).
        var empty = Preview(Lanes(Lane(Seg(0, 0, 100), 0, [])), 0);
        Check(empty.Count == 0, "empty lanes must preview empty");
        return Task.CompletedTask;
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
