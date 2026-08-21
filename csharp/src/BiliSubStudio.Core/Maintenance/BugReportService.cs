using System.Net.Http.Json;
using System.Text.RegularExpressions;

namespace BiliSubStudio.Core.Maintenance;

public sealed record BugReport(
    string Id,
    string Version,
    DateTimeOffset CreatedAt,
    string Page,
    string Note,
    IReadOnlyDictionary<string, string>? Video = null,
    IReadOnlyDictionary<string, string>? Logs = null,
    string Runtime = "native-winui3-windows-x64");

public sealed partial class BugReportService(HttpClient http)
{
    private const string Endpoint = "https://script.google.com/macros/s/AKfycbwQzULsUQZrsXw7BjuM8eMYUwKUQBAKYd1ALKGoy_JT_2JB_aBplW3MVK83InSrkWLDrw/exec";

    public async Task SendAsync(string version, string page, string note, IReadOnlyDictionary<string, string>? logs, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var report = new BugReport(
            $"BUG-{now:yyyyMMdd-HHmmss}-{Math.Abs(now.Ticks % 1_000_000):D6}",
            version,
            now,
            page.Trim(),
            Truncate(Sanitize(note), 4_000),
            Logs: logs?.ToDictionary(x => x.Key, x => Truncate(Sanitize(x.Value), 30_000), StringComparer.Ordinal));
        using var response = await http.PostAsJsonAsync(Endpoint, report, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Gửi báo lỗi HTTP {(int)response.StatusCode}.");
    }

    public static string Sanitize(string? text)
    {
        var value = text ?? string.Empty;
        value = SecretKeyValue().Replace(value, "$1=[ĐÃ ẨN]");
        value = UserPath().Replace(value, @"C:\Users\[ĐÃ ẨN]");
        return QuerySecret().Replace(value, "$1[ĐÃ ẨN]");
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[^length..];
    [GeneratedRegex(@"(?i)(SESSDATA|bili_jct|buvid\d*|DedeUserID|authorization|token|cookie)\s*[:=]\s*([^\s;&]+)")] private static partial Regex SecretKeyValue();
    [GeneratedRegex(@"(?i)C:\\Users\\[^\\\s]+") ] private static partial Regex UserPath();
    [GeneratedRegex(@"(?i)([?&](?:token|auth|cookie|sessdata)=)[^&#\s]+") ] private static partial Regex QuerySecret();
}
