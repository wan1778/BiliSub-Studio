using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BiliSubStudio.Core.Editor;

/// <summary>
/// Local Vietnamese text normalization for Piper/NghiTTS. The behavior intentionally follows
/// the categories handled by nghimestudio/nghitts (numbers, dates, time, percent and decimals)
/// without embedding its browser UI/runtime.
/// </summary>
public static class VietnameseTtsTextNormalizer
{
    public static bool HasSpeakableUnits(string? value)
        => SpeakableUnitCount(value) > 0;

    public static int SpeakableUnitCount(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        var count = 0;
        var unitCounted = false;
        foreach (var rune in value.Normalize(NormalizationForm.FormC).EnumerateRunes())
        {
            var speakable = Rune.IsLetter(rune) || Rune.IsNumber(rune);
            if (speakable && !unitCounted)
            {
                count++;
                unitCounted = true;
            }
            else if (!speakable && rune.Value != '_')
            {
                unitCounted = false;
            }
        }
        return count;
    }

    public static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var text = value.Normalize(NormalizationForm.FormC).Trim();
        text = DateRegex().Replace(text, match =>
        {
            var day = NumberToWords(Parse(match.Groups["d"].Value));
            var month = NumberToWords(Parse(match.Groups["m"].Value));
            var year = NumberToWords(Parse(match.Groups["y"].Value));
            return $"ngày {day} tháng {month} năm {year}";
        });
        text = TimeRegex().Replace(text, match =>
        {
            var hour = NumberToWords(Parse(match.Groups["h"].Value));
            var minute = Parse(match.Groups["m"].Value);
            return minute == 0 ? $"{hour} giờ" : $"{hour} giờ {NumberToWords(minute)} phút";
        });
        text = PercentageRegex().Replace(text, match => NumberExpression(match.Groups["n"].Value) + " phần trăm");
        text = DecimalRegex().Replace(text, match =>
        {
            var left = NumberToWords(Parse(match.Groups["a"].Value));
            var rightDigits = match.Groups["b"].Value;
            var right = string.Join(' ', rightDigits.Select(DigitWord));
            return $"{left} phẩy {right}";
        });
        text = IntegerRegex().Replace(text, match => NumberToWords(Parse(match.Value)));
        text = Regex.Replace(text, @"\s+", " ", RegexOptions.CultureInvariant).Trim();
        return text;
    }

    internal static string NumberToWords(long value)
    {
        if (value == 0) return "không";
        if (value < 0) return "âm " + NumberToWords(Math.Abs(value));
        var groups = new[] { "", "nghìn", "triệu", "tỷ", "nghìn tỷ" };
        var chunks = new List<int>();
        var copy = value;
        while (copy > 0)
        {
            chunks.Add((int)(copy % 1000));
            copy /= 1000;
        }
        var parts = new List<string>();
        for (var index = chunks.Count - 1; index >= 0; index--)
        {
            var chunk = chunks[index];
            if (chunk == 0) continue;
            var forceHundreds = index < chunks.Count - 1 && chunk < 100;
            var words = ReadThreeDigits(chunk, forceHundreds);
            if (groups[index].Length > 0) words += " " + groups[index];
            parts.Add(words);
        }
        return string.Join(' ', parts);
    }

    private static string ReadThreeDigits(int value, bool forceHundreds)
    {
        var hundreds = value / 100;
        var remainder = value % 100;
        var parts = new List<string>();
        if (hundreds > 0 || forceHundreds)
        {
            parts.Add(DigitWord(hundreds));
            parts.Add("trăm");
            if (remainder is > 0 and < 10) parts.Add("lẻ");
        }
        if (remainder >= 20)
        {
            var tens = remainder / 10;
            var ones = remainder % 10;
            parts.Add(DigitWord(tens));
            parts.Add("mươi");
            if (ones > 0) parts.Add(ones switch
            {
                1 => "mốt",
                4 => "tư",
                5 => "lăm",
                _ => DigitWord(ones),
            });
        }
        else if (remainder >= 10)
        {
            parts.Add("mười");
            var ones = remainder % 10;
            if (ones > 0) parts.Add(ones == 5 ? "lăm" : DigitWord(ones));
        }
        else if (remainder > 0)
        {
            parts.Add(DigitWord(remainder));
        }
        return string.Join(' ', parts);
    }

    private static string NumberExpression(string raw)
    {
        var decimalMatch = Regex.Match(raw, @"^(?<a>\d+)[,.](?<b>\d+)$", RegexOptions.CultureInvariant);
        if (!decimalMatch.Success) return NumberToWords(Parse(raw));
        return NumberToWords(Parse(decimalMatch.Groups["a"].Value)) + " phẩy "
            + string.Join(' ', decimalMatch.Groups["b"].Value.Select(DigitWord));
    }

    private static long Parse(string value) => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
        ? Math.Clamp(parsed, -999_999_999_999L, 999_999_999_999L)
        : 0;

    private static string DigitWord(char value) => value is >= '0' and <= '9' ? DigitWord(value - '0') : value.ToString();
    private static string DigitWord(int value) => value switch
    {
        0 => "không",
        1 => "một",
        2 => "hai",
        3 => "ba",
        4 => "bốn",
        5 => "năm",
        6 => "sáu",
        7 => "bảy",
        8 => "tám",
        9 => "chín",
        _ => value.ToString(CultureInfo.InvariantCulture),
    };

    private static Regex DateRegex() => new(@"\b(?<d>\d{1,2})[/-](?<m>\d{1,2})[/-](?<y>\d{2,4})\b", RegexOptions.CultureInvariant);
    private static Regex TimeRegex() => new(@"\b(?<h>\d{1,2}):(?<m>\d{2})\b", RegexOptions.CultureInvariant);
    private static Regex PercentageRegex() => new(@"\b(?<n>\d+(?:[,.]\d+)?)\s*%", RegexOptions.CultureInvariant);
    private static Regex DecimalRegex() => new(@"\b(?<a>\d+)[,.](?<b>\d+)\b", RegexOptions.CultureInvariant);
    private static Regex IntegerRegex() => new(@"\b\d{1,12}\b", RegexOptions.CultureInvariant);
}
