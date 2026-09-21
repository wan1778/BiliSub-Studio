using System.Net;
using System.Net.Http.Headers;
using BiliSubStudio.Core.Video;

namespace BiliSubStudio.Core.ContractTests;

// P1 download contracts: startup CDN race picks the fastest route, and sick
// routes rotate early instead of burning all 32 segment attempts.
// Two short-body shapes, two rotation paths (do not conflate):
// - truncated (chunked close, no length): EndOfStream, partials accumulate,
//   dribbles with big gains never trip the weak-progress path, so the
//   consecutive-short-body threshold (4) rotates;
// - lying length (explicit short Content-Length): rejected without trusting,
//   partials deleted, zero-gain failures rotate via the weak-progress path (2nd).
// Faults are injected by a routing mock HTTP handler: no real network.
internal static class DownloadEndpointContract
{
    private const long SabotageAboveBytes = 256L * 1024;

    public static async Task RunAsync()
    {
        await EndpointRaceSelectsFastestAsync();
        await ConsecutiveShortBodiesRotateFastAsync();
        await LyingLengthShortRotatesWeakAsync();
        await SingleShortBodyRecoversAsync();
        await SingleSickRouteStillCompletesAsync();
    }

    private static async Task EndpointRaceSelectsFastestAsync()
    {
        var root = TempRoot();
        try
        {
            var payload = Pattern(4 * 1024 * 1024);
            var behaviors = new Dictionary<string, EndpointBehavior>(StringComparer.OrdinalIgnoreCase)
            {
                ["slow.invalid"] = new() { ResponseDelayMs = 150 },
                ["fast.invalid"] = new(),
            };
            using var client = new HttpClient(new RoutingHandler(payload, behaviors));
            var downloader = new RangeDownloader(client);
            var stream = FixtureStream(payload.Length,
                ["https://slow.invalid/video", "https://fast.invalid/video"], 0);
            var output = await downloader.DownloadAsync(stream, root, "video", 4, RefreshSimulator(stream).Refresh, null,
                CancellationToken.None);
            Equal(payload.Length, checked((int)new FileInfo(output).Length));
            var actual = await File.ReadAllBytesAsync(output);
            True(payload.AsSpan().SequenceEqual(actual), "raced download differs from source");
            True(behaviors["fast.invalid"].ServedBytes >= payload.Length,
                "segments did not flow through the race winner");
            True(behaviors["slow.invalid"].ServedBytes < SabotageAboveBytes,
                $"sick route kept serving segments after losing the race ({behaviors["slow.invalid"].ServedBytes} bytes)");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task ConsecutiveShortBodiesRotateFastAsync()
    {
        var root = TempRoot();
        try
        {
            // Truncated dribble (chunked close, no length): every attempt gains
            // 100 KiB, so the weak-progress path never fires. The 4th
            // consecutive short body must rotate; attempt 5 resumes from byte
            // 409600 on the healthy route and finishes. Deterministic: single
            // worker, sequential attempts.
            var payload = Pattern(2 * 1024 * 1024);
            var behaviors = new Dictionary<string, EndpointBehavior>(StringComparer.OrdinalIgnoreCase)
            {
                // Sick stays the race winner (0ms < 150ms) so segments start on
                // the dribble route deterministically.
                ["sick.invalid"] = new() { ShortBodyAfterBytes = 100 * 1024 },
                ["well.invalid"] = new() { ResponseDelayMs = 150 },
            };
            using var client = new HttpClient(new RoutingHandler(payload, behaviors));
            var downloader = new RangeDownloader(client);
            var stream = FixtureStream(payload.Length,
                ["https://sick.invalid/video", "https://well.invalid/video"], 0);
            var simulator = RefreshSimulator(stream);
            var output = await downloader.DownloadAsync(stream, root, "video", 1, simulator.Refresh, null,
                CancellationToken.None);
            var actual = await File.ReadAllBytesAsync(output);
            True(payload.AsSpan().SequenceEqual(actual), "rotated download differs from source");
            Equal(1, simulator.Rotations);
            Equal(4, behaviors["sick.invalid"].ShortBodies);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task LyingLengthShortRotatesWeakAsync()
    {
        var root = TempRoot();
        try
        {
            // Lying explicit short Content-Length: rejected without trusting,
            // partials deleted, every failure counts as zero-gain, so the
            // pre-existing weak-progress rotation fires on the 2nd consecutive
            // failure. Same observable outcome (fast rotation, byte-correct),
            // different mechanism — pinned separately so the two paths never
            // get conflated again.
            var payload = Pattern(2 * 1024 * 1024);
            var behaviors = new Dictionary<string, EndpointBehavior>(StringComparer.OrdinalIgnoreCase)
            {
                ["sick.invalid"] = new() { ShortBodyAfterBytes = 100 * 1024, LyingContentLength = true },
                ["well.invalid"] = new() { ResponseDelayMs = 150 },
            };
            using var client = new HttpClient(new RoutingHandler(payload, behaviors));
            var downloader = new RangeDownloader(client);
            var stream = FixtureStream(payload.Length,
                ["https://sick.invalid/video", "https://well.invalid/video"], 0);
            var simulator = RefreshSimulator(stream);
            var output = await downloader.DownloadAsync(stream, root, "video", 1, simulator.Refresh, null,
                CancellationToken.None);
            var actual = await File.ReadAllBytesAsync(output);
            True(payload.AsSpan().SequenceEqual(actual), "rotated download differs from source");
            Equal(1, simulator.Rotations);
            Equal(2, behaviors["sick.invalid"].ShortBodies);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task SingleShortBodyRecoversAsync()
    {
        var root = TempRoot();
        try
        {
            // An isolated short body (sick exactly once, then healthy on the
            // same route) must recover on the immediate retry with no rotation.
            var payload = Pattern(2 * 1024 * 1024);
            var behaviors = new Dictionary<string, EndpointBehavior>(StringComparer.OrdinalIgnoreCase)
            {
                ["flaky.invalid"] = new() { ShortBodyAfterBytes = 100 * 1024, ShortBodyBudget = 1 },
            };
            using var client = new HttpClient(new RoutingHandler(payload, behaviors));
            var downloader = new RangeDownloader(client);
            var stream = FixtureStream(payload.Length, ["https://flaky.invalid/video"], 0);
            var simulator = RefreshSimulator(stream);
            var output = await downloader.DownloadAsync(stream, root, "video", 1, simulator.Refresh, null,
                CancellationToken.None);
            var actual = await File.ReadAllBytesAsync(output);
            True(payload.AsSpan().SequenceEqual(actual), "recovered download differs from source");
            Equal(1, behaviors["flaky.invalid"].ShortBodies);
            Equal(0, simulator.Rotations);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task SingleSickRouteStillCompletesAsync()
    {
        var root = TempRoot();
        try
        {
            // No alternate route: the threshold must not break single-route
            // resilience — retries still finish within the attempt budget.
            var payload = Pattern(2 * 1024 * 1024);
            var behaviors = new Dictionary<string, EndpointBehavior>(StringComparer.OrdinalIgnoreCase)
            {
                ["only.invalid"] = new() { ShortBodyAfterBytes = 100 * 1024 },
            };
            using var client = new HttpClient(new RoutingHandler(payload, behaviors));
            var downloader = new RangeDownloader(client);
            var stream = FixtureStream(payload.Length, ["https://only.invalid/video"], 0);
            var simulator = RefreshSimulator(stream);
            var output = await downloader.DownloadAsync(stream, root, "video", 1, simulator.Refresh, null,
                CancellationToken.None);
            var actual = await File.ReadAllBytesAsync(output);
            True(payload.AsSpan().SequenceEqual(actual), "single-route retry differs from source");
            Equal(0, simulator.Rotations);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static byte[] Pattern(int length)
    {
        var payload = new byte[length];
        for (var index = 0; index < payload.Length; index++) payload[index] = (byte)(index % 251);
        return payload;
    }

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "bilisub-dl-endpoint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static ResolvedStream FixtureStream(long size, IReadOnlyList<string> endpoints, int index) =>
        new(StreamKind.Video, "v1", endpoints[index], new Dictionary<string, string>(), size, 1080, "m4s", 1,
            endpoints, index);

    private static RefreshState RefreshSimulator(ResolvedStream stream) => new(stream);

    private sealed class RefreshState
    {
        private readonly object _gate = new();
        private ResolvedStream _current;
        public int Rotations { get; private set; }

        public RefreshState(ResolvedStream stream) => _current = stream;

        public Task<ResolvedStream> Refresh(long seen, CancellationToken _)
        {
            lock (_gate)
            {
                if (_current.Generation != seen) return Task.FromResult(_current);
                var next = _current.EndpointIndex + 1;
                if (_current.EndpointUrls is null || next >= _current.EndpointUrls.Count) return Task.FromResult(_current);
                Rotations++;
                _current = _current with
                {
                    Url = _current.EndpointUrls[next],
                    EndpointIndex = next,
                    Generation = _current.Generation + 1,
                };
                return Task.FromResult(_current);
            }
        }
    }

    private sealed class EndpointBehavior
    {
        public int ResponseDelayMs;
        public long ShortBodyAfterBytes = -1;
        public int ShortBodyBudget = int.MaxValue;
        // False (default): truncated body with NO Content-Length over a
        // forward-only stream — the faithful shape of a CDN terminating the
        // body early (chunked close). The client must read EndOfStream and keep
        // partial bytes for resume. True: an explicit short Content-Length —
        // a lying length the client must reject without trusting.
        public bool LyingContentLength;
        public long ServedBytes;
        public long ServedRequests;
        public long ShortBodies;
    }

    private sealed class RoutingHandler(byte[] payload, Dictionary<string, EndpointBehavior> behaviors) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri!.Host;
            if (!behaviors.TryGetValue(host, out var behavior))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            Interlocked.Increment(ref behavior.ServedRequests);
            var range = request.Headers.Range?.Ranges.Single();
            var start = range?.From ?? 0;
            var end = range?.To ?? payload.Length - 1;
            if (behavior.ResponseDelayMs > 0)
                await Task.Delay(behavior.ResponseDelayMs, cancellationToken);
            // Probe (bytes=0-0): answered exactly like production CDNs must be —
            // 206 with Content-Range 0-0/total and precisely one body byte.
            if (start == 0 && end == 0)
            {
                var probe = new ByteArrayContent(payload, 0, 1);
                probe.Headers.ContentRange = new ContentRangeHeaderValue(0, 0, payload.Length);
                probe.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
                return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = probe };
            }
            var available = Math.Max(0, payload.Length - checked((int)start));
            var wanted = (int)Math.Min(end - start + 1, available);
            var sabotage = behavior.ShortBodyAfterBytes >= 0 && wanted > SabotageAboveBytes
                && behavior.ShortBodyBudget > 0;
            var limit = sabotage ? Math.Min(wanted, checked((int)behavior.ShortBodyAfterBytes)) : wanted;
            if (sabotage && limit < wanted)
            {
                Interlocked.Increment(ref behavior.ShortBodies);
                Interlocked.Decrement(ref behavior.ShortBodyBudget);
            }
            Stream body = new CappedStream(payload, checked((int)start), limit);
            // A seekable stream lets the framework backfill Content-Length from
            // the truncated length on first header access, which converts every
            // short body into a length-mismatch rejection and deletes partials.
            // Real terminated bodies arrive over forward-only streams, so only
            // sabotaged bodies go forward-only here; healthy traffic keeps the
            // plain (seekable) shape it always had.
            if (sabotage && limit < wanted) body = new ForwardOnlyStream(body);
            var content = new StreamContent(body);
            content.Headers.ContentRange = new ContentRangeHeaderValue(start, start + wanted - 1, payload.Length);
            // A short body must arrive WITHOUT Content-Length, exactly like a CDN
            // that terminates the body early; otherwise the client reports a
            // length mismatch instead of EndOfStream. The lying-length variant
            // below is a separate behavior under test, not the default.
            if (behavior.LyingContentLength && sabotage && limit < wanted)
                content.Headers.ContentLength = limit;
            else if (!sabotage || limit == wanted) content.Headers.ContentLength = wanted;
            content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
            Interlocked.Add(ref behavior.ServedBytes, limit);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content };
            return response;
        }
    }

    private sealed class CappedStream(byte[] payload, int offset, int limit) : MemoryStream(payload, offset, limit, writable: false)
    {
    }

    // Forward-only view over a bounded slice: CanSeek false defeats
    // Content-Length backfill from the truncated length, so a terminated body
    // reaches the client exactly like a chunked CDN close (EndOfStream, partial
    // bytes kept) instead of a length-mismatch rejection.
    private sealed class ForwardOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            await inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
}
