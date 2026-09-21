using System.Diagnostics;
using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

// Vét batch (fill-8) logic thuần túy với dispatch fake — không worker, không GPU.
// Bất biến bắt buộc (lane sống còn phụ thuộc):
// - zero-wait: có pending là chạy, không đợi mate;
// - cùng cờ mới gộp (recover/activeText), khác cờ không bao giờ chung batch;
// - thứ tự submit giữ nguyên trong batch, kết quả map đúng member;
// - hủy từng phần không giết member còn lại; dispose không treo, không mất member.
internal static class OcrBatchCoalesceContract
{
    public static async Task RunAsync()
    {
        // Fail-safe tốc độ: cap theo worker đang chạy (parity 15/9: text/box giống hệt
        // 8/8, nhanh 1.53x; worker từ chối batch >4).
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
        // Một mình vẫn chạy ngay, không đợi mate nào.
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
        // Token chết đăng ký callback đồng bộ -> task xong ngay, không vào channel.
        Check(task.IsCompleted, "submit với token chết phải xong ngay đồng bộ");
        await WaitForCanceledAsync(task, "submit với token chết phải cancel");
        await Task.Delay(200);
        lock (fake.Batches) Check(fake.Batches.Count == 0, "request chết vẫn lọt vào dispatch");
    }

    private static async Task ExceptionMappingAsync()
    {
        // Dispatch lỗi (ném đồng bộ hay faulted-task) phải gán đúng lỗi cho member,
        // không treo — và loop phải sống tiếp cho request sau.
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
        // Batch đầu lỗi -> member nhận đúng lỗi.
        try
        {
            await batcher.SubmitAsync("ea", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            throw new InvalidOperationException("dispatch lỗi mà member vẫn xong");
        }
        catch (InvalidOperationException error) when (error.Message.Contains("boom", StringComparison.Ordinal))
        {
            // Đúng: lỗi gốc tới tay member.
        }
        // Cùng loop sống tiếp: request sau (batch sau, heal rồi) chạy bình thường.
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
            // Đúng: từ chối rõ ràng thay vì treo lane.
        }
    }

    private static async Task OnPerfPassthroughAsync()
    {
        // Batcher không được đụng OnPerf (fan-out chia sample là việc của production
        // dispatch; ở đây chỉ giữ nguyên callback tới tay dispatch).
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
