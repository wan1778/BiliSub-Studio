using System.Net;
using System.Net.Http.Headers;
using BiliSubStudio.Core.Video;

const int TinyFieldRead = 379;
const int LegacyFieldRead = 1_473_579;
const int FourMiB = 4 * 1024 * 1024;

if (RangeDownloader.DefaultChunkSize != FourMiB)
    throw new InvalidOperationException($"Expected field-proven 4 MiB chunks; got {RangeDownloader.DefaultChunkSize}.");

var payload = new byte[FourMiB + 777_777];
for (var index = 0; index < payload.Length; index++) payload[index] = (byte)(index % 251);

var handler = new ShortReadHandler(payload, TinyFieldRead, LegacyFieldRead);
using var client = new HttpClient(handler);
var downloader = new RangeDownloader(client);
var refreshCount = 0;

ResolvedStream Stream(long generation) => new(
    StreamKind.Video,
    "30064",
    $"https://fixture.invalid/video?generation={generation}",
    new Dictionary<string, string>(),
    payload.LongLength,
    720,
    "m4s",
    generation);

var root = Path.Combine(Path.GetTempPath(), $"bilisub-range-shortread-{Guid.NewGuid():N}");
Directory.CreateDirectory(root);
try
{
    var output = await downloader.DownloadAsync(
        Stream(1),
        root,
        "video",
        1,
        (seen, _) =>
        {
            refreshCount++;
            return Task.FromResult(Stream(seen + 1));
        },
        null,
        CancellationToken.None);

    var actual = await File.ReadAllBytesAsync(output);
    if (!payload.AsSpan().SequenceEqual(actual))
        throw new InvalidOperationException("Assembled payload differs after short-read continuation.");

    if (refreshCount < 1)
        throw new InvalidOperationException("Repeated 379-byte progress never refreshed the signed stream URL.");

    var data = handler.DataRequests;
    if (data.Count < 4)
        throw new InvalidOperationException($"Expected multiple continuation requests; got {data.Count}.");

    if (data[0].Start != 0 || data[1].Start != TinyFieldRead || data[2].Start != TinyFieldRead * 2L)
        throw new InvalidOperationException(
            $"Short-read retry restarted or skipped bytes: first starts are {string.Join(", ", data.Take(3).Select(x => x.Start))}.");

    for (var index = 1; index < data.Count; index++)
    {
        if (data[index].Start <= data[index - 1].Start)
            throw new InvalidOperationException(
                $"Range request restarted instead of continuing: {data[index - 1].Start} -> {data[index].Start}.");
    }

    if (!data.All(x => x.Version == HttpVersion.Version11 && x.VersionPolicy == HttpVersionPolicy.RequestVersionExact))
        throw new InvalidOperationException("Range worker did not force exact HTTP/1.1.");

    Console.WriteLine($"PASS: short-read continuation kept bytes, refreshed after tiny reads, and assembled {actual.Length} bytes");
    Console.WriteLine($"Requests={data.Count}; refreshes={refreshCount}; first={string.Join(",", data.Take(6).Select(x => x.Start))}");
    return 0;
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch { }
}

internal sealed class ShortReadHandler(byte[] payload, int tinyRead, int recoveredRead) : HttpMessageHandler
{
    private readonly object _gate = new();
    private readonly List<RequestRecord> _dataRequests = [];

    public IReadOnlyList<RequestRecord> DataRequests
    {
        get { lock (_gate) return _dataRequests.ToArray(); }
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var range = request.Headers.Range?.Ranges.Single()
            ?? throw new InvalidOperationException("Fixture request is missing Range.");
        var start = range.From ?? 0;
        var end = range.To ?? payload.LongLength - 1;
        var requested = checked((int)(end - start + 1));
        var isProbe = start == 0 && end == 0;

        if (!isProbe)
        {
            lock (_gate)
            {
                _dataRequests.Add(new RequestRecord(start, end, request.Version, request.VersionPolicy));
            }
        }

        var recovered = request.RequestUri?.Query.Contains("generation=2", StringComparison.Ordinal) == true;
        var cap = isProbe ? 1 : recovered ? recoveredRead : tinyRead;
        var actualLength = Math.Min(requested, cap);
        var bytes = payload.AsSpan(checked((int)start), actualLength).ToArray();
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, payload.LongLength);
        // Deliberately claim the full requested body while sending fewer bytes. This reproduces
        // the real Bilibili/yt-dlp short-read family: "379 bytes read, ... more expected".
        content.Headers.ContentLength = requested;
        content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = content,
        });
    }

    public sealed record RequestRecord(long Start, long End, Version Version, HttpVersionPolicy VersionPolicy);
}
