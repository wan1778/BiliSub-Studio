using BiliSubStudio.Core.Configuration;

namespace BiliSubStudio.Core.Application;

public sealed class StorageUsageReader : IStorageUsageReader
{
    // Changing the save folder (and every Set* setting) waits on BuildSnapshotAsync, and
    // every snapshot recursively scans all of Data/Tools/Ocr/Temp/Cache — including the
    // Paddle venv + models with tens of thousands of files. On a busy disk the UI hangs 3-5
    // minutes before the path change appears (user issue 27/09). A 60s cache + one shared
    // scan: changing a setting returns the previous number immediately, while the usage
    // figures (display only) refresh in the background.
    // Does not touch Accurate/scan/TTS — only the settings path.
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
        // No await: the caller (settings/UI path) returns immediately. If the scan fails,
        // release _inFlight so the next scan can retry, never stuck permanently.
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
        // The background scan does not consume the caller's cancellation: many settings callers
        // share one scan, so a cancelled caller must not kill someone else's scan.
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
