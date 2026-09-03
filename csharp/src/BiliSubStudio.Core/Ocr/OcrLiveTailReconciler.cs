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
    private double _exactWindow = 0.001;
    private Action? _onUpdated;
    private Action<Exception>? _onError;
    private bool _disposed;

    public long Revision { get { lock (_gate) return _revision; } }
    public long AppliedRevision { get { lock (_gate) return _appliedRevision; } }

    // Authoritative committed view (đã reconcile tới AppliedRevision). Copy để UI render.
    public IReadOnlyList<OcrCue> CommittedView { get { lock (_gate) return _view.ToArray(); } }

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
            var added = false;
            foreach (var cue in cues)
            {
                _committed.Add(cue);
                added = true;
            }
            if (!added) return;
            _committed.Sort(static (a, b) =>
            {
                var byStart = a.Start.CompareTo(b.Start);
                return byStart != 0 ? byStart : a.End.CompareTo(b.End);
            });
            _revision++;
        }
        Pulse();
    }

    public void ResetCommitted(IEnumerable<OcrCue> cues)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _committed.Clear();
            _committed.AddRange(cues);
            _committed.Sort(static (a, b) =>
            {
                var byStart = a.Start.CompareTo(b.Start);
                return byStart != 0 ? byStart : a.End.CompareTo(b.End);
            });
            _covered.Clear();
            _view = [];
            _revision++;
        }
        Pulse();
    }

    public void Start(string videoPath, IPixelComparer comparer, double exactIdenticalMaxGap, Action? onUpdated = null, Action<Exception>? onError = null, CancellationToken linkedToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_worker is not null) throw new InvalidOperationException("Live tail reconciler đã chạy.");
            _videoPath = videoPath;
            _comparer = comparer;
            _exactWindow = exactIdenticalMaxGap;
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
                window = _exactWindow;
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
