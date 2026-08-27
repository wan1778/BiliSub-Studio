using System.Reflection;
using System.Runtime.CompilerServices;
using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

internal static class OcrActiveCueBlankRecoveryRegression
{
    [ModuleInitializer]
    internal static void Verify()
    {
        var scanner = typeof(OcrResult).Assembly.GetType("BiliSubStudio.Core.Ocr.OcrScanner")
            ?? throw new InvalidOperationException("missing OcrScanner");
        var needsRecovery = scanner.GetMethod("NeedsActiveCueBlankRecovery", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing active-cue blank recovery decision");
        var blank = new OcrResult(true, false, string.Empty, 0, []);
        var detected = new OcrResult(true, true, "整整一万年", .60, []);
        var failed = new OcrResult(false, false, string.Empty, 0, [], "worker failed");

        bool Needs(OcrResult result, bool active) => (bool)(needsRecovery.Invoke(null, [result, active])
            ?? throw new InvalidOperationException("blank recovery decision returned null"));

        if (Needs(blank, false))
            throw new InvalidOperationException("background blank OCR frame incorrectly requested enhanced recovery");
        if (!Needs(blank, true))
            throw new InvalidOperationException("single blank OCR frame during an active cue did not request recovery");
        if (Needs(detected, true) || Needs(failed, true))
            throw new InvalidOperationException("blank recovery retried detected or failed OCR result");

        var trackerType = typeof(OcrResult).Assembly.GetType("BiliSubStudio.Core.Ocr.SubtitleTracker")
            ?? throw new InvalidOperationException("missing SubtitleTracker");
        var tracker = Activator.CreateInstance(trackerType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, args: [30d, .68d, true], culture: null)
            ?? throw new InvalidOperationException("could not create exact-frame SubtitleTracker");
        var observe = trackerType.GetMethod("Observe", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, types: [typeof(double), typeof(double), typeof(OcrResult)], modifiers: null)
            ?? throw new InvalidOperationException("missing exact-frame tracker observe");
        var active = trackerType.GetProperty("Active", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing tracker active cue");
        const double frame = 1d / 30d;
        observe.Invoke(tracker, [1d, frame, detected]);
        observe.Invoke(tracker, [1d + frame, frame, detected]);
        observe.Invoke(tracker, [1d + 2d * frame, frame, detected]); // enhanced recovery result replaces the suspicious blank.
        if (active.GetValue(tracker) is not OcrCue cue || cue.Text != detected.Text)
            throw new InvalidOperationException("recovered OCR frame did not preserve the active cue");
    }
}
