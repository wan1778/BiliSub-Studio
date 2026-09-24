using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using BiliSubStudio.Core.Video;

namespace BiliSubStudio.Core.ContractTests;

// Field 2026-09-24: Bilibili reset/timeout thoáng qua giết cả job tải dù thử
// lại là xong; ngược lại 412/403/video xóa retry cũng vô ích. File này ghim
// ma trận phân loại + các cơ chế phục hồi (stall, cooldown) để lần sau tải
// không chết oan cũng không bò vô hạn.
internal static class DownloadResilienceContract
{
    public static Task ResolveRetryClassificationAsync()
    {
        var classifier = typeof(YtDlpResolver).GetMethod("IsTransientResolveError", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing resolve retry classifier");
        bool Classify(string message) => (bool)classifier.Invoke(null, [message])!;
        // Real transient lines from the 2026-09-24 field incidents.
        True(Classify("yt-dlp resolve: ERROR: [BiliBili] 16Xaw64EQn: Unable to download webpage: ('Connection aborted.', ConnectionResetError(10054, 'An existing connection was forcibly closed by the remote host', None, 10054, None))"), "10054 abort must retry");
        True(Classify("yt-dlp resolve: ERROR: [BiliBili] 16Xaw64EQn: Unable to download webpage: HTTPSConnectionPool(host='www.bilibili.com', port=443): Read timed out. (read timeout=20.0)"), "read timeout must retry");
        True(Classify("ERROR: Unable to download webpage: <urlopen error [Errno 11001] getaddrinfo failed>"), "DNS blip must retry");
        // Permanent: retrying never heals these, fail fast with the message.
        True(!Classify("yt-dlp resolve: ERROR: [BiliBili] 185ac6eEE2: Unable to download webpage: HTTP Error 412: Precondition Failed"), "412 must not retry");
        True(!Classify("ERROR: [BiliBili] x: Unable to download webpage: HTTP Error 403: Forbidden"), "403 must not retry");
        True(!Classify("ERROR: [BiliBili] x: Unable to download webpage: HTTP Error 404: Not Found"), "404 must not retry");
        True(!Classify("ERROR: [BiliBili] x: Private video"), "private video must not retry");
        True(!Classify("ERROR: [BiliBili] x: Video unavailable"), "unavailable video must not retry");
        True(!Classify(string.Empty), "empty message must not retry");
        True(!Classify("ERROR: some brand-new yt-dlp failure shape"), "unknown failures must not retry blind");
        return Task.CompletedTask;
    }

    public static Task SessionGuidanceAsync()
    {
        var guidance = typeof(VideoDownloadService).GetMethod("WithBilibiliSessionGuidance", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing 412 session guidance");
        string Guide(string message) => (string)guidance.Invoke(null, [message])!;
        var blocked = "yt-dlp fallback Audio: ERROR: [BiliBili] x: Unable to download webpage: HTTP Error 412: Precondition Failed";
        var guided = Guide(blocked);
        True(guided.StartsWith(blocked, StringComparison.Ordinal), "guidance must keep the original message intact");
        True(guided.Contains("đăng nhập lại", StringComparison.Ordinal), "412 must tell the user to re-login");
        True(guided.Contains("resume", StringComparison.OrdinalIgnoreCase), "412 must reassure resume is kept");
        Equal(guided, Guide(guided));
        var plain = "yt-dlp fallback Audio: ERROR: connection reset by peer";
        Equal(plain, Guide(plain));
        Equal(string.Empty, Guide(string.Empty));
        return Task.CompletedTask;
    }

    public static async Task StallDetectorAsync()
    {
        var root = TempRoot();
        try
        {
            // Headers 206 đầy đủ nhưng body không bao giờ ra byte: tuyến chết
            // hẳn mà không lỗi dứt khoát. Hết StallTimeout phải nổ
            // DownloadStalledException (fail nhanh, câu rõ ràng), không treo.
            var payload = new byte[2 * 1024 * 1024];
            using var client = new HttpClient(new StalledBodyHandler(payload));
            var downloader = new RangeDownloader(client);
            var stream = FixtureStream(payload.Length);
            var error = await ThrowsAsync<DownloadStalledException>(() => downloader.DownloadAsync(
                stream, root, "video", 2, null, null, CancellationToken.None, TimeSpan.FromMilliseconds(400)));
            True(error.Message.Contains("đứng yên", StringComparison.Ordinal), "stall message must say the transfer froze");
            True(error.Message.Contains("resume", StringComparison.OrdinalIgnoreCase), "stall message must reassure resume is kept");
            True(!Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Any(), "stall leaked .tmp artifacts");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    public static async Task UserCancelStillReportsCancelAsync()
    {
        var root = TempRoot();
        try
        {
            // Cùng tuyến chết, nhưng user hủy trước khi hết StallTimeout:
            // phải báo cancel (ý user), không được báo stall.
            var payload = new byte[2 * 1024 * 1024];
            using var client = new HttpClient(new StalledBodyHandler(payload));
            var downloader = new RangeDownloader(client);
            using var cancellation = new CancellationTokenSource(150);
            await ThrowsAsync<OperationCanceledException>(() => downloader.DownloadAsync(
                FixtureStream(payload.Length), root, "video", 2, null, null, cancellation.Token, TimeSpan.FromMinutes(30)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    public static async Task RefreshStormSuppressedAsync()
    {
        var root = TempRoot();
        try
        {
            // Mọi endpoint đều 500: không cooldown, mỗi failure là 1 refetch
            // (hàng chục cuộc gọi trong vài giây). Có cooldown 45s: đúng 1
            // cuộc gọi đầu, còn lại backoff lặng lẽ rồi hủy theo timeout test.
            var payload = new byte[4 * 1024 * 1024];
            var handler = new AlwaysFailHandler();
            using var client = new HttpClient(handler);
            var downloader = new RangeDownloader(client);
            var endpoints = new[] { "https://dead0.invalid/video", "https://dead1.invalid/video", "https://dead2.invalid/video" };
            var stream = FixtureStream(payload.Length, endpoints, 0);
            var refreshCalls = 0;
            Task<ResolvedStream> CountingRefresh(long seen, CancellationToken token)
            {
                Interlocked.Increment(ref refreshCalls);
                return Task.FromResult(stream);
            }
            using var cancellation = new CancellationTokenSource(4_000);
            await ThrowsAsync<OperationCanceledException>(() => downloader.DownloadAsync(
                stream, root, "video", 4, CountingRefresh, null, cancellation.Token));
            Equal(1, refreshCalls);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    public static Task RefreshCooldownDecisionAsync()
    {
        var decide = typeof(RangeDownloader).GetMethod("ShouldRefreshNow", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing refresh cooldown decision");
        bool Decide(DateTime last, DateTime now) => (bool)decide.Invoke(null, [last, now])!;
        var now = DateTime.UtcNow;
        True(Decide(DateTime.MinValue, now), "first failure must always refresh");
        True(Decide(now - TimeSpan.FromHours(1), now), "hour-old refresh must refresh");
        True(!Decide(now, now), "just-refreshed must back off quietly");
        True(!Decide(now - TimeSpan.FromSeconds(44), now), "44s-old refresh must back off quietly");
        True(Decide(now - TimeSpan.FromSeconds(46), now), "46s-old refresh must refresh");
        return Task.CompletedTask;
    }

    private static ResolvedStream FixtureStream(long size) => new(StreamKind.Video, "v1", "https://fixture.invalid/video", new Dictionary<string, string>(), size, 1080, "m4s", 1);

    private static ResolvedStream FixtureStream(long size, IReadOnlyList<string> endpoints, int index) => new(StreamKind.Video, "v1", endpoints[index], new Dictionary<string, string>(), size, 1080, "m4s", 1, endpoints, index);

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "bilisub-dl-resilience-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task<TException> ThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException error)
        {
            return error;
        }
        throw new InvalidOperationException($"expected {typeof(TException).Name}");
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"expected {expected}; got {actual}");
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    // Probe 0-0 trả 206/1 byte bình thường (qua được ProbeAsync); mọi segment
    // trả headers 206 hợp lệ nhưng body không bao giờ ra byte — tuyến chết hẳn
    // mà không lỗi dứt khoát.
    private sealed class StalledBodyHandler(byte[] payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var range = request.Headers.Range?.Ranges.Single();
            var start = range?.From ?? 0;
            var end = range?.To ?? payload.Length - 1;
            HttpContent content;
            if (start == 0 && end == 0)
            {
                content = new ByteArrayContent(payload, 0, 1);
            }
            else
            {
                var length = checked((int)(end - start + 1));
                content = new StreamContent(new NeverStream());
                content.Headers.ContentLength = length;
            }
            content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, payload.Length);
            content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content });
        }
    }

    private sealed class NeverStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("sync read unexpected");
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // Probe 0-0 trả 206 bình thường (vào được vòng worker); mọi segment đều
    // 500: tuyến chết toàn diện, failure nào cũng đủ điều kiện refresh ngay.
    private sealed class AlwaysFailHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var range = request.Headers.Range?.Ranges.Single();
            if ((range?.From ?? 0) == 0 && (range?.To ?? 0) == 0)
            {
                var probe = new ByteArrayContent(new byte[] { 0x01 });
                probe.Headers.ContentRange = new ContentRangeHeaderValue(0, 0, 4 * 1024 * 1024);
                probe.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = probe });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }
}
