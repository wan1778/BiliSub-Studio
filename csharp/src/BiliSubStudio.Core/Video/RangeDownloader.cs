using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace BiliSubStudio.Core.Video;

public sealed class RangeNotSupportedException : IOException
{
    public RangeNotSupportedException(string message) : base(message) { }
}

public sealed class RangeDownloader
{
    // Proven field-stable size from the legacy 3.9.2/3.9.4 downloader. Larger 32 MiB
    // requests regress on Bilibili CDNs that terminate a Range body early.
    public const long DefaultChunkSize = 4L * 1024 * 1024;
    private const int MaxSegmentAttempts = 32;
    private const int ManifestVersion = 2;
    private readonly HttpClient _http;

    public RangeDownloader(HttpClient http) => _http = http;

    public async Task<string> DownloadAsync(
        ResolvedStream original,
        string workDirectory,
        string baseName,
        int concurrency,
        Func<long, CancellationToken, Task<ResolvedStream>>? refresh,
        Action<RangeDownloadStatus>? status,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(original.Url)) throw new ArgumentException("Stream URL rỗng.", nameof(original));
        concurrency = Math.Clamp(concurrency, 1, 8);
        Directory.CreateDirectory(workDirectory);

        var (total, rangeSupported) = await ProbeAsync(original, cancellationToken);
        status?.Invoke(new RangeDownloadStatus(rangeSupported, 0, concurrency, 0, total, 0));
        if (!rangeSupported)
        {
            throw new RangeNotSupportedException("CDN không hỗ trợ HTTP Range hợp lệ.");
        }

        var output = Path.Combine(workDirectory, baseName + ".stream");
        if (File.Exists(output))
        {
            if (new FileInfo(output).Length == total)
            {
                status?.Invoke(new RangeDownloadStatus(true, 0, concurrency, total, total, 0));
                return output;
            }
            File.Delete(output);
        }

        var chunkSize = Math.Min(DefaultChunkSize, Math.Max(1L * 1024 * 1024, (total + concurrency - 1) / concurrency));
        var segments = BuildSegments(total, chunkSize);
        var segmentDirectory = output + ".segments";
        var manifestPath = output + ".resume.json";
        Directory.CreateDirectory(segmentDirectory);
        DeleteFiles(segmentDirectory, "*.tmp");

        var completed = await LoadCompletedAsync(manifestPath, segmentDirectory, total, chunkSize, segments, cancellationToken);
        await SaveManifestAsync(manifestPath, total, chunkSize, completed.Keys, cancellationToken);
        var committedBytes = completed.Keys.Sum(index => segments[index].Length);
        long networkBytes = 0;
        var stopwatch = Stopwatch.StartNew();
        var active = 0;
        var inFlight = new ConcurrentDictionary<int, long>();
        var queue = new ConcurrentQueue<Segment>(segments.Where(x => !completed.ContainsKey(x.Index)));
        using var manifestGate = new SemaphoreSlim(1, 1);
        var current = original;
        using var currentGate = new SemaphoreSlim(1, 1);
        using var workerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var workerToken = workerCancellation.Token;
        Exception? fatalTransportError = null;

        async Task WorkerAsync()
        {
            while (queue.TryDequeue(out var segment))
            {
                workerToken.ThrowIfCancellationRequested();
                try
                {
                    Exception? last = null;
                    var weakProgressFailures = 0;
                    for (var attempt = 1; attempt <= MaxSegmentAttempts; attempt++)
                    {
                        workerToken.ThrowIfCancellationRequested();
                        ResolvedStream stream;
                        await currentGate.WaitAsync(workerToken);
                        try { stream = current; }
                        finally { currentGate.Release(); }

                        var path = SegmentPath(segmentDirectory, segment.Index);
                        var temporary = path + ".tmp";
                        var partialBefore = PartialLength(temporary, segment.Length);
                        inFlight[segment.Index] = partialBefore;
                        try
                        {
                            long bytes;
                            Interlocked.Increment(ref active);
                            try
                            {
                                bytes = await DownloadSegmentAsync(stream, segment, total, path, delta =>
                                {
                                    Interlocked.Add(ref networkBytes, delta);
                                    inFlight.AddOrUpdate(segment.Index, delta, (_, existing) => existing + delta);
                                    var bytesNow = Math.Min(total, Volatile.Read(ref committedBytes) + inFlight.Values.Sum());
                                    var speed = Math.Max(0, Volatile.Read(ref networkBytes) / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds));
                                    status?.Invoke(new RangeDownloadStatus(true, Volatile.Read(ref active), concurrency, bytesNow, total, speed));
                                }, workerToken);
                            }
                            finally { Interlocked.Decrement(ref active); }
                            if (bytes != segment.Length) throw new InvalidDataException($"Segment {segment.Index} thiếu byte.");

                            await manifestGate.WaitAsync(workerToken);
                            try
                            {
                                completed[segment.Index] = true;
                                inFlight.TryRemove(segment.Index, out _);
                                Interlocked.Exchange(ref committedBytes, completed.Keys.Sum(index => segments[index].Length));
                                await SaveManifestAsync(manifestPath, total, chunkSize, completed.Keys, workerToken);
                            }
                            finally { manifestGate.Release(); }
                            last = null;
                            break;
                        }
                        catch (RangeNotSupportedException error)
                        {
                            inFlight.TryRemove(segment.Index, out _);
                            Interlocked.CompareExchange(ref fatalTransportError, error, null);
                            workerCancellation.Cancel();
                            throw;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception error)
                        {
                            var partialAfter = PartialLength(temporary, segment.Length);
                            var gained = Math.Max(0, partialAfter - partialBefore);
                            inFlight[segment.Index] = partialAfter;
                            completed.TryRemove(segment.Index, out _);
                            Interlocked.Exchange(ref committedBytes, completed.Keys.Sum(index => segments[index].Length));
                            last = error;

                            // Preserve every byte. Normal short reads (hundreds of KiB+) continue on the
                            // same URL. Pathological tiny reads like the real 379-byte incident still
                            // continue from the missing byte, but also trigger a signed-URL/CDN refresh.
                            var remainingBefore = Math.Max(1, segment.Length - partialBefore);
                            var meaningfulProgress = Math.Min(64L * 1024, remainingBefore);
                            if (gained >= meaningfulProgress)
                            {
                                weakProgressFailures = 0;
                            }
                            else
                            {
                                weakProgressFailures++;
                                if (refresh is not null && weakProgressFailures % 2 == 0)
                                {
                                    await currentGate.WaitAsync(workerToken);
                                    try
                                    {
                                        if (current.Generation == stream.Generation)
                                        {
                                            current = await refresh(stream.Generation, workerToken);
                                        }
                                    }
                                    finally { currentGate.Release(); }
                                }
                            }

                            var delay = gained > 0
                                ? Math.Min(750, 100 * attempt)
                                : Math.Min(4_000, 250 * Math.Max(1, weakProgressFailures) * Math.Max(1, weakProgressFailures));
                            await Task.Delay(TimeSpan.FromMilliseconds(delay), workerToken);
                        }
                    }
                    if (last is not null)
                    {
                        throw new IOException($"Tải segment {segment.Index} thất bại sau {MaxSegmentAttempts} lượt tiếp tục.", last);
                    }
                }
                finally
                {
                    inFlight.TryRemove(segment.Index, out _);
                }
            }
        }

        try
        {
            try
            {
                await Task.WhenAll(Enumerable.Range(0, Math.Min(concurrency, Math.Max(1, queue.Count))).Select(_ => WorkerAsync()));
            }
            catch (Exception) when (fatalTransportError is not null)
            {
                throw fatalTransportError!;
            }
            if (completed.Count != segments.Count)
            {
                throw new InvalidDataException("Resume manifest thiếu segment hoàn tất.");
            }
            await AssembleAsync(output, segmentDirectory, segments, total, cancellationToken);
            File.Delete(manifestPath);
            Directory.Delete(segmentDirectory, recursive: true);
            status?.Invoke(new RangeDownloadStatus(true, 0, concurrency, total, total, 0));
            return output;
        }
        catch
        {
            DeleteFiles(segmentDirectory, "*.tmp");
            TryDelete(output + ".assembling.tmp");
            throw;
        }
    }

    private async Task<(long Total, bool Supported)> ProbeAsync(ResolvedStream stream, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(stream, new RangeHeaderValue(0, 0));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            var range = response.Content.Headers.ContentRange
                ?? throw new InvalidDataException("Probe thiếu Content-Range.");
            if (!range.HasRange || range.From != 0 || range.To != 0 || range.Length is null or <= 0)
            {
                throw new InvalidDataException($"Probe Content-Range sai: {range}.");
            }
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            var first = new byte[2];
            var read = await body.ReadAsync(first.AsMemory(0, 1), cancellationToken);
            if (read != 1 || await body.ReadAsync(first.AsMemory(1, 1), cancellationToken) != 0)
                throw new InvalidDataException("Probe Range phải trả đúng một byte.");
            return (range.Length.Value, true);
        }
        if (response.IsSuccessStatusCode && response.Content.Headers.ContentLength is > 0)
        {
            return (response.Content.Headers.ContentLength.Value, false);
        }
        throw new HttpRequestException($"Probe CDN HTTP {(int)response.StatusCode}.");
    }

    private async Task<long> DownloadSegmentAsync(
        ResolvedStream stream,
        Segment segment,
        long expectedTotal,
        string finalPath,
        Action<int> transferred,
        CancellationToken cancellationToken)
    {
        var temporary = finalPath + ".tmp";
        var existing = PartialLength(temporary, segment.Length);
        if (existing == segment.Length)
        {
            File.Move(temporary, finalPath, overwrite: true);
            return existing;
        }

        var requestStart = segment.Start + existing;
        var remaining = segment.Length - existing;
        try
        {
            using var request = CreateRequest(stream, new RangeHeaderValue(requestStart, segment.End));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode != HttpStatusCode.PartialContent)
            {
                throw new RangeNotSupportedException($"CDN trả HTTP {(int)response.StatusCode}, cần 206.");
            }
            var range = response.Content.Headers.ContentRange
                ?? throw new InvalidDataException("Segment thiếu Content-Range.");
            if (!range.HasRange || range.From != requestStart || range.To != segment.End || range.Length != expectedTotal)
            {
                throw new InvalidDataException($"Content-Range sai: {range}; cần {requestStart}-{segment.End}/{expectedTotal}.");
            }
            if (response.Content.Headers.ContentLength is { } contentLength && contentLength != remaining)
            {
                throw new InvalidDataException($"Content-Length sai: {contentLength}/{remaining}.");
            }
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (mediaType.Contains("text/html", StringComparison.OrdinalIgnoreCase) || mediaType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"CDN trả nội dung không phải media: {mediaType}.");
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var file = new FileStream(temporary, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 256 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
            if (file.Length != existing)
            {
                file.SetLength(existing);
            }
            file.Position = existing;
            long total = existing;
            var buffer = new byte[256 * 1024];
            while (total < segment.Length)
            {
                var wanted = (int)Math.Min(buffer.Length, segment.Length - total);
                var read = await body.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken);
                if (read == 0)
                {
                    // Preserve the bytes already written. The next attempt starts at this exact byte.
                    throw new EndOfStreamException($"Short body {total}/{segment.Length}; tiếp tục từ byte {segment.Start + total}.");
                }
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                total += read;
                transferred(read);
            }
            var extra = new byte[1];
            if (await body.ReadAsync(extra, cancellationToken) != 0)
            {
                throw new InvalidDataException($"Oversized Range body cho segment {segment.Index}.");
            }
            await file.FlushAsync(cancellationToken);
            file.Flush(flushToDisk: true);
            file.Close();
            File.Move(temporary, finalPath, overwrite: true);
            return total;
        }
        catch (OperationCanceledException)
        {
            TryDelete(temporary);
            throw;
        }
        catch (RangeNotSupportedException)
        {
            TryDelete(temporary);
            throw;
        }
        catch (InvalidDataException)
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static HttpRequestMessage CreateRequest(ResolvedStream stream, RangeHeaderValue range)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, stream.Url)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        foreach (var pair in stream.Headers)
        {
            request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
        }
        request.Headers.Range = range;
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        return request;
    }

    private static List<Segment> BuildSegments(long total, long chunkSize)
    {
        var output = new List<Segment>();
        for (long start = 0; start < total; start += chunkSize)
        {
            var end = Math.Min(total - 1, start + chunkSize - 1);
            output.Add(new Segment(output.Count, start, end));
        }
        return output;
    }

    private static async Task<ConcurrentDictionary<int, bool>> LoadCompletedAsync(
        string manifestPath,
        string segmentDirectory,
        long total,
        long chunkSize,
        IReadOnlyList<Segment> segments,
        CancellationToken cancellationToken)
    {
        var output = new ConcurrentDictionary<int, bool>();
        try
        {
            if (!File.Exists(manifestPath)) return output;
            var manifest = JsonSerializer.Deserialize<ResumeManifest>(await File.ReadAllTextAsync(manifestPath, cancellationToken));
            if (manifest is null || manifest.Version != ManifestVersion || manifest.Total != total || manifest.ChunkSize != chunkSize)
            {
                return output;
            }
            foreach (var index in manifest.Completed.Distinct())
            {
                if (index < 0 || index >= segments.Count) continue;
                var path = SegmentPath(segmentDirectory, index);
                if (File.Exists(path) && new FileInfo(path).Length == segments[index].Length)
                {
                    output[index] = true;
                }
            }
        }
        catch (JsonException) { }
        return output;
    }

    private static async Task SaveManifestAsync(string path, long total, long chunkSize, IEnumerable<int> completed, CancellationToken cancellationToken)
    {
        var manifest = new ResumeManifest(ManifestVersion, total, chunkSize, completed.Order().ToArray());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
        var temporary = path + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await file.WriteAsync(bytes, cancellationToken);
                await file.FlushAsync(cancellationToken);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { TryDelete(temporary); }
    }

    private static async Task AssembleAsync(string output, string segmentDirectory, IReadOnlyList<Segment> segments, long total, CancellationToken cancellationToken)
    {
        var temporary = output + ".assembling.tmp";
        TryDelete(temporary);
        try
        {
            await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                foreach (var segment in segments)
                {
                    await using var source = new FileStream(SegmentPath(segmentDirectory, segment.Index), FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    if (source.Length != segment.Length) throw new InvalidDataException($"Segment {segment.Index} sai kích thước.");
                    await source.CopyToAsync(target, cancellationToken);
                }
                await target.FlushAsync(cancellationToken);
                target.Flush(flushToDisk: true);
            }
            if (new FileInfo(temporary).Length != total) throw new InvalidDataException("File stream ghép sai kích thước.");
            File.Move(temporary, output, overwrite: true);
        }
        finally { TryDelete(temporary); }
    }

    private static long PartialLength(string path, long maximum)
    {
        try
        {
            if (!File.Exists(path)) return 0;
            var length = new FileInfo(path).Length;
            if (length < 0 || length > maximum)
            {
                TryDelete(path);
                return 0;
            }
            return length;
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    private static string SegmentPath(string directory, int index) => Path.Combine(directory, $"{index:D8}.seg");

    private static void DeleteFiles(string directory, string pattern)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var path in Directory.EnumerateFiles(directory, pattern)) TryDelete(path);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed record Segment(int Index, long Start, long End)
    {
        public long Length => End - Start + 1;
    }

    private sealed record ResumeManifest(int Version, long Total, long ChunkSize, int[] Completed);
}
