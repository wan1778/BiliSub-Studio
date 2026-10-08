using BiliSubStudio.Core.Configuration;

namespace BiliSubStudio.Core.Application;

public sealed class StorageUsageReader : IStorageUsageReader
{
    // Đổi thư mục lưu (và mọi Set* settings) đều đợi BuildSnapshotAsync, mà
    // snapshot nào cũng quét ĐỆ QUY toàn bộ Data/Tools/Ocr/Temp/Cache — gồm
    // venv Paddle + model hàng chục nghìn file. Ổ bận là UI treo 3-5 phút mới
    // thấy path đổi (vụ user 27/09). Cache 60s + 1 lần quét chia sẻ: đổi
    // setting trả ngay số cũ, số dung lượng (thuần hiển thị) refresh nền.
    // Không đụng Accurate/scan/TTS — chỉ đường settings.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);
    private readonly object _gate = new();
    private DateTime _lastRunUtc = DateTime.MinValue;
    private StorageUsage? _last;
    private Task<StorageUsage>? _inFlight;

    public Task<StorageUsage> ReadAsync(AppPaths paths, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_last is not null && DateTime.UtcNow - _lastRunUtc < CacheTtl)
                return Task.FromResult(_last);
            if (_inFlight is { } running)
                return running;
            _inFlight = ScanAsync(paths);
            return _inFlight;
        }
    }

    public bool TryGetLast(out StorageUsage usage)
    {
        lock (_gate)
        {
            if (_last is not null)
            {
                usage = _last;
                return true;
            }
        }
        usage = new StorageUsage(0, 0, 0, 0, 0);
        return false;
    }

    public void RefreshInBackground(AppPaths paths)
    {
        Task<StorageUsage>? scan;
        lock (_gate)
        {
            if (_inFlight is not null)
                return;
            if (_last is not null && DateTime.UtcNow - _lastRunUtc < CacheTtl)
                return;
            _inFlight = scan = ScanAsync(paths);
        }
        // Không await: caller (đường settings/UI) trả ngay. Quét lỗi thì
        // nhả _inFlight để lần sau quét lại, không kẹt vĩnh viễn.
        _ = scan.ContinueWith(
            static (completed, state) =>
            {
                if (completed.IsFaulted && state is StorageUsageReader self)
                {
                    lock (self._gate)
                    {
                        if (ReferenceEquals(self._inFlight, completed))
                            self._inFlight = null;
                    }
                }
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task<StorageUsage> ScanAsync(AppPaths paths)
    {
        // Quét nền không ăn cancellation của caller: nhiều caller settings xài
        // chung 1 lần quét, caller hủy không được giết quét của người khác.
        var usage = await Task.Run(
            () => new StorageUsage(
                DirectorySize(paths.Data, CancellationToken.None),
                DirectorySize(paths.Tools, CancellationToken.None),
                DirectorySize(paths.Ocr, CancellationToken.None),
                DirectorySize(paths.Temp, CancellationToken.None),
                DirectorySize(paths.Cache, CancellationToken.None)),
            CancellationToken.None).ConfigureAwait(false);
        lock (_gate)
        {
            _last = usage;
            _lastRunUtc = DateTime.UtcNow;
            _inFlight = null;
        }
        return usage;
    }

    private static long DirectorySize(string root, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root))
        {
            return 0;
        }

        long total = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    try
                    {
                        total += new FileInfo(file).Length;
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }

                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    pending.Push(child);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return total;
    }
}
