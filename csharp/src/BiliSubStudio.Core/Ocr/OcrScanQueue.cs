using System.ComponentModel;

namespace BiliSubStudio.Core.Ocr;

// Trạng thái mục trong hàng chờ OCR (09/10: quét nhiều video nối tiếp,
// mỗi video setup riêng, lỗi bỏ qua chạy tiếp, chỉ sống trong phiên app).
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

// Hàng chờ FIFO chạy tuần tự, một scan tại một thời điểm (khớp guard
// _activeProcesses của OcrScanner). Mục lỗi không chặn hàng: driver đánh
// Failed rồi lấy mục Waiting kế tiếp. Single-thread (UI thread) sở hữu.
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
        Changed?.Invoke();
        return item;
    }

    public bool Remove(Guid id)
    {
        var index = _items.FindIndex(item => item.Id == id);
        if (index < 0) return false;
        if (_items[index].Status == OcrQueueItemStatus.Scanning) return false;
        _items.RemoveAt(index);
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
        Changed?.Invoke();
        return true;
    }

    // Lấy mục Waiting đầu tiên (ưu tiên mục Paused resume trước) và đánh
    // Scanning. Trả null khi đang có mục Scanning hoặc hết việc.
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

    public void MarkDone(Guid id, string? outputSrtPath, string message)
    {
        var item = Find(id);
        if (item is null || item.Status != OcrQueueItemStatus.Scanning) return;
        item.OutputSrtPath = outputSrtPath;
        item.Progress = 100;
        item.Message = message;
        item.Status = OcrQueueItemStatus.Done;
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

    public int ClearFinished()
    {
        var removed = _items.RemoveAll(item =>
            item.Status is OcrQueueItemStatus.Done
                or OcrQueueItemStatus.Failed
                or OcrQueueItemStatus.Cancelled);
        if (removed > 0) Changed?.Invoke();
        return removed;
    }

    private OcrQueueItem? Find(Guid id)
    {
        foreach (var item in _items)
            if (item.Id == id) return item;
        return null;
    }
}
