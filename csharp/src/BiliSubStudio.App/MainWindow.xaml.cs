using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
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
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace BiliSubStudio.App;

public sealed partial class MainWindow : Window
{
    private readonly BiliSubApplication _application;
    private readonly ApplicationLog _globalLog;
    private DiscordPresenceService? _discordPresence;
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
    private bool _navigating;
    private bool _resyncingNavigation;
    private string? _pendingNavigationTag;
    private int _navigationRetries;

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
        try
        {
            // Discord presence driven by real jobs (optional): if Discord is
            // closed or errors, the service quietly retries on its own and never
            // blocks startup.
            _discordPresence = new DiscordPresenceService(_application, "1553667078077030430", _globalLog);
        }
        catch { }
        try
        {
            // The previous session may have been cut off suddenly or crashed,
            // leaving orphaned Python/FFmpeg processes holding VRAM. Clean them
            // up immediately at startup so an old checkpoint can be resumed; at
            // this point the new instance owns no workers yet, so it is safe.
            var orphans = _application.ReapOrphanOcrProcesses();
            if (orphans > 0)
                _globalLog.Warning("Ứng dụng", $"Đã dọn {orphans} process OCR/FFmpeg mồ côi từ phiên trước; checkpoint cũ (nếu còn) vẫn có thể Tiếp tục.");
        }
        catch { }
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
            ["ocr"] = new OcrPage(_application, filePicker, folderPicker),
            ["editor"] = new EditorPage(_application, filePicker),
            ["settings"] = _settingsPage,
        };
        RootGrid.Loaded += OnLoaded;
        Closed += OnClosed;
        AppWindow.Closing += OnAppWindowClosing;
        Navigation.SelectedItem = Navigation.MenuItems[0];
        ApplyNavigationIcons();
        ContentFrame.Content = _pages["video"];
        UpdateErrorBadge();
        _refreshGlobalTranslationProgress();
    }

    public ObservableCollection<GlobalLogItem> GlobalLogEntries { get; } = [];

    internal Task Initialization => _initialization.Task;

    internal async Task RunCueListStressAsync(int count, string resultPath)
    {
        ContentFrame.Content = _pages["ocr"];
        await Task.Delay(300);
        if (_pages["ocr"] is Pages.OcrPage ocrPage)
            await ocrPage.RunCueListStressAsync(count, resultPath);
    }

    internal async Task RunOcrSoakAsync(string videoPath, string reportPath)
    {
        var report = new List<string>();
        ContentFrame.Content = _pages["ocr"];
        await Task.Delay(500);
        if (_pages["ocr"] is Pages.OcrPage ocrPage)
        {
            report.Add(await ocrPage.LoadSoakStateAsync(videoPath));
            if (Services.StartupDiagnostics.SoakDeletePlayer
                && Services.StartupDiagnostics.SoakPlayerPath is { } doomed
                && System.IO.File.Exists(doomed))
            {
                System.IO.File.Delete(doomed);
                report.Add("soak-player-deleted");
                await Task.Delay(1500);
            }
            // Switch tabs the way a user would: OCR -> the other tabs -> back to OCR, for 3 rounds.
            var order = new[] { "video", "editor", "settings", "ocr" };
            async Task CycleTabsAsync(int rounds, string tag)
            {
                for (var round = 0; round < rounds; round++)
                {
                    foreach (var page in order)
                    {
                        ContentFrame.Content = _pages[page];
                        await Task.Delay(800);
                    }
                    report.Add($"tabs-{tag}-{round + 1}-done");
                }
            }
            if (Services.StartupDiagnostics.SoakScan)
            {
                var scanTask = ocrPage.StartSoakScanAsync();
                var rounds = 0;
                while (!scanTask.IsCompleted && rounds < 120)
                {
                    foreach (var tag in order)
                    {
                        ContentFrame.Content = _pages[tag];
                        await Task.Delay(800);
                        if (scanTask.IsCompleted) break;
                    }
                    rounds++;
                }
                report.Add($"scan-cycling-rounds={rounds} completed={scanTask.IsCompleted}");
                await scanTask;
                report.Add("scan-status=" + ocrPage.LastSoakStatus().Replace("\r", " ").Replace("\n", " "));
                await CycleTabsAsync(2, "postdone");
            }
            else
            {
                await CycleTabsAsync(3, "round");
            }
            report.Add("RESULT=PASS");
        }
        else
        {
            report.Add("RESULT=FAIL no-ocr-page");
        }
        await System.IO.File.WriteAllTextAsync(reportPath, string.Join(Environment.NewLine, report) + Environment.NewLine);
    }

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
        MaximizeMainWindow();
        StartupDiagnostics.Write("layout-smoke-pass");
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            ResizeInitialWindow();
            Navigation.IsPaneOpen = false;
            await _application.InitializeAsync();
            ((VideoPage)_pages["video"]).ApplyConfiguration();
            ((OcrPage)_pages["ocr"]).ApplyConfiguration();
            ((EditorPage)_pages["editor"]).ApplyConfiguration();
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
        // Ignore events raised by the machine putting the nav selection back (resync): not the user's intent.
        if (_resyncingNavigation) return;
        if (args.SelectedItemContainer?.Tag is not string tag || !_pages.TryGetValue(tag, out var page)) return;
        try
        {
            // Discord presence follows the tab: a running job wins over it, otherwise the tab is shown.
            _discordPresence?.SetLocation(tag switch
            {
                "video" => "Tải video",
                "ocr" => "Quét OCR",
                "editor" => "Edit",
                "settings" => "Cài đặt",
                _ => string.Empty,
            });
        }
        catch { }
        if (ReferenceEquals(ContentFrame.Content, page) && !_navigating) return;
        // The newest tab wins; only move to it once the switch completes, never
        // interleave while the old page is being torn down.
        _pendingNavigationTag = tag;
        _navigationRetries = 0;
        StartupDiagnostics.Write("nav-select", tag);
        if (_navigating) return;
        _navigating = true;
        // Swap the page one tick later so the old frame finishes tearing down
        // before the new page is mounted; mounting right inside the click tick
        // is exactly where the compositor tends to cancel (E_ABORT).
        DispatcherQueue.TryEnqueue(ProcessPendingNavigation);
    }

    private void ProcessPendingNavigation()
    {
        if (_pendingNavigationTag is not string tag || !_pages.TryGetValue(tag, out var page))
        {
            _navigating = false;
            return;
        }
        if (ReferenceEquals(ContentFrame.Content, page))
        {
            _pendingNavigationTag = null;
            _navigating = false;
            return;
        }
        try
        {
            StartupDiagnostics.Write("nav-swap", tag);
            // The 09/10 issue where the old page's surface overlapped the new
            // one: tear down over 2 ticks — tick 1 removes the old page so the
            // visual tree tears down fully (including releasing the OCR player
            // surface that is holding video), and the next tick mounts the new
            // page. Mounting right inside the click tick is exactly where the
            // compositor tends to cancel (E_ABORT).
            if (ContentFrame.Content is Pages.OcrPage outgoing)
                outgoing.PrepareForTabSwitch();
            if (ContentFrame.Content is not null)
            {
                ContentFrame.Content = null;
                DispatcherQueue.TryEnqueue(ProcessPendingNavigation);
                return;
            }
            ContentFrame.Content = page;
            StartupDiagnostics.Write("nav-done", tag);
        }
        catch (Exception error)
        {
            const int operationAborted = unchecked((int)0x80004004);
            // The old frame has not finished tearing down: back off and retry
            // after ~120ms so the visual tree stabilizes (at most 3 times)
            // instead of forcing it into the next tick and breaking again.
            if (error.HResult == operationAborted && _navigationRetries < 3)
            {
                _navigationRetries++;
                var timer = DispatcherQueue.CreateTimer();
                timer.Interval = TimeSpan.FromMilliseconds(120);
                timer.Tick += (_, _) => { timer.Stop(); ProcessPendingNavigation(); };
                timer.Start();
                return;
            }
            var detail = string.IsNullOrWhiteSpace(error.Message)
                ? $"{error.GetType().Name} (mã 0x{error.HResult:X8})"
                : error.Message;
            _globalLog.Error("Điều hướng", $"Chuyển sang '{tag}' lỗi: {detail}");
            StartupDiagnostics.WriteException("navigation-failed", error);
            _pendingNavigationTag = null;
        }
        _navigating = false;
        // Tab clicks queue up during a switch: finish the newest tab first, then resync the nav selection.
        if (_pendingNavigationTag is { } pending
            && !string.Equals(pending, tag, StringComparison.Ordinal)
            && _pages.ContainsKey(pending))
        {
            _navigating = true;
            DispatcherQueue.TryEnqueue(ProcessPendingNavigation);
            return;
        }
        _pendingNavigationTag = null;
        SyncNavigationSelection();
    }

    private void SyncNavigationSelection()
    {
        // Only adjust the nav selection's appearance to match the page being
        // shown; this spot must never throw (throwing here once dragged native
        // into a crash right afterward).
        try
        {
            _resyncingNavigation = true;
            foreach (var collection in new[] { Navigation.MenuItems, Navigation.FooterMenuItems })
            {
                foreach (var item in collection.OfType<NavigationViewItem>())
                {
                    if (item.Tag is string itemTag && _pages.TryGetValue(itemTag, out var page)
                        && ReferenceEquals(ContentFrame.Content, page))
                    {
                        if (!ReferenceEquals(Navigation.SelectedItem, item))
                            Navigation.SelectedItem = item;
                        return;
                    }
                }
            }
        }
        catch
        {
        }
        finally
        {
            _resyncingNavigation = false;
        }
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
        // Scrolling the list is cosmetic: if layout hangs (E_ABORT), ignore it, never break the app.
        try
        {
            if (GlobalLogEntries.Count > 0) GlobalLogList.ScrollIntoView(GlobalLogEntries[^1]);
        }
        catch
        {
        }

        if (entry.Level == AppLogLevel.Error)
        {
            _errorCount++;
            UpdateErrorBadge();
            ShowGlobalLog(true);
        }
    }

    /// <summary>
    /// A compact left rail: every navigation item uses Segoe Fluent Icons so
    /// the selected item is still recognizable when the pane is closed (the
    /// text label lives in the tooltip).
    /// </summary>
    private void ApplyNavigationIcons()
    {
        var glyphs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["video"] = "\uE896",
            ["ocr"] = "\uE8A5",
            ["editor"] = "\uE714",
            ["settings"] = "\uE713",
        };
        foreach (var item in Navigation.MenuItems.OfType<NavigationViewItem>())
        {
            if (item.Tag is not string tag || !glyphs.TryGetValue(tag, out var glyph)) continue;
            item.Icon = new FontIcon
            {
                Glyph = glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 18,
            };
            ToolTipService.SetToolTip(item, item.Content?.ToString() ?? tag);
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
        // The 07-08/10 issue: a two-monitor setup (monitor 2 powered off but
        // Windows still keeping it) — opening the app dropped it onto a ghost
        // monitor at x=1529, and the user saw nothing. DisplayArea
        // (Primary/GetFromWindowId/GetFromPoint) are all untrustworthy during
        // cold startup (they return the ghost monitor/ghost coordinates
        // inconsistently). Use GetSystemMetrics (the PRIMARY monitor's size,
        // which never races) plus a hard clamp to the screen.
        var primaryWidth = GetSystemMetrics(0);
        var primaryHeight = GetSystemMetrics(1);
        if (primaryWidth < 800 || primaryHeight < 600)
        {
            var fallback = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary).WorkArea;
            primaryWidth = fallback.Width;
            primaryHeight = fallback.Height;
        }
        var width = Math.Min(1500, primaryWidth);
        var height = Math.Min(900, primaryHeight);
        var x = Math.Max(0, (primaryWidth - width) / 2);
        var y = Math.Max(0, (primaryHeight - height) / 2);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        // Open full-screen by default: a 1500x900 frame hides the player and
        // the timeline on large screens. Still clamp to the primary monitor as
        // before.
        if (appWindow.Presenter is OverlappedPresenter overlapped)
        {
            overlapped.Maximize();
            return;
        }
        appWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    private void MaximizeMainWindow()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
            if (appWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();
        }
        catch (Exception error)
        {
            StartupDiagnostics.WriteException("main-window-maximize-failed", error);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out PointInt32 point);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

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
            // EDIT-CLOSE: silently blocking makes the user think the app is
            // frozen. Report it with a dialog and offer a deliberate way out
            // instead of forcing a restart or log hunting.
            await ShowCloseRefusedDialogAsync(error.Message);
        }
    }

    // RELINK (Adobe style): if the source file is missing, state it clearly
    // and let the user pick the new path instead of blocking silently. Once
    // relinked, continue exiting; there is still an option to drop the project
    // when the user does not want to keep it.
    private async Task ShowCloseRefusedDialogAsync(string reason)
    {
        var editorHasOpenProject = _pages.TryGetValue("editor", out var editorElement)
            && editorElement is EditorPage editorPage && editorPage.HasOpenEditorProject;
        var missingPath = reason;
        const string marker = "Video nguồn của project Editor không tồn tại.";
        var missingIndex = reason.IndexOf(marker, StringComparison.Ordinal);
        if (missingIndex >= 0)
            missingPath = reason[(missingIndex + marker.Length)..].Trim();
        ContentDialog dialog;
        try
        {
            dialog = new ContentDialog
            {
                Title = missingIndex >= 0 ? "Không tìm thấy video nguồn" : "Không thể đóng app ngay",
                Content = missingIndex >= 0
                    ? "Không tìm thấy file ở đường dẫn cũ:\n" + missingPath + "\n\nFile có thể đã bị xóa, đổi tên hoặc di chuyển sau khi làm xong. Chọn lại file đó ở vị trí mới để giữ nguyên project và thoát, hoặc bỏ project và thoát luôn."
                    : reason,
                CloseButtonText = "Ở lại xử lý",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
            };
            if (missingIndex >= 0 && editorHasOpenProject)
                dialog.PrimaryButtonText = "Chọn lại file...";
            if (editorHasOpenProject)
                dialog.SecondaryButtonText = "Bỏ project và thoát";
        }
        catch { return; }
        ContentDialogResult choice;
        try { choice = await dialog.ShowAsync(); }
        catch { return; }
        try
        {
            if (choice == ContentDialogResult.Primary
                && _pages["editor"] is EditorPage relinkEditor
                && await relinkEditor.RelinkMissingEditorSourceAsync())
            {
                FooterStatus.Text = "Đã nối lại video nguồn; đang thoát...";
                _globalLog.Info("Ứng dụng", "User nối lại video nguồn để thoát app.");
                Close();
            }
            else if (choice == ContentDialogResult.Secondary
                && _pages["editor"] is EditorPage discardEditor)
            {
                discardEditor.DiscardOpenProjectForForcedClose();
                FooterStatus.Text = "Đã bỏ project Editor đang mở theo yêu cầu; đang thoát...";
                _globalLog.Info("Ứng dụng", "User bỏ project Editor đang mở để thoát app.");
                Close();
            }
        }
        catch (Exception discardError)
        {
            FooterStatus.Text = "Chưa thoát được: " + discardError.Message;
            _globalLog.Error("Ứng dụng", "Chưa thoát được: " + discardError.Message);
        }
    }

    // Discord presence: pages push the name of the video/project being worked on (best-effort).
    public void SetPresenceContext(string? video, string? project)
    {
        try { _discordPresence?.SetContext(video, project); } catch { }
    }

    private async void OnClosed(object sender, WindowEventArgs args)
    {
        _jobProgressTimer.Stop();
        _globalLog.EntryAdded -= OnGlobalLogEntry;
        try { _discordPresence?.Dispose(); } catch { }
        if (_pages["video"] is VideoPage videoPage) videoPage.StopMonitoring();
        await _application.DisposeAsync();
    }
}
