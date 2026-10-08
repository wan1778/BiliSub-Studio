using System.Reflection;
using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

// Scan-mode to model-tier mapping: only Accurate-family modes run Medium
// (untouched); everything else (Balanced default, Fast, unknown) runs Mobile
// nhỏ nhất (28/09, theo lệnh user). Aliases must mirror
// OcrCheckpointStore.ModeFor so sampling policy and model tier never disagree
// about which mode is which.
internal static class OcrModelTierContract
{
    public static Task RunAsync()
    {
        var tierType = typeof(OcrScanRequest).Assembly.GetType("BiliSubStudio.Core.Ocr.OcrModelTier")
            ?? throw new InvalidOperationException("missing OcrModelTier");
        var flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var det = tierType.GetMethod("DetectionModel", flags)
            ?? throw new InvalidOperationException("missing DetectionModel");
        var rec = tierType.GetMethod("RecognitionModel", flags)
            ?? throw new InvalidOperationException("missing RecognitionModel");
        var name = tierType.GetMethod("TierName", flags)
            ?? throw new InvalidOperationException("missing TierName");
        string Det(string mode) => (string)det.Invoke(null, [mode])!;
        string Rec(string mode) => (string)rec.Invoke(null, [mode])!;
        string Tier(string mode) => (string)name.Invoke(null, [mode])!;

        foreach (var mode in new[] { "accurate", "Accurate", " ACCURATE ", "precise", "chinh-xac" })
        {
            Check(Det(mode) == "PP-OCRv6_medium_det", $"accurate-family mode lost Medium det: {mode}");
            Check(Rec(mode) == "PP-OCRv6_medium_rec", $"accurate-family mode lost Medium rec: {mode}");
            Check(Tier(mode) == "medium", $"accurate-family tier name drifted: {mode}");
        }
        foreach (var mode in new[] { "balanced", "fast", "nhanh", "BALANCED", "unknown-mode", "", "   " })
        {
            Check(Det(mode) == "PP-OCRv5_mobile_det", $"sparse mode lost Mobile det: '{mode}'");
            Check(Rec(mode) == "PP-OCRv5_mobile_rec", $"sparse mode lost Mobile rec: '{mode}'");
            Check(Tier(mode) == "mobile", $"sparse tier name drifted: '{mode}'");
        }
        return Task.CompletedTask;
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
