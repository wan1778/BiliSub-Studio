using System.Runtime.InteropServices;
using System.Text;

namespace BiliSubStudio.App.Services;

internal static class StartupDiagnostics
{
    private const string SmokePrefix = "--startup-smoke-test=";
    private const string ListStressPrefix = "--ocr-list-stress=";
    private const string SoakVideoPrefix = "--ocr-soak-video=";
    private const string SoakOutPrefix = "--ocr-soak-out=";
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

    public static void WriteException(string stage, Exception error) => Write(stage, DescribeException(error));

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

    private static string DescribeException(Exception error)
    {
        var detail = new StringBuilder(error.ToString());
        detail.Append(" | HRESULT=0x").Append(error.HResult.ToString("X8"));
        try
        {
            foreach (var property in error.GetType().GetProperties().Where(candidate =>
                         candidate.GetIndexParameters().Length == 0
                         && candidate.Name is not nameof(Exception.Message)
                         && candidate.Name is not nameof(Exception.StackTrace)
                         && candidate.Name is not nameof(Exception.Source)
                         && candidate.Name is not nameof(Exception.InnerException)
                         && candidate.Name is not nameof(Exception.Data)
                         && candidate.Name is not nameof(Exception.TargetSite)
                         && candidate.Name is not nameof(Exception.HResult)))
            {
                var value = property.GetValue(error);
                if (value is not null)
                {
                    detail.Append(" | ").Append(property.Name).Append('=').Append(value);
                }
            }
        }
        catch
        {
            // Reflection metadata is best-effort diagnostics only.
        }
        return detail.ToString();
    }

    public static (int Count, string Path)? ListStressRequest { get; } = ResolveListStress(Environment.GetCommandLineArgs());

    // Test-only soak: dựng đúng trạng thái "OCR xong" (player + checkpoint cues +
    // journal + timer) rồi đảo tab để bắt crash chuyển tab. Chỉ chạy khi có cả 2 flag.
    public static string? SoakVideoPath { get; } = ResolveFlagValue(Environment.GetCommandLineArgs(), SoakVideoPrefix);

    public static string? SoakOutPath { get; } = ResolveFlagValue(Environment.GetCommandLineArgs(), SoakOutPrefix);

    // Soak file-mất: player giữ file này, harness xóa trước khi đảo tab.
    public static string? SoakPlayerPath { get; } = ResolveFlagValue(Environment.GetCommandLineArgs(), "--ocr-soak-player=");

    public static bool SoakDeletePlayer { get; } =
        string.Equals(ResolveFlagValue(Environment.GetCommandLineArgs(), "--ocr-soak-delete="), "1", StringComparison.OrdinalIgnoreCase);

    // Soak quét-thật: chạy scan Fresh trên video soak (nhanh/cpu/4 lane) để bắt
    // crash lúc đang quét + lúc Done/teardown.
    public static bool SoakScan { get; } =
        string.Equals(ResolveFlagValue(Environment.GetCommandLineArgs(), "--ocr-soak-scan="), "1", StringComparison.OrdinalIgnoreCase);

    private static string? ResolveFlagValue(IEnumerable<string> args, string prefix)
    {
        foreach (var arg in args)
        {
            if (!arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var value = arg[prefix.Length..].Trim().Trim('"');
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        return null;
    }

    private static (int Count, string Path)? ResolveListStress(IEnumerable<string> args)
    {
        foreach (var arg in args)
        {
            if (!arg.StartsWith(ListStressPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            // Format: --ocr-list-stress=<count>:<resultPath>
            var value = arg[ListStressPrefix.Length..].Trim().Trim('"');
            var split = value.IndexOf(':');
            if (split <= 0) continue;
            if (!int.TryParse(value[..split], out var count) || count <= 0) continue;
            var path = value[(split + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(path)) continue;
            return (count, Path.GetFullPath(path));
        }
        return null;
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
