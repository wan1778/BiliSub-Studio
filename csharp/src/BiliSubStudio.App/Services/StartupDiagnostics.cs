using System.Runtime.InteropServices;
using System.Text;

namespace BiliSubStudio.App.Services;

internal static class StartupDiagnostics
{
    private const string SmokePrefix = "--startup-smoke-test=";
    private static readonly object Sync = new();
    private static string? _logPath;

    public static string LogPath => _logPath ??= ResolveLogPath();

    public static string? SmokeSentinelPath { get; } = ResolveSmokeSentinel(Environment.GetCommandLineArgs());

    public static bool IsSmokeTest => SmokeSentinelPath is not null;

    public static void Initialize()
    {
        Write("process-start", $"version={typeof(StartupDiagnostics).Assembly.GetName().Version}; os={Environment.OSVersion}");
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception error)
            {
                WriteException("appdomain-unhandled", error);
            }
            else
            {
                Write("appdomain-unhandled", args.ExceptionObject?.ToString());
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) => WriteException("task-unobserved", args.Exception);
    }

    public static void Write(string stage, string? detail = null)
    {
        try
        {
            var line = $"{DateTimeOffset.Now:O} [{Environment.ProcessId}] {stage}";
            if (!string.IsNullOrWhiteSpace(detail))
            {
                line += ": " + detail.Replace("\r", " ").Replace("\n", " | ");
            }

            lock (Sync)
            {
                var path = LogPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch
        {
            // Diagnostics must never become a second startup failure.
        }
    }

    public static void WriteException(string stage, Exception error) => Write(stage, error.ToString());

    public static void ShowFatalError(string stage, Exception error)
    {
        WriteException(stage, error);
        if (IsSmokeTest)
        {
            return;
        }

        var message = "BiliSub Studio không thể khởi động.\n\n"
            + error.Message
            + "\n\nNhật ký lỗi đã được lưu tại:\n"
            + LogPath;
        _ = MessageBoxW(IntPtr.Zero, message, "BiliSub Studio - Lỗi khởi động", 0x00000010u | 0x00000000u);
    }

    public static async Task WriteSmokeSentinelAsync(CancellationToken cancellationToken = default)
    {
        if (SmokeSentinelPath is null)
        {
            return;
        }

        var directory = Path.GetDirectoryName(SmokeSentinelPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
        await File.WriteAllTextAsync(SmokeSentinelPath, "PASS", new UTF8Encoding(false), cancellationToken);
        Write("startup-smoke-pass", SmokeSentinelPath);
    }

    private static string ResolveLogPath()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = string.IsNullOrWhiteSpace(local) ? Path.GetTempPath() : local;
        return Path.Combine(root, "BiliSub Studio", "Logs", "startup.log");
    }

    private static string? ResolveSmokeSentinel(IEnumerable<string> args)
    {
        foreach (var arg in args)
        {
            if (!arg.StartsWith(SmokePrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = arg[SmokePrefix.Length..].Trim().Trim('"');
            return string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value);
        }
        return null;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr hwnd, string text, string caption, uint type);
}
