namespace BiliSubStudio.Core.Editor;

public static class EditorBlurStrength
{
    public const int Minimum = 2;
    public const int Maximum = 40;
    public const int Default = 18;

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

    public static int EffectiveRadius(int strength, int pixelWidth, int pixelHeight)
    {
        if (pixelWidth < 2 || pixelHeight < 2)
            throw new ArgumentOutOfRangeException(nameof(pixelWidth), "Vùng làm mờ phải đạt ít nhất 2 pixel mỗi chiều.");
        var maximumRadius = (Math.Min(pixelWidth, pixelHeight) - 1) / 2;
        return Math.Min(NormalizeStored(strength), maximumRadius);
    }
}
