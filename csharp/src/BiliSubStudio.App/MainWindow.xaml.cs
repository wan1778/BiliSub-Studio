using System.Text.Json;
using BiliSubStudio.App.Pages;
using BiliSubStudio.App.Services;
using BiliSubStudio.App.ViewModels;
using BiliSubStudio.Core.Application;
using BiliSubStudio.Core.Configuration;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace BiliSubStudio.App;

public sealed partial class MainWindow : Window
{
    private readonly BiliSubApplication _application;
    private readonly SettingsPage _settingsPage;
    private readonly SupportPage _supportPage;
    private readonly Dictionary<string, UIElement> _pages;
    private readonly TaskCompletionSource<bool> _initialization = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _buildTag;
    private bool _initialized;
    private bool _safeToClose;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        _buildTag = "dev";
        try
        {
            var identityPath = Path.Combine(AppContext.BaseDirectory, "BUILD_IDENTITY.json");
            if (File.Exists(identityPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(identityPath));
                if (document.RootElement.TryGetProperty("source_revision", out var revisionElement))
                {
                    var revision = revisionElement.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(revision))
                        _buildTag = revision[..Math.Min(7, revision.Length)];
                }
            }
        }
        catch
        {
            _buildTag = "dev";
        }
        Title = $"BiliSub Studio · build {_buildTag}";
        var paths = AppPaths.FromExecutableDirectory();
        _application = new BiliSubApplication(paths);
        var folderPicker = new FolderPickerService(() => this);
        var filePicker = new FilePickerService(() => this);
        _settingsPage = new SettingsPage(new SettingsViewModel(_application.Settings, folderPicker), _application);
        _supportPage = new SupportPage(_application);
        _settingsPage.ThemeRequested += ApplyTheme;
        _pages = new Dictionary<string, UIElement>(StringComparer.Ordinal)
        {
            ["video"] = new VideoPage(_application),
            ["ocr"] = new OcrPage(_application, filePicker),
            ["editor"] = new EditorPage(_application, filePicker),
            ["hardware"] = new HardwarePage(_application),
            ["account"] = new AccountPage(_application),
            ["support"] = _supportPage,
            ["settings"] = _settingsPage,
        };
        _settingsPage.NavigateRequested += NavigateTo;
        RootGrid.Loaded += OnLoaded;
        Closed += OnClosed;
        AppWindow.Closing += OnAppWindowClosing;
        Navigation.SelectedItem = Navigation.MenuItems[0];
        ContentFrame.Content = _pages["video"];
    }

    internal Task Initialization => _initialization.Task;

    internal async Task RunLayoutSmokeAsync()
    {
        StartupDiagnostics.Write("layout-smoke-start");
        var sizes = new[]
        {
            new SizeInt32(800, 600),
            new SizeInt32(1_000, 700),
            new SizeInt32(1_500, 900),
        };
        foreach (var requested in sizes)
        {
            AppWindow.Resize(requested);
            foreach (var (tag, page) in _pages)
            {
                ContentFrame.Content = page;
                StartupDiagnostics.Write("layout-smoke-page", $"{tag}; {requested.Width}x{requested.Height}");
                await Task.Delay(120);
            }
        }
        ContentFrame.Content = _pages["video"];
        StartupDiagnostics.Write("layout-smoke-pass");
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            ResizeInitialWindow();
            await _application.InitializeAsync();
            ((VideoPage)_pages["video"]).ApplyConfiguration();
            ((OcrPage)_pages["ocr"]).ApplyConfiguration();
            var snapshot = await _settingsPage.InitializeAsync();
            ApplyTheme(snapshot.Config.Theme);
            FooterStatus.Text = _application.Sessions.LastLoadWarning
                ?? $"C#/.NET 10/WinUI 3 · native services ready · build {_buildTag}";
            if (_application.Config.CheckUpdates) _ = CheckForUpdatesOnLaunchAsync();
            StartupDiagnostics.Write("main-window-initialized", $"build={_buildTag}");
            _initialization.TrySetResult(true);
        }
        catch (Exception error)
        {
            FooterStatus.Text = "Khởi tạo lỗi: " + error.Message;
            StartupDiagnostics.WriteException("main-window-initialize-failed", error);
            _initialization.TrySetException(error);
        }
    }

    private async Task CheckForUpdatesOnLaunchAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            var info = await _application.Updates.CheckAsync(timeout.Token);
            _supportPage.ApplyUpdateInfo(info);
            if (info.Available && info.ChannelReady)
                FooterStatus.Text = $"Có bản WinUI 3 mới: {info.Latest} · mở Cập nhật & hỗ trợ";
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            _supportPage.ApplyUpdateError("Tự kiểm tra cập nhật quá thời gian; có thể thử lại thủ công.");
        }
        catch (Exception error)
        {
            _supportPage.ApplyUpdateError("Tự kiểm tra cập nhật: " + error.Message);
        }
    }

    private void Navigation_SelectionChanged(Microsoft.UI.Xaml.Controls.NavigationView sender, Microsoft.UI.Xaml.Controls.NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is string tag && _pages.TryGetValue(tag, out var page)) ContentFrame.Content = page;
    }

    private void NavigateTo(string tag)
    {
        if (!_pages.TryGetValue(tag, out var page)) return;
        ContentFrame.Content = page;
        foreach (var candidate in Navigation.MenuItems.Concat(Navigation.FooterMenuItems))
        {
            if (candidate is Microsoft.UI.Xaml.Controls.NavigationViewItem { Tag: string itemTag } item && string.Equals(itemTag, tag, StringComparison.Ordinal))
            {
                Navigation.SelectedItem = item;
                item.Focus(FocusState.Programmatic);
                return;
            }
        }
    }

    private void ApplyTheme(string theme)
    {
        RootGrid.RequestedTheme = string.Equals(theme, "light", StringComparison.Ordinal) ? ElementTheme.Light : ElementTheme.Dark;
        var dark = RootGrid.RequestedTheme == ElementTheme.Dark;
        AppWindow.TitleBar.ButtonForegroundColor = dark ? Windows.UI.Color.FromArgb(255, 243, 247, 252) : Windows.UI.Color.FromArgb(255, 20, 28, 38);
        AppWindow.TitleBar.ButtonBackgroundColor = dark ? Windows.UI.Color.FromArgb(255, 12, 18, 27) : Windows.UI.Color.FromArgb(255, 246, 248, 252);
    }

    private void ResizeInitialWindow()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var workArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min(1500, workArea.Width);
        var height = Math.Min(900, workArea.Height);
        AppWindow.GetFromWindowId(windowId).MoveAndResize(new RectInt32(
            workArea.X + Math.Max(0, (workArea.Width - width) / 2),
            workArea.Y + Math.Max(0, (workArea.Height - height) / 2),
            width,
            height));
    }

    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_safeToClose || _closing) return;
        args.Cancel = true;
        _closing = true;
        FooterStatus.Text = "Đang lưu checkpoint an toàn trước khi đóng...";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(95));
            await _application.PrepareShutdownAsync(timeout.Token);
            _application.LaunchPendingUpdate();
            _safeToClose = true;
            FooterStatus.Text = "Đã an toàn để đóng.";
            Close();
        }
        catch (Exception error)
        {
            _closing = false;
            FooterStatus.Text = "Từ chối đóng để bảo toàn dữ liệu: " + error.Message;
        }
    }

    private async void OnClosed(object sender, WindowEventArgs args) => await _application.DisposeAsync();
}
