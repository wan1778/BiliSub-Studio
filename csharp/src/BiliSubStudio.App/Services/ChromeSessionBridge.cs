using System.Diagnostics;
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
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromSeconds(45);
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
            EnsureExtensionDirectory();
            var chrome = FindChrome()
                ?? throw new InvalidOperationException("Không tìm thấy Google Chrome trên máy. Hãy cài Chrome và đăng nhập Bilibili trước.");
            await using var server = new ChromeLoginBridgeServer(
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
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
        return target;
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
