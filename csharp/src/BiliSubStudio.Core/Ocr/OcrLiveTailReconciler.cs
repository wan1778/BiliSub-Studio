namespace BiliSubStudio.Core.Ocr;

// Live incremental reconcile cho preview trong lúc scan.
//
// Tracker commit cue mới -> AppendCommitted -> background loop reconcile CHỈ tail
// (ReconcileLiveTailAsync, pixel-gated) -> CommittedView thay tail ngay, không đợi Pause.
// - Serialize: một worker loop duy nhất; revision counter; kết quả stale (revision cũ hơn
//   hiện tại) bị discard, không overwrite tail mới hơn.
// - Không mutate raw tracker state: chỉ đọc snapshot committed cues.
// - Active/provisional KHÔNG vào CommittedView; UI ghép riêng qua ViewWithActive để hiển thị.
// - Lỗi pixel/worker trong loop chỉ báo qua onError (job log), không bao giờ giết scan.
public sealed class OcrLiveTailReconciler : IDisposable
{
    // Số committed cue cuối giữ lại để reconcile mỗi vòng, + chain-extend ngược (cap 64).
    public const int TailWindow = 8;

    private readonly object _gate = new();
    private readonly List<OcrCue> _committed = [];
    // Instance đã đưa vào _view (reference equality: out-of-order multi-lane arrival có thể
    // insert trước vùng đã cover; đếm số lượng sẽ sai fresh set).
    private readonly HashSet<OcrCue> _covered = new(ReferenceEqualityComparer.Instance);
    private List<OcrCue> _view = [];
    private long _revision;
    private long _appliedRevision = -1;
    private readonly SemaphoreSlim _signal = new(0, 1);
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private string _videoPath = string.Empty;
    private IPixelComparer? _comparer;
    private double _continuationWindow = 0.001;
    private Action? _onUpdated;
    private Action<Exception>? _onError;
    private bool _disposed;

    public long Revision { get { lock (_gate) return _revision; } }
    public long AppliedRevision { get { lock (_gate) return _appliedRevision; } }

    // Authoritative committed view (đã reconcile tới AppliedRevision). Copy để UI render.
    // LƯU Ý PERF (lag cuối scan 15/9, pause tại 24:27 với ~9000 cue): CommittedView copy
    // toàn bộ _view mỗi lần gọi. PublishTelemetry chạy 8 lane × mỗi 500ms = 16 copy/s
    // của cả lịch sử -> càng về cuối càng đơ (nhảy 2-5s/frame). Đường telemetry PHẢI
    // dùng GetTailBelow (chỉ lấy đuôi dưới frontier, 1 lần lock, không copy full).
    public IReadOnlyList<OcrCue> CommittedView { get { lock (_gate) return _view.ToArray(); } }

    // Tail-only snapshot cho telemetry: lấy tối đa `limit` cue có Start <= frontier,
    // duyệt ngược từ cuối (đã sort) nên cost hằng số, không tăng theo độ dài scan.
    // Kết quả Y HỆT vòng lặp tail cũ trong PublishTelemetry, chỉ khác là không copy full.
    public List<OcrCue> GetTailBelow(double frontier, int limit)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var tail = new List<OcrCue>(Math.Min(limit, _view.Count));
            for (var index = _view.Count - 1; index >= 0 && tail.Count < limit; index--)
            {
                var cue = _view[index];
                if (cue.Start <= frontier + .001) tail.Add(cue);
            }
            tail.Reverse();
            return tail;
        }
    }

    // Ghép active provisional để hiển thị; active KHÔNG thành committed, không vào _view.
    public IReadOnlyList<OcrCue> ViewWithActive(OcrCue? active)
    {
        var view = CommittedView;
        if (active is null) return view;
        return view.Concat([active]).OrderBy(cue => cue.Start).ThenBy(cue => cue.End).ToArray();
    }

    public void AppendCommitted(IEnumerable<OcrCue> cues)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var batch = cues as IList<OcrCue> ?? cues.ToList();
            if (batch.Count == 0) return;
            // PERF (lag cuối scan): _committed luôn giữ sorted. Trường hợp thường gặp là
            // cue mới tới đúng thứ tự (nối tiếp tail) -> chỉ sort batch nhỏ rồi nối đuôi,
            // thay vì sort lại toàn bộ history mỗi lần commit. Chỉ sort full khi batch tới
            // out-of-order (multi-lane arrival muộn). Kết quả cuối Y HỆT như trước.
            var ordered = batch.OrderBy(cue => cue.Start).ThenBy(cue => cue.End).ToList();
            var inOrder = _committed.Count == 0
                || CompareCue(ordered[0], _committed[^1]) >= 0;
            if (inOrder) _committed.AddRange(ordered);
            else if (ordered.Count * 32 < _committed.Count)
            {
                // PERF (lag cuối scan): batch muộn nhỏ (multi-lane arrival) chèn nhị phân
                // từng cue, O(m log n) thay vì sort lại toàn bộ O(n log n) mỗi lần commit.
                // Kết quả Y HỆT sort full (cùng CompareCue).
                foreach (var cue in ordered)
                {
                    var at = _committed.BinarySearch(cue, Comparer<OcrCue>.Create(CompareCue));
                    _committed.Insert(at < 0 ? ~at : at, cue);
                }
            }
            else
            {
                _committed.AddRange(ordered);
                _committed.Sort(CompareCue);
            }
            _revision++;
        }
        Pulse();
    }

    private static int CompareCue(OcrCue a, OcrCue b)
    {
        var byStart = a.Start.CompareTo(b.Start);
        return byStart != 0 ? byStart : a.End.CompareTo(b.End);
    }

    public void ResetCommitted(IEnumerable<OcrCue> cues)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _committed.Clear();
            _committed.AddRange(cues);
            _committed.Sort(CompareCue);
            // Resume/lane-recovery path: expose the restored history immediately
            // (same normalization as the sync path, no pixel work) instead of
            // re-running pixel reconciliation over the whole bulk, which stalls
            // live telemetry until thousands of FFmpeg comparisons finish and
            // leaves the UI list blank. New appends still get incremental
            // pixel-gated tail reconciliation through the normal loop.
            _view = OcrTextCleanup.Cleanup(OcrCueReconciler.MergeTouchingIdentical(_committed)).Cues.ToList();
            _covered.Clear();
            foreach (var cue in _committed) _covered.Add(cue);
            _revision++;
            _appliedRevision = _revision;
        }
        Pulse();
    }

    public void Start(string videoPath, IPixelComparer comparer, double continuationWindow, Action? onUpdated = null, Action<Exception>? onError = null, CancellationToken linkedToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_worker is not null) throw new InvalidOperationException("Live tail reconciler đã chạy.");
            _videoPath = videoPath;
            _comparer = comparer;
            _continuationWindow = continuationWindow;
            _onUpdated = onUpdated;
            _onError = onError;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(linkedToken);
            _revision++;
            _worker = Task.Run(LoopAsync);
        }
        Pulse();
    }

    public async Task StopAsync()
    {
        Task? worker;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            worker = _worker;
            cts = _cts;
            _worker = null;
        }
        if (worker is null) return;
        try { cts?.Cancel(); }
        catch (ObjectDisposedException) { }
        Pulse();
        try { await worker.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception error) { Report(error); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        Pulse();
        try { _worker?.GetAwaiter().GetResult(); }
        catch { }
        _cts?.Dispose();
        _signal.Dispose();
    }

    private void Pulse()
    {
        try
        {
            lock (_gate)
            {
                if (_disposed) return;
                if (_signal.CurrentCount == 0) _signal.Release();
            }
        }
        catch (ObjectDisposedException) { }
        catch (SemaphoreFullException) { }
    }

    private void Report(Exception error)
    {
        Action<Exception>? handler;
        lock (_gate) handler = _onError;
        try { handler?.Invoke(error); }
        catch { }
    }

    private async Task LoopAsync()
    {
        CancellationTokenSource? cts;
        lock (_gate) cts = _cts;
        var token = cts?.Token ?? CancellationToken.None;
        while (true)
        {
            try { await _signal.WaitAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            List<OcrCue> prefix;
            List<OcrCue> fresh;
            long revision;
            string videoPath;
            IPixelComparer? comparer;
            double window;
            lock (_gate)
            {
                if (_disposed) return;
                if (token.IsCancellationRequested) return;
                if (_revision <= _appliedRevision) continue;
                prefix = [.. _view];
                fresh = _committed.Where(cue => !_covered.Contains(cue)).ToList();
                revision = _revision;
                videoPath = _videoPath;
                comparer = _comparer;
                window = _continuationWindow;
            }
            if (comparer is null || string.IsNullOrWhiteSpace(videoPath))
            {
                lock (_gate)
                {
                    if (revision > _appliedRevision && revision >= _revision)
                    {
                        foreach (var cue in fresh) _covered.Add(cue);
                        _view = prefix.Concat(fresh).OrderBy(cue => cue.Start).ThenBy(cue => cue.End).ToList();
                        _appliedRevision = revision;
                    }
                }
                NotifyUpdated();
                continue;
            }
            IReadOnlyList<OcrCue> reconciled;
            try
            {
                reconciled = await OcrCueReconciler.ReconcileLiveTailAsync(
                    prefix, fresh, TailWindow, videoPath, comparer, token, window).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception error)
            {
                Report(error);
                continue;
            }
            lock (_gate)
            {
                // Stale: tail mới đã tới trong lúc reconcile -> discard, loop sẽ chạy lại với mới nhất.
                if (revision < _revision) continue;
                foreach (var cue in fresh) _covered.Add(cue);
                _view = reconciled.ToList();
                _appliedRevision = revision;
            }
            NotifyUpdated();
        }
    }

    private void NotifyUpdated()
    {
        Action? handler;
        lock (_gate) handler = _onUpdated;
        try { handler?.Invoke(); }
        catch { }
    }
}
