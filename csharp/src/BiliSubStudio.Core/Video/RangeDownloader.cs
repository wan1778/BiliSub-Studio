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

// Field 2026-09-24: tuyến CDN chết hẳn nhưng không lỗi dứt khoát — worker quay
// vòng retry/backoff/refresh mãi, UI bò 1/8 cả đêm. Hết StallTimeout mà không
// thêm byte nào thì fail nhanh với câu rõ ràng thay vì treo vô hạn.
public sealed class DownloadStalledException : IOException
{
    public DownloadStalledException(string message) : base(message) { }
}

public sealed class RangeDownloader
{
    // Proven field-stable size from the legacy 3.9.2/3.9.4 downloader. Larger 32 MiB
    // requests regress on Bilibili CDNs that terminate a Range body early.
    public const long DefaultChunkSize = 4L * 1024 * 1024;
    // Zero byte tiến triển trong từng này thì coi như tuyến đã chết (kể cả khi
    // worker vẫn quay retry): fail nhanh để user tải lại sau thay vì bò vô hạn.
    // Chỉ tính đứng yên tuyệt đối — rỉ chậm vẫn là tiến triển, do rotation xử lý.
    public static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromMinutes(10);
    private const int MaxSegmentAttempts = 32;
    private const int MaxProbeAttempts = 6;
    private const int ManifestVersion = 2;
    private const double LowSpeedThresholdBytesPerSecond = 512 * 1024;
    private const int MaxLowSpeedReroutes = 3;
    private const int MinSegmentsForLowSpeedReroute = 8;
    private static readonly TimeSpan LowSpeedWindow = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan LowSpeedRerouteCooldown = TimeSpan.FromMinutes(5);
    private readonly HttpClient _http;

    public RangeDownloader(HttpClient http) => _http = http;

    public async Task<string> DownloadAsync(
        ResolvedStream original,
        string workDirectory,
        string baseName,
        int concurrency,
        Func<long, CancellationToken, Task<ResolvedStream>>? refresh,
        Action<RangeDownloadStatus>? status,
        CancellationToken cancellationToken,
        TimeSpan? stallTimeout = null,
        Func<long, RefreshCause, CancellationToken, Task<ResolvedStream>>? refreshWithCause = null,
        Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(original.Url)) throw new ArgumentException("Stream URL rỗng.", nameof(original));
        concurrency = Math.Clamp(concurrency, 1, 8);
        var canRefresh = refresh is not null || refreshWithCause is not null;
        async Task<ResolvedStream> RefreshStreamAsync(long seen, RefreshCause cause, CancellationToken token)
        {
            if (refreshWithCause is not null) return await refreshWithCause(seen, cause, token);
            if (refresh is not null) return await refresh(seen, token);
            throw new InvalidOperationException("Không có callback làm mới CDN.");
        }
        Directory.CreateDirectory(workDirectory);

        // A signed Bilibili CDN endpoint can transiently answer 403/429/5xx even though the
        // stream itself supports Range. Do not classify one transient probe as permanent
        // no-Range support and jump to yt-dlp. Refresh the signed stream and probe again.
        var current = original;
        long total = 0;
        var rangeSupported = false;
        var probeSucceeded = false;
        Exception? probeError = null;
        for (var attempt = 1; attempt <= MaxProbeAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attempt == 1 && canRefresh && CdnRefreshPolicy.ShouldRefresh(current.SignedAtUtc, DateTimeOffset.UtcNow))
            {
                try
                {
                    current = await RefreshStreamAsync(current.Generation, RefreshCause.SignatureExpired, cancellationToken);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                {
                    log?.Invoke($"CDN {current.Kind}: proactive refresh lỗi: {error.GetBaseException().Message}");
                }
            }
            try
            {
                (total, rangeSupported) = await ProbeAsync(current, cancellationToken);
                // Field 2026-09-05: CDN báo Content-Range total nhỏ hơn metadata
                // playurl (audio 74,6 MB khai còn 43,9 MB). Mọi check segment/
                // assemble phía dưới đều dùng total này nên file thiếu vẫn "xong"
                // và ra file mất tiếng đuôi. Không bao giờ tin total ảo.
                if (original.Size > 0 && !ProbeTotalMatchesMetadata(total, original.Size))
                {
                    probeError = new InvalidDataException(
                        $"CDN báo tổng {total} byte nhưng metadata là {original.Size} byte cho {original.Kind}; " +
                        "làm mới tuyến và probe lại, không ghép file thiếu âm thầm.");
                    if (canRefresh && attempt < MaxProbeAttempts)
                    {
                        current = await RefreshStreamAsync(current.Generation, RefreshCause.TransportFailure, cancellationToken);
                        await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(3_000, 250 * attempt * attempt)), cancellationToken);
                        continue;
                    }
                    break;
                }
                probeSucceeded = true;
                probeError = null;
                break;
            }
            catch (OperationCanceledException) { throw; }
            catch (HttpRequestException error)
            {
                probeError = error;
                if (canRefresh)
                {
                    var cause = CdnRefreshPolicy.IsSignatureExpiry(error)
                        ? RefreshCause.SignatureExpired
                        : RefreshCause.TransportFailure;
                    current = await RefreshStreamAsync(current.Generation, cause, cancellationToken);
                }
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(3_000, 250 * attempt * attempt)), cancellationToken);
            }
        }
        if (!probeSucceeded)
        {
            if (probeError is InvalidDataException invalid) throw invalid;
            throw new IOException($"Probe HTTP Range thất bại sau {MaxProbeAttempts} lượt làm mới CDN.", probeError);
        }

        status?.Invoke(new RangeDownloadStatus(rangeSupported, 0, concurrency, 0, total, 0));
        if (!rangeSupported)
        {
            throw new RangeNotSupportedException("CDN không hỗ trợ HTTP Range hợp lệ.");
        }

        // Startup CDN race (P1): backup endpoints were previously discovered one
        // failure at a time, so a sick primary cost a full error cycle before the
        // downloader even tried the next route. Sample every known endpoint in
        // parallel and start on the fastest. Fail-open: any probe failure keeps
        // the pre-race order and the existing probe/resume machinery below.
        current = await RaceEndpointsAsync(original, current, cancellationToken);

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
        var consecutiveShortBody = 0;
        var stopwatch = Stopwatch.StartNew();
        var active = 0;
        var inFlight = new ConcurrentDictionary<int, long>();
        var queue = new ConcurrentQueue<Segment>(segments.Where(x => !completed.ContainsKey(x.Index)));
        var lastRefreshUtc = DateTime.MinValue;
        // Stall watchdog: high-water chỉ tiến (thụt lùi sau failure không tính
        // là tiến triển). Rỉ chậm vẫn gia hạn — chỉ đứng yên tuyệt đối mới nổ.
        var progressGate = new object();
        var highWaterBytes = committedBytes;
        var lastAdvanceUtc = DateTime.UtcNow;
        var stallLimit = stallTimeout ?? DefaultStallTimeout;
        if (stallLimit <= TimeSpan.Zero) stallLimit = DefaultStallTimeout;
        void NoteProgress(long bytesNow)
        {
            lock (progressGate)
            {
                if (bytesNow > highWaterBytes)
                {
                    highWaterBytes = bytesNow;
                    lastAdvanceUtc = DateTime.UtcNow;
                }
            }
        }
        var stallFired = 0;
        using var manifestGate = new SemaphoreSlim(1, 1);
        using var currentGate = new SemaphoreSlim(1, 1);
        using var workerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var workerToken = workerCancellation.Token;
        Exception? fatalTransportError = null;
        using var stallMonitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var stallCheckInterval = TimeSpan.FromMilliseconds(Math.Clamp(stallLimit.TotalMilliseconds / 4, 50, 15_000));
        var stallMonitor = Task.Run(async () =>
        {
            try
            {
                while (!stallMonitorCancellation.Token.IsCancellationRequested)
                {
                    await Task.Delay(stallCheckInterval, stallMonitorCancellation.Token);
                    DateTime last;
                    lock (progressGate) { last = lastAdvanceUtc; }
                    if (DateTime.UtcNow - last > stallLimit
                        && Interlocked.CompareExchange(ref stallFired, 1, 0) == 0)
                    {
                        workerCancellation.Cancel();
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);
        async Task StopStallMonitorAsync()
        {
            stallMonitorCancellation.Cancel();
            try { await stallMonitor; }
            catch (OperationCanceledException) { }
        }

        // A route chosen by the 256 KiB startup race can still collapse to a
        // trickle for hours without ever failing (field job 2026-10-22: 0.88 MiB/s
        // average on one endpoint while another route served 8 MiB/s). Errors
        // rotate endpoints, but a silently slow route never does; this watchdog
        // converts sustained low throughput into the same bounded reroute path.
        using var lowSpeedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var lowSpeedMonitor = Task.Run(async () =>
        {
            if (!canRefresh || (original.EndpointUrls?.Count ?? 0) < 2) return;
            var samples = new Queue<(DateTime At, long Bytes)>();
            var sampleGate = new object();
            var lowSince = DateTime.MinValue;
            var lastRerouteUtc = DateTime.MinValue;
            var reroutes = 0;
            try
            {
                while (!lowSpeedCancellation.Token.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), lowSpeedCancellation.Token);
                    if (stopwatch.Elapsed < TimeSpan.FromMinutes(2)) continue;
                    var now = DateTime.UtcNow;
                    var bytes = Volatile.Read(ref networkBytes);
                    var reroute = false;
                    lock (sampleGate)
                    {
                        samples.Enqueue((now, bytes));
                        while (samples.Count > 0 && now - samples.Peek().At > LowSpeedWindow) samples.Dequeue();
                        if (samples.Count < 3) continue;
                        var first = samples.Peek();
                        var elapsed = (now - first.At).TotalSeconds;
                        if (elapsed < 45) continue;
                        var speed = (bytes - first.Bytes) / elapsed;
                        if (speed >= LowSpeedThresholdBytesPerSecond)
                        {
                            lowSince = DateTime.MinValue;
                            continue;
                        }
                        if (lowSince == DateTime.MinValue) lowSince = now;
                        if (now - lowSince < TimeSpan.FromSeconds(30)) continue;
                        if (now - lastRerouteUtc < LowSpeedRerouteCooldown) continue;
                        if (reroutes >= MaxLowSpeedReroutes) continue;
                        if (queue.Count < MinSegmentsForLowSpeedReroute) continue;
                        lastRerouteUtc = now;
                        lowSince = DateTime.MinValue;
                        reroutes++;
                        reroute = true;
                    }
                    if (!reroute) continue;
                    log?.Invoke($"CDN {original.Kind}: tốc độ thấp dưới {LowSpeedThresholdBytesPerSecond / (1024 * 1024):0.#} MB/s trong {LowSpeedWindow.TotalSeconds:0} giây; thử đổi endpoint ({reroutes}/{MaxLowSpeedReroutes}).");
                    ResolvedStream snapshot;
                    await currentGate.WaitAsync(lowSpeedCancellation.Token);
                    try
                    {
                        snapshot = current;
                        if (ShouldRefreshNow(lastRefreshUtc, DateTime.UtcNow)) lastRefreshUtc = DateTime.UtcNow;
                    }
                    finally { currentGate.Release(); }
                    try
                    {
                        var fresh = await RefreshStreamAsync(snapshot.Generation, RefreshCause.LowThroughput, lowSpeedCancellation.Token);
                        await currentGate.WaitAsync(lowSpeedCancellation.Token);
                        try
                        {
                            if (current.Generation == snapshot.Generation) current = fresh;
                        }
                        finally { currentGate.Release(); }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error)
                    {
                        log?.Invoke($"CDN {original.Kind}: đổi endpoint do tốc độ thấp lỗi: {error.GetBaseException().Message}");
                    }
                }
            }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);
        async Task StopLowSpeedMonitorAsync()
        {
            lowSpeedCancellation.Cancel();
            try { await lowSpeedMonitor; }
            catch (OperationCanceledException) { }
        }

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
                        var proactiveRefresh = false;
                        await currentGate.WaitAsync(workerToken);
                        try
                        {
                            stream = current;
                            if (canRefresh
                                && CdnRefreshPolicy.ShouldRefresh(stream.SignedAtUtc, DateTimeOffset.UtcNow)
                                && ShouldRefreshNow(lastRefreshUtc, DateTime.UtcNow))
                            {
                                lastRefreshUtc = DateTime.UtcNow;
                                proactiveRefresh = true;
                            }
                        }
                        finally { currentGate.Release(); }
                        if (proactiveRefresh)
                        {
                            try
                            {
                                var fresh = await RefreshStreamAsync(stream.Generation, RefreshCause.SignatureExpired, workerToken);
                                await currentGate.WaitAsync(workerToken);
                                try
                                {
                                    if (current.Generation == stream.Generation) current = fresh;
                                    stream = current;
                                }
                                finally { currentGate.Release(); }
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception error)
                            {
                                log?.Invoke($"CDN {original.Kind}: proactive refresh lỗi: {error.GetBaseException().Message}");
                            }
                        }

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
                                    NoteProgress(bytesNow);
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
                                Interlocked.Exchange(ref consecutiveShortBody, 0);
                                await SaveManifestAsync(manifestPath, total, chunkSize, completed.Keys, workerToken);
                                NoteProgress(Volatile.Read(ref committedBytes));
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
                            // continue from the missing byte. Explicit HTTP transport failures (403/429/5xx,
                            // including the real HTTP 503 incident) refresh the signed stream immediately.
                            var remainingBefore = Math.Max(1, segment.Length - partialBefore);
                            var meaningfulProgress = Math.Min(64L * 1024, remainingBefore);
                            if (gained >= meaningfulProgress)
                            {
                                weakProgressFailures = 0;
                            }
                            else
                            {
                                weakProgressFailures++;
                            }

                            var signatureExpired = CdnRefreshPolicy.IsSignatureExpiry(error);
                            var immediateRefresh = error is HttpRequestException;
                            var shortBody = error is EndOfStreamException;
                            if (shortBody) Interlocked.Increment(ref consecutiveShortBody);
                            // A sick endpoint can dribble meaningful partial progress on
                            // every attempt (so the weak-progress path below never fires
                            // — it only counts zero-gain failures) while a healthy route
                            // idles. After several consecutive short bodies, force the
                            // same generation-guarded rotation instead of waiting it out.
                            var forceRotation = shortBody
                                && Volatile.Read(ref consecutiveShortBody) >= ConsecutiveShortBodyRotationThreshold;
                            // Refresh chạy NGOÀI currentGate: giữ gate suốt yt-dlp
                            // refetch làm mọi worker xếp hàng (kẹt 1/8). Check +
                            // stamp cooldown trong gate; publish chỉ khi generation
                            // còn nguyên (worker khác thắng thì bản này bỏ).
                            var wantsRefresh = canRefresh
                                && (immediateRefresh || forceRotation || (weakProgressFailures > 0 && weakProgressFailures % 2 == 0));
                            var refreshDue = false;
                            await currentGate.WaitAsync(workerToken);
                            try
                            {
                                if (wantsRefresh && current.Generation == stream.Generation
                                    && ShouldRefreshNow(lastRefreshUtc, DateTime.UtcNow))
                                {
                                    lastRefreshUtc = DateTime.UtcNow;
                                    refreshDue = true;
                                }
                            }
                            finally { currentGate.Release(); }
                            if (refreshDue && canRefresh)
                            {
                                var cause = signatureExpired ? RefreshCause.SignatureExpired : RefreshCause.TransportFailure;
                                var fresh = await RefreshStreamAsync(stream.Generation, cause, workerToken);
                                await currentGate.WaitAsync(workerToken);
                                try
                                {
                                    if (current.Generation == stream.Generation)
                                    {
                                        current = fresh;
                                        Interlocked.Exchange(ref consecutiveShortBody, 0);
                                    }
                                }
                                finally { currentGate.Release(); }
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
            catch (OperationCanceledException) when (Volatile.Read(ref stallFired) == 1 && !cancellationToken.IsCancellationRequested)
            {
                // Stall watchdog nổ trước user cancel: tuyến chết hẳn dù worker
                // vẫn quay retry. Fail nhanh với câu rõ ràng; resume giữ nguyên.
                throw new DownloadStalledException(
                    $"Tải đứng yên quá {(int)stallLimit.TotalMinutes} phút (không thêm byte nào) dù đã xoay CDN; " +
                    "Bilibili/CDN có thể đang chặn hoặc nghẽn — hãy đăng nhập lại Bilibili rồi tải lại sau " +
                    "(phần đã tải được giữ lại để resume).");
            }
            catch (Exception) when (fatalTransportError is not null)
            {
                throw fatalTransportError!;
            }
            await StopStallMonitorAsync();
            await StopLowSpeedMonitorAsync();
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
            await StopStallMonitorAsync();
            await StopLowSpeedMonitorAsync();
            DeleteFiles(segmentDirectory, "*.tmp");
            TryDelete(output + ".assembling.tmp");
            throw;
        }
    }

    // Total từ Content-Range phải khớp metadata playurl. Lệch thiếu là file
    // thiếu byte chắc chắn (trường hợp audio 59% gây câm đuôi); lệch thừa quá
    // lớn là nhầm object sau refresh. Metadata approximate nên có biên độ.
    private static bool ProbeTotalMatchesMetadata(long total, long metadataSize)
    {
        if (metadataSize <= 0 || total <= 0) return true;
        var ratio = total / (double)metadataSize;
        return ratio >= 0.995 && ratio <= 1.05;
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
        throw new HttpRequestException($"Probe CDN HTTP {(int)response.StatusCode}.", null, response.StatusCode);
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
                // HTTP 200/other successful response means the CDN actually ignored the Range header.
                // 403/408/429/5xx are transport/CDN conditions: keep resume state and let the caller
                // refresh the signed endpoint instead of declaring Range unsupported immediately.
                if (response.IsSuccessStatusCode)
                {
                    throw new RangeNotSupportedException($"CDN bỏ qua HTTP Range: trả {(int)response.StatusCode}, cần 206.");
                }
                throw new HttpRequestException($"CDN tạm thời trả HTTP {(int)response.StatusCode}, cần 206.", null, response.StatusCode);
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

    private const long RaceSampleBytes = 64L * 1024;
    private const int RaceTimeoutSeconds = 8;
    // Consecutive short bodies (EndOfStreamException) at which the endpoint is
    // declared sick and rotated immediately. Normal transient blips (1-3) keep
    // the old same-URL resume path untouched; only a sustained dribble forces
    // an early route change instead of burning all 32 segment attempts on it.
    // The counter is shared across workers and reset by any full segment
    // success or any route change. This covers dribbles that still make big
    // partial progress (each attempt gains bytes, so the weak-progress path
    // below never fires); zero-gain failures keep rotating via that path.
    private const int ConsecutiveShortBodyRotationThreshold = 4;
    // Field 2026-09-24: tuyến bệnh ném HttpRequestException liên tục, mỗi lỗi
    // refetch playurl 1 lần (yt-dlp -J chục giây, đang 412/chập chờn) trong khi
    // giữ currentGate → 7/8 worker xếp hàng sau gate, UI kẹt 1/8. Cooldown chặn
    // storm: failure trong cửa sổ chỉ backoff thường, không refetch.
    private static readonly TimeSpan RefreshCooldown = TimeSpan.FromSeconds(45);

    // Pure decision seam for contract tests: first failure always refreshes,
    // failures inside the cooldown window back off quietly instead.
    internal static bool ShouldRefreshNow(DateTime lastRefreshUtc, DateTime now) =>
        (now - lastRefreshUtc) >= RefreshCooldown;

    private async Task<ResolvedStream> RaceEndpointsAsync(
        ResolvedStream original, ResolvedStream current, CancellationToken cancellationToken)
    {
        var endpoints = (original.EndpointUrls ?? Array.Empty<string>())
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (endpoints.Length < 2) return current;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(RaceTimeoutSeconds));
        var samples = await Task.WhenAll(
            endpoints.Select(url => SampleEndpointAsync(original, url, timeout.Token, cancellationToken)));
        var winner = samples.Where(sample => sample.BytesPerSecond > 0)
            .OrderByDescending(sample => sample.BytesPerSecond)
            .FirstOrDefault();
        if (winner.Url is null || string.Equals(winner.Url, current.Url, StringComparison.Ordinal)) return current;
        return current with { Url = winner.Url, EndpointIndex = Array.IndexOf(endpoints, winner.Url) };
    }

    private async Task<(string? Url, double BytesPerSecond)> SampleEndpointAsync(
        ResolvedStream template, string url, CancellationToken attemptToken, CancellationToken parentToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var request = CreateRequest(template with { Url = url }, new RangeHeaderValue(0, RaceSampleBytes - 1));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptToken);
            if (response.StatusCode != HttpStatusCode.PartialContent) return (null, 0);
            var bytes = await response.Content.ReadAsByteArrayAsync(attemptToken);
            if (bytes.Length == 0) return (null, 0);
            return (url, bytes.Length / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds));
        }
        catch (OperationCanceledException) when (parentToken.IsCancellationRequested) { throw; }
        catch (Exception) { return (null, 0); }
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
