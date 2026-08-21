namespace BiliSubStudio.Core.Jobs;

public sealed class AppJob : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly List<string> _logs = [];
    private readonly TaskCompletionSource _pauseCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string _status = "queued";
    private double _progress;
    private string _message = "Đang chờ.";
    private bool _done;
    private string? _error;
    private object? _result;
    private bool _pauseRequested;
    private double _bytesPerSecond;
    private int _activeConnections;
    private bool? _rangeSupported;

    public AppJob(string id, string kind, bool pauseSupported = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        Id = id;
        Kind = kind;
        PauseSupported = pauseSupported;
    }

    public string Id { get; }
    public string Kind { get; }
    public bool PauseSupported { get; }
    public CancellationToken CancellationToken => _cancellation.Token;

    public void Cancel()
    {
        _cancellation.Cancel();
        lock (_gate)
        {
            if (_done)
            {
                return;
            }
            _status = "cancelled";
            _message = "Đã hủy tác vụ.";
            _done = true;
            _pauseCompletion.TrySetResult();
        }
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
            _pauseRequested = true;
            _status = "pausing";
            _message = "Đang lưu checkpoint an toàn để tạm dừng...";
            return _pauseCompletion.Task;
        }
    }

    public bool IsPauseRequested
    {
        get { lock (_gate) return _pauseRequested && !_done; }
    }

    public void PauseComplete(string? message = null)
    {
        lock (_gate)
        {
            if (_done)
            {
                return;
            }
            _status = "paused";
            _message = string.IsNullOrWhiteSpace(message) ? "Đã tạm dừng tại checkpoint an toàn." : message;
            _done = true;
            _pauseCompletion.TrySetResult();
        }
    }

    public void Log(string message)
    {
        lock (_gate)
        {
            _logs.Add($"{DateTime.Now:HH:mm:ss}  {message}");
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
        lock (_gate)
        {
            if (_done)
            {
                return;
            }
            _done = true;
            _result = result;
            _pauseCompletion.TrySetResult();
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
        }
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
