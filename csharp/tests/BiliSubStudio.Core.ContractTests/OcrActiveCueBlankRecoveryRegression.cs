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
        var needsRefinement = scanner.GetMethod("NeedsAdaptiveRefinement", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing adaptive OCR transition decision");
        var blank = new OcrResult(true, false, string.Empty, 0, []);
        var detected = new OcrResult(true, true, "整整一万年", .60, []);
        var same = new OcrResult(true, true, "整整一万年", .99, []);
        var subtext = new OcrResult(true, true, "一万年", .99, []);
        var changed = new OcrResult(true, true, "你走吧", .99, []);

        bool Needs(OcrResult previous, OcrResult current) => (bool)(needsRefinement.Invoke(null, [previous, current])
            ?? throw new InvalidOperationException("adaptive transition decision returned null"));

        if (Needs(blank, blank) || Needs(detected, same))
            throw new InvalidOperationException("stable adaptive OCR samples incorrectly requested frame refinement");
        if (!Needs(blank, detected) || !Needs(detected, blank) || !Needs(detected, subtext) || !Needs(detected, changed))
            throw new InvalidOperationException("adaptive OCR missed a blank/text or changed-text transition");

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
