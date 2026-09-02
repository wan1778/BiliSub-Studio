using System.Globalization;

namespace BiliSubStudio.Core.Editor;

public sealed record EditorSubtitleStyle(
    string Preset,
    string TextColor,
    string OutlineColor,
    double OutlineWidth,
    string BackgroundColor,
    double BackgroundOpacity,
    double BackgroundPadding,
    double Shadow,
    bool Bold,
    string FontName = "Arial",
    bool Italic = false,
    bool Underline = false,
    double FontScale = 1);

public static class EditorSubtitleStylePolicy
{
    public const string DefaultPreset = "default";
    public const string CinemaPreset = "cinema";
    public const string YellowPreset = "yellow";
    public const string CyanPreset = "cyan";
    public const string BlackBoxPreset = "black-box";
    public const string LightBoxPreset = "light-box";
    public const string PlainPreset = "plain";
    public const string PinkPreset = "pink";
    public const string RedPreset = "red";
    public const string GreenPreset = "green";
    public const string YellowBoxPreset = "yellow-box";
    public const string BlueBoxPreset = "blue-box";
    public const string CustomPreset = "custom";

    public static EditorSubtitleStyle Default { get; } = FromPreset(DefaultPreset);

    public static EditorSubtitleStyle FromPreset(string? preset) => preset?.Trim().ToLowerInvariant() switch
    {
        CinemaPreset => new(CinemaPreset, "#FFF4D6", "#000000", 3, "#000000", .45, 5, 1, true),
        YellowPreset => new(YellowPreset, "#FFE45C", "#000000", 3, "#000000", 0, 4, 1, true),
        CyanPreset => new(CyanPreset, "#6EE7FF", "#062033", 2.5, "#001018", .35, 4, .8, true),
        BlackBoxPreset => new(BlackBoxPreset, "#FFFFFF", "#000000", 1.5, "#000000", .75, 6, 0, true),
        LightBoxPreset => new(LightBoxPreset, "#111111", "#FFFFFF", 1, "#FFFFFF", .82, 6, 0, true),
        PlainPreset => new(PlainPreset, "#FFFFFF", "#000000", 0, "#000000", 0, 4, 0, false),
        PinkPreset => new(PinkPreset, "#FF73B9", "#321124", 2.5, "#000000", 0, 4, 1, true),
        RedPreset => new(RedPreset, "#FF5D63", "#FFFFFF", 2.2, "#000000", 0, 4, .8, true),
        GreenPreset => new(GreenPreset, "#73E6A2", "#06351E", 2.5, "#000000", 0, 4, .8, true),
        YellowBoxPreset => new(YellowBoxPreset, "#111111", "#000000", 0, "#FFE13B", .95, 6, 0, true),
        BlueBoxPreset => new(BlueBoxPreset, "#FFFFFF", "#0A2857", 1.5, "#164E9C", .88, 6, 0, true),
        _ => new(DefaultPreset, "#FFFFFF", "#101010", 2.2, "#000000", 0, 4, .8, true),
    };

    public static EditorSubtitleStyle Normalize(EditorSubtitleStyle? style)
    {
        if (style is null) return Default;
        var preset = style.Preset?.Trim().ToLowerInvariant();
        if (preset is not (DefaultPreset or CinemaPreset or YellowPreset or CyanPreset
            or BlackBoxPreset or LightBoxPreset or PlainPreset or PinkPreset or RedPreset
            or GreenPreset or YellowBoxPreset or BlueBoxPreset or CustomPreset)) preset = CustomPreset;
        return new EditorSubtitleStyle(
            preset,
            NormalizeHex(style.TextColor, "màu chữ"),
            NormalizeHex(style.OutlineColor, "màu viền"),
            NormalizeFinite(style.OutlineWidth, 0, 8, "độ dày viền"),
            NormalizeHex(style.BackgroundColor, "màu nền"),
            NormalizeFinite(style.BackgroundOpacity, 0, 1, "độ mờ nền"),
            NormalizeFinite(style.BackgroundPadding, 0, 12, "lề nền"),
            NormalizeFinite(style.Shadow, 0, 6, "độ bóng"),
            style.Bold,
            NormalizeFont(style.FontName),
            style.Italic,
            style.Underline,
            NormalizeFinite(style.FontScale, .5, 2, "cỡ chữ"));
    }

    public static string ToAssColor(string hex, double opacity)
    {
        var normalized = NormalizeHex(hex, "màu ASS");
        var red = byte.Parse(normalized.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var green = byte.Parse(normalized.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var blue = byte.Parse(normalized.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var alpha = 255 - (int)Math.Round(Math.Clamp(opacity, 0, 1) * 255);
        return $"&H{alpha:X2}{blue:X2}{green:X2}{red:X2}";
    }

    private static string NormalizeHex(string? value, string label)
    {
        var color = value?.Trim().ToUpperInvariant() ?? string.Empty;
        if (color.Length != 7 || color[0] != '#' || color.AsSpan(1).ContainsAnyExcept("0123456789ABCDEF"))
            throw new InvalidDataException($"Style phụ đề chứa {label} không hợp lệ.");
        return color;
    }

    private static double NormalizeFinite(double value, double minimum, double maximum, string label)
    {
        if (!double.IsFinite(value)) throw new InvalidDataException($"Style phụ đề chứa {label} không hợp lệ.");
        return Math.Clamp(value, minimum, maximum);
    }

    private static string NormalizeFont(string? value)
    {
        var font = value?.Trim() ?? string.Empty;
        return font.ToLowerInvariant() switch
        {
            "system" or "arial" => "Arial",
            "segoe ui" => "Segoe UI",
            "tahoma" => "Tahoma",
            "times new roman" => "Times New Roman",
            "verdana" => "Verdana",
            _ => "Arial",
        };
    }
}
