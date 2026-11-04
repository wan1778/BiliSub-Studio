namespace BiliSubStudio.Core.Ocr;

// Live incremental reconcile for the preview during a scan.
//
// Tracker commits a new cue -> AppendCommitted -> background loop reconciles ONLY the tail
// (ReconcileLiveTailAsync, pixel-gated) -> CommittedView replaces the tail immediately, without waiting for Pause.
// - Serialize: a single worker loop; revision counter; stale results (a revision older than
//   current) are discarded and never overwrite a newer tail.
// - Does not mutate raw tracker state: only reads the committed cue snapshot.
// - Active/provisional never enter CommittedView; the UI merges them separately via ViewWithActive for display.
// - Pixel/worker errors in the loop are only reported via onError (job log), never kill a scan.
public sealed class OcrLiveTailReconciler : IDisposable
{
    // The number of last committed cues retained for each round of reconcile, + backward chain-extend (cap 64).
    public const int TailWindow = 8;

    private readonly object _gate = new();
    private readonly List<OcrCue> _committed = [];
    // Instances already brought into _view (reference equality: out-of-order multi-lane arrival may
    // insert before the covered region; counting quantities would get the fresh set wrong).
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
    private int _resourcesDisposed;

    public long Revision { get { lock (_gate) return _revision; } }
    public long AppliedRevision { get { lock (_gate) return _appliedRevision; } }

    // Authoritative committed view (reconciled up to AppliedRevision). Copied for the UI to render.
    // PERF NOTE (end-of-scan lag 15/9, pause at 24:27 with ~9000 cues): CommittedView copies
    // the entire _view on every call. PublishTelemetry runs 8 lanes × every 500ms = 16 copies/s
    // of the whole history -> the later it gets the stiffer it becomes (jumping 2-5s/frame). The telemetry path MUST
    // use GetTailBelow (only take the tail below the frontier, 1 lock, no full copy).
    public IReadOnlyList<OcrCue> CommittedView { get { lock (_gate) return _view.ToArray(); } }

    // Tail-only snapshot for telemetry: take at most `limit` cues with Start <= frontier,
    // iterating backward from the end (already sorted) so the cost is constant and does not grow with scan length.
    // The result is EXACTLY the old tail loop in PublishTelemetry, just without the full copy.
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

    // Merges the provisional active for display; active does NOT become committed and does not enter _view.
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
            // PERF (end-of-scan lag): _committed always stays sorted. The common case is
            // cues arriving in the right order (continuing the tail) -> only sort the small batch then append to the tail,
            // rather than re-sorting the whole history on every commit. Full sort only when the batch arrives
            // out-of-order (late multi-lane arrival). The final result is EXACTLY as before.
            var ordered = batch.OrderBy(cue => cue.Start).ThenBy(cue => cue.End).ToList();
            var inOrder = _committed.Count == 0
                || CompareCue(ordered[0], _committed[^1]) >= 0;
            if (inOrder) _committed.AddRange(ordered);
            else if (ordered.Count * 32 < _committed.Count)
            {
                // PERF (end-of-scan lag): a small late batch (multi-lane arrival) binary-inserts
                // each cue, O(m log n) instead of re-sorting the whole thing O(n log n) on every commit.
                // Result EXACTLY like a full sort (same CompareCue).
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
        Task? worker;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            worker = _worker;
            _worker = null;
        }
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        if (worker is null)
        {
            DisposeResources();
            return;
        }
        // Never block the UI thread on a reconcile that may be waiting inside
        // FFmpeg/decoder code. Cancel now, release handles when the worker ends.
        _ = worker.ContinueWith(completed =>
        {
            if (completed.Exception is { } error) Report(error);
            DisposeResources();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void DisposeResources()
    {
        if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0) return;
        try { _cts?.Dispose(); }
        catch (ObjectDisposedException) { }
        try { _signal.Dispose(); }
        catch (ObjectDisposedException) { }
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
                // Stale: a newer tail arrived while reconciling -> discard, the loop will run again with the newest.
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
