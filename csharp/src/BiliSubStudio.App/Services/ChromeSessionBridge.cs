using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using BiliSubStudio.Core.Application;
using BiliSubStudio.Core.Authentication;
using BiliSubStudio.Core.Diagnostics;

namespace BiliSubStudio.App.Services;

// Bridges the user's existing Chrome Bilibili login into the app without
// touching Chrome's encrypted cookie database. The optional BiliSub Bridge
// extension reads SESSDATA with Chrome's cookies API and posts it to a
// short-lived loopback listener. Chrome is only launched to open that one
// tokenized handoff tab; the app never renders a login page here.
public sealed class ChromeSessionBridge
{
    private const string SecretPrefix = "self.BILISUB_BRIDGE_SECRET = \"";
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromMinutes(5);
    private readonly BiliSubApplication _application;
    private readonly ApplicationLog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ChromeSessionBridge(BiliSubApplication application, ApplicationLog log)
    {
        _application = application;
        _log = log;
    }

    public async Task<string> SyncAsync(CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("Đang có một lần đồng bộ phiên Chrome khác.");

        try
        {
            var extensionDirectory = EnsureExtensionDirectory();
            var pageSecret = ReadExtensionSecret(extensionDirectory);
            var chrome = FindChrome()
                ?? throw new InvalidOperationException("Không tìm thấy Google Chrome trên máy. Hãy cài Chrome và đăng nhập Bilibili trước.");
            await using var server = new ChromeLoginBridgeServer(
                pageSecret,
                (cookie, token) => _application.Authentication.SetCookieAsync(cookie, token));
            LaunchChrome(chrome, server.Url);
            _log.Info("Đăng nhập", "Đang chờ phiên Bilibili từ Chrome.");
            try
            {
                var user = await server.WaitForSessionAsync(SessionTimeout, cancellationToken);
                _log.Info("Đăng nhập", "Đồng bộ phiên từ Chrome thành công.");
                return user;
            }
            catch (TimeoutException)
            {
                _log.Warning("Đăng nhập", "Không nhận được phiên từ Chrome trong thời gian chờ.");
                throw;
            }
            catch (Exception error)
            {
                _log.Error("Đăng nhập", "Đồng bộ phiên từ Chrome lỗi: " + error.Message);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public string EnsureExtensionDirectory()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "ChromeExtension");
        if (!Directory.Exists(source))
            throw new InvalidOperationException("Bản cài đặt thiếu thư mục ChromeExtension.");

        var target = Path.Combine(_application.Paths.Data, "ChromeExtension");
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (string.Equals(relative, "bridge-secret.js", StringComparison.OrdinalIgnoreCase)) continue;
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
        EnsureExtensionSecret(target);
        return target;
    }

    private static void EnsureExtensionSecret(string extensionDirectory)
    {
        var path = Path.Combine(extensionDirectory, "bridge-secret.js");
        if (File.Exists(path) && TryReadSecret(File.ReadAllText(path)) is not null) return;
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        File.WriteAllText(
            path,
            "// Generated per install. Keep this file with the extension.\n" + SecretPrefix + secret + "\";\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string ReadExtensionSecret(string extensionDirectory)
    {
        var path = Path.Combine(extensionDirectory, "bridge-secret.js");
        return TryReadSecret(File.ReadAllText(path))
            ?? throw new InvalidOperationException("Không đọc được khóa kết nối extension Chrome.");
    }

    private static string? TryReadSecret(string text)
    {
        var start = text.IndexOf(SecretPrefix, StringComparison.Ordinal);
        if (start < 0) return null;
        start += SecretPrefix.Length;
        var end = text.IndexOf('"', start);
        return end == start + 64 ? text[start..end] : null;
    }

    public void OpenExtensionFolder()
    {
        var path = EnsureExtensionDirectory();
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
    }

    private static string? FindChrome()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static void LaunchChrome(string chrome, string url)
    {
        var start = new ProcessStartInfo(chrome) { UseShellExecute = false };
        start.ArgumentList.Add("--new-tab");
        start.ArgumentList.Add(url);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Không mở được Google Chrome.");
    }
}
