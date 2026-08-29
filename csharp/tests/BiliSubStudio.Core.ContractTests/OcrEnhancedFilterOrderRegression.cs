using System.Reflection;
using System.Runtime.CompilerServices;
using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

internal static class OcrEnhancedFilterOrderRegression
{
    [ModuleInitializer]
    internal static void Verify()
    {
        var scanner = typeof(OcrResult).Assembly.GetType("BiliSubStudio.Core.Ocr.OcrScanner")
            ?? throw new InvalidOperationException("missing OcrScanner");
        var filter = scanner.GetMethod("FilterOffBaselineOverlayLines", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing OCR overlay filter");
        var needsEnhanced = scanner.GetMethod("NeedsEnhancedRecognition", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing OCR enhanced decision helper");
        var prefer = scanner.GetMethod("PreferRecognition", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing OCR recognition preference");
        var lowerRegion = new OcrRegion(.05, .65, .90, .29);

        var overlay = new OcrResult(true, true, "宗忌森林", .99,
            [new OcrLine("宗忌森林", .99, [49, 30, 497, 131])]);
        var filteredOverlay = (OcrResult)(filter.Invoke(null, [overlay, lowerRegion])
            ?? throw new InvalidOperationException("overlay filtering returned null"));
        if ((bool)(needsEnhanced.Invoke(null, [filteredOverlay, .68d]) ?? true))
            throw new InvalidOperationException("filtered overlay still controlled the enhanced OCR decision");

        var lowDialogue = new OcrResult(true, true, "整整一万年", .60,
            [new OcrLine("整整一万年", .60, [514, 221, 765, 277])]);
        var filteredDialogue = (OcrResult)(filter.Invoke(null, [lowDialogue, lowerRegion])
            ?? throw new InvalidOperationException("dialogue filtering returned null"));
        if (!(bool)(needsEnhanced.Invoke(null, [filteredDialogue, .68d]) ?? false))
            throw new InvalidOperationException("retained low-confidence dialogue did not request enhanced OCR");
        if ((bool)(prefer.Invoke(null, [filteredOverlay, filteredDialogue]) ?? true))
            throw new InvalidOperationException("filtered enhanced overlay replaced retained dialogue");

        var completePrimary = new OcrResult(true, true, "吃我的喝我的", .70, []);
        var shorterEnhanced = new OcrResult(true, true, "吃我的喝的", .90, []);
        if ((bool)(prefer.Invoke(null, [shorterEnhanced, completePrimary]) ?? true))
            throw new InvalidOperationException("higher-confidence enhanced retry erased a glyph from the fuller primary reading");

        var digitNoise = new OcrResult(true, true, "万年\n8", .74310483,
            [new OcrLine("万年", .999969065, [610, 140, 720, 202]),
             new OcrLine("8", .486240596, [505, 154, 518, 170])]);
        var filteredDigitNoise = (OcrResult)(filter.Invoke(null, [digitNoise, lowerRegion])
            ?? throw new InvalidOperationException("digit-noise filtering returned null"));
        if (!filteredDigitNoise.Detected || filteredDigitNoise.Text != "万年" || filteredDigitNoise.Lines.Count != 1)
            throw new InvalidOperationException("tiny low-confidence digit companion survived beside a strong Chinese subtitle");

        var legitimateDigit = new OcrResult(true, true, "第8集", .99,
            [new OcrLine("第8集", .99, [500, 140, 700, 202])]);
        var retainedDigit = (OcrResult)(filter.Invoke(null, [legitimateDigit, lowerRegion])
            ?? throw new InvalidOperationException("legitimate-digit filtering returned null"));
        if (!retainedDigit.Detected || retainedDigit.Text != "第8集")
            throw new InvalidOperationException("a legitimate digit inside a Chinese subtitle was removed");
    }
}
