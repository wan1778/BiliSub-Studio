using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

// Automatic subtitle style detection (16/09, no manual switch): vote from frame
// scores measured by the worker.
// Black ring -> strong, gray ring (soft shadow) -> medium, anything else -> off.
// Not enough evidence / error -> off (same as before, never drops text).
internal static class OcrSubtitleStyleContract
{
    public static Task RunAsync()
    {
        HardOutlineVotesStrong();
        SoftShadowVotesMedium();
        BareTextVotesOff();
        TooFewVotesStaysOff();
        TieFallsBackToMedium();
        NullAndEmptyStaysOff();
        return Task.CompletedTask;
    }

    private static OcrSubtitleStyle.FrameScore Frame(double bright, double? ring) =>
        new(bright, ring);

    private static void HardOutlineVotesStrong()
    {
        // Black outline: pitch-black ring (darkness 215-255).
        var frames = new[]
        {
            Frame(0.012, 231.0), Frame(0.009, 244.0), Frame(0.015, 218.0),
            Frame(0.011, 240.0), Frame(0.010, 225.0), Frame(0.008, 250.0),
        };
        Check(OcrSubtitleStyle.Decide(frames) == "strong", "viền đen phải lọc mạnh");
    }

    private static void SoftShadowVotesMedium()
    {
        // Soft shadow: gray ring (darkness 75-135).
        var frames = new[]
        {
            Frame(0.010, 95.0), Frame(0.012, 120.0), Frame(0.009, 80.0),
            Frame(0.011, 110.0), Frame(0.013, 100.0),
        };
        Check(OcrSubtitleStyle.Decide(frames) == "medium", "bóng nhẹ phải lọc vừa");
    }

    private static void BareTextVotesOff()
    {
        // Colored/bare text on a light background: bright ring (darkness < 60).
        var frames = new[]
        {
            Frame(0.010, 30.0), Frame(0.012, 12.0), Frame(0.009, 45.0),
            Frame(0.011, 20.0),
        };
        Check(OcrSubtitleStyle.Decide(frames) == "off", "chữ màu phải tắt lọc");
    }

    private static void TooFewVotesStaysOff()
    {
        // Sparse dialogue (2/12 frames contain text): not enough evidence -> off,
        // never filter blindly.
        var frames = new[]
        {
            Frame(0.012, 231.0), Frame(0.009, 244.0),
            Frame(0.0001, null), Frame(0.0, null), Frame(0.0004, null),
            Frame(0.0, null), Frame(0.0002, null), Frame(0.0, null),
        };
        Check(OcrSubtitleStyle.Decide(frames) == "off", "ít khung có chữ phải tắt lọc");
    }

    private static void TieFallsBackToMedium()
    {
        // Strong/medium tie (no majority): fall back to medium for safety.
        var frames = new[]
        {
            Frame(0.012, 231.0), Frame(0.009, 244.0), Frame(0.015, 218.0),
            Frame(0.010, 95.0), Frame(0.012, 120.0), Frame(0.009, 80.0),
        };
        Check(OcrSubtitleStyle.Decide(frames) == "medium", "hòa phải về vừa");
    }

    private static void NullAndEmptyStaysOff()
    {
        Check(OcrSubtitleStyle.Decide(null) == "off", "null phải tắt");
        Check(OcrSubtitleStyle.Decide([]) == "off", "rỗng phải tắt");
        Check(OcrSubtitleStyle.Decide([null!]) == "off", "entry null phải bỏ qua");
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
