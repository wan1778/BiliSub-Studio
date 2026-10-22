using BiliSubStudio.Core.Application;
using BiliSubStudio.Core.Authentication;
using BiliSubStudio.Core.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;

namespace BiliSubStudio.App;

// Bilibili không có OAuth cho app bên thứ ba và trình duyệt hệ thống không cho
// đọc cookie của domain khác. WebView2 là "tab trình duyệt" nhúng: user đăng
// nhập trên đúng trang passport.bilibili.com, app đọc cookie từ CookieManager
// ngay khi SESSDATA xuất hiện rồi lưu bằng DPAPI như luồng cookie/QR hiện có.
public sealed partial class BrowserLoginWindow : Window
{
    private const string LoginUrl = "https://passport.bilibili.com/login";
    private readonly BiliSubApplication _application;
    private readonly ApplicationLog _log;
    private readonly DispatcherQueueTimer _pollTimer;
    private readonly CancellationTokenSource _lifetime = new();
    private string _profileDirectory = string.Empty;
    private bool _initialized;
    private bool _capturing;
    private bool _completed;
    private DateTime _nextCaptureUtc;

    public BrowserLoginWindow(BiliSubApplication application, ApplicationLog log)
    {
        _application = application;
        _log = log;
        InitializeComponent();
        Title = "Đăng nhập Bilibili bằng trình duyệt";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1080, 820));
        _pollTimer = DispatcherQueue.CreateTimer();
        _pollTimer.Interval = TimeSpan.FromSeconds(1.5);
        _pollTimer.Tick += async (_, _) => await TryCaptureAsync(manual: false);
        Closed += OnClosed;
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            _profileDirectory = Path.Combine(_application.Paths.Temp, "webview-login-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_profileDirectory);
            // WinUI exposes only the parameterless WinRT CreateAsync; point its
            // default profile at our temp folder via the documented env override
            // so the login session never touches the user's normal Edge profile.
            var previousProfile = Environment.GetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER");
            try
            {
                Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", _profileDirectory);
                var environment = await CoreWebView2Environment.CreateAsync();
                await Browser.EnsureCoreWebView2Async(environment);
            }
            finally
            {
                Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", previousProfile);
            }
            Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Browser.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
            Browser.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            Browser.CoreWebView2.Navigate(LoginUrl);
            _pollTimer.Start();
            BrowserProgress.IsActive = true;
            CaptureButton.IsEnabled = true;
            StatusText.Text = "Đăng nhập trên trang Bilibili. App tự lưu phiên khi đăng nhập xong.";
        }
        catch (Exception error)
        {
            BrowserProgress.IsActive = false;
            StatusText.Text = "Không mở được trình duyệt: " + error.Message;
            _log.Error("Đăng nhập", "Không mở được WebView2 đăng nhập: " + error.Message);
        }
    }

    private async void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (args.IsSuccess) await TryCaptureAsync(manual: false);
    }

    private async void Capture_Click(object sender, RoutedEventArgs e) => await TryCaptureAsync(manual: true);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private async Task TryCaptureAsync(bool manual)
    {
        if (_capturing || _completed) return;
        if (!manual && DateTime.UtcNow < _nextCaptureUtc) return;
        if (Browser.CoreWebView2 is null)
        {
            if (manual) StatusText.Text = "Trình duyệt chưa sẵn sàng.";
            return;
        }
        _capturing = true;
        CaptureButton.IsEnabled = false;
        try
        {
            var cookie = await ReadCookieHeaderAsync();
            if (!HasSession(cookie))
            {
                if (manual) StatusText.Text = "Chưa thấy phiên đăng nhập. Hãy đăng nhập Bilibili trong cửa sổ này trước.";
                return;
            }
            if (!Uri.TryCreate(Browser.CoreWebView2.Source, UriKind.Absolute, out var source)
                || !source.Host.EndsWith("bilibili.com", StringComparison.OrdinalIgnoreCase))
            {
                if (manual) StatusText.Text = "Hãy quay lại trang bilibili.com sau khi đăng nhập rồi thử lại.";
                return;
            }
            StatusText.Text = "Đã thấy phiên đăng nhập, đang xác minh...";
            var user = await _application.Authentication.SetCookieAsync(cookie, _lifetime.Token);
            _completed = true;
            _pollTimer.Stop();
            BrowserProgress.IsActive = false;
            StatusText.Text = string.IsNullOrWhiteSpace(user) ? "Đăng nhập thành công." : "Đăng nhập thành công: " + user;
            _log.Info("Đăng nhập", "Đăng nhập bằng trình duyệt nhúng thành công.");
            CaptureButton.IsEnabled = false;
            await Task.Delay(900, _lifetime.Token);
            Close();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            _nextCaptureUtc = DateTime.UtcNow.AddSeconds(10);
            StatusText.Text = "Chưa xác minh được phiên: " + error.Message;
            if (manual) _log.Warning("Đăng nhập", "Phiên trình duyệt chưa hợp lệ: " + error.Message);
        }
        finally
        {
            if (!_completed)
            {
                _capturing = false;
                if (_initialized && Browser.CoreWebView2 is not null) CaptureButton.IsEnabled = true;
                BrowserProgress.IsActive = false;
            }
        }
    }

    private async Task<string> ReadCookieHeaderAsync()
    {
        var manager = Browser.CoreWebView2!.CookieManager;
        var cookies = new List<CoreWebView2Cookie>();
        foreach (var uri in new[] { "https://www.bilibili.com", "https://passport.bilibili.com" })
        {
            foreach (var cookie in await manager.GetCookiesAsync(uri))
            {
                if (!cookies.Any(existing => string.Equals(existing.Name, cookie.Name, StringComparison.OrdinalIgnoreCase)))
                    cookies.Add(cookie);
            }
        }
        // Store only SESSDATA, matching the proven-good manual/QR session shape.
        // Passing buvid3/buvid4 from a full browser jar makes wbi/view/detail
        // return -352 even though the session itself is valid.
        var session = cookies.FirstOrDefault(cookie => string.Equals(cookie.Name, "SESSDATA", StringComparison.Ordinal));
        return session is null ? string.Empty : SessionStore.NormalizeCookie("SESSDATA=" + session.Value);
    }

    private static bool HasSession(string cookie) => cookie
        .Split(';', StringSplitOptions.RemoveEmptyEntries)
        .Any(part => part.TrimStart().StartsWith("SESSDATA=", StringComparison.Ordinal) && part.Length > "SESSDATA=".Length);

    private void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        // Bilibili login helpers (QQ/WeChat/phone) stay inside the embedded tab;
        // never let the login flow escape to a browser whose cookies we cannot read.
        args.Handled = true;
        if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) && uri.Host.EndsWith("bilibili.com", StringComparison.OrdinalIgnoreCase))
            sender.Navigate(args.Uri);
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _pollTimer.Stop();
        _lifetime.Cancel();
        try { Browser.Close(); }
        catch (Exception) { }
        _ = DeleteProfileAsync();
    }

    private async Task DeleteProfileAsync()
    {
        var path = _profileDirectory;
        if (path.Length == 0) return;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) { await Task.Delay(400); }
            catch (UnauthorizedAccessException) { await Task.Delay(400); }
        }
    }
}
