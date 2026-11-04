using System.Threading.Channels;

namespace BiliSubStudio.Core.Ocr;

// Batch scavenging (fill-4 on a running worker): merges individual OCR requests that are ALREADY WAITING
// into one worker batch.
// Iron rules:
// - ZERO-WAIT: never wait for enough mates. Idle worker + >=1 pending means run immediately.
// - Same flags to merge (recoverShortBlank + activeShortText): different flags means different batch,
//   because the worker applies one flag set to the whole batch — merging with wrong flags changes results.
// - LIMIT 4 (parity 15/9 against the actually running worker): the 27/8 worker rejects batches >4
//   ("must be a batch of 1-4 images", measured), even though the newer repo protocol allows 8. Exceeding is a batcher error.
// - Order: a batch preserves the exact submit order; results map by index (the worker preserves order).
// - Partial cancellation: whichever member is cancelled has its TCS cancelled, the batch still runs for the rest.
// - Fail-safe: if dispatch throws -> every unfinished member receives that same error, never hangs.
public sealed class OcrRequestBatcher : IAsyncDisposable
{
    public const int WorkerBatchLimit = 4;

    public sealed class Pending
    {
        public string ImageBase64 { get; }
        public bool RecoverShortBlank { get; }
        public string? ActiveShortText { get; }
        public Action<OcrPerfSample>? OnPerf { get; }
        public Task<OcrResult> Task => _tcs.Task;
        private readonly TaskCompletionSource<OcrResult> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenRegistration _registration;
        private int _settled;

        internal Pending(string imageBase64, bool recoverShortBlank, string? activeShortText, Action<OcrPerfSample>? onPerf)
        {
            ImageBase64 = imageBase64;
            RecoverShortBlank = recoverShortBlank;
            ActiveShortText = activeShortText;
            OnPerf = onPerf;
        }

        internal void AttachCancellation(CancellationToken token)
        {
            if (token.CanBeCanceled)
                _registration = token.Register(() => TrySetCanceled());
        }

        public bool TrySetResult(OcrResult result)
        {
            if (Interlocked.Exchange(ref _settled, 1) != 0) return false;
            _registration.Dispose();
            return _tcs.TrySetResult(result);
        }

        public bool TrySetException(Exception error)
        {
            if (Interlocked.Exchange(ref _settled, 1) != 0) return false;
            _registration.Dispose();
            return _tcs.TrySetException(error);
        }

        public bool TrySetCanceled()
        {
            if (Interlocked.Exchange(ref _settled, 1) != 0) return false;
            _registration.Dispose();
            return _tcs.TrySetCanceled();
        }
    }

    private readonly int _cap;
    private readonly Func<IReadOnlyList<Pending>, CancellationToken, Task> _dispatch;
    private readonly Func<int>? _workerCountProvider;
    private readonly TimeSpan _holdWindow;
    private readonly Channel<Pending> _pending = Channel.CreateUnbounded<Pending>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly object _trackedSync = new();
    private readonly List<Task> _tracked = [];

    // Busy-coalesce hold (26/09): each call costs ~380ms of fixed overhead
    // while compute is only ~40-140ms — a full batch of 4 is ~3x cheaper per image. Keep
    // the idle-run-immediately rule: only hold when every worker is busy (no worker
    // is idle, so waiting more costs nothing). workerCountProvider null means
    // the pool is unknown (old path/old tests): behavior identical to the previous zero-wait.
    internal static readonly TimeSpan CoalesceHold = TimeSpan.FromMilliseconds(15);

    // dispatch: accepts one batch (1..cap, same flags, exact order) and MUST settle
    // (result/exception/canceled) every member when done. If it throws -> the batcher assigns
    // the error to members still hanging (safety net, never hangs a lane).
    public OcrRequestBatcher(
        Func<IReadOnlyList<Pending>, CancellationToken, Task> dispatch,
        int cap = WorkerBatchLimit,
        Func<int>? workerCountProvider = null,
        TimeSpan holdWindow = default)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        ArgumentOutOfRangeException.ThrowIfLessThan(cap, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(cap, WorkerBatchLimit);
        if (holdWindow < TimeSpan.Zero || holdWindow > TimeSpan.FromSeconds(1))
            throw new ArgumentOutOfRangeException(nameof(holdWindow), "Coalesce hold phải trong 0..1s.");
        _dispatch = dispatch;
        _cap = cap;
        _workerCountProvider = workerCountProvider;
        _holdWindow = holdWindow == default ? CoalesceHold : holdWindow;
        _loop = Task.Run(LoopAsync);
    }

    public Task<OcrResult> SubmitAsync(
        string imageBase64,
        CancellationToken cancellationToken,
        bool recoverShortBlank = false,
        string? activeShortText = null,
        Action<OcrPerfSample>? onPerf = null)
    {
        if (string.IsNullOrWhiteSpace(imageBase64))
            throw new ArgumentException("Ảnh OCR rỗng.", nameof(imageBase64));
        var pending = new Pending(imageBase64, recoverShortBlank, activeShortText, onPerf);
        pending.AttachCancellation(cancellationToken);
        if (pending.Task.IsCompleted)
            return pending.Task;
        if (!_pending.Writer.TryWrite(pending))
        {
            pending.TrySetException(new InvalidOperationException("OCR batcher đã dừng, không nhận request mới."));
            return pending.Task;
        }
        return pending.Task;
    }

    private async Task LoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                // Spill first (never starvation): a batch of all leftover items with matching flags.
                if (_spill.Count > 0)
                {
                    Track(DispatchSafeAsync(TakeSpillBatch()));
                    continue;
                }
                // Zero-wait when idle: only block when there is NOTHING waiting. If anything is
                // there, scavenge immediately, no waiting for mates — except when every worker is busy (then
                // waiting at most the hold window to collect a full batch costs nothing).
                if (!await _pending.Reader.WaitToReadAsync(_stop.Token)) break;
                if (!_pending.Reader.TryRead(out var head) || head is null) continue;
                if (HoldApplies())
                {
                    try { await Task.Delay(_holdWindow, _stop.Token); }
                    catch (OperationCanceledException)
                    {
                        // Head was taken out of the channel but not yet dispatched: settle
                        // immediately as Dispose does with leftovers, never hang a lane.
                        head.TrySetCanceled();
                        break;
                    }
                }
                var batch = new List<Pending>(_cap) { head };
                while (batch.Count < _cap && _pending.Reader.TryRead(out var next) && next is not null)
                {
                    if (Compatible(next, head)) batch.Add(next);
                    else _spill.Add(next);
                }
                Track(DispatchSafeAsync(batch));
            }
        }
        catch (OperationCanceledException) { }
    }

    private static bool Compatible(Pending candidate, Pending head) =>
        candidate.RecoverShortBlank == head.RecoverShortBlank
        && string.Equals(candidate.ActiveShortText, head.ActiveShortText, StringComparison.Ordinal);

    // If every worker is holding one unfinished dispatch, holding longer for mates is wasted
    // effort (no idle worker is forced to wait). Otherwise run immediately as before.
    // Provider errors/returns <= 0 fail-open back to zero-wait (never hold blindly).
    private bool HoldApplies()
    {
        var provider = _workerCountProvider;
        if (provider is null) return false;
        int workers;
        try { workers = provider(); }
        catch { return false; }
        if (workers <= 0) return false;
        lock (_trackedSync)
        {
            _tracked.RemoveAll(task => task.IsCompleted);
            return _tracked.Count >= workers;
        }
    }

    // Take a batch from the spill: head = the longest-surviving item + mates with matching flags (preserving order).
    // Incompatible ones stay in the spill waiting for the next round — but every round drains the spill
    // before the channel so there is no starvation (worst case: it goes alone as it does today).
    private List<Pending> TakeSpillBatch()
    {
        var head = _spill[0];
        _spill.RemoveAt(0);
        var batch = new List<Pending>(_cap) { head };
        for (var index = _spill.Count - 1; index >= 0 && batch.Count < _cap; index--)
        {
            if (Compatible(_spill[index], head))
            {
                batch.Add(_spill[index]);
                _spill.RemoveAt(index);
            }
        }
        return batch;
    }

    private void Track(Task dispatch)
    {
        lock (_trackedSync)
        {
            if (_tracked.Count > 64)
                _tracked.RemoveAll(task => task.IsCompleted);
            _tracked.Add(dispatch);
        }
        // Dispatch settles members ITSELF. An error escaping it is a dispatch bug:
        // the production dispatch has a catch-all that settles members; the contract test catches
        // fake dispatches that settle everything. No lane hangs because DisposeAsync cancels all leftovers.
    }

    // Net against a dispatch throwing synchronously (kills the loop -> hangs every later submit):
    // any error is assigned to that batch's members, and the loop lives on.
    private async Task DispatchSafeAsync(IReadOnlyList<Pending> batch)
    {
        try
        {
            await _dispatch(batch, _stop.Token);
        }
        catch (Exception error)
        {
            foreach (var pending in batch)
                pending.TrySetException(error);
        }
    }

    // Only the loop thread touches _spill; Dispose reads it after the loop finishes, so no lock.
    private readonly List<Pending> _spill = [];

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _pending.Writer.TryComplete();
        try { await _loop; } catch (OperationCanceledException) { }
        Task[] inflight;
        lock (_trackedSync) inflight = _tracked.ToArray();
        try { await Task.WhenAll(inflight); } catch { }
        while (_pending.Reader.TryRead(out var leftover) && leftover is not null)
            leftover.TrySetCanceled();
        foreach (var spilled in _spill)
            spilled.TrySetCanceled();
        _spill.Clear();
        _stop.Dispose();
    }
}
