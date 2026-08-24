namespace BiliSubStudio.Core.Editor;

public readonly record struct EditorMosaicDimensions(int Width, int Height);

public static class EditorMosaicStrength
{
    public const int Minimum = 4;
    public const int Maximum = 64;
    public const int Default = 12;

    public static bool TryFromInput(double value, out int strength)
    {
        strength = Default;
        if (!double.IsFinite(value) || value < Minimum || value > Maximum) return false;
        strength = (int)Math.Round(value, MidpointRounding.AwayFromZero);
        return true;
    }

    public static int NormalizeInput(double value, int fallback = Default)
    {
        if (!double.IsFinite(value)) return NormalizeStored(fallback);
        return (int)Math.Round(Math.Clamp(value, Minimum, Maximum), MidpointRounding.AwayFromZero);
    }

    public static int NormalizeStored(int strength) => Math.Clamp(strength, Minimum, Maximum);

    public static EditorMosaicDimensions DownsampleDimensions(
        int strength,
        int pixelWidth,
        int pixelHeight,
        double scaleX = 1,
        double scaleY = 1)
    {
        if (pixelWidth < 2 || pixelHeight < 2)
            throw new ArgumentOutOfRangeException(nameof(pixelWidth), "Vùng Mosaic phải đạt ít nhất 2 pixel mỗi chiều.");
        if (!double.IsFinite(scaleX) || scaleX <= 0 || !double.IsFinite(scaleY) || scaleY <= 0)
            throw new ArgumentOutOfRangeException(nameof(scaleX), "Tỷ lệ proxy Mosaic phải hữu hạn và lớn hơn 0.");
        var normalized = NormalizeStored(strength);
        return new EditorMosaicDimensions(
            Math.Max(1, (int)Math.Floor(pixelWidth / (normalized * Math.Min(1, scaleX)))),
            Math.Max(1, (int)Math.Floor(pixelHeight / (normalized * Math.Min(1, scaleY)))));
    }
}
