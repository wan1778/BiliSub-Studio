using System.Reflection;
using System.Runtime.CompilerServices;
using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

internal static class OcrTrackerFullerTextRegression
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
        var active = trackerType.GetProperty("Active", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing SubtitleTracker.Active");
        var canCheckpoint = trackerType.GetProperty("CanCheckpoint", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing SubtitleTracker.CanCheckpoint");

        const double frame = 1d / 30d;
        var shortText = new OcrResult(true, true, "吃我的喝我", .98, []);
        var completeText = new OcrResult(true, true, "吃我的喝我的", .84, []);

        var recovered = constructor.Invoke([30d, .68d, true]);
        observe.Invoke(recovered, [11d, frame, shortText]);
        observe.Invoke(recovered, [11d + frame, frame, shortText]);
        observe.Invoke(recovered, [11d + 2d * frame, frame, completeText]);
        var afterOneLonger = (OcrCue)(active.GetValue(recovered)
            ?? throw new InvalidOperationException("tracker did not promote the stable short subtitle candidate"));
        if (afterOneLonger.Text != "吃我的喝我")
            throw new InvalidOperationException("one lower-confidence longer OCR read overrode a high-confidence active cue");
        if ((bool)(canCheckpoint.GetValue(recovered) ?? true))
            throw new InvalidOperationException("tracker checkpointed while fuller-text evidence was unresolved");

        observe.Invoke(recovered, [11d + 3d * frame, frame, completeText]);
        var recoveredCue = (OcrCue)(active.GetValue(recovered)
            ?? throw new InvalidOperationException("tracker lost the active cue while recovering fuller text"));
        if (recoveredCue.Text != "吃我的喝我的")
            throw new InvalidOperationException("two consecutive compatible fuller reads did not recover the omitted glyph");
        if (!(bool)(canCheckpoint.GetValue(recovered) ?? false))
            throw new InvalidOperationException("resolved fuller-text evidence kept checkpointing blocked");

        var interrupted = constructor.Invoke([30d, .68d, true]);
        observe.Invoke(interrupted, [20d, frame, shortText]);
        observe.Invoke(interrupted, [20d + frame, frame, shortText]);
        observe.Invoke(interrupted, [20d + 2d * frame, frame, completeText]);
        observe.Invoke(interrupted, [20d + 3d * frame, frame, shortText]);
        observe.Invoke(interrupted, [20d + 4d * frame, frame, completeText]);
        var interruptedCue = (OcrCue)(active.GetValue(interrupted)
            ?? throw new InvalidOperationException("tracker lost interrupted fuller-text fixture"));
        if (interruptedCue.Text != "吃我的喝我")
            throw new InvalidOperationException("non-consecutive fuller OCR evidence overrode the active cue");
        observe.Invoke(interrupted, [20d + 5d * frame, frame, completeText]);
        interruptedCue = (OcrCue)(active.GetValue(interrupted)
            ?? throw new InvalidOperationException("tracker lost the recovered interrupted fixture"));
        if (interruptedCue.Text != "吃我的喝我的")
            throw new InvalidOperationException("fresh consecutive fuller OCR evidence did not recover after interruption");
    }
}
