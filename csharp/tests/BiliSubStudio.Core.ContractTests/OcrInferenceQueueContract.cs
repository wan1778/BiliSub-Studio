using System.Reflection;
using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

internal static class OcrInferenceQueueContract
{
    private static readonly Type QueueType = typeof(OcrManager).Assembly.GetType("BiliSubStudio.Core.Ocr.OcrInferenceQueue")!;
    private static readonly MethodInfo Run = QueueType.GetMethod("RunAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.MakeGenericMethod(typeof(int));
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static async Task RunAsync()
    {
        object Queue(int workers, int capacity) => Activator.CreateInstance(QueueType,
            BindingFlags.Instance | BindingFlags.NonPublic, null, [workers, capacity], null)!;
        Task<int> Submit(object queue, Func<CancellationToken, Task<int>> operation, CancellationToken token = default) =>
            (Task<int>)Run.Invoke(queue, [operation, token])!;
        static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        var queue = Queue(1, 2);
        var entered = Signal();
        var release = Signal();
        try
        {
            var first = Submit(queue, async token => { entered.SetResult(); await release.Task.WaitAsync(token); return 0; });
            await entered.Task.WaitAsync(Timeout);
            var seen = new List<int>();
            using var queuedCancellation = new CancellationTokenSource();
            var canceled = Submit(queue, _ => { throw new InvalidOperationException("Canceled queued request executed"); }, queuedCancellation.Token);
            var second = Submit(queue, _ => { seen.Add(1); return Task.FromResult(1); });
            using var blockedCancellation = new CancellationTokenSource();
            var blocked = Submit(queue, _ => { throw new InvalidOperationException("Blocked request executed"); }, blockedCancellation.Token);
            Check(!blocked.IsCompleted, "full queue did not apply backpressure");
            Check((int)QueueType.GetProperty("PendingCount", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(queue)! == 2,
                "queue admitted more than its bounded capacity");
            blockedCancellation.Cancel();
            await Canceled(blocked);
            queuedCancellation.Cancel();
            await Canceled(canceled);
            Check(!second.IsCompleted, "single consumer exceeded execution bound");
            release.SetResult();
            Check(await first.WaitAsync(Timeout) == 0 && await second.WaitAsync(Timeout) == 1 && seen.SequenceEqual([1]),
                "accepted work was dropped or reordered");
            var error = new IOException("deterministic inference failure");
            try { await Submit(queue, _ => Task.FromException<int>(error)).WaitAsync(Timeout); throw new Exception("failure swallowed"); }
            catch (IOException actual) { Check(ReferenceEquals(error, actual), "request lost original failure"); }
            Check(await Submit(queue, _ => Task.FromResult(7)).WaitAsync(Timeout) == 7, "failure poisoned consumer");
            using var activeCancellation = new CancellationTokenSource();
            var activeEntered = Signal();
            var interrupted = Submit(queue, async token =>
            {
                activeEntered.SetResult();
                await Task.Delay(System.Threading.Timeout.Infinite, token);
                return 0;
            }, activeCancellation.Token);
            await activeEntered.Task.WaitAsync(Timeout);
            activeCancellation.Cancel();
            await Canceled(interrupted);
            Check(await Submit(queue, _ => Task.FromResult(8)).WaitAsync(Timeout) == 8,
                "active cancellation stranded the next request");
        }
        finally { release.TrySetResult(); await ((IAsyncDisposable)queue).DisposeAsync(); }

        // Stop must settle active, queued, and capacity-blocked producers without
        // waiting for a lane to submit another request to restart the pool.
        queue = Queue(1, 1);
        entered = Signal();
        var active = Submit(queue, async token => { entered.SetResult(); await Task.Delay(System.Threading.Timeout.Infinite, token); return 0; });
        await entered.Task.WaitAsync(Timeout);
        var waiting = Submit(queue, _ => Task.FromResult(1));
        var full = Submit(queue, _ => Task.FromResult(2));
        await ((IAsyncDisposable)queue).DisposeAsync().AsTask().WaitAsync(Timeout);
        await Canceled(active);
        await Canceled(waiting);
        try { await full.WaitAsync(Timeout); throw new Exception("stop admitted blocked request"); }
        catch (Exception error) when (error is OperationCanceledException or System.Threading.Channels.ChannelClosedException) { }

        queue = Queue(2, 2);
        var bothEntered = Signal();
        var unblockFirst = Signal();
        var activeCount = 0;
        var maxActive = 0;
        var calls = 0;
        try
        {
            var lanes = Enumerable.Range(0, 6).Select(async lane =>
            {
                var ordered = new List<int>();
                for (var pts = 0; pts < 12; pts++)
                {
                    var expected = pts;
                    var secondCall = false;
                    var value = await Submit(queue, async token =>
                    {
                        var count = Interlocked.Increment(ref activeCount);
                        int old;
                        do { old = Volatile.Read(ref maxActive); }
                        while (old < count && Interlocked.CompareExchange(ref maxActive, count, old) != old);
                        try
                        {
                            var call = Interlocked.Increment(ref calls);
                            if (call == 1) await unblockFirst.Task.WaitAsync(token);
                            if (call == 2) { secondCall = true; bothEntered.TrySetResult(); }
                            return expected;
                        }
                        finally { Interlocked.Decrement(ref activeCount); }
                    });
                    ordered.Add(value);
                    if (secondCall) unblockFirst.TrySetResult();
                }
                Check(ordered.SequenceEqual(Enumerable.Range(0, 12)), $"lane {lane} tracker PTS order changed");
            }).ToArray();
            await bothEntered.Task.WaitAsync(Timeout);
            await Task.WhenAll(lanes).WaitAsync(Timeout);
            Check(maxActive == 2 && calls == 72, "six lanes must execute exactly 72 requests through two consumers");
        }
        finally { unblockFirst.TrySetResult(); await ((IAsyncDisposable)queue).DisposeAsync(); }

        var topology = typeof(OcrManager).Assembly.GetType("BiliSubStudio.Core.Ocr.OcrTopologyBenchmark")!;
        var target = topology.GetMethod("WorkerTarget", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (var lanes in new[] { 1, 2, 4, 8, 16 })
        {
            Check((int)target.Invoke(null, ["gpu", lanes])! == Math.Min(2, lanes), "GPU worker target follows decoder lanes");
            Check((int)target.Invoke(null, ["cpu", lanes])! == lanes, "CPU topology changed");
        }
        Check((int)target.Invoke(null, ["hybrid", 8])! == 2, "Hybrid must retain GPU + CPU independently of eight lanes");
    }

    private static async Task Canceled(Task task)
    {
        try { await task.WaitAsync(Timeout); throw new Exception("request did not cancel"); }
        catch (OperationCanceledException) { }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
