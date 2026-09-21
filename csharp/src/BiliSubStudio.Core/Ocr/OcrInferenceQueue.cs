using System.Diagnostics;
using System.Threading.Channels;

namespace BiliSubStudio.Core.Ocr;

internal sealed class OcrInferenceQueue : IAsyncDisposable
{
    // E.4c (OCR Gates v1.2 verdict): capacity >= 8 la dieu kien di kem BAT BUOC
    // cua zero-wait fill (capacity 4 thi worker khong bao gio co 8 request de vet).
    // Tang buffer dem, khong doi ngu nghia backpressure (FullMode.Wait giu nguyen).
    internal const int Capacity = 12;
    private readonly Channel<(Func<Task> Work, long EnqueuedTicks)> _requests;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _completion;
    // Diagnostic counters only: no scheduling behavior depends on these.
    private long _executed;
    private long _depthSamples;
    private long _depthSum;
    private int _peakDepth;
    private long _totalWaitTicks;
    internal int PendingCount => _requests.Reader.Count;

    internal (long Executed, int PeakDepth, double AvgDepth, long TotalWaitMs) Snapshot()
    {
        var executed = Interlocked.Read(ref _executed);
        var samples = Interlocked.Read(ref _depthSamples);
        var sum = Interlocked.Read(ref _depthSum);
        return (executed, Volatile.Read(ref _peakDepth),
            samples > 0 ? sum / (double)samples : 0,
            Interlocked.Read(ref _totalWaitTicks) * 1000 / Stopwatch.Frequency);
    }

    internal OcrInferenceQueue(int workers, int capacity = Capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(workers, 1);
        _requests = Channel.CreateBounded<(Func<Task> Work, long EnqueuedTicks)>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = false,
            SingleReader = workers == 1,
        });
        _completion = Task.WhenAll(Enumerable.Range(0, workers).Select(_ => ConsumeAsync()));
    }

    internal async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        var token = linked.Token;
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() => result.TrySetCanceled(token));
        var enqueuedTicks = Stopwatch.GetTimestamp();
        await _requests.Writer.WriteAsync((async () =>
        {
            if (result.Task.IsCompleted) return;
            try { result.TrySetResult(await operation(token)); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { result.TrySetCanceled(token); }
            catch (Exception error) { result.TrySetException(error); }
        }, enqueuedTicks), token);
        var depth = PendingCount;
        Interlocked.Increment(ref _depthSamples);
        Interlocked.Add(ref _depthSum, depth);
        int peak;
        while (depth > (peak = Volatile.Read(ref _peakDepth))
            && Interlocked.CompareExchange(ref _peakDepth, depth, peak) != peak) { }
        return await result.Task;
    }

    private async Task ConsumeAsync()
    {
        await foreach (var request in _requests.Reader.ReadAllAsync())
        {
            Interlocked.Increment(ref _executed);
            Interlocked.Add(ref _totalWaitTicks, Stopwatch.GetTimestamp() - request.EnqueuedTicks);
            await request.Work();
        }
    }

    internal void Stop()
    {
        _requests.Writer.TryComplete();
        _stop.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        await _completion;
        _stop.Dispose();
    }
}
