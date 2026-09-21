using System.Reflection;
using System.Runtime.CompilerServices;
using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

internal static class OcrCheckpointRegionRegression
{
    [ModuleInitializer]
    internal static void Verify()
    {
        var storeType = typeof(OcrRegion).Assembly.GetType("BiliSubStudio.Core.Ocr.OcrCheckpointStore")
            ?? throw new InvalidOperationException("missing OCR checkpoint store");
        var canonical = storeType.GetMethod("CanonicalRegion", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing OCR canonical ROI policy");

        static OcrRegion Apply(MethodInfo method, OcrRegion region) =>
            (OcrRegion)(method.Invoke(null, [region]) ?? throw new InvalidOperationException("canonical ROI returned null"));

        var dragged = Apply(canonical, new OcrRegion(0.0537, 0.6523, 0.8920, 0.2860));
        var reopened = Apply(canonical, new OcrRegion(0.05, 0.65, 0.90, 0.29));

        if (dragged != reopened || dragged != new OcrRegion(0.05, 0.65, 0.90, 0.29))
            throw new InvalidOperationException("OCR ROI identity changes after config percent persistence/restart");

        // Resume-override rule (pause + sua ROI + tiep tuc): giong het -> khong
        // override (chay nhu cu); mo rong khung (text tran nhu vu user) -> override.
        var untouched = new OcrRegion(0.05, 0.65, 0.90, 0.29);
        if (!untouched.Equals(reopened))
            throw new InvalidOperationException("untouched ROI must resume without override");
        var widened = Apply(canonical, new OcrRegion(0.03, 0.65, 0.94, 0.29));
        if (widened.Equals(reopened))
            throw new InvalidOperationException("widened ROI must trigger resume override");
    }
}
