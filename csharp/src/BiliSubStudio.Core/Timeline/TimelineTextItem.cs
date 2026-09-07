namespace BiliSubStudio.Core.Timeline;

// G23 text overlay items (user-approved Concat parity, 2026-09-08). Payload is
// structured fields (not freeform payload JSON) so validation is explicit and
// the ASS builder can map them deterministically. Geometry is output-frame
// fractions, matching G21 transform semantics.
public sealed record TimelineTextAlignment(
    double AnchorX,
    double AnchorY,
    double AlignHorizontal,
    double AlignVertical)
{
    public static TimelineTextAlignment Center { get; } =
        new(0.5, 0.5, 0.5, 0.5);

    public void Validate()
    {
        if (!double.IsFinite(AnchorX) || AnchorX < 0 || AnchorX > 1)
            throw new InvalidDataException("Timeline text AnchorX phải 0..1.");
        if (!double.IsFinite(AnchorY) || AnchorY < 0 || AnchorY > 1)
            throw new InvalidDataException("Timeline text AnchorY phải 0..1.");
        if (!double.IsFinite(AlignHorizontal) || AlignHorizontal < 0 || AlignHorizontal > 1)
            throw new InvalidDataException("Timeline text AlignHorizontal phải 0..1.");
        if (!double.IsFinite(AlignVertical) || AlignVertical < 0 || AlignVertical > 1)
            throw new InvalidDataException("Timeline text AlignVertical phải 0..1.");
    }
}

public sealed record TimelineTextStyle(
    string FontFamily,
    double FontSizeFraction,
    bool Bold,
    bool Italic,
    string FillHex,
    string OutlineHex,
    double OutlineWidthFraction,
    string ShadowHex,
    double ShadowOpacity)
{
    public static TimelineTextStyle Default { get; } =
        new("Segoe UI", 0.06, false, false, "#FFFFFF", "#000000", 0.004, "#000000", 0.6);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(FontFamily) || FontFamily.Length > 120)
            throw new InvalidDataException("Timeline text FontFamily phải 1..120 ký tự.");
        if (!double.IsFinite(FontSizeFraction) || FontSizeFraction <= 0 || FontSizeFraction > 0.5)
            throw new InvalidDataException("Timeline text FontSizeFraction phải 0..0.5.");
        if (string.IsNullOrWhiteSpace(FillHex) || !IsValidHexColor(FillHex))
            throw new InvalidDataException("Timeline text FillHex phải #RRGGBB hoặc #RRGGBBAA.");
        if (string.IsNullOrWhiteSpace(OutlineHex) || !IsValidHexColor(OutlineHex))
            throw new InvalidDataException("Timeline text OutlineHex phải #RRGGBB hoặc #RRGGBBAA.");
        if (!double.IsFinite(OutlineWidthFraction) || OutlineWidthFraction < 0 || OutlineWidthFraction > 0.05)
            throw new InvalidDataException("Timeline text OutlineWidthFraction phải 0..0.05.");
        if (string.IsNullOrWhiteSpace(ShadowHex) || !IsValidHexColor(ShadowHex))
            throw new InvalidDataException("Timeline text ShadowHex phải #RRGGBB hoặc #RRGGBBAA.");
        if (!double.IsFinite(ShadowOpacity) || ShadowOpacity < 0 || ShadowOpacity > 1)
            throw new InvalidDataException("Timeline text ShadowOpacity phải 0..1.");
    }

    private static bool IsValidHexColor(string value) =>
        value.StartsWith('#') && value.Length is 7 or 9
        && value[1..].All(character => char.IsAsciiHexDigit(character));
}

public enum TimelineTextAnimation
{
    None = 0,
    FadeIn = 1,
    FadeOut = 2,
    FadeInOut = 3,
    SlideUp = 4,
    SlideDown = 5,
}

// G23 text item. Text lives on a Text track; sequence-global by default
// (ClipId null) but may bind to a clip like G7 items so it moves with trims.
public sealed record TimelineTextItem(
    string Id,
    string TrackId,
    string? ClipId,
    long TimelineStartUs,
    long DurationUs,
    string Content,
    TimelineTextStyle Style,
    TimelineTextAlignment Alignment,
    TimelineTextAnimation Animation,
    int AnimationDurationUs)
{
    public static string NewId() => Guid.NewGuid().ToString("N");

    public static TimelineTextItem Create(
        string trackId,
        long timelineStartUs,
        long durationUs,
        string content,
        TimelineTextStyle? style = null,
        TimelineTextAlignment? alignment = null,
        TimelineTextAnimation animation = TimelineTextAnimation.None,
        int animationDurationUs = 300_000)
    {
        var item = new TimelineTextItem(
            NewId(), trackId, null, timelineStartUs, durationUs, content,
            style ?? TimelineTextStyle.Default,
            alignment ?? TimelineTextAlignment.Center,
            animation, animationDurationUs);
        item.Validate();
        return item;
    }

    public long TimelineEndUs => checked(TimelineStartUs + DurationUs);

    public void Validate()
    {
        if (Id is not { Length: 32 } || !Id.All(character => char.IsAsciiHexDigit(character)))
            throw new InvalidDataException("Timeline text item ID không hợp lệ.");
        if (string.IsNullOrWhiteSpace(TrackId))
            throw new InvalidDataException("Timeline text item thiếu TrackId.");
        if (ClipId is not null && ClipId is not { Length: 32 })
            throw new InvalidDataException("Timeline text item ClipId không hợp lệ.");
        if (TimelineStartUs < 0 || DurationUs <= 0)
            throw new InvalidDataException("Timeline text item range không hợp lệ.");
        if (string.IsNullOrWhiteSpace(Content) || Content.Length > 2_000)
            throw new InvalidDataException("Timeline text Content phải 1..2000 ký tự.");
        Style.Validate();
        Alignment.Validate();
        if (!Enum.IsDefined(typeof(TimelineTextAnimation), Animation))
            throw new InvalidDataException("Timeline text animation không hợp lệ.");
        if (Animation is not TimelineTextAnimation.None
            && (AnimationDurationUs <= 0 || AnimationDurationUs > 3_000_000))
            throw new InvalidDataException("Timeline text AnimationDurationUs phải 0..3s.");
    }
}
