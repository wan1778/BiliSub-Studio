using BiliSubStudio.Core.Application;
using BiliSubStudio.Core.Authentication;
using BiliSubStudio.Core.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace BiliSubStudio.App.Pages;

public sealed partial class AccountPage : Page
{
    private readonly BiliSubApplication _application;
    private readonly ApplicationLog _log;
    private CancellationTokenSource? _qrCancellation;
    private bool _busy;

    public AccountPage(BiliSubApplication application, ApplicationLog log)
    {
        _application = application;
        _log = log;
        InitializeComponent();
        Loaded += async (_, _) => await RefreshStatusAsync();
        Unloaded += (_, _) => _qrCancellation?.Cancel();
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
        _qrCancellation?.Cancel();
        _qrCancellation?.Dispose();
        _qrCancellation = new CancellationTokenSource();
        try
        {
            SetBusy(true);
            QrProgress.IsActive = true;
            QrProgress.Visibility = Visibility.Visible;
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
                if (poll.Expired)
                {
                    _log.Warning("Đăng nhập", "QR đăng nhập đã hết hạn.");
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
            QrProgress.IsActive = false;
            QrProgress.Visibility = Visibility.Collapsed;
            SetBusy(false);
        }
    }

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
        try
        {
            SetBusy(true);
            LoginStatusText.Text = "Đang kiểm tra phiên đăng nhập...";
            await RefreshStatusAsync();
        }
        finally { SetBusy(false); }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SetBusy(true);
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
        finally { SetBusy(false); }
    }

    private async void SaveCookie_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(CookieBox.Password)) return;
        try
        {
            SetBusy(true);
            LoginStatusText.Text = "Đang xác minh cookie...";
            var user = await _application.Authentication.SetCookieAsync(CookieBox.Password, CancellationToken.None);
            CookieBox.Password = string.Empty;
            LoginStatusText.Text = "Đã đăng nhập: " + user;
            _log.Info("Đăng nhập", "Cookie được xác minh và lưu bằng Windows DPAPI.");
        }
        catch (Exception error)
        {
            LoginStatusText.Text = error.Message;
            _log.Error("Đăng nhập", "Xác minh cookie lỗi: " + error.Message);
        }
        finally { SetBusy(false); }
    }

    private void CookieBox_PasswordChanged(object sender, RoutedEventArgs e) =>
        SaveCookieButton.IsEnabled = !_busy && !string.IsNullOrWhiteSpace(CookieBox.Password);

    private void SetBusy(bool busy)
    {
        _busy = busy;
        QrButton.IsEnabled = !busy;
        ValidateButton.IsEnabled = !busy;
        DeleteButton.IsEnabled = !busy;
        SaveCookieButton.IsEnabled = !busy && !string.IsNullOrWhiteSpace(CookieBox.Password);
    }
}
