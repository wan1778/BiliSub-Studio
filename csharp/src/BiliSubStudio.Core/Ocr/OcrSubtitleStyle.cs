namespace BiliSubStudio.Core.Ocr;

// Auto-detect the subtitle style when a scan starts (user demanded 16/09: no manual switch, look
// at what the logic must handle). The worker measures each ROI frame: bright pixel ratio (= white
// text strokes) + darkness of the ring around the text (black outline ~215-255, soft shadow ~75-135,
// bright background <55). Voting rule (pure, directly tested):
// - a frame with no bright text (bright < 0.3%) is dropped, no vote.
// - dark ring (darkness ≥ 170) -> vote strong; gray ring (≥ 60) -> vote medium;
//   the rest (colored/bare text) -> vote off.
// - with ≥3 voting frames the majority wins (tie -> medium); fewer -> OFF (if no
//   white text is seen, do not filter blindly). Any error/broken probe -> OFF (exactly as before).
public static class OcrSubtitleStyle
{
    public const double MinBrightRatio = 0.003;
    public const double HardRingDarkness = 170.0;
    public const double SoftRingDarkness = 60.0;
    public const int MinVotes = 3;

    public sealed record FrameScore(double BrightRatio, double? RingDarkness);

    // Returns "off" | "medium" | "strong".
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
