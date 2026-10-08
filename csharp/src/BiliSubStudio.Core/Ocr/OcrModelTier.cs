namespace BiliSubStudio.Core.Ocr;

// Model tier per scan mode: Accurate keeps Medium for maximum fidelity,
// untouched; Balanced/Fast run Mobile (nhỏ nhất, theo lệnh user 28/09).
// Aliases mirror OcrCheckpointStore.ModeFor so tier and sampling policy never
// disagree; unknown modes fall to Mobile like ModeFor falls to Balanced.
// Checkpoint key ăn tên model nên đổi tier là checkpoint cũ vô hiệu (quét mới).
internal static class OcrModelTier
{
    public static string DetectionModel(string scanMode) =>
        IsAccurate(scanMode) ? OcrInstaller.DetectionModel : OcrInstaller.MobileDetectionModel;

    public static string RecognitionModel(string scanMode) =>
        IsAccurate(scanMode) ? OcrInstaller.RecognitionModel : OcrInstaller.MobileRecognitionModel;

    public static string TierName(string scanMode) => IsAccurate(scanMode) ? "medium" : "mobile";

    private static bool IsAccurate(string? scanMode)
    {
        var mode = scanMode?.Trim().ToLowerInvariant();
        return string.Equals(mode, "accurate", StringComparison.Ordinal)
            || string.Equals(mode, "precise", StringComparison.Ordinal)
            || string.Equals(mode, "chinh-xac", StringComparison.Ordinal);
    }
}
