using BiliSubStudio.Core.Application;
using BiliSubStudio.Core.Authentication;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace BiliSubStudio.App.Pages;

public sealed partial class AccountPage : Page
{
    private readonly BiliSubApplication _application;
    private CancellationTokenSource? _qrCancellation;
    private bool _busy;
    public AccountPage(BiliSubApplication application) { _application = application; InitializeComponent(); Loaded += async (_, _) => await RefreshStatusAsync(); Unloaded += (_, _) => _qrCancellation?.Cancel(); }

    private async Task RefreshStatusAsync()
    {
        var status = await _application.Authentication.StatusAsync(validate: true, CancellationToken.None);
        LoginStatusText.Text = status.Valid ? $"Đã đăng nhập: {status.User}" : status.Saved ? "Cookie đã lưu nhưng không hợp lệ: " + status.Error : "Chưa đăng nhập.";
    }

    private async void Qr_Click(object sender, RoutedEventArgs e)
    {
        _qrCancellation?.Cancel(); _qrCancellation?.Dispose(); _qrCancellation = new CancellationTokenSource();
        try
        {
            SetBusy(true); QrProgress.IsActive = true; QrProgress.Visibility = Visibility.Visible; QrStatusText.Text = "Đang tạo QR...";
            var start = await _application.Authentication.StartQrAsync(_qrCancellation.Token); RenderQr(start.Matrix); QrStatusText.Text = "Chưa quét mã QR.";
            while (!_qrCancellation.IsCancellationRequested)
            {
                await Task.Delay(2000, _qrCancellation.Token);
                var poll = await _application.Authentication.PollQrAsync(start.Key, _qrCancellation.Token); QrStatusText.Text = poll.Message;
                if (poll.LoggedIn) { await RefreshStatusAsync(); break; }
                if (poll.Expired) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { QrStatusText.Text = error.Message; }
        finally { QrProgress.IsActive = false; QrProgress.Visibility = Visibility.Collapsed; SetBusy(false); }
    }

    private void RenderQr(QrMatrix matrix)
    {
        QrCanvas.Children.Clear(); var module = 240d / matrix.Size; var offset = 10d;
        for (var y = 0; y < matrix.Size; y++) for (var x = 0; x < matrix.Size; x++) if (matrix.At(x, y))
        {
            var rectangle = new Rectangle { Width = module + 0.2, Height = module + 0.2, Fill = new SolidColorBrush(Colors.Black) };
            Canvas.SetLeft(rectangle, offset + x * module); Canvas.SetTop(rectangle, offset + y * module); QrCanvas.Children.Add(rectangle);
        }
    }

    private async void Validate_Click(object sender, RoutedEventArgs e) { try { SetBusy(true); LoginStatusText.Text = "Đang kiểm tra phiên đăng nhập..."; await RefreshStatusAsync(); } catch (Exception error) { LoginStatusText.Text = error.Message; } finally { SetBusy(false); } }
    private async void Delete_Click(object sender, RoutedEventArgs e) { try { SetBusy(true); _qrCancellation?.Cancel(); await _application.Authentication.DeleteAsync(CancellationToken.None); CookieBox.Password = string.Empty; await RefreshStatusAsync(); } catch (Exception error) { LoginStatusText.Text = error.Message; } finally { SetBusy(false); } }
    private async void SaveCookie_Click(object sender, RoutedEventArgs e) { if (string.IsNullOrWhiteSpace(CookieBox.Password)) return; try { SetBusy(true); LoginStatusText.Text = "Đang xác minh cookie..."; var user = await _application.Authentication.SetCookieAsync(CookieBox.Password, CancellationToken.None); CookieBox.Password = string.Empty; LoginStatusText.Text = "Đã đăng nhập: " + user; } catch (Exception error) { LoginStatusText.Text = error.Message; } finally { SetBusy(false); } }

    private void CookieBox_PasswordChanged(object sender, RoutedEventArgs e) => SaveCookieButton.IsEnabled = !_busy && !string.IsNullOrWhiteSpace(CookieBox.Password);

    private void SetBusy(bool busy)
    {
        _busy = busy;
        QrButton.IsEnabled = !busy;
        ValidateButton.IsEnabled = !busy;
        DeleteButton.IsEnabled = !busy;
        SaveCookieButton.IsEnabled = !busy && !string.IsNullOrWhiteSpace(CookieBox.Password);
    }
}
