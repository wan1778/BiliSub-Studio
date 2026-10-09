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
            // Discord presence theo job thật (không bắt buộc): Discord tắt hay
            // lỗi thì service tự lặng lẽ thử lại, không bao giờ chặn khởi động.
            _discordPresence = new DiscordPresenceService(_application, "1553667078077030430", _globalLog);
        }
        catch { }
        try
        {
            // Phiên trước có thể đã bị tắt đột ngột/crash, để lại Python/FFmpeg
            // mồ côi giữ VRAM. Dọn ngay khi khởi động để checkpoint cũ Tiếp tục
            // được; lúc này instance mới chưa sở hữu worker nào nên an toàn.
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
            // Đảo tab đúng kiểu user: OCR -> các tab khác -> về OCR, lặp 3 vòng.
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
        // Bỏ qua sự kiện do máy tự trả thanh chọn về (resync): không phải ý user.
        if (_resyncingNavigation) return;
        if (args.SelectedItemContainer?.Tag is not string tag || !_pages.TryGetValue(tag, out var page)) return;
        try
        {
            // Discord presence bám tab: có job chạy thì job thắng, không thì hiện tab.
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
        // Tab mới nhất thắng; đổi xong mới qua đó, không chen ngang lúc đang dỡ trang.
        _pendingNavigationTag = tag;
        _navigationRetries = 0;
        StartupDiagnostics.Write("nav-select", tag);
        if (_navigating) return;
        _navigating = true;
        // Đổi trang sau 1 nhịp để khung hình cũ dỡ xong hẳn rồi mới lắp trang mới;
        // lắp ngay trong nhịp bấm chính là chỗ hay bị thư viện màn hình hủy (E_ABORT).
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
            ContentFrame.Content = page;
            StartupDiagnostics.Write("nav-done", tag);
        }
        catch (Exception error)
        {
            const int operationAborted = unchecked((int)0x80004004);
            // Khung hình cũ chưa dỡ xong: lùi lại thử sau ~120ms cho cây hình ổn
            // định (tối đa 3 lần) thay vì chen ngay nhịp sau rồi gãy tiếp.
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
        // Tab bấm dồn trong lúc đổi: qua nốt tab mới nhất, xong mới đồng bộ thanh chọn.
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
        // Chỉ sửa hiển thị thanh chọn cho khớp trang đang hiện; chỗ này tuyệt đối
        // không được nổ (nổ ở đây từng kéo gãy native ngay sau đó).
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
        // Cuộn list là việc trang trí: kẹt layout (E_ABORT) thì bỏ qua, không được gãy app.
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
        // Vụ 07-08/10: máy 2 màn (màn 2 tắt nhưng Windows vẫn giữ) — mở app
        // là lọt sang màn ma ở x=1529, user không thấy gì. DisplayArea
        // (Primary/GetFromWindowId/GetFromPoint) đều không tin được lúc khởi
        // động lạnh (trả màn ma/tọa độ ma thất thường). Dùng GetSystemMetrics
        // (kích thước màn CHÍNH, không bao giờ race) + kẹp chặt trong màn hình.
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
        AppWindow.GetFromWindowId(windowId).MoveAndResize(new RectInt32(x, y, width, height));
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
            // EDIT-CLOSE: chặn im lặng khiến user tưởng app treo. Báo bằng hộp
            // thoại và cho lối thoát chủ động thay vì bắt restart/mò log.
            await ShowCloseRefusedDialogAsync(error.Message);
        }
    }

    // RELINK (kiểu Adobe): mất file nguồn thì báo rõ và cho user chọn lại
    // đường dẫn thay vì chặn im lặng. Nối xong thì thoát tiếp; vẫn có lối
    // bỏ project khi user không cần giữ.
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

    // Discord presence: page đẩy tên video/project đang làm (best-effort).
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
