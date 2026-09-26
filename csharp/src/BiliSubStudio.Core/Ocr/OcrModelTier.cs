namespace BiliSubStudio.Core.Ocr;

// Model tier per scan mode (2026-09-26): Accurate keeps Medium for maximum
// fidelity; Balanced/Fast run Small (2.8x cheaper per image on field
// hardware, dialogue-equivalent output — A/B 2026-09-26 on 30 real frames:
// identical on every real dialogue line, diffs only on noise scraps the
// tracker filters downstream). Aliases mirror OcrCheckpointStore.ModeFor so
// tier and sampling policy never disagree; unknown modes fall to Small like
// ModeFor falls to Balanced.
internal static class OcrModelTier
{
    public static string DetectionModel(string scanMode) =>
        IsAccurate(scanMode) ? OcrInstaller.DetectionModel : OcrInstaller.SmallDetectionModel;

    public static string RecognitionModel(string scanMode) =>
        IsAccurate(scanMode) ? OcrInstaller.RecognitionModel : OcrInstaller.SmallRecognitionModel;

    public static string TierName(string scanMode) => IsAccurate(scanMode) ? "medium" : "small";

    private static bool IsAccurate(string? scanMode)
    {
        var mode = scanMode?.Trim().ToLowerInvariant();
        return string.Equals(mode, "accurate", StringComparison.Ordinal)
            || string.Equals(mode, "precise", StringComparison.Ordinal)
            || string.Equals(mode, "chinh-xac", StringComparison.Ordinal);
    }
}
