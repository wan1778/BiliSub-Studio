using System.Reflection;
using System.Runtime.CompilerServices;
using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

internal static class OcrSampledOneRuneRegression
{
    [ModuleInitializer]
    internal static void Verify()
    {
        var trackerType = typeof(OcrResult).Assembly.GetType("BiliSubStudio.Core.Ocr.SubtitleTracker")
            ?? throw new InvalidOperationException("missing SubtitleTracker");
        var constructor = trackerType.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, types: [typeof(double), typeof(double), typeof(bool)], modifiers: null)
            ?? throw new InvalidOperationException("missing exact-frame SubtitleTracker constructor");
        var observe = trackerType.GetMethod("Observe", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, types: [typeof(double), typeof(double), typeof(OcrResult)], modifiers: null)
            ?? throw new InvalidOperationException("missing exact-frame SubtitleTracker observe");
        var active = trackerType.GetProperty("Active", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing SubtitleTracker active cue");
        var high = new OcrResult(true, true, "走", .98, []);
        var low = new OcrResult(true, true, "走", .58, []);

        object Tracker(bool exact) => constructor.Invoke([2.5d, .68d, exact]);
        void Observe(object tracker, int count, OcrResult result)
        {
            for (var index = 0; index < count; index++) observe.Invoke(tracker, [index / 2.5d, .4d, result]);
        }

        var sampledHigh = Tracker(exact: false);
        Observe(sampledHigh, 2, high);
        if (active.GetValue(sampledHigh) is not OcrCue cue || cue.Text != "走")
            throw new InvalidOperationException("sampled high-confidence one-rune subtitle still required three hits");

        var sampledLow = Tracker(exact: false);
        Observe(sampledLow, 2, low);
        if (active.GetValue(sampledLow) is not null)
            throw new InvalidOperationException("sampled low-confidence one-rune subtitle promoted before a third hit");
        Observe(sampledLow, 1, low);
        if (active.GetValue(sampledLow) is null)
            throw new InvalidOperationException("sampled low-confidence one-rune subtitle did not promote after three hits");

        var accurateHigh = Tracker(exact: true);
        Observe(accurateHigh, 2, high);
        if (active.GetValue(accurateHigh) is not null)
            throw new InvalidOperationException("every-frame one-rune subtitle stopped requiring three source frames");
        Observe(accurateHigh, 1, high);
        if (active.GetValue(accurateHigh) is null)
            throw new InvalidOperationException("stable one-rune subtitle did not promote after sufficient evidence");
    }
}
