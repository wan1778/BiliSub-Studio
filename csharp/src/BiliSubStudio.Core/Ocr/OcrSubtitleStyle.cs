namespace BiliSubStudio.Core.Ocr;

// Tự nhận diện kiểu sub lúc bắt đầu quét (user đòi 16/09: khỏi gạt tay, nhìn
// logic phải xử lý được). Worker đo từng khung ROI: tỉ lệ pixel sáng (= nét
// chữ trắng) + độ tối vành quanh chữ (viền đen ~215-255, bóng nhẹ ~75-135,
// nền sáng <55). Luật vote (thuần, test trực tiếp):
// - khung không chữ sáng (bright < 0.3%) thì bỏ, không vote.
// - vành đen (darkness ≥ 170) -> vote mạnh; vành xám (≥ 60) -> vote vừa;
//   còn lại (chữ màu/trần) -> vote tắt.
// - đủ ≥3 khung vote thì đa số thắng (hòa -> vừa); ít hơn -> TẮT (không thấy
//   chữ trắng thì đừng lọc bừa). Mọi lỗi/probe hỏng -> TẮT (y hệt cũ).
public static class OcrSubtitleStyle
{
    public const double MinBrightRatio = 0.003;
    public const double HardRingDarkness = 170.0;
    public const double SoftRingDarkness = 60.0;
    public const int MinVotes = 3;

    public sealed record FrameScore(double BrightRatio, double? RingDarkness);

    // Trả "off" | "medium" | "strong".
    public static string Decide(IEnumerable<FrameScore>? frames)
    {
        var hard = 0;
        var soft = 0;
        var none = 0;
        if (frames is not null)
        {
            foreach (var frame in frames)
            {
                if (frame is null || !(frame.BrightRatio >= MinBrightRatio)) continue;
                if (frame.RingDarkness is { } ring && ring >= HardRingDarkness) hard++;
                else if (frame.RingDarkness is { } ring2 && ring2 >= SoftRingDarkness) soft++;
                else none++;
            }
        }
        var total = hard + soft + none;
        if (total < MinVotes) return "off";
        if (hard * 2 > total) return "strong";
        if (soft * 2 > total) return "medium";
        if (none * 2 > total) return "off";
        return "medium";
    }

    public static string Describe(string mode) => mode switch
    {
        "strong" => "viền đen (lọc mạnh)",
        "medium" => "bóng nhẹ (lọc vừa)",
        "off" => "không rõ/chữ màu (không lọc, như cũ)",
        _ => "không rõ (không lọc, như cũ)",
    };
}
