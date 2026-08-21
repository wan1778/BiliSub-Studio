using BiliSubStudio.App.ViewModels;
using BiliSubStudio.Core.Application;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BiliSubStudio.App.Pages;

public sealed partial class SettingsPage : Page
{
    private readonly BiliSubApplication _application;
    private bool _syncing;

    public SettingsPage(SettingsViewModel viewModel, BiliSubApplication application)
    {
        ViewModel = viewModel;
        _application = application;
        InitializeComponent();
    }

    public SettingsViewModel ViewModel { get; }

    public event Action<string>? ThemeRequested;
    public event Action<string>? NavigateRequested;

    public async Task<SettingsSnapshot> InitializeAsync()
    {
        var snapshot = await ViewModel.InitializeAsync();
        SyncControlsFromViewModel();
        UpdateLayoutMode(ActualWidth);
        return snapshot;
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

    private void Page_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateLayoutMode(e.NewSize.Width);

    private void LoginTab_Click(object sender, RoutedEventArgs e) => NavigateRequested?.Invoke("account");
    private void SupportTab_Click(object sender, RoutedEventArgs e) => NavigateRequested?.Invoke("support");

    private async void Cleanup_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync("Dọn Temp và Cache?", "Chỉ dữ liệu tạm/chưa hoàn tất bị xóa.")) return;
        try { _application.CleanupStorage(); await ViewModel.InitializeAsync(); }
        catch (Exception error) { await ShowErrorAsync(error.Message); }
    }

    private async void ResetTools_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync("Reset toàn bộ Tools?", "FFmpeg, yt-dlp và OCR runtime sẽ cần tải lại.")) return;
        try { await _application.ResetToolsAsync(CancellationToken.None); await ViewModel.InitializeAsync(); }
        catch (Exception error) { await ShowErrorAsync(error.Message); }
    }

    private async void RemoveOcr_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync("Xóa OCR runtime?", "Private Python, PaddleOCR và model cache sẽ bị xóa.")) return;
        try { await _application.RemoveOcrAsync(CancellationToken.None); await ViewModel.InitializeAsync(); }
        catch (Exception error) { await ShowErrorAsync(error.Message); }
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = title, Content = message, PrimaryButtonText = "Tiếp tục", CloseButtonText = "Hủy", DefaultButton = ContentDialogButton.Close };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task ShowErrorAsync(string message) => await new ContentDialog { XamlRoot = XamlRoot, Title = "Không thể thực hiện", Content = message, CloseButtonText = "Đóng" }.ShowAsync();

    private void UpdateLayoutMode(double width)
    {
        var narrow = width > 0 && width < 940;
        SettingsGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        SettingsGrid.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(RightColumn, narrow ? 0 : 1);
        Grid.SetRow(RightColumn, narrow ? 1 : 0);
        Grid.SetColumnSpan(RightColumn, narrow ? 2 : 1);
        RightColumn.Margin = narrow ? new Thickness(0, 2, 0, 0) : new Thickness(0);
    }
}
