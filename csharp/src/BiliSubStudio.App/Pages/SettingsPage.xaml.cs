using BiliSubStudio.App.ViewModels;
using BiliSubStudio.Core.Application;
using BiliSubStudio.Core.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BiliSubStudio.App.Pages;

public sealed partial class SettingsPage : Page
{
    private readonly BiliSubApplication _application;
    private readonly HardwarePage _hardwarePage;
    private readonly AccountPage _accountPage;
    private readonly SupportPage _supportPage;
    private readonly ApplicationLog _log;
    private bool _syncing;

    public SettingsPage(
        SettingsViewModel viewModel,
        BiliSubApplication application,
        HardwarePage hardwarePage,
        AccountPage accountPage,
        SupportPage supportPage,
        ApplicationLog log)
    {
        ViewModel = viewModel;
        _application = application;
        _hardwarePage = hardwarePage;
        _accountPage = accountPage;
        _supportPage = supportPage;
        _log = log;
        InitializeComponent();
        LogPathText.Text = log.FilePath;
        SelectSection("general");
    }

    public SettingsViewModel ViewModel { get; }

    public event Action<string>? ThemeRequested;

    public async Task<SettingsSnapshot> InitializeAsync()
    {
        var snapshot = await ViewModel.InitializeAsync();
        SyncControlsFromViewModel();
        return snapshot;
    }

    internal async Task RunLayoutSmokeAsync()
    {
        foreach (var section in new[] { "general", "hardware", "account", "support" })
        {
            SelectSection(section);
            await Task.Delay(80);
        }
        SelectSection("general");
    }

    private async void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncing || !ViewModel.IsInitialized || sender is not RadioButton { Tag: string theme })
        {
            return;
        }

        if (await ViewModel.SetThemeAsync(theme))
        {
            ThemeRequested?.Invoke(ViewModel.Theme);
            _log.Info("Cài đặt", $"Đã đổi theme sang {ViewModel.Theme}.");
        }
        SyncControlsFromViewModel();
    }

    private async void AutoUpdate_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncing || !ViewModel.IsInitialized)
        {
            return;
        }

        await ViewModel.SetUpdateCheckAsync(AutoUpdateToggle.IsOn);
        _log.Info("Cài đặt", $"Tự kiểm tra cập nhật: {(AutoUpdateToggle.IsOn ? "bật" : "tắt")}.");
        SyncControlsFromViewModel();
    }

    private void SyncControlsFromViewModel()
    {
        _syncing = true;
        try
        {
            DarkThemeRadio.IsChecked = ViewModel.Theme == "dark";
            LightThemeRadio.IsChecked = ViewModel.Theme == "light";
            AutoUpdateToggle.IsOn = ViewModel.CheckUpdates;
            ThemeChip.Text = ViewModel.Theme == "light" ? "Light" : "Dark";
        }
        finally
        {
            _syncing = false;
        }
    }

    private void SectionTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string section }) SelectSection(section);
    }

    private void SelectSection(string section)
    {
        _syncing = true;
        try
        {
            GeneralTab.IsChecked = section == "general";
            HardwareTab.IsChecked = section == "hardware";
            AccountTab.IsChecked = section == "account";
            SupportTab.IsChecked = section == "support";
        }
        finally { _syncing = false; }

        GeneralScroll.Visibility = section == "general" ? Visibility.Visible : Visibility.Collapsed;
        SectionHost.Visibility = section == "general" ? Visibility.Collapsed : Visibility.Visible;
        SectionHost.Content = section switch
        {
            "hardware" => _hardwarePage,
            "account" => _accountPage,
            "support" => _supportPage,
            _ => null,
        };

        (BreadcrumbText.Text, SectionSubtitle.Text) = section switch
        {
            "hardware" => ("BiliSub Studio / Cài đặt / Hiệu năng", "Phần cứng, công cụ portable và benchmark trước khi OCR Auto nâng tải"),
            "account" => ("BiliSub Studio / Cài đặt / Đăng nhập", "Phiên Bilibili, QR native và cookie được bảo vệ bằng Windows DPAPI"),
            "support" => ("BiliSub Studio / Cài đặt / Cập nhật & hỗ trợ", "Cập nhật đã xác minh, báo lỗi và nhật ký chẩn đoán chung"),
            _ => ("BiliSub Studio / Cài đặt / Chung", "Giao diện, thư mục, dung lượng và cấu hình portable"),
        };
    }

    private async void Cleanup_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync("Dọn Temp và Cache?", "Chỉ dữ liệu tạm/chưa hoàn tất bị xóa.")) return;
        try
        {
            _application.CleanupStorage();
            await ViewModel.InitializeAsync();
            _log.Info("Cài đặt", "Đã dọn Temp và Cache.");
        }
        catch (Exception error)
        {
            _log.Error("Cài đặt", "Dọn Temp/Cache lỗi: " + error.Message);
            await ShowErrorAsync(error.Message);
        }
    }

    private async void ResetTools_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync("Reset toàn bộ Tools?", "FFmpeg, yt-dlp và OCR runtime sẽ cần tải lại.")) return;
        try
        {
            await _application.ResetToolsAsync(CancellationToken.None);
            await ViewModel.InitializeAsync();
            _log.Info("Cài đặt", "Đã reset Tools.");
        }
        catch (Exception error)
        {
            _log.Error("Cài đặt", "Reset Tools lỗi: " + error.Message);
            await ShowErrorAsync(error.Message);
        }
    }

    private async void RemoveOcr_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync("Xóa OCR runtime?", "Private Python, PaddleOCR và model cache sẽ bị xóa.")) return;
        try
        {
            await _application.RemoveOcrAsync(CancellationToken.None);
            await ViewModel.InitializeAsync();
            _log.Info("Cài đặt", "Đã xóa OCR runtime.");
        }
        catch (Exception error)
        {
            _log.Error("Cài đặt", "Xóa OCR runtime lỗi: " + error.Message);
            await ShowErrorAsync(error.Message);
        }
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = title, Content = message, PrimaryButtonText = "Tiếp tục", CloseButtonText = "Hủy", DefaultButton = ContentDialogButton.Close };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task ShowErrorAsync(string message) => await new ContentDialog { XamlRoot = XamlRoot, Title = "Không thể thực hiện", Content = message, CloseButtonText = "Đóng" }.ShowAsync();
}
