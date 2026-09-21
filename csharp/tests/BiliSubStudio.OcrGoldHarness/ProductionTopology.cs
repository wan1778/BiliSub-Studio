using System.Reflection;
using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.OcrGoldHarness;

// Reads the production lane segmentation (OcrCheckpointStore is internal).
// Replicating the segment math here would drift from production on the first
// policy tweak, so the harness reflects the exact values the scanner used.
internal sealed record ProductionSegment(int Index, double CoreStart, double CoreEnd, double ScanStart, double ScanEnd);

internal static class ProductionTopology
{
    public static IReadOnlyList<ProductionSegment> BuildSegments(string mode, double sensitivity, double duration, int parallelism, double offset)
    {
        var store = typeof(OcrScanRequest).Assembly.GetType("BiliSubStudio.Core.Ocr.OcrCheckpointStore")
            ?? throw new ManifestException("missing OcrCheckpointStore");
        var modeFor = store.GetMethod("ModeFor", BindingFlags.Public | BindingFlags.Static)
            ?? throw new ManifestException("missing OcrCheckpointStore.ModeFor");
        var scanMode = modeFor.Invoke(null, [mode, sensitivity])
            ?? throw new ManifestException("ModeFor returned no scan mode.");
        double Value(string property)
        {
            var value = scanMode.GetType().GetProperty(property)?.GetValue(scanMode)
                ?? throw new ManifestException($"missing OcrScanMode.{property}");
            return Convert.ToDouble(value);
        }
        var overlap = Math.Max(Value("Guard"), Value("ActiveGuard"));
        var build = store.GetMethod("BuildSegments", BindingFlags.Public | BindingFlags.Static)
            ?? throw new ManifestException("missing OcrCheckpointStore.BuildSegments");
        var segments = (System.Collections.IEnumerable)build.Invoke(null, [duration, parallelism, overlap, offset])!;
        var result = new List<ProductionSegment>();
        foreach (var segment in segments)
        {
            var type = segment.GetType();
            double Get(string property) => Convert.ToDouble(type.GetProperty(property)!.GetValue(segment)!);
            result.Add(new ProductionSegment((int)type.GetProperty("Index")!.GetValue(segment)!,
                Get("CoreStart"), Get("CoreEnd"), Get("ScanStart"), Get("ScanEnd")));
        }
        if (result.Count != parallelism)
            throw new ManifestException($"production segmentation returned {result.Count} lanes for parallelism {parallelism}.");
        return result;
    }
}
