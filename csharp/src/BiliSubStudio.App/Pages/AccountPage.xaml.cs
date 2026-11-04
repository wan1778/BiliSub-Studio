using BiliSubStudio.Core.Application;
using BiliSubStudio.Core.Authentication;
using BiliSubStudio.Core.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System.Diagnostics;
using Windows.System;

namespace BiliSubStudio.App.Pages;

public sealed partial class AccountPage : Page
{
    private readonly BiliSubApplication _application;
    private readonly ApplicationLog _log;
    private CancellationTokenSource? _qrCancellation;
    private bool _disposed;
    // BUG-LOGIN-LOCK 02/11: a QR session must NOT lock the manual cookie path.
    // The old SetBusy disabled Kiểm tra lại / Đăng xuất / Xác minh cookie for up
    // to the whole QR lifetime, so a user whose cookie had expired could not
    // paste a new one - the page looked frozen.
    private bool _qrRunning;

    public AccountPage(BiliSubApplication application, ApplicationLog log)
    {
        _application = application;
        _log = log;
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            await RefreshStatusAsync();
            await StartQrAsync();
        };
        Unloaded += (_, _) =>
        {
            _disposed = true;
            _qrCancellation?.Cancel();
        };
    }

    private async Task RefreshStatusAsync()
    {
        try
        {
            var status = await _application.Authentication.StatusAsync(validate: true, CancellationToken.None);
            LoginStatusText.Text = status.Valid
                ? $"Đã đăng nhập: {status.User}"
                : status.Saved ? "Cookie đã lưu nhưng không hợp lệ: " + status.Error : "Chưa đăng nhập.";
            if (status.Valid) _log.Info("Đăng nhập", "Phiên Bilibili hợp lệ.");
            else if (status.Saved) _log.Warning("Đăng nhập", "Cookie đã lưu nhưng phiên không còn hợp lệ.");
        }
        catch (Exception error)
        {
            LoginStatusText.Text = error.Message;
            _log.Error("Đăng nhập", "Kiểm tra phiên Bilibili lỗi: " + error.Message);
        }
    }

    private async void Qr_Click(object sender, RoutedEventArgs e)
    {
        try { await StartQrAsync(); }
        catch (Exception error)
        {
            _log.Error("Đăng nhập", "Tạo QR lỗi: " + error.Message);
            QrStatusText.Text = error.Message;
        }
    }

    private async Task StartQrAsync()
    {
        if (_qrRunning) return;
        _qrCancellation?.Cancel();
        _qrCancellation?.Dispose();
        _qrCancellation = new CancellationTokenSource();
        SetQrSession(true);
        // Bilibili QR codes live about 180s: bound the wait so the session always
        // ends on its own instead of polling forever on an ignored key.
        var deadline = DateTime.UtcNow.AddMinutes(3);
        try
        {
            QrStatusText.Text = "Đang tạo QR...";
            _log.Info("Đăng nhập", "Đang tạo QR đăng nhập Bilibili.");
            var start = await _application.Authentication.StartQrAsync(_qrCancellation.Token);
            RenderQr(start.Matrix);
            QrStatusText.Text = "Chưa quét mã QR.";
            while (!_qrCancellation.IsCancellationRequested)
            {
                await Task.Delay(2000, _qrCancellation.Token);
                var poll = await _application.Authentication.PollQrAsync(start.Key, _qrCancellation.Token);
                QrStatusText.Text = poll.Message;
                if (poll.LoggedIn)
                {
                    _log.Info("Đăng nhập", "Đăng nhập QR thành công.");
                    await RefreshStatusAsync();
                    break;
                }
                if (poll.Expired || DateTime.UtcNow >= deadline)
                {
                    _log.Warning("Đăng nhập", "QR đăng nhập đã hết hạn.");
                    QrStatusText.Text = "QR đã hết hạn. Bấm Tạo mã QR để làm mới, hoặc dán cookie còn hiệu lực ở bên phải.";
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            QrStatusText.Text = error.Message;
            _log.Error("Đăng nhập", "QR đăng nhập lỗi: " + error.Message);
        }
        finally
        {
            SetQrSession(false);
        }
    }

    // Only the QR controls follow a QR session: the cookie/validate/delete
    // controls stay live so an expired session never blocks entering a new one.
    private void SetQrSession(bool active)
    {
        _qrRunning = active;
        QrButton.IsEnabled = !active;
        QrProgress.IsActive = active;
        QrProgress.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void SetActionBusy(Button button, bool busy) => button.IsEnabled = !busy;

    private void RenderQr(QrMatrix matrix)
    {
        QrCanvas.Children.Clear();
        var module = 240d / matrix.Size;
        var offset = 10d;
        for (var y = 0; y < matrix.Size; y++)
        for (var x = 0; x < matrix.Size; x++)
        if (matrix.At(x, y))
        {
            var rectangle = new Rectangle { Width = module + 0.2, Height = module + 0.2, Fill = new SolidColorBrush(Colors.Black) };
            Canvas.SetLeft(rectangle, offset + x * module);
            Canvas.SetTop(rectangle, offset + y * module);
            QrCanvas.Children.Add(rectangle);
        }
    }

    private async void Validate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        try
        {
            SetActionBusy(button, true);
            LoginStatusText.Text = "Đang kiểm tra phiên đăng nhập...";
            await RefreshStatusAsync();
        }
        finally { SetActionBusy(button, false); }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        try
        {
            SetActionBusy(button, true);
            // A saved session is gone: the QR session chasing it is pointless now.
            _qrCancellation?.Cancel();
            await _application.Authentication.DeleteAsync(CancellationToken.None);
            CookieBox.Password = string.Empty;
            _log.Info("Đăng nhập", "Đã xóa phiên Bilibili đã lưu.");
            await RefreshStatusAsync();
        }
        catch (Exception error)
        {
            LoginStatusText.Text = error.Message;
            _log.Error("Đăng nhập", "Đăng xuất/xóa cookie lỗi: " + error.Message);
        }
        finally { SetActionBusy(button, false); }
    }

    private void OpenLoginPage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://passport.bilibili.com/login") { UseShellExecute = true });
            _log.Info("Đăng nhập", "Đã mở trang đăng nhập Bilibili trong trình duyệt mặc định.");
            // The user asked for the obvious thing: log in in the browser and
            // the app follows by itself. Watch the browser's cookie store for a
            // few minutes and import the session the moment it appears.
            _ = WatchBrowserLoginAsync();
        }
        catch (Exception error)
        {
            QrStatusText.Text = "Không mở được trình duyệt: " + error.Message;
            _log.Error("Đăng nhập", "Mở trang đăng nhập lỗi: " + error.Message);
        }
    }

    // Bounded watch (3 minutes, one read every 3s): stops on import, on a
    // verified session, or when the page goes away.
    private async Task WatchBrowserLoginAsync()
    {
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (DateTime.UtcNow < deadline && !_disposed)
        {
            await Task.Delay(3000);
            if (_disposed) return;
            var candidate = BrowserCookieReader.ReadBilibiliSessions().FirstOrDefault();
            if (candidate is null) continue;
            var status = await _application.Authentication.StatusAsync(validate: true, CancellationToken.None);
            if (status.Valid)
            {
                LoginStatusText.Text = $"Đã đăng nhập: {status.User}";
                return;
            }
            await ImportBrowserSessionAsync(candidate);
            return;
        }
    }

    private async void ImportBrowserCookie_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        try
        {
            SetActionBusy(button, true);
            var candidate = BrowserCookieReader.ReadBilibiliSessions().FirstOrDefault();
            if (candidate is null)
            {
                LoginStatusText.Text = "Không thấy phiên Bilibili trong Chrome/Edge/Firefox trên máy này. Hãy đăng nhập bilibili.com trên trình duyệt trước.";
                _log.Warning("Đăng nhập", "Không tìm thấy phiên Bilibili trong cookie trình duyệt.");
                return;
            }
            await ImportBrowserSessionAsync(candidate);
        }
        catch (Exception error)
        {
            LoginStatusText.Text = error.Message;
            _log.Error("Đăng nhập", "Lấy phiên từ trình duyệt lỗi: " + error.Message);
        }
        finally { SetActionBusy(button, false); }
    }

    private async Task ImportBrowserSessionAsync(BrowserCookieSession session)
    {
        var result = await _application.Authentication.SetCookieAsync(session.CookieHeader, CancellationToken.None);
        if (result.Verified)
        {
            // A real session makes the QR session pointless.
            _qrCancellation?.Cancel();
            CookieBox.Password = string.Empty;
            _log.Info("Đăng nhập", $"Đã lấy phiên Bilibili từ trình duyệt {session.Browser}.");
            LoginStatusText.Text = $"Đã đăng nhập bằng phiên {session.Browser}: {result.User}";
            await RefreshStatusAsync();
        }
        else
        {
            LoginStatusText.Text = result.Message;
            _log.Warning("Đăng nhập", result.Message);
        }
    }

    private async void SaveCookie_Click(object sender, RoutedEventArgs e)
    {
        try { await SaveCookieAsync(); }
        catch (Exception error)
        {
            _log.Error("Đăng nhập", "Lưu cookie lỗi: " + error.Message);
            LoginStatusText.Text = error.Message;
        }
    }

    private async void CookieBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        try { await SaveCookieAsync(); }
        catch (Exception error)
        {
            _log.Error("Đăng nhập", "Lưu cookie lỗi: " + error.Message);
            LoginStatusText.Text = error.Message;
        }
    }

    private async Task SaveCookieAsync()
    {
        if (string.IsNullOrWhiteSpace(CookieBox.Password)) return;
        try
        {
            SetActionBusy(SaveCookieButton, true);
            // A new cookie supersedes any QR session the page started on load.
            _qrCancellation?.Cancel();
            LoginStatusText.Text = "Đang xác minh cookie...";
            var result = await _application.Authentication.SetCookieAsync(CookieBox.Password, CancellationToken.None);
            CookieBox.Password = string.Empty;
            LoginStatusText.Text = result.Message;
            if (result.Verified)
            {
                _log.Info("Đăng nhập", "Cookie được xác minh và lưu bằng Windows DPAPI.");
                await RefreshStatusAsync();
            }
            else
            {
                _log.Warning("Đăng nhập", result.Message);
            }
        }
        catch (Exception error)
        {
            LoginStatusText.Text = error.Message;
            _log.Error("Đăng nhập", "Xác minh cookie lỗi: " + error.Message);
        }
        finally
        {
            SetActionBusy(SaveCookieButton, !string.IsNullOrWhiteSpace(CookieBox.Password));
        }
    }

    private void CookieBox_PasswordChanged(object sender, RoutedEventArgs e) =>
        SaveCookieButton.IsEnabled = !string.IsNullOrWhiteSpace(CookieBox.Password);
}
