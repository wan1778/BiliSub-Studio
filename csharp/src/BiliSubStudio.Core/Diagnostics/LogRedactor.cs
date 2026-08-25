using System.Text.RegularExpressions;

namespace BiliSubStudio.Core.Diagnostics;

/// <summary>
/// Shared sanitizer for text that may reach persistent logs or bug reports.
/// </summary>
public static partial class LogRedactor
{
    public static string Redact(string? text)
    {
        var value = text ?? string.Empty;
        value = SecretKeyValue().Replace(value, "$1=[ĐÃ ẨN]");
        value = UserPath().Replace(value, @"C:\Users\[ĐÃ ẨN]");
        return QuerySecret().Replace(value, "$1[ĐÃ ẨN]");
    }

    [GeneratedRegex(@"(?i)(SESSDATA|bili_jct|buvid\d*|DedeUserID|authorization|token|cookie)\s*[:=]\s*([^\s;&]+)")]
    private static partial Regex SecretKeyValue();

    [GeneratedRegex(@"(?i)C:\\Users\\[^\\\s]+")]
    private static partial Regex UserPath();

    [GeneratedRegex(@"(?i)([?&](?:token|auth|cookie|sessdata)=)[^&#\s]+")]
    private static partial Regex QuerySecret();
}
