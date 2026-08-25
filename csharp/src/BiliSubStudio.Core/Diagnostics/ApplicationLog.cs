using System.Text;
using System.Threading.Channels;

namespace BiliSubStudio.Core.Diagnostics;

public enum AppLogLevel
{
    Info,
    Warning,
    Error,
}

public sealed record AppLogEntry(
    long Sequence,
    DateTimeOffset Timestamp,
    AppLogLevel Level,
    string Source,
    string Message,
    string? JobId = null);

public sealed class ApplicationLog : IDisposable
{
    private const int MaxMemoryEntries = 2_000;
    private const long MaxLogBytes = 5L * 1024 * 1024;
    private const int MaxGenerations = 5;

    private readonly object _gate = new();
    private readonly List<AppLogEntry> _entries = [];
    private readonly Channel<LogWriteItem> _writeQueue = Channel.CreateUnbounded<LogWriteItem>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Task _writerLoop;
    private long _sequence;
    private int _disposeState;

    public ApplicationLog(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var directory = Path.Combine(Path.GetFullPath(dataDirectory), "Logs");
        Directory.CreateDirectory(directory);
        FilePath = Path.Combine(directory, "application.log");
        RotateIfNeeded();
        _writerLoop = Task.Run(WriterLoopAsync);
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    public string FilePath { get; }

    public event Action<AppLogEntry>? EntryAdded;

    public AppLogEntry Info(string source, string message, string? jobId = null) =>
        Write(AppLogLevel.Info, source, message, jobId);

    public AppLogEntry Warning(string source, string message, string? jobId = null) =>
        Write(AppLogLevel.Warning, source, message, jobId);

    public AppLogEntry Error(string source, string message, string? jobId = null) =>
        Write(AppLogLevel.Error, source, message, jobId);

    public AppLogEntry Error(string source, string message, Exception exception, string? jobId = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var detail = string.IsNullOrWhiteSpace(message)
            ? exception.ToString()
            : $"{message} ⏎⏎ {exception}";
        return Write(AppLogLevel.Error, source, detail, jobId);
    }

    public IReadOnlyList<AppLogEntry> Snapshot(long afterSequence = 0)
    {
        lock (_gate)
        {
            return _entries.Where(x => x.Sequence > afterSequence).ToArray();
        }
    }

    public void Flush(TimeSpan? timeout = null)
    {
        if (Volatile.Read(ref _disposeState) != 0) return;
        FlushCore(timeout ?? TimeSpan.FromSeconds(2));
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposeState, 1, 0) != 0) return;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        FlushCore(TimeSpan.FromSeconds(2));
        _writeQueue.Writer.TryComplete();
        try { _writerLoop.Wait(TimeSpan.FromSeconds(2)); }
        catch { }
    }


    private void OnProcessExit(object? sender, EventArgs args) => Dispose();

    private AppLogEntry Write(AppLogLevel level, string source, string message, string? jobId)
    {
        source = LogRedactor.Redact(string.IsNullOrWhiteSpace(source) ? "App" : source.Trim());
        message = LogRedactor.Redact(Normalize(message));
        jobId = string.IsNullOrWhiteSpace(jobId) ? null : LogRedactor.Redact(jobId.Trim());
        if (level == AppLogLevel.Info
            && (message.Contains("cảnh báo", StringComparison.OrdinalIgnoreCase)
                || message.Contains("bỏ qua", StringComparison.OrdinalIgnoreCase)
                || message.Contains("thất bại; chuyển yt-dlp fallback", StringComparison.OrdinalIgnoreCase)))
        {
            level = AppLogLevel.Warning;
        }

        AppLogEntry entry;
        lock (_gate)
        {
            entry = new AppLogEntry(++_sequence, DateTimeOffset.Now, level, source, message, jobId);
            _entries.Add(entry);
            if (_entries.Count > MaxMemoryEntries)
                _entries.RemoveRange(0, _entries.Count - MaxMemoryEntries);

            var job = string.IsNullOrWhiteSpace(entry.JobId) ? string.Empty : $" [{entry.JobId}]";
            var line = $"{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{entry.Level.ToString().ToUpperInvariant()}] [{entry.Source}]{job} {entry.Message}";
            _writeQueue.Writer.TryWrite(new LogWriteItem(line, null));
        }

        var subscribers = EntryAdded;
        if (subscribers is not null)
        {
            foreach (Action<AppLogEntry> subscriber in subscribers.GetInvocationList())
            {
                try { subscriber(entry); }
                catch { }
            }
        }
        return entry;
    }

    private void FlushCore(TimeSpan timeout)
    {
        var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_writeQueue.Writer.TryWrite(new LogWriteItem(null, signal))) return;
        try { signal.Task.Wait(timeout); }
        catch { }
    }

    private async Task WriterLoopAsync()
    {
        await foreach (var item in _writeQueue.Reader.ReadAllAsync())
        {
            if (item.FlushSignal is not null)
            {
                item.FlushSignal.TrySetResult(true);
                continue;
            }
            if (item.Line is null) continue;
            try
            {
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxLogBytes)
                    RotateIfNeeded();
                await File.AppendAllTextAsync(FilePath, item.Line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void RotateIfNeeded()
    {
        try
        {
            if (!File.Exists(FilePath) || new FileInfo(FilePath).Length <= MaxLogBytes) return;
            var oldest = FilePath + "." + MaxGenerations;
            if (File.Exists(oldest)) File.Delete(oldest);
            for (var generation = MaxGenerations - 1; generation >= 1; generation--)
            {
                var source = FilePath + "." + generation;
                var destination = FilePath + "." + (generation + 1);
                if (File.Exists(source)) File.Move(source, destination, overwrite: true);
            }
            File.Move(FilePath, FilePath + ".1", overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string Normalize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "(không có nội dung)";
        return message.Trim().Replace("\r\n", " ⏎ ", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ');
    }

    private sealed record LogWriteItem(string? Line, TaskCompletionSource<bool>? FlushSignal);
}
