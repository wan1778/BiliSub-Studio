using System.Threading.Channels;

namespace BiliSubStudio.Core.Jobs;

/// <summary>
/// Runs retained jobs in FIFO order. Cancelling a waiting item completes it immediately;
/// cancelling the active item keeps the queue blocked until its cleanup has unwound.
/// </summary>
public sealed class SequentialJobQueue : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Channel<WorkItem> _channel = Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions
    {
        SingleReader = true,
        AllowSynchronousContinuations = false,
    });
    private readonly List<WorkItem> _items = [];
    private readonly Task _consumer;
    private TaskCompletionSource<bool> _idle = CompletedIdle();
    private bool _accepting = true;
    private bool _disposed;
    private WorkItem? _active;

    public SequentialJobQueue() => _consumer = ConsumeAsync();

    public int Count
    {
        get { lock (_gate) return _items.Count; }
    }

    public void Enqueue(AppJob job, Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(operation);
        var item = new WorkItem(job, operation);
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SequentialJobQueue));
            if (!_accepting) throw new InvalidOperationException("Hàng đợi đang chuẩn bị đóng ứng dụng.");
            if (_items.Count == 0)
                _idle = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _items.Add(item);
            item.RegisterQueuedCancellation();
            if (!_channel.Writer.TryWrite(item))
            {
                item.Complete();
                _items.Remove(item);
                if (_items.Count == 0) _idle.TrySetResult(true);
                throw new InvalidOperationException("Không thể thêm tác vụ vào hàng đợi.");
            }
        }
    }

    public async Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        Task idle;
        lock (_gate) idle = _idle.Task;
        await idle.WaitAsync(cancellationToken);
    }

    public string? CancelCurrent()
    {
        while (true)
        {
            WorkItem? target;
            lock (_gate)
            {
                target = _active is not null && !_active.Job.Snapshot().Done
                    ? _active
                    : _items.FirstOrDefault(item => !item.Job.Snapshot().Done);
            }
            if (target is null) return null;
            if (target.Job.CancellationToken.IsCancellationRequested) return target.Job.Id;
            if (target.Job.TryCancel()) return target.Job.Id;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        WorkItem[] items;
        lock (_gate)
        {
            if (!_disposed)
            {
                _disposed = true;
                _accepting = false;
                items = _items.ToArray();
                _channel.Writer.TryComplete();
            }
            else
            {
                items = [];
            }
        }
        foreach (var item in items) item.Job.Cancel();
        return _consumer.WaitAsync(cancellationToken);
    }

    public Task PauseAndDrainAsync(CancellationToken cancellationToken = default)
    {
        WorkItem[] items;
        Task idle;
        lock (_gate)
        {
            if (_disposed) return _consumer.WaitAsync(cancellationToken);
            _accepting = false;
            items = _items.ToArray();
            idle = _idle.Task;
        }
        foreach (var item in items) item.Job.Cancel();
        return idle.WaitAsync(cancellationToken);
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (!_disposed) _accepting = true;
        }
    }

    private async Task ConsumeAsync()
    {
        await foreach (var item in _channel.Reader.ReadAllAsync())
        {
            try
            {
                if (!item.TryStart()) continue;
                lock (_gate) _active = item;
                item.Job.Set("starting", 0, "Đang chuẩn bị tải media...");
                item.Job.CancellationToken.ThrowIfCancellationRequested();
                await item.Operation();
                if (!item.Job.Snapshot().Done)
                    item.Job.Finish(null, "Hoàn tất.");
            }
            catch (OperationCanceledException) when (item.Job.CancellationToken.IsCancellationRequested)
            {
                item.Job.CancelComplete();
            }
            catch (Exception error)
            {
                item.Job.Finish(error, error.Message);
            }
            finally
            {
                item.Complete();
                lock (_gate)
                {
                    if (ReferenceEquals(_active, item)) _active = null;
                    _items.Remove(item);
                    if (_items.Count == 0) _idle.TrySetResult(true);
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    private static TaskCompletionSource<bool> CompletedIdle()
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        completion.SetResult(true);
        return completion;
    }

    private sealed class WorkItem(AppJob job, Func<Task> operation)
    {
        // 0 = waiting, 1 = running, 2 = terminal/skipped.
        private int _state;
        private CancellationTokenRegistration _registration;

        public AppJob Job { get; } = job;
        public Func<Task> Operation { get; } = operation;

        public void RegisterQueuedCancellation() =>
            _registration = Job.CancellationToken.Register(() =>
            {
                if (Interlocked.CompareExchange(ref _state, 2, 0) == 0)
                    Job.CancelComplete("Đã hủy mục đang chờ.");
            });

        public bool TryStart() => Interlocked.CompareExchange(ref _state, 1, 0) == 0;

        public void Complete()
        {
            Interlocked.Exchange(ref _state, 2);
            _registration.Dispose();
        }
    }
}
