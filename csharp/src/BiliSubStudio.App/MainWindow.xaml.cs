using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using BiliSubStudio.App.Pages;
using BiliSubStudio.App.Services;
using BiliSubStudio.App.ViewModels;
using BiliSubStudio.Core.Application;
using BiliSubStudio.Core.Configuration;
using BiliSubStudio.Core.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace BiliSubStudio.App;

public sealed partial class MainWindow : Window
{
    private readonly BiliSubApplication _application;
    private readonly ApplicationLog _globalLog;
    private readonly SettingsPage _settingsPage;
    private readonly SupportPage _supportPage;
    private readonly Dictionary<string, UIElement> _pages;
    private readonly TaskCompletionSource<bool> _initialization = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _buildTag;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _jobProgressTimer;
    private readonly Action _refreshGlobalTranslationProgress;
    private bool _initialized;
    private bool _safeToClose;
    private bool _closing;
    private int _errorCount;

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
        _globalLog = new ApplicationLog(paths.Data);
        _globalLog.EntryAdded += OnGlobalLogEntry;
        LogFileText.Text = "Lưu bền vững: " + _globalLog.FilePath;
        _globalLog.Info("Ứng dụng", $"Khởi tạo BiliSub Studio · build {_buildTag}.");

        _application = new BiliSubApplication(paths);
        _application.Jobs.AttachLog(_globalLog);
        _refreshGlobalTranslationProgress = () =>
        {
            var snapshot = _application.Jobs.ActiveSnapshots()
                .FirstOrDefault(x => x.Kind is "translation" or "translation-prepare");
            if (snapshot is null)
            {
                GlobalJobProgressPanel.Visibility = Visibility.Collapsed;
                GlobalJobProgressBar.Value = 0;
                GlobalJobProgressPercent.Text = "0%";
                GlobalJobProgressText.Text = string.Empty;
                return;
            }

            GlobalJobProgressPanel.Visibility = Visibility.Visible;
            GlobalJobProgressTitle.Text = snapshot.Kind == "translation-prepare" ? "Chuẩn bị AI Vietsub" : "Vietsub AI local";
            GlobalJobProgressBar.Value = Math.Clamp(snapshot.Progress, 0, 100);
            GlobalJobProgressPercent.Text = $"{Math.Clamp(snapshot.Progress, 0, 100):0.#}%";
            GlobalJobProgressText.Text = snapshot.Message;
        };
        _jobProgressTimer = DispatcherQueue.CreateTimer();
        _jobProgressTimer.Interval = TimeSpan.FromMilliseconds(350);
        _jobProgressTimer.Tick += (_, _) => _refreshGlobalTranslationProgress();
        _jobProgressTimer.Start();
        var folderPicker = new FolderPickerService(() => this);
        var filePicker = new FilePickerService(() => this);
        var hardwarePage = new HardwarePage(_application, _globalLog);
        var accountPage = new AccountPage(_application, _globalLog);
        _supportPage = new SupportPage(_application, _globalLog);
        _settingsPage = new SettingsPage(
            new SettingsViewModel(_application.Settings, folderPicker),
            _application,
            hardwarePage,
            accountPage,
            _supportPage,
            _globalLog);
        _settingsPage.ThemeRequested += ApplyTheme;
        _pages = new Dictionary<string, UIElement>(StringComparer.Ordinal)
        {
            ["video"] = new VideoPage(_application, folderPicker, _globalLog),
            ["ocr"] = new OcrPage(_application, filePicker),
            ["editor"] = new EditorPage(_application, filePicker),
            ["settings"] = _settingsPage,
        };
        RootGrid.Loaded += OnLoaded;
        Closed += OnClosed;
        AppWindow.Closing += OnAppWindowClosing;
        Navigation.SelectedItem = Navigation.MenuItems[0];
        ContentFrame.Content = _pages["video"];
        UpdateErrorBadge();
        _refreshGlobalTranslationProgress();
    }

    public ObservableCollection<GlobalLogItem> GlobalLogEntries { get; } = [];

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
                if (string.Equals(tag, "settings", StringComparison.Ordinal))
                    await _settingsPage.RunLayoutSmokeAsync();
                else if (string.Equals(tag, "editor", StringComparison.Ordinal) && page is EditorPage editorPage)
                    await editorPage.RunLayoutSmokeAsync();
            }
            ShowGlobalLog(true);
            await Task.Delay(100);
            ShowGlobalLog(false);
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
                ?? $"Native services ready · build {_buildTag}";
            if (_application.Sessions.LastLoadWarning is { Length: > 0 } sessionWarning)
                _globalLog.Warning("Đăng nhập", sessionWarning);
            _globalLog.Info("Ứng dụng", "Khởi tạo native services hoàn tất.");
            if (_application.Config.CheckUpdates) _ = CheckForUpdatesOnLaunchAsync();
            StartupDiagnostics.Write("main-window-initialized", $"build={_buildTag}");
            _initialization.TrySetResult(true);
        }
        catch (Exception error)
        {
            FooterStatus.Text = "Khởi tạo lỗi: " + error.Message;
            _globalLog.Error("Ứng dụng", "Khởi tạo lỗi: " + error.Message);
            StartupDiagnostics.WriteException("main-window-initialize-failed", error);
            _initialization.TrySetException(error);
        }
    }

    private async Task CheckForUpdatesOnLaunchAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            _globalLog.Info("Cập nhật", "Đang tự kiểm tra kênh cập nhật.");
            var info = await _application.Updates.CheckAsync(timeout.Token);
            _supportPage.ApplyUpdateInfo(info);
            _globalLog.Info("Cập nhật", info.Message);
            if (info.Available && info.ChannelReady)
                FooterStatus.Text = $"Có bản WinUI 3 mới: {info.Latest} · mở Cài đặt > Cập nhật & hỗ trợ";
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            const string message = "Tự kiểm tra cập nhật quá thời gian; có thể thử lại thủ công.";
            _supportPage.ApplyUpdateError(message);
            _globalLog.Warning("Cập nhật", message);
        }
        catch (Exception error)
        {
            _supportPage.ApplyUpdateError("Tự kiểm tra cập nhật: " + error.Message);
            _globalLog.Error("Cập nhật", "Tự kiểm tra cập nhật lỗi: " + error.Message);
        }
    }

    private void Navigation_SelectionChanged(Microsoft.UI.Xaml.Controls.NavigationView sender, Microsoft.UI.Xaml.Controls.NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is string tag && _pages.TryGetValue(tag, out var page)) ContentFrame.Content = page;
    }

    private void ApplyTheme(string theme)
    {
        RootGrid.RequestedTheme = string.Equals(theme, "light", StringComparison.Ordinal) ? ElementTheme.Light : ElementTheme.Dark;
        var dark = RootGrid.RequestedTheme == ElementTheme.Dark;
        AppWindow.TitleBar.ButtonForegroundColor = dark ? Windows.UI.Color.FromArgb(255, 243, 247, 252) : Windows.UI.Color.FromArgb(255, 20, 28, 38);
        AppWindow.TitleBar.ButtonBackgroundColor = dark ? Windows.UI.Color.FromArgb(255, 12, 18, 27) : Windows.UI.Color.FromArgb(255, 246, 248, 252);
    }

    private void OnGlobalLogEntry(AppLogEntry entry)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => OnGlobalLogEntry(entry));
            return;
        }

        GlobalLogEntries.Add(new GlobalLogItem(entry));
        while (GlobalLogEntries.Count > 500) GlobalLogEntries.RemoveAt(0);
        if (GlobalLogEntries.Count > 0) GlobalLogList.ScrollIntoView(GlobalLogEntries[^1]);

        if (entry.Level == AppLogLevel.Error)
        {
            _errorCount++;
            UpdateErrorBadge();
            ShowGlobalLog(true);
        }
    }

    private void UpdateErrorBadge()
    {
        var hasErrors = _errorCount > 0;
        LogHealthyCountBorder.Visibility = hasErrors ? Visibility.Collapsed : Visibility.Visible;
        LogErrorCountBorder.Visibility = hasErrors ? Visibility.Visible : Visibility.Collapsed;
        LogErrorCount.Text = $"{_errorCount} lỗi";
    }

    private void GlobalLogToggle_Click(object sender, RoutedEventArgs e) =>
        ShowGlobalLog(GlobalLogPanel.Visibility != Visibility.Visible);

    private void CollapseLog_Click(object sender, RoutedEventArgs e) => ShowGlobalLog(false);

    private void ClearLogView_Click(object sender, RoutedEventArgs e)
    {
        GlobalLogEntries.Clear();
        _errorCount = 0;
        UpdateErrorBadge();
        _globalLog.Info("Nhật ký", "Đã xóa phần hiển thị; file log bền vững vẫn được giữ.");
    }

    private void OpenLogFile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{_globalLog.FilePath}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception error)
        {
            _globalLog.Error("Nhật ký", "Không mở được file log: " + error.Message);
        }
    }

    private void ShowGlobalLog(bool show)
    {
        GlobalLogPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) _refreshGlobalTranslationProgress();
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
        _globalLog.Info("Ứng dụng", "Đang chuẩn bị đóng an toàn.");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(95));
            if (_pages["editor"] is EditorPage editorPage)
                await editorPage.FlushForAppCloseAsync(timeout.Token);
            await _application.PrepareShutdownAsync(timeout.Token);
            _application.LaunchPendingUpdate();
            _safeToClose = true;
            FooterStatus.Text = "Đã an toàn để đóng.";
            _globalLog.Info("Ứng dụng", "Đóng an toàn hoàn tất.");
            Close();
        }
        catch (Exception error)
        {
            _closing = false;
            FooterStatus.Text = "Từ chối đóng để bảo toàn dữ liệu: " + error.Message;
            _globalLog.Error("Ứng dụng", "Từ chối đóng để bảo toàn dữ liệu: " + error.Message);
        }
    }

    private async void OnClosed(object sender, WindowEventArgs args)
    {
        _jobProgressTimer.Stop();
        _globalLog.EntryAdded -= OnGlobalLogEntry;
        await _application.DisposeAsync();
    }
}
