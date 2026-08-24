namespace BiliSubStudio.Core.Editor;

public sealed class EditorPreviewRequestCoordinator
{
    private readonly object _sync = new();
    private CancellationTokenSource? _currentCancellation;
    private Task _currentCompletion = Task.CompletedTask;

    public bool IsActive
    {
        get
        {
            lock (_sync) return _currentCancellation is not null;
        }
    }

    public async Task RunLatestAsync(Func<CancellationToken, Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var cancellation = new CancellationTokenSource();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource? previousCancellation;
        Task previousCompletion;
        lock (_sync)
        {
            previousCancellation = _currentCancellation;
            previousCompletion = _currentCompletion;
            _currentCancellation = cancellation;
            _currentCompletion = completion.Task;
        }

        RequestCancellation(previousCancellation);
        try
        {
            // A replacement waits until the cancelled request has finished its
            // FFmpeg/temp-file cleanup. Queued intermediate requests are cancelled
            // before their operation can start, so only the newest request renders.
            await previousCompletion;
            cancellation.Token.ThrowIfCancellationRequested();
            await operation(cancellation.Token);
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_currentCancellation, cancellation))
                {
                    _currentCancellation = null;
                    _currentCompletion = Task.CompletedTask;
                }
                cancellation.Dispose();
                completion.TrySetResult(true);
            }
        }
    }

    public async Task CancelAsync()
    {
        while (true)
        {
            CancellationTokenSource? cancellation;
            Task completion;
            lock (_sync)
            {
                cancellation = _currentCancellation;
                completion = _currentCompletion;
            }
            if (cancellation is null) return;
            RequestCancellation(cancellation);
            await completion;
        }
    }

    private static void RequestCancellation(CancellationTokenSource? cancellation)
    {
        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
    }
}
