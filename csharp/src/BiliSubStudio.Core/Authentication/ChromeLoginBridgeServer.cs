using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BiliSubStudio.Core.Authentication;

// One-shot loopback handoff used by the optional Chrome extension. The app
// launches Chrome with a tokenized local URL; the extension reads the user's
// existing Bilibili SESSDATA and posts it back. This server never renders a
// login form, never reads a browser cookie database and stores only SESSDATA
// through the normal authentication service.
public sealed class ChromeLoginBridgeServer : IAsyncDisposable
{
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxBodyBytes = 8 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly Func<string, CancellationToken, Task<string>> _importSession;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource<string> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _acceptLoop;
    private readonly string _pageSecret;
    private readonly int _port;

    public ChromeLoginBridgeServer(string pageSecret, Func<string, CancellationToken, Task<string>> importSession)
    {
        _pageSecret = string.IsNullOrWhiteSpace(pageSecret) ? throw new ArgumentException("Page secret is required.", nameof(pageSecret)) : pageSecret;
        _importSession = importSession ?? throw new ArgumentNullException(nameof(importSession));
        Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync();
    }

    public string Token { get; }

    public string Url => $"http://127.0.0.1:{_port}/bilisub-bridge?token={Token}";

    public async Task<string> WaitForSessionAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var delay = Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
        var completed = await Task.WhenAny(_completion.Task, delay);
        if (completed == _completion.Task) return await _completion.Task;
        cancellationToken.ThrowIfCancellationRequested();
        throw new TimeoutException("Không nhận được phiên Bilibili từ Chrome. Kiểm tra extension BiliSub Bridge đã được cài trong đúng profile Chrome và Chrome đang đăng nhập Bilibili.");
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _listener.Stop();
        try { await _acceptLoop; }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
        _lifetime.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_lifetime.Token); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { break; }
            _ = HandleClientAsync(client);
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                using var stream = client.GetStream();
                var request = await ReadRequestAsync(stream, timeout.Token);
                if (request is null) return;
                switch (request.Method, request.Path)
                {
                    case ("GET", "/bilisub-bridge"):
                        if (!TokenEquals(request.QueryToken))
                        {
                            await WriteTextAsync(stream, 403, "Forbidden", "text/plain", "Token không hợp lệ.", timeout.Token);
                            return;
                        }
                        await WriteTextAsync(stream, 200, "OK", "text/html", BuildPage(Token, _pageSecret), timeout.Token);
                        return;
                    case ("POST", "/session"):
                        await HandleSessionAsync(stream, request, timeout.Token);
                        return;
                    case ("POST", "/error"):
                        await HandleErrorAsync(stream, request, timeout.Token);
                        return;
                    case ("GET", "/favicon.ico"):
                        await WriteTextAsync(stream, 204, "No Content", "text/plain", string.Empty, timeout.Token);
                        return;
                    default:
                        await WriteTextAsync(stream, 404, "Not Found", "text/plain", "Not found.", timeout.Token);
                        return;
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }
    }

    private async Task HandleSessionAsync(NetworkStream stream, HttpRequest request, CancellationToken cancellationToken)
    {
        var payload = Deserialize<BridgePayload>(request.Body);
        if (payload is null || !TokenEquals(payload.Token))
        {
            await WriteJsonAsync(stream, 403, "Forbidden", new { ok = false, error = "Token không hợp lệ." }, cancellationToken);
            return;
        }

        var sessdata = (payload.Sessdata ?? string.Empty).Trim();
        if (sessdata.Length == 0 || sessdata.Length > 4096 || sessdata.Any(character => character <= 0x20 || character == 0x7F || character == ';'))
        {
            const string message = "SESSDATA không hợp lệ.";
            await WriteJsonAsync(stream, 400, "Bad Request", new { ok = false, error = message }, cancellationToken);
            _completion.TrySetException(new InvalidOperationException(message));
            return;
        }

        try
        {
            var user = await _importSession("SESSDATA=" + sessdata, cancellationToken);
            try { await WriteJsonAsync(stream, 200, "OK", new { ok = true, user }, cancellationToken); }
            catch (Exception) { }
            _completion.TrySetResult(user);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            var message = Sanitize(error.Message, "Không xác minh được phiên Bilibili.");
            try { await WriteJsonAsync(stream, 400, "Bad Request", new { ok = false, error = message }, cancellationToken); }
            catch (Exception) { }
            _completion.TrySetException(new InvalidOperationException(message));
        }
    }

    private async Task HandleErrorAsync(NetworkStream stream, HttpRequest request, CancellationToken cancellationToken)
    {
        var payload = Deserialize<ErrorPayload>(request.Body);
        if (payload is null || !TokenEquals(payload.Token))
        {
            await WriteJsonAsync(stream, 403, "Forbidden", new { ok = false, error = "Token không hợp lệ." }, cancellationToken);
            return;
        }

        var message = Sanitize(payload.Error, "Không đọc được phiên Bilibili từ Chrome.");
        try { await WriteJsonAsync(stream, 200, "OK", new { ok = true }, cancellationToken); }
        catch (Exception) { }
        _completion.TrySetException(new InvalidOperationException(message));
    }

    private bool TokenEquals(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        var expected = Encoding.UTF8.GetBytes(Token);
        var actual = Encoding.UTF8.GetBytes(value);
        return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static T? Deserialize<T>(byte[] body) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(Encoding.UTF8.GetString(body), JsonOptions); }
        catch (JsonException) { return null; }
    }

    private static string Sanitize(string? value, string fallback)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0) return fallback;
        return text.Length <= 300 ? text : text[..300];
    }

    private static async Task<HttpRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxHeaderBytes];
        var length = 0;
        var headerEnd = -1;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken);
            if (read == 0) return null;
            length += read;
            headerEnd = FindHeaderEnd(buffer.AsSpan(0, length));
            if (headerEnd >= 0) break;
        }
        if (headerEnd < 0) return null;

        var headerText = Encoding.ASCII.GetString(buffer, 0, headerEnd);
        var lines = headerText.Split("\r\n", StringSplitOptions.None);
        if (lines.Length == 0) return null;
        var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length < 2) return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        var contentLength = 0;
        if (headers.TryGetValue("Content-Length", out var contentLengthText)
            && (!int.TryParse(contentLengthText, out contentLength) || contentLength < 0 || contentLength > MaxBodyBytes))
        {
            return null;
        }

        var body = new byte[contentLength];
        var bodyRead = Math.Min(Math.Max(0, length - headerEnd - 4), contentLength);
        if (bodyRead > 0) Buffer.BlockCopy(buffer, headerEnd + 4, body, 0, bodyRead);
        while (bodyRead < contentLength)
        {
            var read = await stream.ReadAsync(body.AsMemory(bodyRead), cancellationToken);
            if (read == 0) return null;
            bodyRead += read;
        }

        var target = requestLine[1];
        var queryIndex = target.IndexOf('?');
        var path = queryIndex >= 0 ? target[..queryIndex] : target;
        var query = queryIndex >= 0 ? target[(queryIndex + 1)..] : string.Empty;
        return new HttpRequest(requestLine[0], path, query, body);
    }

    private static int FindHeaderEnd(ReadOnlySpan<byte> buffer)
    {
        for (var index = 0; index <= buffer.Length - 4; index++)
        {
            if (buffer[index] == '\r' && buffer[index + 1] == '\n'
                && buffer[index + 2] == '\r' && buffer[index + 3] == '\n')
            {
                return index;
            }
        }
        return -1;
    }

    private static async Task WriteJsonAsync(NetworkStream stream, int status, string reason, object payload, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(payload);
        await WriteTextAsync(stream, status, reason, "application/json", body, cancellationToken);
    }

    private static async Task WriteTextAsync(NetworkStream stream, int status, string reason, string contentType, string body, CancellationToken cancellationToken)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var header = $"HTTP/1.1 {status} {reason}\r\n"
            + $"Content-Type: {contentType}; charset=utf-8\r\n"
            + $"Content-Length: {bodyBytes.Length}\r\n"
            + "Cache-Control: no-store\r\n"
            + "X-Content-Type-Options: nosniff\r\n"
            + "Referrer-Policy: no-referrer\r\n"
            + "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cancellationToken);
        if (bodyBytes.Length > 0) await stream.WriteAsync(bodyBytes, cancellationToken);
    }

    private static string BuildPage(string token, string pageSecret) => $$"""
<!doctype html>
<html lang="vi">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="bilisub-bridge" content="{{token}}">
<meta name="bilisub-bridge-secret" content="{{pageSecret}}">
<title>BiliSub Studio - Đồng bộ từ Chrome</title>
<style>
:root { color-scheme: dark; }
body { margin: 0; min-height: 100vh; display: grid; place-items: center; background: #0f172a; color: #e2e8f0; font-family: "Segoe UI", system-ui, sans-serif; }
main { width: min(560px, calc(100vw - 48px)); padding: 32px; border-radius: 18px; background: #1e293b; box-shadow: 0 24px 80px rgba(0,0,0,.35); }
h1 { margin: 0 0 12px; font-size: 22px; }
p { margin: 0; line-height: 1.6; color: #cbd5e1; }
</style>
</head>
<body>
<main>
<h1>Đang lấy phiên Bilibili từ Chrome…</h1>
<p id="status">Nếu tab này không tự đóng, extension <strong>BiliSub Bridge</strong> chưa được cài hoặc chưa bật trong đúng profile Chrome.</p>
</main>
<script>
setTimeout(function () {
  var status = document.getElementById('status');
  if (status && status.dataset.done !== '1') {
    var message = 'Không thấy extension BiliSub Bridge. Hãy cài extension trong chrome://extensions rồi bấm “Đồng bộ từ Chrome” lại.';
    status.dataset.done = '1';
    status.textContent = message;
    var meta = document.querySelector('meta[name="bilisub-bridge"]');
    var token = meta ? meta.getAttribute('content') : '';
    fetch('/error', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ token: token, error: message })
    }).catch(function () {});
  }
}, 10000);
</script>
</body>
</html>
""";

    private sealed record HttpRequest(string Method, string Path, string Query, byte[] Body)
    {
        public string? QueryToken
        {
            get
            {
                foreach (var part in Query.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var pair = part.Split('=', 2);
                    if (pair.Length == 2 && pair[0] == "token") return Uri.UnescapeDataString(pair[1]);
                }
                return null;
            }
        }
    }

    private sealed record BridgePayload(string? Token, string? Sessdata);
    private sealed record ErrorPayload(string? Token, string? Error);
}
