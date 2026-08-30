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
            throw new InvalidOperationException("two compatible fuller reads did not recover the omitted glyph");
        if (!(bool)(canCheckpoint.GetValue(recovered) ?? false))
            throw new InvalidOperationException("resolved fuller-text evidence kept checkpointing blocked");

        var interleaved = constructor.Invoke([30d, .68d, true]);
        observe.Invoke(interleaved, [20d, frame, shortText]);
        observe.Invoke(interleaved, [20d + frame, frame, shortText]);
        observe.Invoke(interleaved, [20d + 2d * frame, frame, completeText]);
        observe.Invoke(interleaved, [20d + 3d * frame, frame, shortText]);
        observe.Invoke(interleaved, [20d + 4d * frame, frame, completeText]);
        var interleavedCue = (OcrCue)(active.GetValue(interleaved)
            ?? throw new InvalidOperationException("tracker lost interleaved fuller-text fixture"));
        if (interleavedCue.Text != "吃我的喝我的")
            throw new InvalidOperationException("full-short-full OCR evidence inside one caption did not recover the omitted glyph");
        if (!(bool)(canCheckpoint.GetValue(interleaved) ?? false))
            throw new InvalidOperationException("resolved interleaved fuller-text evidence kept checkpointing blocked");

        var middleOmission = constructor.Invoke([30d, .68d, true]);
        var missingMiddle = new OcrResult(true, true, "吃我的喝的", .98, []);
        observe.Invoke(middleOmission, [25d, frame, missingMiddle]);
        observe.Invoke(middleOmission, [25d + frame, frame, missingMiddle]);
        observe.Invoke(middleOmission, [25d + 2d * frame, frame, completeText]);
        observe.Invoke(middleOmission, [25d + 3d * frame, frame, completeText]);
        var middleRecoveredCue = (OcrCue)(active.GetValue(middleOmission)
            ?? throw new InvalidOperationException("tracker lost internal-omission fixture"));
        if (middleRecoveredCue.Text != "吃我的喝我的")
            throw new InvalidOperationException("two fuller reads did not recover a glyph omitted inside the cue");

        var expired = constructor.Invoke([30d, .68d, true]);
        observe.Invoke(expired, [30d, frame, shortText]);
        observe.Invoke(expired, [30d + frame, frame, shortText]);
        observe.Invoke(expired, [30d + 2d * frame, frame, completeText]);
        observe.Invoke(expired, [31d, frame, shortText]);
        observe.Invoke(expired, [31d + frame, frame, completeText]);
        var expiredCue = (OcrCue)(active.GetValue(expired)
            ?? throw new InvalidOperationException("tracker lost expired fuller-text fixture"));
        if (expiredCue.Text != "吃我的喝我")
            throw new InvalidOperationException("stale fuller-text evidence leaked across the safety window");
        observe.Invoke(expired, [31d + 2d * frame, frame, completeText]);
        expiredCue = (OcrCue)(active.GetValue(expired)
            ?? throw new InvalidOperationException("tracker lost renewed fuller-text fixture"));
        if (expiredCue.Text != "吃我的喝我的")
            throw new InvalidOperationException("fresh fuller-text evidence after expiry did not recover the omitted glyph");

        var punctuationAndDuplicate = constructor.Invoke([30d, .68d, true]);
        var stableSpaced = new OcrResult(true, true, "少主……是不死丹帝， 药逆命", .99, []);
        var stable = new OcrResult(true, true, "少主……是不死丹帝，药逆命", .99, []);
        var duplicated = new OcrResult(true, true, "少主……是是不死丹帝，药逆命", .999, []);
        observe.Invoke(punctuationAndDuplicate, [40d, frame, stableSpaced]);
        observe.Invoke(punctuationAndDuplicate, [40d + frame, frame, stableSpaced]);
        observe.Invoke(punctuationAndDuplicate, [40d + 2d * frame, frame, stable]);
        observe.Invoke(punctuationAndDuplicate, [40d + 3d * frame, frame, duplicated]);
        observe.Invoke(punctuationAndDuplicate, [40d + 4d * frame, frame, stable]);
        observe.Invoke(punctuationAndDuplicate, [40d + 5d * frame, frame, stable]);
        var stableCue = (OcrCue)(active.GetValue(punctuationAndDuplicate)
            ?? throw new InvalidOperationException("tracker lost punctuation/duplicate fixture"));
        var committed = (IReadOnlyList<OcrCue>)(trackerType.GetProperty("Cues")!.GetValue(punctuationAndDuplicate)
            ?? throw new InvalidOperationException("tracker did not expose committed cues"));
        if (stableCue.Text != "少主……是不死丹帝，药逆命" || committed.Count != 0)
            throw new InvalidOperationException("one-frame duplicated glyph or punctuation spacing fragmented a stable caption");

        var legitimateRepeat = constructor.Invoke([30d, .68d, true]);
        var oneThanks = new OcrResult(true, true, "谢", .99, []);
        var twoThanks = new OcrResult(true, true, "谢谢", .94, []);
        observe.Invoke(legitimateRepeat, [50d, frame, oneThanks]);
        observe.Invoke(legitimateRepeat, [50d + frame, frame, oneThanks]);
        observe.Invoke(legitimateRepeat, [50d + 2d * frame, frame, oneThanks]);
        observe.Invoke(legitimateRepeat, [50d + 3d * frame, frame, twoThanks]);
        observe.Invoke(legitimateRepeat, [50d + 4d * frame, frame, twoThanks]);
        var repeatedCue = (OcrCue)(active.GetValue(legitimateRepeat)
            ?? throw new InvalidOperationException("tracker lost legitimate repeated-glyph fixture"));
        if (repeatedCue.Text != "谢谢")
            throw new InvalidOperationException("stable repeated Chinese glyph was rejected as a transient duplicate");
    }
}
