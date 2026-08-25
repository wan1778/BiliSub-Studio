using System.Net.Http.Json;
using BiliSubStudio.Core.Diagnostics;

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

public sealed class BugReportService(HttpClient http)
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
            Truncate(LogRedactor.Redact(note), 4_000),
            Logs: logs?.ToDictionary(x => x.Key, x => Truncate(LogRedactor.Redact(x.Value), 30_000), StringComparer.Ordinal));
        using var response = await http.PostAsJsonAsync(Endpoint, report, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Gửi báo lỗi HTTP {(int)response.StatusCode}.");
    }

    public static string Sanitize(string? text) => LogRedactor.Redact(text);

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[^length..];
}
