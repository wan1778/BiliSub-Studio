using System.Globalization;

namespace BiliSubStudio.Core.Timeline;

// G23 text overlay ASS builder (user-approved Concat parity, 2026-09-08).
// Timeline text items render through the same ASS pipeline as subtitle burns
// (DEC-023 single-render-graph rule): one Dialogue event per item with a
// style derived from TimelineTextStyle. Geometry uses output-frame fractions
// resolved to PlayRes coordinates so items never drift between preview and
// export. ASS alignment uses numpad convention (1-9); our AnchorX/AnchorY
// fractions map to the closest numpad corner.
public static class TimelineTextAssBuilder
{
    private const int PlayResX = 1920;
    private const int PlayResY = 1080;

    public static string BuildDialogue(TimelineResolvedTextItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.SequenceEndUs <= item.SequenceStartUs)
            throw new InvalidDataException("Text item duration phải dương.");
        var start = FormatAssTime(item.SequenceStartUs);
        var end = FormatAssTime(item.SequenceEndUs);
        var style = BuildInlineStyle(item.Style, item.Alignment, item.Animation, item.AnimationDurationUs);
        return $"Dialogue: 0,{start},{end},T2,,0,0,0,,{{{style}}}{Escape(item.Content)}";
    }

    // Builds the [V4+ Styles] line for one text item (one style per item keeps
    // overrides local and avoids style-name collisions across tracks).
    public static string BuildStyleDefinition(
        string styleName, TimelineTextStyle style, TimelineTextAlignment alignment)
    {
        ArgumentNullException.ThrowIfNull(style);
        ArgumentException.ThrowIfNullOrWhiteSpace(styleName);
        var fontSize = Math.Clamp(
            (int)Math.Round(style.FontSizeFraction * PlayResY), 8, PlayResY);
        var outline = Math.Max(1, (int)Math.Round(style.OutlineWidthFraction * PlayResY));
        var shadow = style.ShadowOpacity > 0 ? 1 : 0;
        return string.Create(CultureInfo.InvariantCulture,
            $"Style: {styleName},{style.FontFamily},{fontSize}," +
            $"{ToAssColor(style.FillHex)},{ToAssColor(style.OutlineHex)}," +
            $"{ToAssColor(style.OutlineHex)},{ToAssColor(style.ShadowHex)}," +
            $"{(style.Bold ? -1 : 0)},{(style.Italic ? -1 : 0)},0,0,100,100," +
            $"0,0,1,{outline},{shadow},2," +
            $"{(int)(alignment.AnchorX * PlayResX)}," +
            $"{(int)(alignment.AnchorY * PlayResY)},0,0");
    }

    private static string BuildInlineStyle(
        TimelineTextStyle style, TimelineTextAlignment alignment,
        TimelineTextAnimation animation, int animationDurationUs)
    {
        var fontSize = Math.Clamp(
            (int)Math.Round(style.FontSizeFraction * PlayResY), 8, PlayResY);
        var tags = $"\\an{ToAssAlignment(alignment)}" +
                   $"\\fs{fontSize.ToString(CultureInfo.InvariantCulture)}" +
                   $"\\c{ToAssColor(style.FillHex)}" +
                   $"\\3c{ToAssColor(style.OutlineHex)}" +
                   $"\\bord{Math.Max(1, (int)Math.Round(style.OutlineWidthFraction * PlayResY)).ToString(CultureInfo.InvariantCulture)}";
        if (style.Bold) tags += "\\b1";
        if (style.Italic) tags += "\\i1";
        tags += animation switch
        {
            TimelineTextAnimation.FadeIn => $"\\fad({Math.Min(animationDurationUs / 1000, 3000).ToString(CultureInfo.InvariantCulture)},0)",
            TimelineTextAnimation.FadeOut => $"\\fad(0,{Math.Min(animationDurationUs / 1000, 3000).ToString(CultureInfo.InvariantCulture)})",
            TimelineTextAnimation.FadeInOut => $"\\fad({Math.Min(animationDurationUs / 2000, 3000).ToString(CultureInfo.InvariantCulture)},{Math.Min(animationDurationUs / 2000, 3000).ToString(CultureInfo.InvariantCulture)})",
            _ => string.Empty,
        };
        return tags;
    }

    private static int ToAssAlignment(TimelineTextAlignment alignment)
    {
        // Anchor fractions map to the nearest ASS numpad alignment.
        var row = alignment.AnchorY < 0.34 ? 1 : alignment.AnchorY <= 0.67 ? 2 : 3;
        var column = alignment.AnchorX < 0.34 ? 1 : alignment.AnchorX <= 0.67 ? 2 : 3;
        return (3 - row) * 3 + column; // ASS vertical axis is inverted vs UI.
    }

    private static string ToAssColor(string hexColor)
    {
        // #RRGGBB[AA] -> &HAABBGGRR (ASS uses inverted channel order).
        var value = hexColor.TrimStart('#');
        var r = value[0..2];
        var g = value[2..4];
        var b = value[4..6];
        var alpha = value.Length == 8 ? value[6..8] : "00";
        return "&H" + alpha + b + g + r;
    }

    private static string FormatAssTime(long microseconds)
    {
        var totalSeconds = microseconds / 1_000_000.0;
        var hours = (int)(totalSeconds / 3600);
        var minutes = (int)(totalSeconds % 3600 / 60);
        var seconds = (int)(totalSeconds % 60);
        var centis = (int)Math.Round(totalSeconds % 1 * 100);
        if (centis == 100)
        {
            centis = 99;
        }
        return string.Create(CultureInfo.InvariantCulture,
            $"{hours}:{minutes:00}:{seconds:00}.{centis:00}");
    }

    private static string Escape(string content) =>
        content.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("{", "\\{", StringComparison.Ordinal)
            .Replace("}", "\\}", StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", "\\N", StringComparison.Ordinal);
}
