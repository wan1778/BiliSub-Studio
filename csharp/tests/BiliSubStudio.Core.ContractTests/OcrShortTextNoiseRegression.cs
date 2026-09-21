using System.Reflection;
using System.Runtime.CompilerServices;
using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

internal static class OcrShortTextNoiseRegression
{
    [ModuleInitializer]
    internal static void Verify()
    {
        var trackerType = typeof(OcrResult).Assembly.GetType("BiliSubStudio.Core.Ocr.SubtitleTracker")
            ?? throw new InvalidOperationException("missing SubtitleTracker");
        var constructor = trackerType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(double), typeof(double), typeof(bool)],
            modifiers: null)
            ?? throw new InvalidOperationException("missing exact-frame SubtitleTracker constructor");
        var observe = trackerType.GetMethod(
            "Observe",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(double), typeof(double), typeof(OcrResult)],
            modifiers: null)
            ?? throw new InvalidOperationException("missing exact-frame SubtitleTracker.Observe");
        var cues = trackerType.GetProperty("Cues", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing SubtitleTracker.Cues");
        var active = trackerType.GetProperty("Active", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing SubtitleTracker.Active");
        var frame = 1d / 30d;
        var falseRead = new OcrResult(true, true, "小小", .99, []);
        var blank = new OcrResult(true, false, string.Empty, 0, []);

        var transient = constructor.Invoke([30d, .68d, true]);
        observe.Invoke(transient, [10d, frame, falseRead]);
        observe.Invoke(transient, [10d + frame, frame, falseRead]);
        observe.Invoke(transient, [10d + 2 * frame, frame, blank]);
        observe.Invoke(transient, [10d + 3 * frame, frame, blank]);
        if (((IReadOnlyList<OcrCue>)cues.GetValue(transient)!).Count != 0
            || active.GetValue(transient) is not null)
            throw new InvalidOperationException("two-frame scene shapes became a two-rune subtitle cue");

        var longTransient = constructor.Invoke([30d, .68d, true]);
        var falseSentence = falseRead with { Text = "这不是字幕" };
        observe.Invoke(longTransient, [15d, frame, falseSentence]);
        observe.Invoke(longTransient, [15d + frame, frame, falseSentence]);
        observe.Invoke(longTransient, [15d + 2 * frame, frame, blank]);
        observe.Invoke(longTransient, [15d + 3 * frame, frame, blank]);
        if (((IReadOnlyList<OcrCue>)cues.GetValue(longTransient)!).Count != 0
            || active.GetValue(longTransient) is not null)
            throw new InvalidOperationException("two-frame scene text bypassed the global time filter");

        var genuine = constructor.Invoke([30d, .68d, true]);
        for (var index = 0; index < 6; index++)
            observe.Invoke(genuine, [20d + index * frame, frame, falseRead]);
        // A strong caption now tolerates brief dropouts, so four trailing
        // blanks (not two) close it; production lane ends via Finish anyway.
        for (var index = 6; index < 10; index++)
            observe.Invoke(genuine, [20d + index * frame, frame, blank]);
        var retained = (IReadOnlyList<OcrCue>)cues.GetValue(genuine)!;
        if (retained.Count != 1 || retained[0].Text != "小小"
            || retained[0].End - retained[0].Start < .20 - .000001)
            throw new InvalidOperationException("stable readable two-rune subtitle was discarded");
    }
}
