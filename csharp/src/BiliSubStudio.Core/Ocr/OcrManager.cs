using System.Threading.Channels;
using BiliSubStudio.Core.Configuration;
using BiliSubStudio.Core.Hardware;

namespace BiliSubStudio.Core.Ocr;

public sealed class OcrManager : IAsyncDisposable
{
    private readonly AppPaths _paths;
    private readonly HardwareService _hardware;
    private readonly OcrInstaller _installer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _probeGate = new(2, 2);
    private readonly object _lifecycleSync = new();
    private readonly List<OcrWorkerClient> _workers = [];
    private CancellationTokenSource _preparationCancellation = new();
    private Channel<OcrWorkerClient>? _available;
    private OcrInferenceQueue? _inference;
    private readonly List<Task> _retiredInference = [];
    private string _deviceMode = "auto";
    private string _activeMode = string.Empty;
    // Model tier currently running in the pool: Accurate = Medium, Balanced/Fast =
    // Mobile (28/09; previously Small). Defaults to Medium (old behaviour); changing tier
    // forces a rebuild. Mobile is lighter than Small so every small-tier rule (grow cap 4,
    // OOM backstop) applies unchanged.
    private string _activeDetModel = OcrInstaller.DetectionModel;
    private string _activeRecModel = OcrInstaller.RecognitionModel;
    // OOM backstop for small-tier growth (26/09): 2 unplanned deaths in a row
    // inside a grown pool pins it, and the next scan grows no more. Never
    // shrink mid-scan (in-flight requests hold worker references; disposing
    // halfway would strand them) — the pin applies on the next start, while this scan
    // continues via the existing 1:1 replacement.
    private int _smallTierDeaths;
    private bool _smallGrowSuppressed;
    private string _lastSmallTierGrowReason = string.Empty;

    private bool IsSmallTierActiveLocked() =>
        (string.Equals(_activeDetModel, OcrInstaller.SmallDetectionModel, StringComparison.Ordinal)
        && string.Equals(_activeRecModel, OcrInstaller.SmallRecognitionModel, StringComparison.Ordinal))
        || (string.Equals(_activeDetModel, OcrInstaller.MobileDetectionModel, StringComparison.Ordinal)
        && string.Equals(_activeRecModel, OcrInstaller.MobileRecognitionModel, StringComparison.Ordinal));

    // Tier-aware worker cap (26/09): Small may go up to 4 (light model), other tiers
    // keep the old WorkerTarget rule. All clamps (configure/resume/grow) pass through
    // here so resume does not cut a pool of 4 down to 2 and a cold build does not exceed the cap.
    private int EffectiveWorkerCapLocked(string mode, int requested) =>
        IsSmallTierActiveLocked() && !string.Equals(mode, "cpu", StringComparison.OrdinalIgnoreCase)
            ? Math.Min(OcrTopologyBenchmark.SmallTierMaxWorkers, Math.Max(1, requested))
            : OcrTopologyBenchmark.WorkerTarget(mode, requested);

    // Snapshot for scanner validation (holds the gate itself; the caller does not).
    internal async Task<int> EffectiveWorkerTargetAsync(string mode, int lanes, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return EffectiveWorkerCapLocked(mode, lanes); }
        finally { _gate.Release(); }
    }
    private string _state = "stopped";
    private string? _error;
    private int _committedWorkerTarget = 1;
    // Diagnostic counters only (Interlocked): never gate scheduling decisions.
    private long _ocrBatchCalls;
    private long _ocrBatchImages;
    private long _probeCalls;
    private long _probeImages;
    private long _workerReplacements;
    private long _plannedRecycles;
    // Proactive recycling against performance degradation over time: a worker is fungible (cache is only for perf,
    // errors are self-destructing) so retire it after this many images, staggered, at a genuinely safe point.
    // ~25k images ≈ 2h at 3.5 images/s/worker. Age is per worker incarnation (a freshly spawned worker starts at 0).
    private const long RecycleAfterImages = 25000;

    internal OcrManager(AppPaths paths, HardwareService hardware, OcrInstaller installer)
    {
        _paths = paths;
        _hardware = hardware;
        _installer = installer;
    }

    public OcrStatus Status => new(
        _state == "ready" && _workers.Count > 0 && _workers.All(x => x.IsAlive),
        _state,
        _deviceMode,
        _activeMode,
        _workers.Count,
        string.Join(" + ", _workers.GroupBy(x => x.Kind, StringComparer.OrdinalIgnoreCase)
            .Select(group => $"{group.Count()} {group.Key.ToUpperInvariant()}")),
        "PaddleOCR",
        OcrInstaller.DetectionModel + " + " + OcrInstaller.RecognitionModel,
        _error,
        CountLiveWorkers());

    // Snapshot best-effort cho quyet dinh retry: pool dung duoc neu kenh con song
    // va con >=1 worker song. Rebuild giua scan GIET viec dang bay cua moi lane
    // (retire queue cancel hang loat -> rebuild storm 10:40 + 19:00 13/9), nen chi
    // rebuild khi that su khong dung duoc; meo 1 slot thi request-recovery tu va.
    internal bool IsPoolUsableForRetry()
    {
        try
        {
            if (_available is null) return false;
            foreach (var worker in _workers)
            {
                try { if (worker.IsAlive) return true; }
                catch { }
            }
            return false;
        }
        catch { return false; }
    }

    private int CountLiveWorkers()
    {
        try
        {
            var live = 0;
            foreach (var worker in _workers)
            {
                try { if (worker.IsAlive) live++; }
                catch { }
            }
            return live;
        }
        catch { return 0; }
    }

    public async Task ConfigureDeviceAsync(string mode, CancellationToken cancellationToken)
    {
        mode = NormalizeDevice(mode);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_deviceMode == mode) return;
            await StopWorkersLockedAsync();
            _deviceMode = mode;
            _activeMode = string.Empty;
            _committedWorkerTarget = MinimumWorkerTarget(mode);
            _state = "stopped";
            _error = null;
        }
        finally { _gate.Release(); }
    }

    public async Task EnsureAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource linked;
        lock (_lifecycleSync)
        {
            if (_preparationCancellation.IsCancellationRequested)
            {
                _preparationCancellation.Dispose();
                _preparationCancellation = new CancellationTokenSource();
            }
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _preparationCancellation.Token);
        }
        using (linked)
        {
            var operationToken = linked.Token;
            await _gate.WaitAsync(operationToken);
            try
            {
                if (Status.Ready) return;
                _state = "starting";
                _error = null;
                var hardware = _hardware.Snapshot();
                try
                {
                    var mode = _deviceMode;
                    if (mode == "auto") mode = hardware.NvidiaDetected ? "gpu" : "cpu";
                    var target = Math.Max(_committedWorkerTarget, MinimumWorkerTarget(mode));
                    await BuildPoolLockedAsync(mode, target, hardware, operationToken);
                }
                catch (Exception gpuError) when (_deviceMode == "auto" && gpuError is not OperationCanceledException)
                {
                    await StopWorkersLockedAsync();
                    _error = "GPU không khởi tạo được, đã chuyển CPU: " + gpuError.Message;
                    await BuildPoolLockedAsync("cpu", _committedWorkerTarget, hardware, operationToken);
                }
                catch (Exception hybridError) when (_deviceMode == "hybrid" && hybridError is not OperationCanceledException)
                {
                    await StopWorkersLockedAsync();
                    _error = "Hybrid không khởi tạo đủ GPU + CPU, đã chuyển sang GPU: " + hybridError.Message;
                    _committedWorkerTarget = 1;
                    await BuildPoolLockedAsync("gpu", 1, hardware, operationToken);
                }
                _state = "ready";
            }
            catch (OperationCanceledException)
            {
                _state = "stopped";
                _error = null;
                throw;
            }
            catch (Exception error)
            {
                _state = "failed";
                _error = error.Message;
                throw;
            }
            finally { _gate.Release(); }
        }
    }

    public async Task<int> ConfigureWorkerPoolAsync(int target, CancellationToken cancellationToken)
    {
        target = Math.Clamp(target, 1, 16);
        await EnsureAsync(cancellationToken);
        using var lifetime = LinkPreparationCancellation(cancellationToken);
        cancellationToken = lifetime.Token;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            target = EffectiveWorkerCapLocked(_activeMode, target);
            var minimum = MinimumWorkerTarget(_activeMode);
            if (target < minimum)
                throw new ArgumentOutOfRangeException(nameof(target),
                    $"OCR {_activeMode} cần ít nhất {minimum} worker.");
            if (_workers.Count == target && _workers.All(x => x.IsAlive)
                && _workers.All(x => string.Equals(x.DetModel, _activeDetModel, StringComparison.Ordinal)
                    && string.Equals(x.RecognitionModel, _activeRecModel, StringComparison.Ordinal)))
            {
                _committedWorkerTarget = target;
                return target;
            }
            var hardware = _hardware.Snapshot();
            _state = "starting";
            try
            {
                await ResizePoolLockedAsync(_activeMode, target, hardware, cancellationToken);
                _state = "ready";
                _error = null;
                _committedWorkerTarget = target;
                return _workers.Count;
            }
            catch (OperationCanceledException)
            {
                var retained = _available is not null && _workers.Count > 0 && _workers.All(x => x.IsAlive);
                _state = retained ? "ready" : "stopped";
                _error = null;
                throw;
            }
            catch (Exception error)
            {
                var retained = _available is not null && _workers.Count > 0 && _workers.All(x => x.IsAlive);
                _state = retained ? "ready" : "failed";
                _error = retained ? null : error.Message;
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    // Model tier per scan mode (2026-09-26): Accurate = Medium, Balanced/Fast
    // = Small. Called by the scanner after device ensure, before the worker
    // topology lock. Publishes the desired tier first so a cold device build
    // lands on it directly (no throwaway Medium pool); rebuilds only when the
    // live pool runs another tier.
    public async Task EnsureModelTierAsync(string detModel, string recModel, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detModel);
        ArgumentException.ThrowIfNullOrWhiteSpace(recModel);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _activeDetModel = detModel.Trim();
            _activeRecModel = recModel.Trim();
        }
        finally { _gate.Release(); }
        await EnsureAsync(cancellationToken);
        using var lifetime = LinkPreparationCancellation(cancellationToken);
        cancellationToken = lifetime.Token;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_workers.Count > 0 && _workers.All(x => x.IsAlive)
                && _workers.All(x => string.Equals(x.DetModel, _activeDetModel, StringComparison.Ordinal)
                    && string.Equals(x.RecognitionModel, _activeRecModel, StringComparison.Ordinal)))
                return;
            var hardware = _hardware.Snapshot();
            string python;
            if (_workers.Count > 0)
            {
                python = _workers[0].Runtime.Python;
            }
            else
            {
                var runtime = await _installer.EnsureAsync(_activeMode, hardware, cancellationToken);
                python = runtime.Python;
            }
            await _installer.EnsureTierModelsAsync(_installer.TierModelsRoot(), python, _activeDetModel, _activeRecModel, cancellationToken);
            var target = Math.Max(_workers.Count, MinimumWorkerTarget(_activeMode));
            await BuildPoolLockedAsync(_activeMode, target, hardware, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    // One-shot grow after the topology lock, while there is no traffic yet (the scanner calls it before
    // the lane loop). Only Small tier (light model) + enough VRAM + not pinned by OOM.
    // Returns the current worker count (unchanged if conditions are not met).
    public async Task<int> TryGrowSmallTierAsync(int lanes, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!IsSmallTierActiveLocked())
            {
                _lastSmallTierGrowReason = "model tier không phải small/mobile nên không mở rộng worker.";
                return _workers.Count;
            }
            if (_smallGrowSuppressed)
            {
                _lastSmallTierGrowReason = "pool đã bị ghim OOM ở lượt quét trước nên không mở rộng worker.";
                return _workers.Count;
            }
            if (string.Equals(_activeMode, "cpu", StringComparison.OrdinalIgnoreCase))
            {
                _lastSmallTierGrowReason = "chế độ CPU không mở rộng worker.";
                return _workers.Count;
            }
            var want = OcrTopologyBenchmark.SmallTierWorkerTarget(lanes);
            if (_workers.Count >= want)
            {
                _lastSmallTierGrowReason = string.Empty;
                return _workers.Count;
            }
            var live = _hardware.ResourceSnapshot();
            if (!OcrTopologyBenchmark.ShouldGrowSmallTier(
                live.VramTelemetryAvailable, live.AvailableVramBytes, live.TotalVramBytes,
                live.AvailableMemoryBytes, _workers.Count, lanes))
            {
                _lastSmallTierGrowReason = DescribeSmallTierGrowBlock(live, lanes);
                return _workers.Count;
            }
            var hardware = _hardware.Snapshot();
            var desired = DesiredKinds(_activeMode, want);
            for (var index = _workers.Count; index < desired.Length; index++)
            {
                var runtime = await _installer.EnsureAsync(desired[index], hardware, cancellationToken);
                var worker = new OcrWorkerClient(runtime, _activeDetModel, _activeRecModel);
                try { await worker.StartAsync(cancellationToken); }
                catch { await worker.DisposeAsync(); throw; }
                _workers.Add(worker);
            }
            RebuildAvailabilityLocked();
            _lastSmallTierGrowReason = string.Empty;
            return _workers.Count;
        }
        finally { _gate.Release(); }
    }

    // Reason small-tier growth is skipped (logged for the user): small card, RAM
    // squeezed, or NVDEC lanes already ate the remaining VRAM.
    internal static string DescribeSmallTierGrowBlock(HardwareResourceSnapshot live, int lanes)
    {
        if (!live.VramTelemetryAvailable)
            return "không đọc được VRAM trống nên không mở rộng worker.";
        if (live.TotalVramBytes < OcrTopologyBenchmark.SmallTierGrowMinTotalVramBytes)
            return $"card chỉ có {live.TotalVramBytes / (1024.0 * 1024 * 1024):0.0} GiB VRAM "
                + $"(cần >= {OcrTopologyBenchmark.SmallTierGrowMinTotalVramBytes / (1024.0 * 1024 * 1024):0.0} GiB để chứa cả lane NVDEC lẫn worker) nên giữ pool 2 worker ổn định.";
        if (live.AvailableVramBytes < OcrTopologyBenchmark.SmallTierGrowReserveBytes)
            return $"VRAM trống {live.AvailableVramBytes / (1024.0 * 1024 * 1024):0.0} GiB dưới ngưỡng "
                + $"{OcrTopologyBenchmark.SmallTierGrowReserveBytes / (1024.0 * 1024 * 1024):0.0} GiB nên không mở rộng worker.";
        if (live.AvailableMemoryBytes < OcrTopologyBenchmark.SmallTierGrowMinFreeRamBytes)
            return $"RAM trống {live.AvailableMemoryBytes / (1024.0 * 1024 * 1024):0.0} GiB dưới ngưỡng "
                + $"{OcrTopologyBenchmark.SmallTierGrowMinFreeRamBytes / (1024.0 * 1024 * 1024):0.0} GiB nên không mở rộng worker.";
        if (lanes > OcrTopologyBenchmark.SmallTierMaxGrowLanes)
            return $"{lanes} lane NVDEC đã ăn quá nhiều VRAM nên không mở rộng worker.";
        return "không đủ điều kiện mở rộng worker.";
    }

    internal string SmallTierGrowReason => _lastSmallTierGrowReason;

    // OOM backstop: the 2nd unplanned death inside a grown pool pins it —
    // the next scan grows no more. Only sets the flag (does not mutate the pool here).
    // Called under _gate, after a successful replace.
    private void NoteSmallTierDeathLocked()
    {
        if (!IsSmallTierActiveLocked()
            || _workers.Count <= OcrTopologyBenchmark.WorkerTarget(_activeMode, _workers.Count))
            return;
        _smallTierDeaths++;
        if (_smallTierDeaths >= 2)
            _smallGrowSuppressed = true;
    }

    public async Task<OcrResult> RunAsync(string imageBase64, CancellationToken cancellationToken, bool recoverShortBlank = false, string? activeShortText = null, Action<OcrPerfSample>? onPerf = null)
    {
        Interlocked.Increment(ref _ocrBatchCalls);
        Interlocked.Increment(ref _ocrBatchImages);
        return await RunWithRecoveryAsync(
            (worker, token) => worker.RunAsync(imageBase64, token, recoverShortBlank, activeShortText, onPerf),
            cancellationToken);
    }

    public async Task<IReadOnlyList<OcrResult>> RunBatchAsync(
        IReadOnlyList<string> imageBase64,
        CancellationToken cancellationToken,
        bool recoverShortBlank = false,
        string? activeShortText = null,
        Action<OcrPerfSample>? onPerf = null)
    {
        if (imageBase64.Count is < 1 or > 8 || imageBase64.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Batch OCR phải có từ 1 đến 8 ảnh hợp lệ.", nameof(imageBase64));
        return await RunBatchInternalAsync(imageBase64, cancellationToken, recoverShortBlank, activeShortText, onPerf);
    }

    // Internal batch path for OcrBatchScope (batch scavenging): validation in the worker client
    // stays intact, here we only count + recover as on the public path.
    internal Task<IReadOnlyList<OcrResult>> RunBatchInternalAsync(
        IReadOnlyList<string> imageBase64,
        CancellationToken cancellationToken,
        bool recoverShortBlank = false,
        string? activeShortText = null,
        Action<OcrPerfSample>? onPerf = null)
    {
        Interlocked.Increment(ref _ocrBatchCalls);
        Interlocked.Add(ref _ocrBatchImages, imageBase64.Count);
        return RunWithRecoveryAsync(
            (worker, token) => worker.RunBatchAsync(imageBase64, token, recoverShortBlank, activeShortText, onPerf),
            cancellationToken);
    }

    // Per-scan batch scavenging scope: coalesce pending single requests into a worker batch.
    // = 4 (parity 15/9 with the real running worker: text/box identical 8/8, 1.53x faster;
    // the worker rejects batches >4 so the cap follows the worker, not the protocol).
    // Contract test ghim == 4.
    public static int BatchCoalesceCap = 4;

    public OcrBatchScope CreateBatchScope(CancellationToken scanToken) => new(this, scanToken, () => _workers.Count);

    public sealed class OcrBatchScope : IAsyncDisposable
    {
        private readonly OcrManager _manager;
        private readonly OcrRequestBatcher? _batcher;

        internal OcrBatchScope(OcrManager manager, CancellationToken scanToken, Func<int>? workerCountProvider)
        {
            _manager = manager;
            _ = scanToken;
            // Cap 1: do not build a batcher, SubmitAsync takes the old per-image path.
            // workerCountProvider tells the batcher how wide the pool is so it can hold
            // and coalesce mates at the right moment (only hold when busy, run immediately when idle). Read
            // live on every check (the pool may rebuild) — being slightly off is harmless
            // because this is only a batch-coalescing heuristic, not a correctness gate.
            if (BatchCoalesceCap > 1)
                _batcher = new OcrRequestBatcher(manager.DispatchBatchAsync, BatchCoalesceCap, workerCountProvider);
        }

        public Task<OcrResult> SubmitAsync(
            string imageBase64,
            CancellationToken cancellationToken,
            bool recoverShortBlank = false,
            string? activeShortText = null,
            Action<OcrPerfSample>? onPerf = null)
        {
            var batcher = _batcher;
            return batcher is null
                ? _manager.RunAsync(imageBase64, cancellationToken, recoverShortBlank, activeShortText, onPerf)
                : batcher.SubmitAsync(imageBase64, cancellationToken, recoverShortBlank, activeShortText, onPerf);
        }

        public ValueTask DisposeAsync() => _batcher?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    // The style probe goes through a direct worker lease (once at scan start,
    // not via the batcher). An old/erroring worker -> throws -> the scanner fails open to "off".
    public Task<IReadOnlyList<OcrSubtitleStyle.FrameScore>> ProbeSubtitleStyleAsync(
        IReadOnlyList<string> imageBase64,
        CancellationToken cancellationToken)
        => RunWithRecoveryAsync(
            (worker, token) => worker.ProbeSubtitleStyleAsync(imageBase64, token),
            cancellationToken);

    private static readonly OcrPerfSample ZeroPerf = new(0, 0, 0, 0, 0, 0);

    // Production dispatch for the batcher: 1 batch = 1 worker call (recovery/retry stay
    // intact via ExecuteWithRecoveryAsync). The token is a scope-stop (only fires on dispose):
    // pause cancels member TCS entries immediately (the lane exits fast), the GPU finishes the orphan batch within
    // the timeout limit then discards the result — do not kill the worker (avoids a replacement storm).
    // Perf fan-out keeps the total EXACT: the first member absorbs the whole batch, the rest sample 0.
    private async Task DispatchBatchAsync(
        IReadOnlyList<OcrRequestBatcher.Pending> batch,
        CancellationToken scopeToken)
    {
        var live = new List<OcrRequestBatcher.Pending>(batch.Count);
        foreach (var pending in batch)
        {
            if (!pending.Task.IsCompleted)
                live.Add(pending);
        }
        if (live.Count == 0) return;
        if (live.Count > OcrRequestBatcher.WorkerBatchLimit)
        {
            var overflow = new ArgumentException($"Batcher vượt giới hạn worker {OcrRequestBatcher.WorkerBatchLimit}.");
            foreach (var pending in live)
                pending.TrySetException(overflow);
            return;
        }
        var images = live.Select(pending => pending.ImageBase64).ToList();
        var recover = live[0].RecoverShortBlank;
        var active = live[0].ActiveShortText;
        try
        {
            var results = await RunBatchInternalAsync(
                images,
                scopeToken,
                recover,
                active,
                sample =>
                {
                    try
                    {
                        var first = true;
                        foreach (var pending in live)
                        {
                            if (first)
                            {
                                pending.OnPerf?.Invoke(sample);
                                first = false;
                            }
                            else
                            {
                                pending.OnPerf?.Invoke(ZeroPerf);
                            }
                        }
                    }
                    catch { }
                });
            for (var index = 0; index < live.Count; index++)
                live[index].TrySetResult(results[index]);
        }
        catch (OperationCanceledException) when (scopeToken.IsCancellationRequested)
        {
            foreach (var pending in live)
                pending.TrySetCanceled();
        }
        catch (Exception error)
        {
            foreach (var pending in live)
                pending.TrySetException(error);
        }
    }

    public async Task<IReadOnlyList<double>> ProbeVisualChangesAsync(
        IReadOnlyList<string> imageBase64,
        CancellationToken cancellationToken)
    {
        var probe = await ProbeVisualChangesExAsync(imageBase64, cancellationToken);
        return probe.ChangeScores;
    }

    public async Task<VisualProbeResult> ProbeVisualChangesExAsync(
        IReadOnlyList<string> imageBase64,
        CancellationToken cancellationToken)
    {
        try
        {
            return await RunProbeCoreAsync(imageBase64, cancellationToken);
        }
        catch (Exception error) when (imageBase64.Count > 2 && IsMemoryPressure(error))
        {
            // A 65-image probe decodes every frame at once. On a busy machine that
            // can exhaust the worker even when the pool is healthy; split at the
            // boundary frame and keep every transition score instead of failing
            // the whole lane and making the user resume from checkpoint.
            var split = (imageBase64.Count + 1) / 2;
            var left = imageBase64.Take(split).ToArray();
            var right = imageBase64.Skip(split - 1).ToArray();
            var first = await ProbeVisualChangesExAsync(left, cancellationToken);
            var second = await ProbeVisualChangesExAsync(right, cancellationToken);
            return CombineProbeResults(first, second);
        }
    }

    private async Task<VisualProbeResult> RunProbeCoreAsync(
        IReadOnlyList<string> imageBase64,
        CancellationToken cancellationToken)
    {
        await _probeGate.WaitAsync(cancellationToken);
        try
        {
            Interlocked.Increment(ref _probeCalls);
            Interlocked.Add(ref _probeImages, imageBase64.Count);
            return await RunWithRecoveryAsync(
                (worker, token) => worker.ProbeVisualChangesExAsync(imageBase64, token),
                cancellationToken);
        }
        finally { _probeGate.Release(); }
    }

    internal static VisualProbeResult CombineProbeResults(VisualProbeResult first, VisualProbeResult second) =>
        new(
            first.ChangeScores.Concat(second.ChangeScores).ToArray(),
            CombineOptional(first.BandChangeScores, second.BandChangeScores),
            CombineEdgeDensity(first.EdgeDensity, second.EdgeDensity));

    private static IReadOnlyList<double>? CombineOptional(
        IReadOnlyList<double>? first,
        IReadOnlyList<double>? second) =>
        first is null || second is null ? null : first.Concat(second).ToArray();

    private static IReadOnlyList<double>? CombineEdgeDensity(
        IReadOnlyList<double>? first,
        IReadOnlyList<double>? second) =>
        first is null || second is null || second.Count == 0
            ? null
            : first.Concat(second.Skip(1)).ToArray();

    private static bool IsMemoryPressure(Exception error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is OutOfMemoryException) return true;
            var message = current.Message;
            if (message.Contains("Insufficient memory", StringComparison.OrdinalIgnoreCase)
                || message.Contains("OutOfMemory", StringComparison.OrdinalIgnoreCase)
                || message.Contains("Cannot allocate memory", StringComparison.OrdinalIgnoreCase)
                || message.Contains("bad_alloc", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public OcrInferenceTelemetry GetInferenceTelemetry()
    {
        var queue = _inference?.Snapshot() ?? (0, 0, 0, 0);
        return new OcrInferenceTelemetry(
            Interlocked.Read(ref _ocrBatchCalls),
            Interlocked.Read(ref _ocrBatchImages),
            Interlocked.Read(ref _probeCalls),
            Interlocked.Read(ref _probeImages),
            Interlocked.Read(ref _workerReplacements),
            queue.Executed, queue.PeakDepth, queue.AvgDepth, queue.TotalWaitMs,
            Interlocked.Read(ref _plannedRecycles));
    }

    private async Task<T> RunWithRecoveryAsync<T>(
        Func<OcrWorkerClient, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken);
        using var lifetime = LinkPreparationCancellation(cancellationToken);
        var inference = _inference ?? throw new InvalidOperationException("OCR inference queue is unavailable.");
        var channel = _available ?? throw new InvalidOperationException("OCR worker pool is unavailable.");
        // Stop cancels this lifetime before waiting for _gate, including a
        // replacement's StartAsync while recovery owns that gate.
        return await inference.RunAsync(token => ExecuteWithRecoveryAsync(operation, channel, token), lifetime.Token);
    }

    private async Task<T> ExecuteWithRecoveryAsync<T>(
        Func<OcrWorkerClient, CancellationToken, Task<T>> operation,
        Channel<OcrWorkerClient> channel,
        CancellationToken cancellationToken)
    {
        var worker = await channel.Reader.ReadAsync(cancellationToken);
        Exception? firstFailure = null;
        var recoveryCount = 0;
        try
        {
            while (true)
            {
                try
                {
                    var result = await operation(worker, cancellationToken);
                    worker = await MaybeRecycleAsync(worker, channel, cancellationToken);
                    return result;
                }
                catch (Exception error) when (IsRecoverableWorkerFailure(worker, error, cancellationToken))
                {
                    firstFailure ??= error;
                    recoveryCount++;
                    worker = await ReplaceWorkerAsync(worker, channel, error, cancellationToken);
                    if (recoveryCount <= 1) continue;
                    throw new OcrWorkerUnavailableException(
                        "OCR request vẫn làm worker dừng sau một lần tự khởi động lại. " + error.Message,
                        new AggregateException(firstFailure, error));
                }
            }
        }
        finally
        {
            // Mot lane chet KHONG BAO GIO duoc dong kenh chung: TryComplete o day
            // tung lam tat ca lane khac chet theo (ChannelClosedException day chuyen
            // -> job chet, vu 06:43 13/9). Loi pool that da truyen qua exception;
            // lane recovery + job lo phan con lai.
            // - Cung kenh + song -> tra ve pool (TryWrite that bai chi khi teardown
            //   da chot kenh: luc do teardown tu huy workers, Dispose thua o day vo hai).
            // - Cung kenh + chet -> huy sach se (khong de python mo coi). Replace
            //   da huy worker cu; Dispose goi thua vo hai.
            // - Khac kenh (pool rebuild/teardown giua chung) -> KHONG DUNG VAO:
            //   instance van thuoc pool moi (rebuild restart tai cho + republish),
            //   huy o day se giet worker pool dang dung (vu resize test).
            if (ReferenceEquals(_available, channel))
            {
                if (!worker.IsAlive || !channel.Writer.TryWrite(worker))
                {
                    try { await worker.DisposeAsync(); } catch { }
                }
            }
        }
    }

    private static bool IsRecoverableWorkerFailure(
        OcrWorkerClient worker,
        Exception error,
        CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested
        && (error is OcrWorkerUnavailableException or TimeoutException || !worker.IsAlive);

    // Proactive recycling (against "the longer a scan runs, the slower it gets"): a worker past its age limit retires
    // EVEN WHILE HEALTHY, spawning a new worker to take its place — resetting CUDA fragmentation/accumulated heat/RNG
    // image pool churn. Runs on the existing ReplaceWorkerAsync path (keeps _gate, surgical pool edits).
    // Absolute fail-open: every error (mid-run pool rebuild, cancel, failed startup)
    // returns the old worker for continued service + resets its age (no _gate churn per request);
    // a crippled pool (missing live workers) skips this round (leave it to recovery).
    // Single-worker pools still recycle (~30s pause to load the model every ~2h, capped).
    private async Task<OcrWorkerClient> MaybeRecycleAsync(
        OcrWorkerClient worker,
        Channel<OcrWorkerClient> channel,
        CancellationToken cancellationToken)
    {
        if (worker.ImagesServed < RecycleAfterImages) return worker;
        if (CountLiveWorkers() < _workers.Count) return worker;
        try
        {
            var fresh = await ReplaceWorkerAsync(worker, channel,
                new InvalidOperationException(
                    $"OCR worker tái tạo chủ động sau {worker.ImagesServed} ảnh (chống suy giảm theo giờ)."),
                cancellationToken, planned: true);
            Interlocked.Increment(ref _plannedRecycles);
            return fresh;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            worker.ResetAge();
            return worker;
        }
    }

    private async Task<OcrWorkerClient> ReplaceWorkerAsync(
        OcrWorkerClient failed,
        Channel<OcrWorkerClient> channel,
        Exception cause,
        CancellationToken cancellationToken,
        bool planned = false)
    {
        // Proactive recycling does NOT count toward WorkerReplacements-by-death (counted separately as planned):
        // these two numbers are acceptance evidence for time-invariance (deaths≈0, recycling even).
        if (!planned) Interlocked.Increment(ref _workerReplacements);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!ReferenceEquals(_available, channel))
                throw new OcrWorkerUnavailableException(
                    "OCR worker pool đã được dựng lại bởi lane khác trong lúc tự khôi phục.", cause);
            var index = _workers.IndexOf(failed);
            if (index < 0) throw new InvalidOperationException("OCR worker lỗi không còn thuộc pool hiện tại.", cause);
            _workers.RemoveAt(index);
            try { await failed.DisposeAsync(); } catch { }

            var replacement = new OcrWorkerClient(failed.Runtime, failed.DetModel, failed.RecognitionModel);
            try
            {
                await replacement.StartAsync(cancellationToken);
                _workers.Insert(index, replacement);
                _state = "ready";
                _error = null;
                if (!planned) NoteSmallTierDeathLocked();
                return replacement;
            }
            catch (Exception replacementError)
            {
                try { await replacement.DisposeAsync(); } catch { }
                var error = "Không tự khởi động lại được OCR worker sau lỗi: " + cause.Message;
                channel.Writer.TryComplete(new OcrWorkerUnavailableException(error, replacementError));
                _available = null;
                _state = "failed";
                _error = error;
                throw new OcrWorkerUnavailableException(error, replacementError);
            }
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_lifecycleSync)
        {
            if (!_preparationCancellation.IsCancellationRequested) _preparationCancellation.Cancel();
        }

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!cancellationToken.CanBeCanceled) bounded.CancelAfter(TimeSpan.FromSeconds(90));
        Task[] retired;
        await _gate.WaitAsync(bounded.Token);
        try
        {
            await StopWorkersLockedAsync();
            _state = "stopped";
            _activeMode = string.Empty;
            _activeDetModel = OcrInstaller.DetectionModel;
            _activeRecModel = OcrInstaller.RecognitionModel;
            _smallTierDeaths = 0;
            _smallGrowSuppressed = false;
            _committedWorkerTarget = MinimumWorkerTarget(_deviceMode);
            _error = null;
            retired = _retiredInference.ToArray();
        }
        finally { _gate.Release(); }
        await Task.WhenAll(retired).WaitAsync(bounded.Token);
    }

    public async Task RemoveAsync(CancellationToken cancellationToken)
    {
        lock (_lifecycleSync)
        {
            if (!_preparationCancellation.IsCancellationRequested) _preparationCancellation.Cancel();
        }
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await StopWorkersLockedAsync();
            if (Directory.Exists(_paths.Ocr)) Directory.Delete(_paths.Ocr, recursive: true);
            _installer.RemoveBootstrap();
            _state = "stopped";
            _activeMode = string.Empty;
            _activeDetModel = OcrInstaller.DetectionModel;
            _activeRecModel = OcrInstaller.RecognitionModel;
            _smallTierDeaths = 0;
            _smallGrowSuppressed = false;
            _committedWorkerTarget = MinimumWorkerTarget(_deviceMode);
            _error = null;
        }
        finally { _gate.Release(); }
    }

    private async Task BuildPoolLockedAsync(string mode, int target, HardwareSnapshot hardware, CancellationToken cancellationToken)
    {
        target = OcrTopologyBenchmark.WorkerTarget(mode, target);
        await StopWorkersLockedAsync();
        _smallTierDeaths = 0;
        _smallGrowSuppressed = false;
        var kinds = DesiredKinds(mode, target);
        try
        {
            foreach (var kind in kinds)
            {
                var runtime = await _installer.EnsureAsync(kind, hardware, cancellationToken);
                var worker = new OcrWorkerClient(runtime, _activeDetModel, _activeRecModel);
                try { await worker.StartAsync(cancellationToken); }
                catch { await worker.DisposeAsync(); throw; }
                _workers.Add(worker);
            }
            RebuildAvailabilityLocked();
            _activeMode = mode;
        }
        catch
        {
            await StopWorkersLockedAsync();
            throw;
        }
    }

    private async Task ResizePoolLockedAsync(string mode, int target, HardwareSnapshot hardware, CancellationToken cancellationToken)
    {
        var desired = DesiredKinds(mode, target);
        var shared = Math.Min(_workers.Count, desired.Length);
        var canReuse = string.Equals(_activeMode, mode, StringComparison.OrdinalIgnoreCase)
            && _workers.All(x => x.IsAlive)
            && _workers.All(x => string.Equals(x.DetModel, _activeDetModel, StringComparison.Ordinal)
                && string.Equals(x.RecognitionModel, _activeRecModel, StringComparison.Ordinal))
            && Enumerable.Range(0, shared).All(index =>
                string.Equals(_workers[index].Kind, desired[index], StringComparison.OrdinalIgnoreCase));
        if (!canReuse)
        {
            await BuildPoolLockedAsync(mode, target, hardware, cancellationToken);
            return;
        }

        RetireInferenceLocked();
        _available?.Writer.TryComplete();
        _available = null;
        // Retiring the queue cancels request tokens and can kill workers that
        // passed canReuse above. Stop drains each request gate before restarting
        // its process; never republish those possibly killed processes as idle.
        foreach (var worker in _workers.Take(shared))
        {
            await worker.StopAsync();
            await worker.StartAsync(cancellationToken);
        }
        if (_workers.Count > target)
        {
            for (var index = _workers.Count - 1; index >= target; index--)
            {
                var worker = _workers[index];
                _workers.RemoveAt(index);
                await worker.DisposeAsync();
            }
            RebuildAvailabilityLocked();
            return;
        }

        var retained = _workers.Count;
        try
        {
            for (var index = retained; index < desired.Length; index++)
            {
                var runtime = await _installer.EnsureAsync(desired[index], hardware, cancellationToken);
                var worker = new OcrWorkerClient(runtime, _activeDetModel, _activeRecModel);
                try { await worker.StartAsync(cancellationToken); }
                catch { await worker.DisposeAsync(); throw; }
                _workers.Add(worker);
            }
            RebuildAvailabilityLocked();
        }
        catch
        {
            for (var index = _workers.Count - 1; index >= retained; index--)
            {
                var worker = _workers[index];
                _workers.RemoveAt(index);
                await worker.DisposeAsync();
            }
            RebuildAvailabilityLocked();
            throw;
        }
    }

    private void RebuildAvailabilityLocked()
    {
        RetireInferenceLocked();
        if (_workers.Count == 0)
        {
            _available = null;
            return;
        }
        _available = Channel.CreateBounded<OcrWorkerClient>(new BoundedChannelOptions(_workers.Count)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        foreach (var worker in _workers) _available.Writer.TryWrite(worker);
        _inference = new OcrInferenceQueue(_workers.Count);
    }

    private static string[] DesiredKinds(string mode, int target) => mode switch
    {
        "cpu" => Enumerable.Repeat("cpu", target).ToArray(),
        "gpu" => Enumerable.Repeat("gpu", target).ToArray(),
        "hybrid" when target >= 2 => new[] { "gpu", "cpu" }
            .Concat(Enumerable.Repeat("gpu", target - 2)).ToArray(),
        "hybrid" => throw new ArgumentOutOfRangeException(nameof(target),
            "OCR Hybrid cần ít nhất 2 worker: 1 GPU + 1 CPU."),
        _ => throw new ArgumentException("Chế độ OCR nội bộ không hợp lệ."),
    };

    private static int MinimumWorkerTarget(string mode) => mode == "hybrid" ? 2 : 1;

    private async Task StopWorkersLockedAsync()
    {
        RetireInferenceLocked();
        _available?.Writer.TryComplete();
        _available = null;
        Exception? firstFailure = null;
        foreach (var worker in _workers)
        {
            try { await worker.DisposeAsync(); }
            catch (Exception error) { firstFailure ??= error; }
        }
        _workers.Clear();
        if (firstFailure is not null)
            throw new IOException("Không thu hồi sạch toàn bộ OCR Python worker.", firstFailure);
    }

    private static string NormalizeDevice(string? mode)
    {
        var value = (mode ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Length == 0) return "auto";
        return value is "auto" or "cpu" or "gpu" or "hybrid"
            ? value
            : throw new ArgumentException($"Thiết bị OCR không hợp lệ: {mode}");
    }

    private void RetireInferenceLocked()
    {
        if (_inference is null) return;
        // Recovery may be waiting for _gate. Cancel here, but never await its
        // consumer while holding that same gate.
        _retiredInference.RemoveAll(task => task.IsCompletedSuccessfully);
        _retiredInference.Add(_inference.DisposeAsync().AsTask());
        _inference = null;
    }

    private CancellationTokenSource LinkPreparationCancellation(CancellationToken cancellationToken)
    {
        lock (_lifecycleSync)
            return CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _preparationCancellation.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await Task.WhenAll(_retiredInference);
        lock (_lifecycleSync) _preparationCancellation.Dispose();
        _gate.Dispose();
    }
}
