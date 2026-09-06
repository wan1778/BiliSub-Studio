using BiliSubStudio.Core.Jobs;

namespace BiliSubStudio.Core.ContractTests;

internal static class SequentialJobQueueContract
{
    public static async Task RunAsync()
    {
        using var jobs = new JobManager();
        await using var queue = new SequentialJobQueue();
        var starts = new List<int>();
        var active = 0;
        var peakActive = 0;

        void Enter(int number)
        {
            lock (starts) starts.Add(number);
            var current = Interlocked.Increment(ref active);
            int observed;
            do
            {
                observed = Volatile.Read(ref peakActive);
                if (current <= observed) break;
            } while (Interlocked.CompareExchange(ref peakActive, current, observed) != observed);
        }

        void Exit() => Interlocked.Decrement(ref active);

        var firstStarted = NewSignal();
        var releaseFirst = NewSignal();
        var secondRan = false;
        var first = jobs.Create("media", cleanupAwareCancel: true);
        var second = jobs.Create("media", cleanupAwareCancel: true);
        var third = jobs.Create("media", cleanupAwareCancel: true);
        queue.Enqueue(first, async () =>
        {
            Enter(1);
            firstStarted.TrySetResult(true);
            await releaseFirst.Task;
            first.Finish(null, "first done");
            Exit();
        });
        queue.Enqueue(second, () =>
        {
            secondRan = true;
            second.Finish(null, "second done");
            return Task.CompletedTask;
        });
        queue.Enqueue(third, () =>
        {
            Enter(3);
            third.Finish(null, "third done");
            Exit();
            return Task.CompletedTask;
        });

        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        jobs.Cancel(second.Id);
        await second.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Check(second.Snapshot().Status == "cancelled", "waiting cancellation must become terminal immediately");
        Check(!secondRan, "cancelled waiting operation must not run");
        releaseFirst.TrySetResult(true);
        await Task.WhenAll(first.Completion, third.Completion).WaitAsync(TimeSpan.FromSeconds(5));
        await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Check(starts.SequenceEqual([1, 3]), "FIFO must retain order while skipping the cancelled item");
        Check(peakActive == 1, "queue must never run two operations concurrently");

        var handoffBlocked = NewSignal();
        var releaseHandoff = NewSignal();
        var handoffSuccessorRan = false;
        var handoffFirst = jobs.Create("media", cleanupAwareCancel: true);
        var handoffSuccessor = jobs.Create("media", cleanupAwareCancel: true);
        queue.Enqueue(handoffFirst, async () =>
        {
            handoffFirst.Finish(null, "finished before operation unwind");
            handoffBlocked.TrySetResult(true);
            await releaseHandoff.Task;
        });
        queue.Enqueue(handoffSuccessor, () =>
        {
            handoffSuccessorRan = true;
            handoffSuccessor.Finish(null, "should not run");
            return Task.CompletedTask;
        });
        await handoffBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var cancelledAtHandoff = queue.CancelCurrent();
        Check(cancelledAtHandoff == handoffSuccessor.Id,
            "global cancellation must target the waiting successor when the active job is already terminal");
        releaseHandoff.TrySetResult(true);
        await handoffSuccessor.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Check(!handoffSuccessorRan, "handoff cancellation must skip the successor operation");

        var fourthStarted = NewSignal();
        var cleanupStarted = NewSignal();
        var releaseCleanup = NewSignal();
        var fifthStarted = NewSignal();
        var fourth = jobs.Create("media", cleanupAwareCancel: true);
        var fifth = jobs.Create("media", cleanupAwareCancel: true);
        queue.Enqueue(fourth, async () =>
        {
            Enter(4);
            fourthStarted.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, fourth.CancellationToken);
            }
            catch (OperationCanceledException)
            {
                cleanupStarted.TrySetResult(true);
                await releaseCleanup.Task;
                throw;
            }
            finally
            {
                Exit();
            }
        });
        queue.Enqueue(fifth, () =>
        {
            Enter(5);
            fifthStarted.TrySetResult(true);
            fifth.Finish(null, "fifth done");
            Exit();
            return Task.CompletedTask;
        });

        await fourthStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(queue.CancelCurrent() == fourth.Id, "global cancellation must target the active operation");
        await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(queue.CancelCurrent() == fourth.Id, "repeated cancellation must not spin or cancel the successor");
        await Task.Delay(100);
        Check(!fourth.Snapshot().Done, "active cancellation must remain nonterminal during cleanup");
        Check(!fifthStarted.Task.IsCompleted, "successor must not start before active cleanup finishes");
        releaseCleanup.TrySetResult(true);
        await Task.WhenAll(fourth.Completion, fifth.Completion).WaitAsync(TimeSpan.FromSeconds(5));
        Check(fourth.Snapshot().Status == "cancelled", "active item must finish cancelled after cleanup");
        Check(fifthStarted.Task.IsCompletedSuccessfully, "successor must run after cleanup");

        var failed = jobs.Create("media", cleanupAwareCancel: true);
        var afterFailure = jobs.Create("media", cleanupAwareCancel: true);
        queue.Enqueue(failed, () => throw new InvalidOperationException("controlled queue failure"));
        queue.Enqueue(afterFailure, () =>
        {
            afterFailure.Finish(null, "continued");
            return Task.CompletedTask;
        });
        await Task.WhenAll(failed.Completion, afterFailure.Completion).WaitAsync(TimeSpan.FromSeconds(5));
        await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Check(failed.Snapshot().Status == "error", "operation failure must be isolated to its job");
        Check(afterFailure.Snapshot().Status == "done", "queue must continue after an operation failure");
        Check(queue.Count == 0, "queue must report idle after all work finishes");

        var shutdownStarted = NewSignal();
        var shutdown = jobs.Create("media", cleanupAwareCancel: true);
        queue.Enqueue(shutdown, async () =>
        {
            shutdownStarted.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, shutdown.CancellationToken);
        });
        await shutdownStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await queue.PauseAndDrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Check(shutdown.Snapshot().Status == "cancelled", "queue pause must cancel and drain active work");
        var rejectedWhilePaused = jobs.Create("media", cleanupAwareCancel: true);
        var pausedAdmissionRejected = false;
        try
        {
            queue.Enqueue(rejectedWhilePaused, () => Task.CompletedTask);
        }
        catch (InvalidOperationException)
        {
            pausedAdmissionRejected = true;
        }
        Check(pausedAdmissionRejected, "paused queue must atomically reject new submissions");
        queue.Resume();
        var recovered = jobs.Create("media", cleanupAwareCancel: true);
        queue.Enqueue(recovered, () =>
        {
            recovered.Finish(null, "recovered");
            return Task.CompletedTask;
        });
        await recovered.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Check(recovered.Snapshot().Status == "done", "queue must accept work after a failed shutdown resumes admission");

        await queue.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var rejected = jobs.Create("media", cleanupAwareCancel: true);
        var rejectedAfterStop = false;
        try
        {
            queue.Enqueue(rejected, () => Task.CompletedTask);
        }
        catch (ObjectDisposedException)
        {
            rejectedAfterStop = true;
        }
        Check(rejectedAfterStop, "disposed queue must reject new submissions permanently");
    }

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
