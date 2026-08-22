using System.Reflection;
using System.Runtime.CompilerServices;
using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

internal static class OcrTrackerModeRegression
{
    [ModuleInitializer]
    internal static void Verify()
    {
        var assembly = typeof(OcrResult).Assembly;
        var trackerType = assembly.GetType("BiliSubStudio.Core.Ocr.SubtitleTracker")
            ?? throw new InvalidOperationException("missing SubtitleTracker");
        var constructor = trackerType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(double), typeof(double)],
            modifiers: null)
            ?? throw new InvalidOperationException("missing mode-aware SubtitleTracker constructor");
        var observe = trackerType.GetMethod("Observe", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing SubtitleTracker.Observe");
        var active = trackerType.GetProperty("Active", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing SubtitleTracker.Active");

        var sample = new OcrResult(true, true, "你好", 0.60, []);
        object Tracker(double lowConfidence) => constructor.Invoke([1.5d, lowConfidence]);

        var fast = Tracker(0.58);
        observe.Invoke(fast, [0d, sample]);
        observe.Invoke(fast, [2d / 3d, sample]);
        if (active.GetValue(fast) is null)
            throw new InvalidOperationException("Fast OCR did not promote a 0.60-confidence two-hit subtitle");

        var accurate = Tracker(0.68);
        observe.Invoke(accurate, [0d, sample]);
        observe.Invoke(accurate, [0.25d, sample]);
        if (active.GetValue(accurate) is not null)
            throw new InvalidOperationException("Accurate OCR promoted a low-confidence subtitle before the third hit");
        observe.Invoke(accurate, [0.50d, sample]);
        if (active.GetValue(accurate) is null)
            throw new InvalidOperationException("Accurate OCR did not promote a stable third low-confidence hit");

        var interrupted = Tracker(0.58);
        observe.Invoke(interrupted, [0d, sample]);
        observe.Invoke(interrupted, [0.25d, new OcrResult(true, true, "A N", 0.95, [])]);
        observe.Invoke(interrupted, [0.50d, sample]);
        if (active.GetValue(interrupted) is not null)
            throw new InvalidOperationException("foreign-script OCR garbage did not break an unconfirmed subtitle candidate");
        observe.Invoke(interrupted, [1.00d, sample]);
        if (active.GetValue(interrupted) is null)
            throw new InvalidOperationException("subtitle candidate did not recover after fresh consecutive valid hits");

        var scannerType = typeof(OcrResult).Assembly.GetType("BiliSubStudio.Core.Ocr.OcrScanner")
            ?? throw new InvalidOperationException("missing OcrScanner");
        var similarity = scannerType.GetMethod("Similarity", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing OcrScanner.Similarity");
        var reversed = (double)(similarity.Invoke(null, ["不是", "是不"])
            ?? throw new InvalidOperationException("similarity returned null"));
        if (reversed >= 0.82)
            throw new InvalidOperationException("reordered Chinese text can still be merged at a lane boundary");
    }
}
