using BiliSubStudio.Core.Diagnostics;

namespace BiliSubStudio.Core.Jobs;

public sealed class AppJob : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly List<string> _logs = [];
    private readonly TaskCompletionSource<bool> _pauseCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ApplicationLog? _applicationLog;
    private string _status = "queued";
    private double _progress;
    private string _message = "Đang chờ.";
    private bool _done;
    private string? _error;
    private object? _result;
    private bool _pauseRequested;
    private bool _cancelRequested;
    private double _bytesPerSecond;
    private int _activeConnections;
    private bool? _rangeSupported;

    public AppJob(string id, string kind, bool pauseSupported = false, ApplicationLog? applicationLog = null, bool cleanupAwareCancel = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        Id = id;
        Kind = kind;
        PauseSupported = pauseSupported;
        CleanupAwareCancel = cleanupAwareCancel;
        _applicationLog = applicationLog;
        _applicationLog?.Info(Kind, "Bắt đầu tác vụ.", Id);
    }

    public string Id { get; }
    public string Kind { get; }
    public bool PauseSupported { get; }
    public bool CleanupAwareCancel { get; }
    public CancellationToken CancellationToken => _cancellation.Token;
    public Task Completion => _completion.Task;

    public void Cancel()
    {
        _cancellation.Cancel();
        var changed = false;
        lock (_gate)
        {
            if (_done || _cancelRequested)
            {
                return;
            }
            _cancelRequested = true;
            var waitsForCleanup = PauseSupported || CleanupAwareCancel;
            _status = waitsForCleanup ? "cancelling" : "cancelled";
            _message = waitsForCleanup ? "Đang dừng tác vụ và dọn dữ liệu dở..." : "Đã hủy tác vụ.";
            _done = !waitsForCleanup;
            changed = true;
            if (_done)
            {
                _pauseCompletion.TrySetResult(true);
                _completion.TrySetResult(true);
            }
        }
        if (changed) _applicationLog?.Info(Kind, PauseSupported || CleanupAwareCancel ? "Đang hủy tác vụ an toàn." : "Đã hủy tác vụ.", Id);
    }

    public void CancelComplete(string? message = null)
    {
        string finalMessage;
        lock (_gate)
        {
            if (_done) return;
            _cancelRequested = true;
            _status = "cancelled";
            _message = string.IsNullOrWhiteSpace(message) ? "Đã hủy tác vụ và dọn dữ liệu dở." : message;
            finalMessage = _message;
            _done = true;
            _pauseCompletion.TrySetResult(true);
            _completion.TrySetResult(true);
        }
        _applicationLog?.Info(Kind, finalMessage, Id);
    }

    public Task RequestPauseAsync()
    {
        lock (_gate)
        {
            if (!PauseSupported)
            {
                throw new InvalidOperationException("Tác vụ này không hỗ trợ tạm dừng.");
            }
            if (_done)
            {
                throw new InvalidOperationException("Tác vụ đã kết thúc.");
            }
            if (_cancelRequested)
            {
                throw new InvalidOperationException("Tác vụ đang được hủy.");
            }
            _pauseRequested = true;
            _status = "pausing";
            _message = "Đang lưu checkpoint an toàn để tạm dừng...";
            return _pauseCompletion.Task;
        }
    }

    public bool IsPauseRequested
    {
        get { lock (_gate) return _pauseRequested && !_cancelRequested && !_done; }
    }

    public void PauseComplete(string? message = null)
    {
        string finalMessage;
        lock (_gate)
        {
            if (_done || _cancelRequested)
            {
                return;
            }
            _status = "paused";
            _message = string.IsNullOrWhiteSpace(message) ? "Đã tạm dừng tại checkpoint an toàn." : message;
            finalMessage = _message;
            _done = true;
            _pauseCompletion.TrySetResult(true);
            _completion.TrySetResult(true);
        }
        _applicationLog?.Info(Kind, finalMessage, Id);
    }

    public void Log(string message) => AddLog(AppLogLevel.Info, message);

    public void Warn(string message) => AddLog(AppLogLevel.Warning, message);

    public void Error(string message) => AddLog(AppLogLevel.Error, message);

    private void AddLog(AppLogLevel level, string message)
    {
        lock (_gate)
        {
            _logs.Add($"{DateTime.Now:HH:mm:ss}  {message}");
        }
        switch (level)
        {
            case AppLogLevel.Warning:
                _applicationLog?.Warning(Kind, message, Id);
                break;
            case AppLogLevel.Error:
                _applicationLog?.Error(Kind, message, Id);
                break;
            default:
                _applicationLog?.Info(Kind, message, Id);
                break;
        }
    }

    public void Set(string status, double progress, string message)
    {
        lock (_gate)
        {
            if (_done)
            {
                return;
            }
            _status = status;
            if (progress >= 0)
            {
                _progress = Math.Clamp(progress, 0, 100);
            }
            _message = message;
        }
    }

    public void SetTransport(double bytesPerSecond, int activeConnections, bool? rangeSupported)
    {
        lock (_gate)
        {
            _bytesPerSecond = Math.Max(0, bytesPerSecond);
            _activeConnections = Math.Max(0, activeConnections);
            _rangeSupported = rangeSupported;
        }
    }

    public void SetResult(object? result)
    {
        lock (_gate)
        {
            _result = result;
        }
    }

    public void Finish(Exception? error, string message, object? result = null)
    {
        string finalMessage;
        lock (_gate)
        {
            if (_done)
            {
                return;
            }
            _done = true;
            _result = result;
            _pauseCompletion.TrySetResult(true);
            _completion.TrySetResult(true);
            if (error is null)
            {
                _status = "done";
                _progress = 100;
            }
            else
            {
                _status = "error";
                _error = error.Message;
            }
            _message = string.IsNullOrWhiteSpace(message) ? error?.Message ?? "Hoàn tất." : message;
            finalMessage = _message;
        }

        if (error is null)
            _applicationLog?.Info(Kind, finalMessage, Id);
        else
            _applicationLog?.Error(Kind, finalMessage, Id);
    }

    public JobSnapshot Snapshot(int after = 0)
    {
        lock (_gate)
        {
            after = Math.Clamp(after, 0, _logs.Count);
            return new JobSnapshot(
                Id, Kind, _status, _progress, _message, _logs.Skip(after).ToArray(), _logs.Count,
                _done, _error, _result, PauseSupported, _pauseRequested,
                _bytesPerSecond, _activeConnections, _rangeSupported);
        }
    }

    public void Dispose() => _cancellation.Dispose();
}
