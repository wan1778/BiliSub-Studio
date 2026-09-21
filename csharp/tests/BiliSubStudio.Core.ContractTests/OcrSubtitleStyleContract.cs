using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

// Tự nhận diện kiểu sub (16/09, khỏi gạt tay): vote từ điểm worker đo.
// Vành đen -> mạnh, vành xám (bóng nhẹ) -> vừa, còn lại -> tắt.
// Không đủ bằng chứng / lỗi -> tắt (y hệt cũ, không mất chữ).
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
        // Viền đen: vành tối kịt (darkness 215-255).
        var frames = new[]
        {
            Frame(0.012, 231.0), Frame(0.009, 244.0), Frame(0.015, 218.0),
            Frame(0.011, 240.0), Frame(0.010, 225.0), Frame(0.008, 250.0),
        };
        Check(OcrSubtitleStyle.Decide(frames) == "strong", "viền đen phải lọc mạnh");
    }

    private static void SoftShadowVotesMedium()
    {
        // Bóng nhẹ: vành xám (darkness 75-135).
        var frames = new[]
        {
            Frame(0.010, 95.0), Frame(0.012, 120.0), Frame(0.009, 80.0),
            Frame(0.011, 110.0), Frame(0.013, 100.0),
        };
        Check(OcrSubtitleStyle.Decide(frames) == "medium", "bóng nhẹ phải lọc vừa");
    }

    private static void BareTextVotesOff()
    {
        // Chữ màu/trần trên nền sáng: vành sáng (darkness < 60).
        var frames = new[]
        {
            Frame(0.010, 30.0), Frame(0.012, 12.0), Frame(0.009, 45.0),
            Frame(0.011, 20.0),
        };
        Check(OcrSubtitleStyle.Decide(frames) == "off", "chữ màu phải tắt lọc");
    }

    private static void TooFewVotesStaysOff()
    {
        // Phim thoại thưa (2/12 khung có chữ): không đủ bằng chứng -> tắt,
        // đừng lọc bừa.
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
        // Hòa mạnh/vừa (không đa số): về vừa cho an toàn.
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
