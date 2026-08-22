using System.Text;
using System.Text.RegularExpressions;

namespace BiliSubStudio.Core.Ocr;

public static partial class ChineseSubtitleNormalizer
{
    public static bool TryNormalize(string? input, out string output)
    {
        var text = string.Join(" ", (input ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        text = RepeatedPunctuation().Replace(text, "$1");

        var hanCount = 0;
        var latinCount = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (IsHan(rune.Value))
            {
                hanCount++;
                continue;
            }
            if (rune.Value is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
            {
                latinCount++;
                continue;
            }
            if (IsForbiddenLetter(rune.Value))
            {
                output = string.Empty;
                return false;
            }
        }

        output = text.Trim();
        if (hanCount == 0 || output.Length == 0) return false;

        // Keep mixed Chinese tokens such as B站, VIP会员 or OpenAI模型, but reject
        // standalone Latin OCR noise such as ABC or A N that should never enter Chinese SRT.
        if (Regex.IsMatch(output,
                @"(?:^|\s)[A-Za-z]+(?=\s|[，。！？、；：,.!?…]|$)",
                RegexOptions.CultureInvariant))
        {
            output = string.Empty;
            return false;
        }
        if (latinCount > Math.Max(6, hanCount * 2))
        {
            output = string.Empty;
            return false;
        }
        return true;
    }

    private static bool IsHan(int value) =>
        value is >= 0x3400 and <= 0x4DBF or
        >= 0x4E00 and <= 0x9FFF or
        >= 0xF900 and <= 0xFAFF or
        >= 0x20000 and <= 0x323AF;

    private static bool IsForbiddenLetter(int value)
    {
        if (value is >= 0x3040 and <= 0x30FF) return true; // Kana
        if (value is >= 0xAC00 and <= 0xD7AF) return true; // Hangul
        if (value is >= 0x0400 and <= 0x052F) return true; // Cyrillic
        var category = Rune.GetUnicodeCategory(new Rune(value));
        return category is System.Globalization.UnicodeCategory.UppercaseLetter or
            System.Globalization.UnicodeCategory.LowercaseLetter or
            System.Globalization.UnicodeCategory.TitlecaseLetter or
            System.Globalization.UnicodeCategory.ModifierLetter or
            System.Globalization.UnicodeCategory.OtherLetter;
    }

    [GeneratedRegex(@"([，。！？、；：,.!?])(?:\s*\1)+", RegexOptions.CultureInvariant)]
    private static partial Regex RepeatedPunctuation();
}
