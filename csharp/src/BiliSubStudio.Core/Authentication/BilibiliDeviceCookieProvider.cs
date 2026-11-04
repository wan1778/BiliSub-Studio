using System.Text.Json;
using BiliSubStudio.Core.Video;

namespace BiliSubStudio.Core.Authentication;

public static class BilibiliDeviceCookieProvider
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string? _cookies;

    public static async Task<string> FingerCookiesAsync(HttpClient http, CancellationToken cancellationToken)
    {
        if (_cookies is not null) return _cookies;
        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (_cookies is not null) return _cookies;
            var cookies = string.Empty;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("https://api.bilibili.com/x/frontend/finger/spi"));
                request.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
                request.Headers.TryAddWithoutValidation("User-Agent", BilibiliPlayurlClient.BrowserUserAgent);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                    var root = document.RootElement;
                    if (root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.GetInt32() == 0
                        && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                    {
                        var parts = new List<string>();
                        if (data.TryGetProperty("b_3", out var b3) && b3.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(b3.GetString()))
                            parts.Add("buvid3=" + b3.GetString());
                        if (data.TryGetProperty("b_4", out var b4) && b4.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(b4.GetString()))
                            parts.Add("buvid4=" + b4.GetString());
                        cookies = string.Join("; ", parts);
                    }
                }
            }
            catch { }
            _cookies = cookies;
            return cookies;
        }
        finally { Gate.Release(); }
    }

    public static async Task<string> MergeAsync(string cookieHeader, HttpClient http, CancellationToken cancellationToken)
    {
        var finger = await FingerCookiesAsync(http, cancellationToken);
        if (string.IsNullOrWhiteSpace(finger)) return cookieHeader;
        foreach (var part in finger.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = part.Trim();
            var split = trimmed.IndexOf('=');
            if (split <= 0) continue;
            var name = trimmed[..split].Trim();
            if (name.Length == 0 || ContainsCookie(cookieHeader, name)) continue;
            cookieHeader = string.IsNullOrWhiteSpace(cookieHeader) ? trimmed : cookieHeader + "; " + trimmed;
        }
        return cookieHeader;
    }

    public static bool ContainsCookie(string? cookieHeader, string name) => (cookieHeader ?? string.Empty)
        .Split(';', StringSplitOptions.RemoveEmptyEntries)
        .Any(part => part.TrimStart().StartsWith(name + "=", StringComparison.OrdinalIgnoreCase));
}
