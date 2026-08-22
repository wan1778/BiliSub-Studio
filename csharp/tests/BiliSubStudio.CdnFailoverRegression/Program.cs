using System.Net;
using System.Net.Http.Headers;
using BiliSubStudio.Core.Video;

const int TinyRead = 379;
const int FourMiB = 4 * 1024 * 1024;

var payload = new byte[FourMiB + 123_457];
for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 251);

var handler = new PrimaryBackupHandler(payload, TinyRead);
using var http = new HttpClient(handler);
var downloader = new RangeDownloader(http);
var refreshCount = 0;

ResolvedStream Primary(long generation) => new(
    StreamKind.Video,
    "30064",
    "https://primary-cdn.fixture/media/video-30064.m4s?token=primary-secret",
    new Dictionary<string, string>(),
    payload.LongLength,
    720,
    "m4s",
    generation,
    new[]
    {
        "https://primary-cdn.fixture/media/video-30064.m4s?token=primary-secret",
        "https://backup-cdn.fixture/media/video-30064.m4s?token=backup-secret",
    },
    0);

ResolvedStream Backup(long generation) => Primary(generation) with
{
    Url = "https://backup-cdn.fixture/media/video-30064.m4s?token=backup-secret",
    EndpointIndex = 1,
};

var root = Path.Combine(Path.GetTempPath(), $"bilisub-cdn-failover-{Guid.NewGuid():N}");
Directory.CreateDirectory(root);
try
{
    var output = await downloader.DownloadAsync(
        Primary(1),
        root,
        "video",
        1,
        (seen, _) =>
        {
            refreshCount++;
            if (seen != 1) throw new InvalidOperationException($"Unexpected refresh generation {seen}.");
            return Task.FromResult(Backup(2));
        },
        null,
        CancellationToken.None);

    var actual = await File.ReadAllBytesAsync(output);
    if (!payload.AsSpan().SequenceEqual(actual))
        throw new InvalidOperationException("Final payload changed during primary -> backup CDN rotation.");
    if (refreshCount != 1)
        throw new InvalidOperationException($"Expected one CDN rotation; got {refreshCount}.");

    var requests = handler.DataRequests;
    if (requests.Count < 4)
        throw new InvalidOperationException($"Expected continuation + backup traffic; got {requests.Count} requests.");

    var first = requests[0];
    var second = requests[1];
    var third = requests[2];
    if (!string.Equals(first.Host, "primary-cdn.fixture", StringComparison.OrdinalIgnoreCase) || first.Start != 0)
        throw new InvalidOperationException($"Unexpected first data request: {first.Host} {first.Start}-{first.End}.");
    if (!string.Equals(second.Host, "primary-cdn.fixture", StringComparison.OrdinalIgnoreCase) || second.Start != TinyRead)
        throw new InvalidOperationException($"First short-read did not continue from byte {TinyRead}: {second.Host} {second.Start}-{second.End}.");
    if (!string.Equals(third.Host, "backup-cdn.fixture", StringComparison.OrdinalIgnoreCase) || third.Start != TinyRead * 2L)
        throw new InvalidOperationException($"CDN failover did not preserve exact missing offset {TinyRead * 2L}: {third.Host} {third.Start}-{third.End}.");

    if (requests.Any(x => x.Url.Contains("token=", StringComparison.Ordinal) && x.Host is not ("primary-cdn.fixture" or "backup-cdn.fixture")))
        throw new InvalidOperationException("Unexpected CDN route in fixture.");

    Console.WriteLine("PASS: primary CDN short-read rotated to backup CDN and preserved the exact Range offset");
    Console.WriteLine("First requests: " + string.Join(" | ", requests.Take(5).Select(x => $"{x.Host}:{x.Start}-{x.End}")));
    return 0;
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch { }
}

internal sealed class PrimaryBackupHandler(byte[] payload, int tinyRead) : HttpMessageHandler
{
    private readonly object _gate = new();
    private readonly List<RequestRecord> _requests = [];

    public IReadOnlyList<RequestRecord> DataRequests
    {
        get { lock (_gate) return _requests.ToArray(); }
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri ?? throw new InvalidOperationException("Missing request URI.");
        var range = request.Headers.Range?.Ranges.Single()
            ?? throw new InvalidOperationException("Missing Range header.");
        var start = range.From ?? 0;
        var end = range.To ?? payload.LongLength - 1;
        var requested = checked((int)(end - start + 1));
        var probe = start == 0 && end == 0;

        if (!probe)
        {
            lock (_gate) _requests.Add(new RequestRecord(uri.Host, start, end, uri.ToString()));
        }

        var primary = string.Equals(uri.Host, "primary-cdn.fixture", StringComparison.OrdinalIgnoreCase);
        var cap = probe ? 1 : primary ? tinyRead : requested;
        var actualLength = Math.Min(requested, cap);
        var bytes = payload.AsSpan(checked((int)start), actualLength).ToArray();
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, payload.LongLength);
        content.Headers.ContentLength = requested;
        content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content });
    }

    public sealed record RequestRecord(string Host, long Start, long End, string Url);
}
