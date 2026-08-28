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
    private readonly object _lifecycleSync = new();
    private readonly List<OcrWorkerClient> _workers = [];
    private CancellationTokenSource _preparationCancellation = new();
    private Channel<OcrWorkerClient>? _available;
    private string _deviceMode = "auto";
    private string _activeMode = string.Empty;
    private string _state = "stopped";
    private string? _error;

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
        _error);

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
                    await BuildPoolLockedAsync(mode, 1, hardware, operationToken);
                }
                catch (Exception gpuError) when (_deviceMode == "auto" && gpuError is not OperationCanceledException)
                {
                    await StopWorkersLockedAsync();
                    _error = "GPU không khởi tạo được, đã chuyển CPU: " + gpuError.Message;
                    await BuildPoolLockedAsync("cpu", 1, hardware, operationToken);
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
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_workers.Count == target && _workers.All(x => x.IsAlive)) return target;
            var hardware = _hardware.Snapshot();
            _state = "starting";
            try
            {
                await ResizePoolLockedAsync(_activeMode, target, hardware, cancellationToken);
                _state = "ready";
                _error = null;
                return _workers.Count;
            }
            catch (OperationCanceledException)
            {
                var retained = _workers.Count > 0 && _workers.All(x => x.IsAlive);
                _state = retained ? "ready" : "stopped";
                _error = null;
                throw;
            }
            catch (Exception error)
            {
                var retained = _workers.Count > 0 && _workers.All(x => x.IsAlive);
                _state = retained ? "ready" : "failed";
                _error = retained ? null : error.Message;
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<OcrResult> RunAsync(string imageBase64, CancellationToken cancellationToken, bool recoverShortBlank = false, string? activeShortText = null)
    {
        await EnsureAsync(cancellationToken);
        var channel = _available ?? throw new InvalidOperationException("OCR worker pool chưa sẵn sàng.");
        var worker = await channel.Reader.ReadAsync(cancellationToken);
        try
        {
            return await worker.RunAsync(imageBase64, cancellationToken, recoverShortBlank, activeShortText);
        }
        finally
        {
            if (worker.IsAlive) channel.Writer.TryWrite(worker);
            else
            {
                var error = "OCR worker đã dừng; nhấn Chuẩn bị OCR để khởi tạo lại.";
                channel.Writer.TryComplete(new InvalidOperationException(error));
                if (ReferenceEquals(_available, channel))
                {
                    _state = "failed";
                    _error = error;
                }
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_lifecycleSync)
        {
            if (!_preparationCancellation.IsCancellationRequested) _preparationCancellation.Cancel();
        }

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!cancellationToken.CanBeCanceled) bounded.CancelAfter(TimeSpan.FromSeconds(90));
        await _gate.WaitAsync(bounded.Token);
        try
        {
            await StopWorkersLockedAsync();
            _state = "stopped";
            _activeMode = string.Empty;
            _error = null;
        }
        finally { _gate.Release(); }
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
            _error = null;
        }
        finally { _gate.Release(); }
    }

    private async Task BuildPoolLockedAsync(string mode, int target, HardwareSnapshot hardware, CancellationToken cancellationToken)
    {
        await StopWorkersLockedAsync();
        var kinds = DesiredKinds(mode, target);
        try
        {
            foreach (var kind in kinds)
            {
                var runtime = await _installer.EnsureAsync(kind, hardware, cancellationToken);
                var worker = new OcrWorkerClient(runtime);
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
            && Enumerable.Range(0, shared).All(index =>
                string.Equals(_workers[index].Kind, desired[index], StringComparison.OrdinalIgnoreCase));
        if (!canReuse)
        {
            await BuildPoolLockedAsync(mode, target, hardware, cancellationToken);
            return;
        }

        _available?.Writer.TryComplete();
        _available = null;
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
                var worker = new OcrWorkerClient(runtime);
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
    }

    private static string[] DesiredKinds(string mode, int target) => mode switch
    {
        "cpu" => Enumerable.Repeat("cpu", target).ToArray(),
        "gpu" => Enumerable.Repeat("gpu", target).ToArray(),
        "hybrid" => target == 1
            ? ["gpu"]
            : new[] { "gpu", "cpu" }.Concat(Enumerable.Repeat("gpu", target - 2)).ToArray(),
        _ => throw new ArgumentException("Chế độ OCR nội bộ không hợp lệ."),
    };

    private async Task StopWorkersLockedAsync()
    {
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

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        lock (_lifecycleSync) _preparationCancellation.Dispose();
        _gate.Dispose();
    }
}
