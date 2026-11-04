using System.ComponentModel;

namespace BiliSubStudio.Core.Ocr;

// State of an item in the OCR wait queue (09/10: scans many videos back to back,
// each video has its own setup, errors are skipped and scanning continues, lives only within the app session).
public enum OcrQueueItemStatus
{
    Waiting,
    Scanning,
    Paused,
    Done,
    Failed,
    Cancelled,
}

public sealed class OcrQueueItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid Id { get; } = Guid.NewGuid();

    public string VideoPath { get; }

    public OcrRegion Region { get; }

    public string Mode { get; }

    public string Device { get; }

    public string Parallelism { get; }

    public double Duration { get; set; }

    private OcrQueueItemStatus _status = OcrQueueItemStatus.Waiting;

    public OcrQueueItemStatus Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(StatusText));
        }
    }

    // Ordinal in the queue (1-based), the queue compacts/re-resets whenever a video
    // enters/leaves — the user sees the processing position. Only OcrScanQueue assigns this.
    private int _queueNumber;

    public int QueueNumber
    {
        get => _queueNumber;
        internal set
        {
            if (_queueNumber == value) return;
            _queueNumber = value;
            OnPropertyChanged(nameof(QueueNumber));
            OnPropertyChanged(nameof(NumberedName));
        }
    }

    private double _progress;

    public double Progress
    {
        get => _progress;
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            if (Math.Abs(_progress - clamped) < 0.05) return;
            _progress = clamped;
            OnPropertyChanged(nameof(Progress));
        }
    }

    private string _message = string.Empty;

    public string Message
    {
        get => _message;
        set
        {
            if (string.Equals(_message, value, StringComparison.Ordinal)) return;
            _message = value;
            OnPropertyChanged(nameof(Message));
        }
    }

    private string? _outputSrtPath;

    public string? OutputSrtPath
    {
        get => _outputSrtPath;
        set
        {
            if (string.Equals(_outputSrtPath, value, StringComparison.Ordinal)) return;
            _outputSrtPath = value;
            OnPropertyChanged(nameof(OutputSrtPath));
        }
    }

    public OcrQueueItem(
        string VideoPath,
        OcrRegion Region,
        string Mode,
        string Device,
        string Parallelism,
        double Duration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(VideoPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(Mode);
        ArgumentException.ThrowIfNullOrWhiteSpace(Device);
        ArgumentException.ThrowIfNullOrWhiteSpace(Parallelism);
        this.VideoPath = VideoPath;
        this.Region = Region;
        this.Mode = Mode;
        this.Device = Device;
        this.Parallelism = Parallelism;
        this.Duration = Duration;
    }

    public string DisplayName => Path.GetFileName(VideoPath);

    public string NumberedName => $"{QueueNumber}. {DisplayName}";

    public string StatusText => Status switch
    {
        OcrQueueItemStatus.Waiting => "Đang chờ",
        OcrQueueItemStatus.Scanning => $"Đang quét {Progress:0}%",
        OcrQueueItemStatus.Paused => "Tạm dừng",
        OcrQueueItemStatus.Done => "Xong",
        OcrQueueItemStatus.Failed => "Lỗi (đã bỏ qua)",
        OcrQueueItemStatus.Cancelled => "Đã hủy",
        _ => Status.ToString(),
    };

    public string SetupSummary =>
        $"{Mode} · {Device} · {Parallelism} luồng · ROI {Region.X:0.00},{Region.Y:0.00},{Region.Width:0.00},{Region.Height:0.00}";

    public OcrScanRequest ToRequest() => new(VideoPath, Region, Mode, Device, Parallelism, 1, Duration);

    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// FIFO wait queue runs sequentially, one scan at a time (matching the
// _activeProcesses guard of OcrScanner). A failed item does not block the queue: the driver flags
// Failed then takes the next Waiting item. Single-threaded (owned by the UI thread).
public sealed class OcrScanQueue
{
    private readonly List<OcrQueueItem> _items = new();

    public event Action? Changed;

    public IReadOnlyList<OcrQueueItem> Items => _items;

    public int WaitingCount
    {
        get
        {
            var count = 0;
            foreach (var item in _items)
                if (item.Status == OcrQueueItemStatus.Waiting) count++;
            return count;
        }
    }

    public OcrQueueItem? ActiveItem
    {
        get
        {
            foreach (var item in _items)
                if (item.Status == OcrQueueItemStatus.Scanning) return item;
            return null;
        }
    }

    public OcrQueueItem Enqueue(
        string videoPath,
        OcrRegion region,
        string mode,
        string device,
        string parallelism,
        double duration)
    {
        var item = new OcrQueueItem(videoPath, region, mode, device, parallelism, duration);
        _items.Add(item);
        Renumber();
        Changed?.Invoke();
        return item;
    }

    public bool Remove(Guid id)
    {
        var index = _items.FindIndex(item => item.Id == id);
        if (index < 0) return false;
        if (_items[index].Status == OcrQueueItemStatus.Scanning) return false;
        _items.RemoveAt(index);
        Renumber();
        Changed?.Invoke();
        return true;
    }

    public bool MoveUp(Guid id) => Move(id, -1);

    public bool MoveDown(Guid id) => Move(id, +1);

    private bool Move(Guid id, int direction)
    {
        var index = _items.FindIndex(item => item.Id == id);
        if (index < 0) return false;
        var other = index + direction;
        if (other < 0 || other >= _items.Count) return false;
        if (_items[index].Status != OcrQueueItemStatus.Waiting
            || _items[other].Status != OcrQueueItemStatus.Waiting) return false;
        (_items[index], _items[other]) = (_items[other], _items[index]);
        Renumber();
        Changed?.Invoke();
        return true;
    }

    // Take the first Waiting item (Paused items resume first) and mark
    // Scanning. Returns null when an item is already Scanning or there is no work.
    public OcrQueueItem? TakeNext()
    {
        if (ActiveItem is not null) return null;
        foreach (var item in _items)
            if (item.Status == OcrQueueItemStatus.Paused)
            {
                item.Status = OcrQueueItemStatus.Scanning;
                Changed?.Invoke();
                return item;
            }
        foreach (var item in _items)
            if (item.Status == OcrQueueItemStatus.Waiting)
            {
                item.Status = OcrQueueItemStatus.Scanning;
                item.Progress = 0;
                Changed?.Invoke();
                return item;
            }
        return null;
    }

    // A finished video leaves the queue immediately (user rule: once done it disappears, the rest
    // shift up, an empty queue resets to 1). The driver keeps path/message from the scan result
    // for reporting, no need for the item to stay.
    public void MarkDone(Guid id, string? outputSrtPath, string message)
    {
        var index = _items.FindIndex(item => item.Id == id);
        if (index < 0 || _items[index].Status != OcrQueueItemStatus.Scanning) return;
        _items.RemoveAt(index);
        Renumber();
        Changed?.Invoke();
    }

    public void MarkFailed(Guid id, string error)
    {
        var item = Find(id);
        if (item is null || item.Status != OcrQueueItemStatus.Scanning) return;
        item.Message = error;
        item.Status = OcrQueueItemStatus.Failed;
        Changed?.Invoke();
    }

    public void MarkCancelled(Guid id, string message)
    {
        var item = Find(id);
        if (item is null || item.Status != OcrQueueItemStatus.Scanning) return;
        item.Message = message;
        item.Status = OcrQueueItemStatus.Cancelled;
        Changed?.Invoke();
    }

    public void MarkPaused(Guid id)
    {
        var item = Find(id);
        if (item is null || item.Status != OcrQueueItemStatus.Scanning) return;
        item.Status = OcrQueueItemStatus.Paused;
        Changed?.Invoke();
    }

    // Deadlock pin (an item still Scanning but no OCR job is alive anymore, e.g. stuck
    // mid-run): move it back to Paused so TakeNext can resume it, instead of the whole queue
    // standing silently frozen. The caller must verify no live OCR job remains first.
    public bool RequeueStuck(Guid id)
    {
        var item = Find(id);
        if (item is null || item.Status != OcrQueueItemStatus.Scanning) return false;
        item.Status = OcrQueueItemStatus.Paused;
        Changed?.Invoke();
        return true;
    }

    public int ClearFinished()
    {
        // Done already left the queue in MarkDone; this cleans up leftover failed/cancelled items.
        var removed = _items.RemoveAll(item =>
            item.Status is OcrQueueItemStatus.Failed
                or OcrQueueItemStatus.Cancelled);
        if (removed > 0)
        {
            Renumber();
            Changed?.Invoke();
        }
        return removed;
    }

    // Re-renumber 1..N in the exact order of the current queue.
    private void Renumber()
    {
        for (var index = 0; index < _items.Count; index++)
            _items[index].QueueNumber = index + 1;
    }

    private OcrQueueItem? Find(Guid id)
    {
        foreach (var item in _items)
            if (item.Id == id) return item;
        return null;
    }
}
