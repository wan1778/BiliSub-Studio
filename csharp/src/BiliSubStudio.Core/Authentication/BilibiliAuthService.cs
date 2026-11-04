using System.Net;
using System.Text.Json;

namespace BiliSubStudio.Core.Authentication;

public sealed record LoginStatus(bool Saved, bool Valid, string User, string? Error);
public sealed record QrStartResult(string Url, string Key, QrMatrix Matrix);
public sealed record QrPollResult(bool LoggedIn, string Message, string User, bool Expired);
public sealed record SessionProbe(bool HasSession, bool Valid, bool Expired, string Message);
public sealed record CookieSaveResult(bool Verified, string User, string Message);

public sealed class BilibiliAuthService
{
    private readonly HttpClient _http;
    private readonly SessionStore _sessions;

    public BilibiliAuthService(HttpClient http, SessionStore sessions)
    {
        _http = http;
        _sessions = sessions;
    }

    public async Task<SessionProbe> ProbeAsync(CancellationToken cancellationToken)
    {
        if (!_sessions.HasCookie) return new SessionProbe(false, false, false, string.Empty);
        try
        {
            var user = await ValidateCookieAsync(_sessions.Cookie, cancellationToken);
            return new SessionProbe(true, true, false, user);
        }
        catch (Exception error)
        {
            var message = Compact(error.GetBaseException().Message);
            if (message.Contains("code -101", StringComparison.OrdinalIgnoreCase)
                || message.Contains("không hợp lệ hoặc đã hết hạn", StringComparison.OrdinalIgnoreCase))
            {
                return new SessionProbe(true, false, true,
                    "Phiên đăng nhập Bilibili đã hết hạn hoặc bị vô hiệu (Bilibili xoay vòng phiên khi bạn đăng nhập web ở tab khác). Hãy dán lại cookie SESSDATA mới trong Cài đặt → Đăng nhập, dán sau khi đã đăng nhập xong trên trình duyệt.");
            }
            if (message.Contains("HTTP 412", StringComparison.OrdinalIgnoreCase)
                || message.Contains("HTTP 403", StringComparison.OrdinalIgnoreCase))
            {
                return new SessionProbe(true, false, true,
                    "Bilibili đang chặn API (412): thiếu cookie thiết bị hoặc phiên đã hết hạn. Hãy dán lại cookie SESSDATA mới trong Cài đặt → Đăng nhập.");
            }
            return new SessionProbe(true, false, false, "Không kiểm tra được phiên Bilibili: " + message);
        }
    }

    private static string Compact(string value)
    {
        var text = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 500 ? text : text[..500];
    }

    public async Task<LoginStatus> StatusAsync(bool validate, CancellationToken cancellationToken)
    {
        if (!_sessions.HasCookie) return new LoginStatus(false, false, string.Empty, null);
        if (!validate) return new LoginStatus(true, false, string.Empty, null);
        try
        {
            var user = await ValidateCookieAsync(_sessions.Cookie, cancellationToken);
            return new LoginStatus(true, true, user, null);
        }
        catch (Exception error) { return new LoginStatus(true, false, string.Empty, error.Message); }
    }

    public async Task<CookieSaveResult> SetCookieAsync(string raw, CancellationToken cancellationToken)
    {
        var normalized = SessionStore.NormalizeCookie(raw);
        normalized = SessionStore.NormalizeCookie(await BilibiliDeviceCookieProvider.MergeAsync(normalized, _http, cancellationToken));
        if (!BilibiliDeviceCookieProvider.ContainsCookie(normalized, "SESSDATA"))
            throw new InvalidOperationException("Cookie phải có SESSDATA (mở Chrome → bilibili.com → F12 → Application → Cookies → SESSDATA).");
        var user = await ValidateCookieAsync(normalized, cancellationToken);
        await _sessions.SetCookieAsync(normalized, cancellationToken);
        return new CookieSaveResult(true, user, user.Length > 0 ? "Đã đăng nhập: " + user : "Đã lưu phiên đăng nhập.");
    }

    public Task DeleteAsync(CancellationToken cancellationToken) => _sessions.DeleteAsync(cancellationToken);

    public async Task<QrStartResult> StartQrAsync(CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync("https://passport.bilibili.com/x/passport-login/web/qrcode/generate", cancellationToken);
        var root = document.RootElement;
        EnsureApiSuccess(root, "Bilibili QR");
        var data = root.GetProperty("data");
        var url = data.GetProperty("url").GetString() ?? string.Empty;
        var key = data.GetProperty("qrcode_key").GetString() ?? string.Empty;
        if (url.Length == 0 || key.Length == 0) throw new InvalidDataException("Bilibili QR thiếu URL/key.");
        return new QrStartResult(url, key, QrMatrixEncoder.Encode(url));
    }

    public async Task<QrPollResult> PollQrAsync(string key, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Thiếu qrcode key.", nameof(key));
        var endpoint = "https://passport.bilibili.com/x/passport-login/web/qrcode/poll?qrcode_key=" + Uri.EscapeDataString(key.Trim());
        using var request = CreateRequest(endpoint);
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var setCookies = response.Headers.TryGetValues("Set-Cookie", out var values) ? values.ToArray() : [];
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));
        var root = document.RootElement;
        EnsureApiSuccess(root, "Bilibili QR poll");
        var data = root.GetProperty("data");
        var code = data.GetProperty("code").GetInt32();
        if (code == 0)
        {
            var callback = data.TryGetProperty("url", out var urlNode) ? urlNode.GetString() ?? string.Empty : string.Empty;
            var cookie = SessionStore.NormalizeCookie(CookieFromQr(callback, setCookies));
            cookie = SessionStore.NormalizeCookie(await BilibiliDeviceCookieProvider.MergeAsync(cookie, _http, cancellationToken));
            if (cookie.Length == 0) throw new InvalidDataException("QR thành công nhưng không lấy được Cookie.");
            var user = await ValidateCookieAsync(cookie, cancellationToken);
            await _sessions.SetCookieAsync(cookie, cancellationToken);
            return new QrPollResult(true, user.Length > 0 ? "Đăng nhập thành công: " + user : "Đăng nhập thành công.", user, false);
        }
        return code switch
        {
            86101 => new QrPollResult(false, "Chưa quét mã QR.", string.Empty, false),
            86090 => new QrPollResult(false, "Đã quét, hãy xác nhận trên điện thoại.", string.Empty, false),
            86038 => new QrPollResult(false, "Mã QR đã hết hạn.", string.Empty, true),
            _ => new QrPollResult(false, data.TryGetProperty("message", out var message) ? message.GetString() ?? $"Bilibili QR {code}" : $"Bilibili QR {code}", string.Empty, false),
        };
    }

    private async Task<string> ValidateCookieAsync(string cookie, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cookie)) throw new ArgumentException("Cookie rỗng.");
        var header = await BilibiliDeviceCookieProvider.MergeAsync(cookie, _http, cancellationToken);
        using var request = CreateRequest("https://api.bilibili.com/x/web-interface/nav");
        request.Headers.TryAddWithoutValidation("Cookie", header);
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));
        var root = document.RootElement;
        if (root.TryGetProperty("code", out var apiCode) && apiCode.TryGetInt32(out var codeValue) && codeValue == -101)
            throw new InvalidOperationException("Bilibili không nhận phiên từ cookie này (SESSDATA sai hoặc đã hết hạn). Mở Chrome, đăng nhập bilibili.com, rồi copy lại giá trị SESSDATA mới.");
        EnsureApiSuccess(root, "Bilibili nav");
        var data = root.GetProperty("data");
        if (!data.TryGetProperty("isLogin", out var login) || !login.GetBoolean())
            throw new InvalidOperationException("Bilibili không nhận phiên từ cookie này (SESSDATA sai hoặc đã hết hạn). Mở Chrome, đăng nhập bilibili.com, rồi copy lại giá trị SESSDATA mới.");
        return data.TryGetProperty("uname", out var user) ? user.GetString()?.Trim() ?? string.Empty : string.Empty;
    }

    private async Task<JsonDocument> GetJsonAsync(string endpoint, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(endpoint);
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));
    }

    private static HttpRequestMessage CreateRequest(string endpoint)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.TryAddWithoutValidation("User-Agent", Video.BilibiliPlayurlClient.BrowserUserAgent);
        request.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
        request.Headers.TryAddWithoutValidation("Origin", "https://www.bilibili.com");
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        return request;
    }

    private static void EnsureApiSuccess(JsonElement root, string owner)
    {
        var code = root.TryGetProperty("code", out var codeNode) ? codeNode.GetInt32() : -1;
        if (code != 0)
        {
            var message = root.TryGetProperty("message", out var messageNode) ? messageNode.GetString() : null;
            throw new InvalidOperationException($"{owner}: {code} {message}".Trim());
        }
    }

    private static string CookieFromQr(string callback, IEnumerable<string> setCookieHeaders)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in setCookieHeaders)
        {
            var pair = header.Split(';', 2)[0].Split('=', 2);
            if (pair.Length == 2 && pair[0].Trim().Length > 0) values[pair[0].Trim()] = pair[1].Trim();
        }
        if (Uri.TryCreate(callback, UriKind.Absolute, out var uri))
        {
            foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = part.Split('=', 2);
                if (pair.Length == 2) values[WebUtility.UrlDecode(pair[0])] = WebUtility.UrlDecode(pair[1]);
            }
        }
        var priority = new[] { "SESSDATA", "bili_jct", "DedeUserID", "DedeUserID__ckMd5", "sid", "buvid3", "buvid4", "b_nut", "buvid_fp", "buvid_fp_plain", "b_lsid" };
        var output = new List<string>();
        foreach (var name in priority) if (values.Remove(name, out var value) && value.Length > 0) output.Add(name + "=" + value);
        output.AddRange(values.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key + "=" + x.Value));
        return string.Join("; ", output);
    }
}
