using System.Threading.Channels;

namespace BiliSubStudio.Core.Ocr;

// Vét batch (fill-4 trên worker đang chạy): gộp các request OCR đơn lẻ đang CHỜ SẴN
// thành 1 batch worker.
// Quy tắc thép:
// - ZERO-WAIT: không bao giờ đợi đủ mate. Worker rảnh + có >=1 pending là chạy ngay.
// - Cùng cờ mới gộp (recoverShortBlank + activeShortText): khác cờ là khác batch,
//   vì worker áp 1 bộ cờ cho cả batch — gộp sai cờ là đổi kết quả.
// - GIỚI HẠN 4 (parity 15/9 bằng worker thật đang chạy): worker 27/8 từ chối batch >4
//   ("phải là batch 1-4 ảnh", đã đo), dù protocol repo mới cho 8. Vượt là lỗi batcher.
// - Thứ tự: batch giữ đúng thứ tự submit; kết quả map theo index (worker giữ order).
// - Hủy từng phần: member nào hủy thì TCS đó cancel, batch vẫn chạy cho số còn lại.
// - Fail-safe: dispatch ném lỗi -> mọi member chưa xong nhận đúng lỗi đó, không treo.
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

    // Busy-coalesce hold (26/09): mỗi lượt gọi tốn ~380ms overhead cố định
    // trong khi compute chỉ ~40-140ms — batch đầy 4 rẻ gấp ~3x mỗi ảnh. Giữ
    // luật rảnh-chạy-ngay: chỉ hold khi mọi worker đều đang bận (không worker
    // nào rảnh thì chờ thêm cũng không mất gì). workerCountProvider null nghĩa
    // là không biết pool (đường cũ/test cũ): hành vi y hệt zero-wait trước đây.
    internal static readonly TimeSpan CoalesceHold = TimeSpan.FromMilliseconds(15);

    // dispatch: nhận 1 batch (1..cap, cùng cờ, đúng thứ tự) và PHẢI hoàn thành
    // (result/exception/canceled) mọi member khi xong. Ném lỗi -> batcher tự
    // gán lỗi cho member còn treo (lưới an toàn, không treo lane).
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
                // Spill trước (không đói): batch toàn item tồn cùng cờ.
                if (_spill.Count > 0)
                {
                    Track(DispatchSafeAsync(TakeSpillBatch()));
                    continue;
                }
                // Zero-wait khi rảnh: chỉ block khi KHÔNG có gì chờ. Có là vét
                // ngay, không đợi mate — trừ khi mọi worker đều bận (lúc đó
                // chờ thêm tối đa hold window để gom batch đầy, không mất gì).
                if (!await _pending.Reader.WaitToReadAsync(_stop.Token)) break;
                if (!_pending.Reader.TryRead(out var head) || head is null) continue;
                if (HoldApplies())
                {
                    try { await Task.Delay(_holdWindow, _stop.Token); }
                    catch (OperationCanceledException)
                    {
                        // Head đã lấy ra khỏi channel mà chưa dispatch: settle
                        // ngay như Dispose làm với leftovers, không treo lane.
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

    // Mọi worker đều đang giữ 1 dispatch chưa xong thì hold thêm mate là miễn
    // phí (không worker rảnh nào bị bắt đợi). Ngược lại chạy ngay như cũ.
    // Provider lỗi/trả <= 0 là fail-open về zero-wait (không bao giờ hold mù).
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

    // Lấy batch từ spill: head = item tồn lâu nhất + mate cùng cờ (giữ thứ tự).
    // Không tương thích thì ở lại spill chờ vòng sau — nhưng vòng nào cũng xả spill
    // trước channel nên không đói (xấu nhất: đi một mình như hôm nay).
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
        // Dispatch TỰ hoàn thành member. Ném lỗi thoát ra là bug của dispatch:
        // production dispatch có catch-all hoàn thành member; contract test bắt
        // dispatch fake hoàn thành đủ. Không treo lane vì DisposeAsync cancel hết tồn.
    }

    // Lưới chống dispatch ném đồng bộ (giết loop -> treo mọi submit sau):
    // lỗi nào cũng gán cho member batch đó, loop sống tiếp.
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

    // Chỉ loop thread đụng _spill; Dispose đọc sau khi loop xong nên không lock.
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
