using System.Reflection;
using System.Runtime.CompilerServices;
using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

internal static class OcrOverlayFilterRegression
{
    [ModuleInitializer]
    internal static void Verify()
    {
        var scanner = typeof(OcrResult).Assembly.GetType("BiliSubStudio.Core.Ocr.OcrScanner")
            ?? throw new InvalidOperationException("missing OcrScanner");
        var filter = scanner.GetMethod("FilterOffBaselineOverlayLines", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing OCR overlay filter");

        var sceneTitle = new OcrResult(true, true, "宗忌森林", .68,
            [new OcrLine("宗忌森林", .68, [49, 30, 497, 131])]);
        var defaultLowerRegion = new OcrRegion(.05, .65, .90, .29);
        var filtered = (OcrResult)(filter.Invoke(null, [sceneTitle, defaultLowerRegion])
            ?? throw new InvalidOperationException("OCR overlay filter returned null"));
        if (filtered.Detected || filtered.Lines.Count != 0)
            throw new InvalidOperationException("left-aligned upper scene title leaked into default lower subtitle OCR");

        var dialogue = new OcrResult(true, true, "所以？", .84,
            [new OcrLine("所以？", .84, [560, 221, 698, 278])]);
        var retainedDialogue = (OcrResult)(filter.Invoke(null, [dialogue, defaultLowerRegion])
            ?? throw new InvalidOperationException("OCR overlay filter returned null for dialogue"));
        if (!retainedDialogue.Detected || retainedDialogue.Text != "所以？")
            throw new InvalidOperationException("bottom dialogue was discarded as an overlay");

        var upperRegion = new OcrRegion(.05, .20, .90, .29);
        var retainedUpper = (OcrResult)(filter.Invoke(null, [sceneTitle, upperRegion])
            ?? throw new InvalidOperationException("OCR overlay filter returned null for upper ROI"));
        if (!retainedUpper.Detected || retainedUpper.Text != "宗忌森林")
            throw new InvalidOperationException("user-selected upper ROI lost valid OCR text");
    }
}
