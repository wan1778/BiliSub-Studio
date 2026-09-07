namespace BiliSubStudio.Core.Timeline;

// G9 lifecycle owner. Media IO and process work stay behind the V2 generator;
// the controller owns only revision, latest-request and artifact publication.
public sealed class TimelinePreviewController : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly ITimelineProcessedPreviewGenerator? _generator;
    private CancellationTokenSource? _currentCancellation;
    private Task _currentCompletion = Task.CompletedTask;
    private TimelineProcessedPreviewArtifact? _currentArtifact;
    private long _revision;
    private bool _disposed;

    public TimelinePreviewController()
    {
    }

    public TimelinePreviewController(ITimelineProcessedPreviewGenerator generator)
    {
        _generator = generator ?? throw new ArgumentNullException(nameof(generator));
    }

    public long CurrentRevision
    {
        get { lock (_sync) return _revision; }
    }

    public bool IsActive
    {
        get { lock (_sync) return _currentCancellation is not null; }
    }

    public TimelineProcessedPreviewArtifact? CurrentArtifact
    {
        get { lock (_sync) return _currentArtifact; }
    }

    public Task<string> DescribeAsync(TimelineRenderPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();
        TimelineProjectV2.ValidateId(plan.ProjectId);
        return Task.FromResult($"timeline-preview-shell:{plan.ProjectId}");
    }

    public async Task<TimelineProcessedPreviewArtifact> RequestAsync(
        TimelineRenderPlan plan,
        long sequenceStartUs,
        long durationUs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var generator = _generator
            ?? throw new InvalidOperationException("Timeline processed-preview generator chưa được cấu hình.");
        CancellationTokenSource requestCancellation;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource? previousCancellation;
        Task previousCompletion;
        long revision;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            revision = _revision = checked(_revision + 1);
            previousCancellation = _currentCancellation;
            previousCompletion = _currentCompletion;
            _currentCancellation = requestCancellation;
            _currentCompletion = completion.Task;
        }

        RequestCancellation(previousCancellation);
        TimelineProcessedPreviewArtifact? generated = null;
        var accepted = false;
        try
        {
            // A replacement cannot start until the previous renderer, process
            // group and temporary artifacts have completed cleanup.
            await previousCompletion.ConfigureAwait(false);
            requestCancellation.Token.ThrowIfCancellationRequested();
            generated = await generator.GenerateAsync(
                    plan, sequenceStartUs, durationUs, revision, requestCancellation.Token)
                .ConfigureAwait(false);
            requestCancellation.Token.ThrowIfCancellationRequested();
            if (generated.Revision != revision)
                throw new InvalidDataException("Timeline preview artifact mang sai revision.");

            TimelineProcessedPreviewArtifact? replaced = null;
            lock (_sync)
            {
                if (ReferenceEquals(_currentCancellation, requestCancellation)
                    && _revision == revision)
                {
                    replaced = _currentArtifact;
                    _currentArtifact = generated;
                    accepted = true;
                }
            }
            if (!accepted)
                throw new OperationCanceledException(requestCancellation.Token);
            if (replaced is not null
                && !string.Equals(replaced.Path, generated.Path, StringComparison.OrdinalIgnoreCase))
                await generator.DeleteAsync(replaced, CancellationToken.None).ConfigureAwait(false);
            return generated;
        }
        catch
        {
            if (generated is not null && !accepted)
                await generator.DeleteAsync(generated, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_currentCancellation, requestCancellation))
                {
                    _currentCancellation = null;
                    _currentCompletion = Task.CompletedTask;
                }
                requestCancellation.Dispose();
                completion.TrySetResult(true);
            }
        }
    }

    public Task CancelAsync() => CancelCoreAsync();

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }
        await CancelCoreAsync().ConfigureAwait(false);
    }

    private async Task CancelCoreAsync()
    {
        TimelineProcessedPreviewArtifact? artifact;
        CancellationTokenSource? cancellation;
        Task completion;
        long cancellationRevision;
        lock (_sync)
        {
            cancellationRevision = _revision = checked(_revision + 1);
            cancellation = _currentCancellation;
            completion = _currentCompletion;
            artifact = _currentArtifact;
            _currentArtifact = null;
        }
        // Issue Cancel() from inside the lock window or right after capturing
        // the CTS so the dispose-after-finally path cannot leave a live
        // renderer uncancelled. RequestAsync may null out fields while we
        // already hold the captured state; the completion task itself owns
        // all post-cancel cleanup.
        RequestCancellation(cancellation);
        if (completion is { IsCompleted: false })
            await completion.ConfigureAwait(false);
        else
            await Task.Yield();
        if (artifact is not null && _generator is not null)
        {
            try
            {
                await _generator.DeleteAsync(artifact, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                lock (_sync)
                {
                    if (_revision == cancellationRevision && _currentArtifact is null)
                        _currentArtifact = artifact;
                }
                throw;
            }
        }
    }

    private static void RequestCancellation(CancellationTokenSource? cancellation)
    {
        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
    }
}
