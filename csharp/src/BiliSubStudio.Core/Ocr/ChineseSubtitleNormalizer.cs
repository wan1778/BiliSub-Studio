using System.Text;
using System.Text.RegularExpressions;

namespace BiliSubStudio.Core.Ocr;

public static partial class ChineseSubtitleNormalizer
{
    public static bool TryNormalize(string? input, out string output)
    {
        var text = string.Join(" ", (input ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        text = RepeatedPunctuation().Replace(text, "$1");
        var hasHan = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (IsHan(rune.Value))
            {
                hasHan = true;
                continue;
            }
            if (IsForbiddenLetter(rune.Value))
            {
                output = string.Empty;
                return false;
            }
        }
        output = text.Trim();
        return hasHan && output.Length > 0;
    }

    private static bool IsHan(int value) =>
        value is >= 0x3400 and <= 0x4DBF or
        >= 0x4E00 and <= 0x9FFF or
        >= 0xF900 and <= 0xFAFF or
        >= 0x20000 and <= 0x323AF;

    private static bool IsForbiddenLetter(int value)
    {
        if (value is >= 'A' and <= 'Z' or >= 'a' and <= 'z') return true;
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
