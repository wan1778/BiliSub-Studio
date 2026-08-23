using System.Globalization;
using System.Text.RegularExpressions;

namespace BiliSubStudio.Core.Editor;

public static partial class VietnameseTtsTextNormalizer
{
    private static readonly string[] Digits = ["không", "một", "hai", "ba", "bốn", "năm", "sáu", "bảy", "tám", "chín"];

    public static string Normalize(string? value)
    {
        var text = string.Join(' ', (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length == 0) return string.Empty;
        text = PercentRegex().Replace(text, match => NumberToWords(match.Groups[1].Value) + " phần trăm");
        text = TimeRegex().Replace(text, match => NumberToWords(match.Groups[1].Value) + " giờ " + NumberToWords(match.Groups[2].Value) + " phút");
        text = NumberRegex().Replace(text, match => NumberToWords(match.Value));
        return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    internal static string NumberToWords(string raw)
    {
        if (!long.TryParse(raw.Replace(".", string.Empty, StringComparison.Ordinal), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            return raw;
        if (number < 0) return "âm " + NumberToWords((-number).ToString(CultureInfo.InvariantCulture));
        if (number < 10) return Digits[number];
        if (number < 20) return number == 10 ? "mười" : "mười " + UnitAfterTens((int)(number % 10));
        if (number < 100)
        {
            var tens = number / 10;
            var units = (int)(number % 10);
            return units == 0 ? Digits[tens] + " mươi" : Digits[tens] + " mươi " + UnitAfterTens(units);
        }
        if (number < 1_000)
        {
            var hundreds = number / 100;
            var remainder = number % 100;
            if (remainder == 0) return Digits[hundreds] + " trăm";
            if (remainder < 10) return Digits[hundreds] + " trăm lẻ " + Digits[remainder];
            return Digits[hundreds] + " trăm " + NumberToWords(remainder.ToString(CultureInfo.InvariantCulture));
        }
        if (number < 1_000_000) return Scale(number, 1_000, "nghìn");
        if (number < 1_000_000_000) return Scale(number, 1_000_000, "triệu");
        if (number < 1_000_000_000_000) return Scale(number, 1_000_000_000, "tỷ");
        return string.Join(' ', raw.Select(character => char.IsDigit(character) ? Digits[character - '0'] : character.ToString()));
    }

    private static string Scale(long number, long unit, string name)
    {
        var high = number / unit;
        var low = number % unit;
        if (low == 0) return NumberToWords(high.ToString(CultureInfo.InvariantCulture)) + " " + name;
        if (low < 10) return NumberToWords(high.ToString(CultureInfo.InvariantCulture)) + " " + name + " không trăm lẻ " + NumberToWords(low.ToString(CultureInfo.InvariantCulture));
        if (low < 100) return NumberToWords(high.ToString(CultureInfo.InvariantCulture)) + " " + name + " không trăm " + NumberToWords(low.ToString(CultureInfo.InvariantCulture));
        return NumberToWords(high.ToString(CultureInfo.InvariantCulture)) + " " + name + " " + NumberToWords(low.ToString(CultureInfo.InvariantCulture));
    }

    private static string UnitAfterTens(int unit) => unit switch
    {
        1 => "mốt",
        4 => "tư",
        5 => "lăm",
        _ => Digits[unit],
    };

    [GeneratedRegex(@"\b(\d{1,3})\s*%", RegexOptions.CultureInvariant)]
    private static partial Regex PercentRegex();

    [GeneratedRegex(@"\b(\d{1,2}):(\d{2})\b", RegexOptions.CultureInvariant)]
    private static partial Regex TimeRegex();

    [GeneratedRegex(@"\b\d{1,12}\b", RegexOptions.CultureInvariant)]
    private static partial Regex NumberRegex();
}
