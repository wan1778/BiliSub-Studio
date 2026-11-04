using System.Diagnostics;
using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

// Pure batch coalescing (fill-8) logic with a fake dispatch — no worker, no GPU.
// Required invariants (the survival of the lane depends on them):
// - zero-wait: any pending runs immediately, without waiting for a mate;
// - only the same flag coalesces (recover/activeText), different flags never share a batch;
// - submit order is preserved inside a batch, results map to the right member;
// - partial cancel does not kill the remaining members; dispose never hangs, never loses a member.
internal static class OcrBatchCoalesceContract
{
    public static async Task RunAsync()
    {
        // Speed fail-safe: cap by what the live worker accepts (parity 15/9: text/box identical
        // 8/8, 1.53x faster; the worker rejects batches > 4).
        Check(OcrManager.BatchCoalesceCap == 4, "vét batch vượt giới hạn worker đang chạy");
        await OrderPreservedAsync();
        await FlagGroupingAsync();
        await CapRespectedAsync();
        await ZeroWaitAsync();
        await MemberCancelAsync();
        await PreCancelledSubmitAsync();
        await ExceptionMappingAsync();
        await DisposeCompletesInflightAsync();
        await SubmitAfterDisposeFailsAsync();
        await OnPerfPassthroughAsync();
        await CheckCtorGuardsAsync();
        await BusyHoldsForMatesAsync();
        await IdleIgnoresHoldAsync();
    }

    private sealed class FakeDispatch
    {
        public readonly List<IReadOnlyList<OcrRequestBatcher.Pending>> Batches = [];
        public readonly TaskCompletionSource Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<OcrRequestBatcher.Pending, OcrResult> ResultFor =
            pending => new OcrResult(true, true, "t-" + pending.ImageBase64, 0.99, [], null);

        public async Task DispatchAsync(
            IReadOnlyList<OcrRequestBatcher.Pending> batch,
            CancellationToken cancellationToken)
        {
            Check(batch.Count >= 1 && batch.Count <= 4, "batch phải 1..4 ảnh (giới hạn worker đang chạy)");
            lock (Batches) Batches.Add(batch);
            await Gate.Task.WaitAsync(TimeSpan.FromSeconds(10));
            foreach (var pending in batch)
                pending.TrySetResult(ResultFor(pending));
        }
    }

    private static OcrRequestBatcher NewBatcher(FakeDispatch fake, int cap = 4) =>
        new(fake.DispatchAsync, cap);

    private static async Task OrderPreservedAsync()
    {
        var fake = new FakeDispatch();
        fake.Gate.TrySetResult();
        await using var batcher = NewBatcher(fake);
        var tasks = Enumerable.Range(0, 5)
            .Select(index => batcher.SubmitAsync("img" + index, CancellationToken.None))
            .ToArray();
        var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
        for (var index = 0; index < 5; index++)
            Check(results[index].Text == "t-img" + index, $"kết quả map sai member {index}");
    }

    private static async Task FlagGroupingAsync()
    {
        var fake = new FakeDispatch();
        await using var batcher = NewBatcher(fake);
        var cancellations = new CancellationTokenSource();
        var taskA1 = batcher.SubmitAsync("a1", CancellationToken.None, recoverShortBlank: false);
        var taskB = batcher.SubmitAsync("b1", CancellationToken.None, recoverShortBlank: true);
        var taskA2 = batcher.SubmitAsync("a2", CancellationToken.None, recoverShortBlank: false);
        var taskActive = batcher.SubmitAsync("c1", CancellationToken.None, recoverShortBlank: false, activeShortText: "他");
        fake.Gate.TrySetResult();
        await Task.WhenAll(taskA1, taskB, taskA2, taskActive).WaitAsync(TimeSpan.FromSeconds(10));
        cancellations.Dispose();
        List<IReadOnlyList<OcrRequestBatcher.Pending>> batches;
        lock (fake.Batches) batches = fake.Batches.ToList();
        Check(batches.Count >= 2, "khác cờ phải tách batch");
        foreach (var batch in batches)
        {
            var head = batch[0];
            Check(batch.All(pending =>
                pending.RecoverShortBlank == head.RecoverShortBlank
                && string.Equals(pending.ActiveShortText, head.ActiveShortText, StringComparison.Ordinal)),
                "batch lẫn cờ khác nhau");
        }
        var flat = batches.SelectMany(batch => batch).ToList();
        Check(flat.Count == 4, "mất member khi tách cờ");
        Check((await taskB).Text == "t-b1", "member recover=true mất kết quả");
        Check((await taskActive).Text == "t-c1", "member activeText mất kết quả");
    }

    private static async Task CapRespectedAsync()
    {
        var fake = new FakeDispatch();
        await using var batcher = NewBatcher(fake, cap: 3);
        var tasks = Enumerable.Range(0, 7)
            .Select(index => batcher.SubmitAsync("k" + index, CancellationToken.None))
            .ToArray();
        fake.Gate.TrySetResult();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
        List<IReadOnlyList<OcrRequestBatcher.Pending>> batches;
        lock (fake.Batches) batches = fake.Batches.ToList();
        Check(batches.All(batch => batch.Count <= 3), "batch vượt cap");
        Check(batches.Sum(batch => batch.Count) == 7, "mất member khi cắt cap");
    }

    private static async Task ZeroWaitAsync()
    {
        var fake = new FakeDispatch();
        fake.Gate.TrySetResult();
        await using var batcher = NewBatcher(fake);
        // Alone it still runs immediately, without waiting for any mate.
        var result = await batcher.SubmitAsync("solo", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Check(result.Text == "t-solo", "single không mate phải vẫn chạy");
        await WaitForAsync(() =>
        {
            lock (fake.Batches) return fake.Batches.Count == 1 && fake.Batches[0].Count == 1;
        }, "zero-wait không dispatch batch 1");
    }

    private static async Task MemberCancelAsync()
    {
        var fake = new FakeDispatch();
        await using var batcher = NewBatcher(fake);
        using var doomed = new CancellationTokenSource();
        var taskA = batcher.SubmitAsync("ma", CancellationToken.None);
        var taskB = batcher.SubmitAsync("mb", doomed.Token);
        var taskC = batcher.SubmitAsync("mc", CancellationToken.None);
        doomed.Cancel();
        fake.Gate.TrySetResult();
        Check((await taskA.WaitAsync(TimeSpan.FromSeconds(10))).Text == "t-ma", "hủy B giết luôn A");
        Check((await taskC.WaitAsync(TimeSpan.FromSeconds(10))).Text == "t-mc", "hủy B giết luôn C");
        await WaitForCanceledAsync(taskB, "member hủy phải cancel");
    }

    private static async Task PreCancelledSubmitAsync()
    {
        var fake = new FakeDispatch();
        await using var batcher = NewBatcher(fake);
        using var dead = new CancellationTokenSource();
        dead.Cancel();
        var task = batcher.SubmitAsync("ghost", dead.Token);
        // A dead token registers the callback synchronously -> the task finishes at once, never enters the channel.
        Check(task.IsCompleted, "submit với token chết phải xong ngay đồng bộ");
        await WaitForCanceledAsync(task, "submit với token chết phải cancel");
        await Task.Delay(200);
        lock (fake.Batches) Check(fake.Batches.Count == 0, "request chết vẫn lọt vào dispatch");
    }

    private static async Task ExceptionMappingAsync()
    {
        // A dispatch error (thrown synchronously or a faulted task) must map the exact error to the
        // member, not hang — and the loop must survive for the next request.
        await ThrowsThenSurvivesAsync(syncThrow: true);
        await ThrowsThenSurvivesAsync(syncThrow: false);
    }

    private static async Task ThrowsThenSurvivesAsync(bool syncThrow)
    {
        var boom = new InvalidOperationException(syncThrow ? "boom-sync" : "boom-task");
        var failFirst = true;
        Task Dispatch(IReadOnlyList<OcrRequestBatcher.Pending> batch, CancellationToken token)
        {
            if (failFirst)
            {
                failFirst = false;
                if (syncThrow) throw boom;
                return Task.FromException(boom);
            }
            foreach (var pending in batch)
                pending.TrySetResult(new OcrResult(true, true, "t-" + pending.ImageBase64, 0.99, [], null));
            return Task.CompletedTask;
        }
        await using var batcher = new OcrRequestBatcher(Dispatch);
        // The first batch errors -> the member receives the exact error.
        try
        {
            await batcher.SubmitAsync("ea", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            throw new InvalidOperationException("dispatch lỗi mà member vẫn xong");
        }
        catch (InvalidOperationException error) when (error.Message.Contains("boom", StringComparison.Ordinal))
        {
            // Correct: the original error reaches the member.
        }
        // The same loop survives: the next request (next batch, after healing) runs normally.
        var again = await batcher.SubmitAsync("ec", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Check(again.Text == "t-ec", "loop chết sau dispatch lỗi");
    }

    private static async Task DisposeCompletesInflightAsync()
    {
        var fake = new FakeDispatch();
        var batcher = NewBatcher(fake);
        var taskA = batcher.SubmitAsync("da", CancellationToken.None);
        var taskB = batcher.SubmitAsync("db", CancellationToken.None);
        await WaitForAsync(() =>
        {
            lock (fake.Batches) return fake.Batches.Count >= 1;
        }, "dispatch không chạy để test dispose");
        fake.Gate.TrySetResult();
        await batcher.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Check((await taskA).Text == "t-da", "dispose làm mất kết quả inflight");
        Check((await taskB).Text == "t-db", "dispose làm mất kết quả inflight");
    }

    private static async Task SubmitAfterDisposeFailsAsync()
    {
        var fake = new FakeDispatch();
        fake.Gate.TrySetResult();
        var batcher = NewBatcher(fake);
        await batcher.DisposeAsync();
        try
        {
            await batcher.SubmitAsync("late", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            throw new InvalidOperationException("submit sau dispose vẫn nhận");
        }
        catch (InvalidOperationException error) when (error.Message.Contains("đã dừng", StringComparison.Ordinal))
        {
            // Correct: an explicit refusal instead of hanging the lane.
        }
    }

    private static async Task OnPerfPassthroughAsync()
    {
        // The batcher must not touch OnPerf (fan-out sample splitting is the job of the production
        // dispatch; here the callback is simply passed through to the dispatch).
        var fake = new FakeDispatch();
        fake.Gate.TrySetResult();
        await using var batcher = NewBatcher(fake);
        var perfCalls = 0;
        var result = await batcher.SubmitAsync("p1", CancellationToken.None, onPerf: _ => Interlocked.Increment(ref perfCalls))
            .WaitAsync(TimeSpan.FromSeconds(10));
        Check(result.Text == "t-p1", "passthrough làm mất kết quả");
        Check(Volatile.Read(ref perfCalls) == 0, "batcher tự gọi OnPerf (việc của dispatch)");
        List<IReadOnlyList<OcrRequestBatcher.Pending>> batches;
        lock (fake.Batches) batches = fake.Batches.ToList();
        Check(batches.Count == 1 && batches[0][0].OnPerf is not null, "OnPerf không tới dispatch");
    }

    private static async Task CheckCtorGuardsAsync()
    {
        foreach (var badCap in new[] { 0, -1, 5, 9, 64 })
        {
            try
            {
                await using var rejected = new OcrRequestBatcher((_, _) => Task.CompletedTask, badCap);
                throw new InvalidOperationException($"cap {badCap} vẫn lọt");
            }
            catch (ArgumentOutOfRangeException) { }
        }
        await using (var ok = new OcrRequestBatcher((_, _) => Task.CompletedTask, 1)) { }
        try
        {
            await using var batcher = new OcrRequestBatcher((_, _) => Task.CompletedTask);
            _ = batcher.SubmitAsync("  ", CancellationToken.None);
            throw new InvalidOperationException("ảnh rỗng vẫn lọt");
        }
        catch (ArgumentException) { }
    }

    private static async Task BusyHoldsForMatesAsync()
    {
        // The single worker is busy (the first dispatch blocks on the Gate): the 3 later-submitted
        // mates must coalesce into exactly 1 batch after the hold — no wall-clock assertion,
        // only a batch structure assertion (deterministic).
        var fake = new FakeDispatch();
        await using var batcher = new OcrRequestBatcher(
            fake.DispatchAsync, 4, () => 1, TimeSpan.FromMilliseconds(100));
        var first = batcher.SubmitAsync("h1", CancellationToken.None);
        await WaitForAsync(() =>
        {
            lock (fake.Batches) return fake.Batches.Count == 1;
        }, "dispatch đầu chưa chạy");
        var rest = Enumerable.Range(2, 3)
            .Select(index => batcher.SubmitAsync("h" + index, CancellationToken.None))
            .ToArray();
        await WaitForAsync(() =>
        {
            lock (fake.Batches) return fake.Batches.Count == 2;
        }, "hold không gom batch khi busy");
        List<IReadOnlyList<OcrRequestBatcher.Pending>> batches;
        lock (fake.Batches) batches = fake.Batches.ToList();
        Check(batches[1].Count == 3, $"busy-hold gom sai (got {batches[1].Count}, want 3)");
        Check(batches.SelectMany(batch => batch).Count() == 4, "mất member khi hold");
        fake.Gate.TrySetResult();
        await Task.WhenAll(new[] { first }.Concat(rest)).WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task IdleIgnoresHoldAsync()
    {
        // Worker idle (dispatch finishes instantly): a submit must finish immediately and must not
        // wait out the hold window. Hold 1000ms, margin 800ms: correct is ~ms, wrong is >=1000ms.
        var fake = new FakeDispatch();
        fake.Gate.TrySetResult();
        await using var batcher = new OcrRequestBatcher(
            fake.DispatchAsync, 4, () => 2, TimeSpan.FromMilliseconds(1000));
        var watch = Stopwatch.StartNew();
        var result = await batcher.SubmitAsync("solo", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        watch.Stop();
        Check(result.Text == "t-solo", "idle submit mất kết quả");
        Check(watch.Elapsed < TimeSpan.FromMilliseconds(800), $"idle mà hold là sai luật rảnh-chạy-ngay ({watch.ElapsedMilliseconds}ms)");
    }

    private static async Task WaitForAsync(Func<bool> condition, string message, int timeoutMs = 5000)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > timeoutMs)
                throw new InvalidOperationException("timeout: " + message);
            await Task.Delay(15);
        }
    }

    private static async Task WaitForCanceledAsync(Task<OcrResult> task, string message)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (OperationCanceledException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
