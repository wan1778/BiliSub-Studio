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
                    if (mode == "hybrid" && HardwareService.RecommendedOcrLanes(hardware, "hybrid") < 2)
                        throw new InvalidOperationException("Máy hiện tại không đủ headroom để chạy Hybrid OCR an toàn; hãy dùng CPU, GPU hoặc Auto.");
                    await BuildPoolLockedAsync(mode, mode == "hybrid" ? 2 : 1, hardware, operationToken);
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

    public async Task<int> ConfigureScanWorkersAsync(int target, CancellationToken cancellationToken)
    {
        target = Math.Clamp(target, 1, 16);
        await EnsureAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var minimum = _activeMode == "hybrid" ? 2 : 1;
            target = Math.Max(target, minimum);
            if (_workers.Count == target && _workers.All(x => x.IsAlive)) return target;
            var hardware = _hardware.Snapshot();
            await BuildPoolLockedAsync(_activeMode, target, hardware, cancellationToken);
            _state = "ready";
            return _workers.Count;
        }
        finally { _gate.Release(); }
    }

    public async Task<OcrResult> RunAsync(string imageBase64, CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken);
        var channel = _available ?? throw new InvalidOperationException("OCR worker pool chưa sẵn sàng.");
        var worker = await channel.Reader.ReadAsync(cancellationToken);
        try
        {
            return await worker.RunAsync(imageBase64, cancellationToken);
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
            _state = "stopped";
            _activeMode = string.Empty;
            _error = null;
        }
        finally { _gate.Release(); }
    }

    private async Task BuildPoolLockedAsync(string mode, int target, HardwareSnapshot hardware, CancellationToken cancellationToken)
    {
        await StopWorkersLockedAsync();
        var kinds = mode switch
        {
            "cpu" => Enumerable.Repeat("cpu", target).ToArray(),
            "gpu" => Enumerable.Repeat("gpu", target).ToArray(),
            "hybrid" => new[] { "gpu", "cpu" }.Concat(Enumerable.Repeat("gpu", Math.Max(0, target - 2))).ToArray(),
            _ => throw new ArgumentException("Chế độ OCR nội bộ không hợp lệ."),
        };
        try
        {
            foreach (var kind in kinds)
            {
                var runtime = await _installer.EnsureAsync(kind, hardware, cancellationToken);
                var worker = new OcrWorkerClient(runtime);
                await worker.StartAsync(cancellationToken);
                _workers.Add(worker);
            }
            _available = Channel.CreateBounded<OcrWorkerClient>(new BoundedChannelOptions(_workers.Count)
            {
                SingleReader = false,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            });
            foreach (var worker in _workers) _available.Writer.TryWrite(worker);
            _activeMode = mode;
        }
        catch
        {
            await StopWorkersLockedAsync();
            throw;
        }
    }

    private async Task StopWorkersLockedAsync()
    {
        _available?.Writer.TryComplete();
        _available = null;
        foreach (var worker in _workers) await worker.DisposeAsync();
        _workers.Clear();
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
