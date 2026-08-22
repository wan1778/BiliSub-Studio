using System.Text;

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

public sealed class ApplicationLog
{
    private const int MaxMemoryEntries = 2_000;
    private const long MaxLogBytes = 5L * 1024 * 1024;
    private readonly object _gate = new();
    private readonly List<AppLogEntry> _entries = [];
    private long _sequence;

    public ApplicationLog(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var directory = Path.Combine(Path.GetFullPath(dataDirectory), "Logs");
        Directory.CreateDirectory(directory);
        FilePath = Path.Combine(directory, "application.log");
        RotateIfNeeded();
    }

    public string FilePath { get; }

    public event Action<AppLogEntry>? EntryAdded;

    public AppLogEntry Info(string source, string message, string? jobId = null) =>
        Write(AppLogLevel.Info, source, message, jobId);

    public AppLogEntry Warning(string source, string message, string? jobId = null) =>
        Write(AppLogLevel.Warning, source, message, jobId);

    public AppLogEntry Error(string source, string message, string? jobId = null) =>
        Write(AppLogLevel.Error, source, message, jobId);

    public IReadOnlyList<AppLogEntry> Snapshot(long afterSequence = 0)
    {
        lock (_gate)
        {
            return _entries.Where(x => x.Sequence > afterSequence).ToArray();
        }
    }

    private AppLogEntry Write(AppLogLevel level, string source, string message, string? jobId)
    {
        source = string.IsNullOrWhiteSpace(source) ? "App" : source.Trim();
        message = Normalize(message);
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
            Persist(entry);
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

    private void Persist(AppLogEntry entry)
    {
        try
        {
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxLogBytes)
                RotateIfNeeded();
            var job = string.IsNullOrWhiteSpace(entry.JobId) ? string.Empty : $" [{entry.JobId}]";
            var line = $"{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{entry.Level.ToString().ToUpperInvariant()}] [{entry.Source}]{job} {entry.Message}{Environment.NewLine}";
            File.AppendAllText(FilePath, line, new UTF8Encoding(false));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void RotateIfNeeded()
    {
        try
        {
            if (!File.Exists(FilePath) || new FileInfo(FilePath).Length <= MaxLogBytes) return;
            var previous = FilePath + ".1";
            File.Move(FilePath, previous, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string Normalize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "(không có nội dung)";
        return message.Trim().Replace("\r\n", " ⏎ ", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ');
    }
}
