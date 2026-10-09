using BiliSubStudio.App.Services;
using BiliSubStudio.Core.Application;
using BiliSubStudio.Core.Jobs;
using BiliSubStudio.Core.Media;
using BiliSubStudio.Core.Ocr;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Collections.ObjectModel;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;

namespace BiliSubStudio.App.Pages;

public sealed partial class OcrPage : Page
{
    private readonly BiliSubApplication _application;
    private readonly IFilePickerService _picker;
    private readonly IFolderPickerService _folderPicker;
    private string _ocrOutputDirectory = string.Empty;
    private string? _path;
    private MediaPreviewInfo? _media;
    private string? _jobId;
    private OcrScanRequest? _checkpointRequest;
    private OcrScanRequest? _activeRequest;
    private bool _cancelInProgress;
    private bool _listening;
    // Hàng chờ OCR (09/10): quét nhiều video nối tiếp, mỗi video giữ setup
    // riêng lúc thêm, lỗi bỏ qua chạy tiếp, chỉ sống trong phiên app.
    private readonly OcrScanQueue _ocrQueue = new();
    private readonly ObservableCollection<OcrQueueItem> _ocrQueueView = [];
    private bool _queueRunning;
    private string? _listenProjectId;
    private IReadOnlyList<OcrCue> _cues = [];
    // Dòng đang hiển thị = dòng pipeline chung (OcrExportRow: Start/End/Text
    // đã loại-trừ/đắp-fix/normalize/trim — live-quét-xong = SRT theo cấu trúc).
    private IReadOnlyList<OcrExportRow> _visibleCues = [];
    // Chế độ hiển thị: true = quét xong/dừng (list qua pipeline chung, bằng
    // SRT); false = đang quét (list tạm từ accumulator + journal, bản chất
    // chưa chốt nên chỉ là preview).
    private bool _authoritative;
    private IReadOnlyList<OcrExportRow> _displayRows = [];
    private IReadOnlyList<OcrCueExclusion>? _displayExclusions;
    private IReadOnlyList<OcrCueFix>? _displayFixes;
    private string _displayDropNote = "";
    // LIFO các câu user tự xoá khỏi list chốt (21/09): mỗi phần tử là entry đã
    // ghi vào .exclude.json + số thứ tự lúc xoá (để báo lại khi hoàn tác).
    // Hoàn tác = gỡ đúng entry đó khỏi file — quyết định của user vẫn nằm
    // ngoài checkpoint, resume/quét lại không đụng.
    private readonly Stack<(OcrCueExclusion Entry, int RowNumber)> _manualDeletes = new();
    // Verdict lọc thời gian trên bảng live: câu nào bị drop thì ẨN khỏi list
    // (list = SRT 100%). Đọc từ nhật ký temporal đang ghi live lúc quét
    // (append-only nên so số lượng là đủ; đổi là vẽ lại toàn bảng 1 lần).
    private Dictionary<string, OcrCueVerdict> _judgeMarks = new(StringComparer.Ordinal);
    private readonly OcrLiveCueAccumulator _liveCues = new();
    private DateTimeOffset _nextCueRenderAt;
    private bool _cueViewDirty;
    // Live-follow: true khi viewport đang ở cuối danh sách (tự follow cue mới),
    // false khi user tự scroll lên xem cue cũ (giữ nguyên vị trí, resume khi quay lại gần cuối).
    private bool _userAtEnd = true;
    private ScrollViewer? _cueScrollViewer;
    private OcrRegion _region = new(0.05, 0.65, 0.90, 0.29);
    private OcrRegion _dragOriginRegion = new(0.05, 0.65, 0.90, 0.29);
    private Point? _dragStart;
    private RoiDragMode _roiDragMode;
    private MediaPlayer? _player;
    private bool _playerMode;
    private bool _syncingTimeline;
    private bool _syncingCue;

    public OcrPage(BiliSubApplication application, IFilePickerService picker, IFolderPickerService folderPicker)
    {
        _application = application;
        _picker = picker;
        _folderPicker = folderPicker;
        InitializeComponent();
        PreviewCanvas.SizeChanged += (_, _) => ApplyRegionVisual();

        ApplyConfiguration();
        CueList.Loaded += (_, _) => AttachCueListScroll();
        Unloaded += (_, _) => DetachCueListScroll();
        Unloaded += (_, _) =>
        {
            Services.StartupDiagnostics.Write("ocr-unloaded", $"cues={_cues.Count} player={_player is not null} job={_jobId is not null}");
            _player?.Pause();
        };
        _ocrQueue.Changed += SyncQueueView;
        QueueList.ItemsSource = _ocrQueueView;
        var judgeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        judgeTimer.Tick += (_, _) =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            RefreshJudgeMarks();
            sw.Stop();
            if (sw.ElapsedMilliseconds >= 1000)
                Services.StartupDiagnostics.Write("judge-slow-tick", $"ms={sw.ElapsedMilliseconds:0} cues={_cues.Count}");
        };
        judgeTimer.Start();
    }

    // Test-only stress hook (chỉ chạy với flag --ocr-list-stress, không ảnh hưởng production):
    // load synthetic cues qua đúng RenderCues production, scroll đầu/giữa/cuối, đếm realized
    // containers, kiểm tra follow/selection, ghi kết quả ra file. Không scan video.
    internal async Task RunCueListStressAsync(int count, string resultPath)
    {
        var report = new List<string>();
        void Record(string line) => report.Add(line);
        try
        {
            AttachCueListScroll();
            if (_cueScrollViewer is null) throw new InvalidOperationException("no ListView ScrollViewer");
            // Cue bắt đầu từ t=100 để Timeline.Value=0 không khớp cue nào (loại nhiễu selection
            // khỏi phép đo follow/scroll; selection test riêng ở dưới).
            _cues = Enumerable.Range(0, count)
                .Select(i => new OcrCue(100 + i * 2.0, 100 + i * 2.0 + 1.5, $"Cue {i + 1} 测试字幕内容", .9))
                .ToList();
            _userAtEnd = true;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            RenderCues();
            await Task.Delay(500);
            sw.Stop();
            Record($"count={CueList.Items.Count}");
            Record($"initialRenderMs={sw.Elapsed.TotalMilliseconds:0}");
            Record($"realizedTop={CountRealizedCueItems()}");
            await ScrollCueListTo(_cueScrollViewer.ScrollableHeight / 2);
            Record($"realizedMiddle={CountRealizedCueItems()}");
            await ScrollCueListTo(_cueScrollViewer.ScrollableHeight);
            Record($"realizedEnd={CountRealizedCueItems()}");

            // C1: đang ở cuối -> append -> tự follow.
            var maxBefore = _cueScrollViewer.ScrollableHeight;
            AppendStressCues(50);
            Record($"followAtEnd={IsNearCueListEnd()} offsetBefore={maxBefore:0} offsetAfter={_cueScrollViewer.VerticalOffset:0} maxAfter={_cueScrollViewer.ScrollableHeight:0}");

            // C2: scroll lên đầu -> append nhiều -> KHÔNG kéo xuống.
            await ScrollCueListTo(0);
            var topBefore = _cueScrollViewer.VerticalOffset;
            AppendStressCues(100);
            Record($"noYankOnScrollUp={Math.Abs(_cueScrollViewer.VerticalOffset - topBefore) < OcrLiveViewPolicy.FollowThresholdPx} offsetBefore={topBefore:0} offsetAfter={_cueScrollViewer.VerticalOffset:0}");

            // C3: quay lại gần cuối -> append -> follow lại.
            await ScrollCueListTo(_cueScrollViewer.ScrollableHeight - 32);
            AppendStressCues(50);
            Record($"resumeNearEnd={IsNearCueListEnd()}");

            // D: selection giữ qua scroll xa. Mở rộng Timeline range theo synthetic times
            // để seek không bị clamp về Maximum mặc định (tránh round-trip giả do clamp).
            Timeline.Maximum = 100 + count * 2.0 + 1000;
            CueList.SelectedIndex = 1234;
            await Task.Delay(200);
            await ScrollCueListTo(0);
            await ScrollCueListTo(_cueScrollViewer.ScrollableHeight);
            var selectedOk = CueList.SelectedIndex == 1234;
            var textOk = CueList.Items.Count > 1234 && (CueList.Items[1234]?.ToString() ?? "").Contains("1235.");
            Record($"selectionKept={selectedOk} selectedTextOk={textOk}");

            // E: memory.
            Record($"peakWorkingSetMB={System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64 / 1048576}");
            Record($"managedMB={GC.GetTotalMemory(false) / 1048576}");
            Record("RESULT=PASS");
        }
        catch (Exception error)
        {
            report.Insert(0, "RESULT=FAIL " + error.Message);
        }
        await System.IO.File.WriteAllTextAsync(resultPath, string.Join(Environment.NewLine, report) + Environment.NewLine);
    }

    // Test-only soak (chỉ chạy với flag --ocr-soak-video/--ocr-soak-out):
    // dựng đúng trạng thái "OCR xong" như PickVideo + resume checkpoint mới nhất
    // (player thật, cues authoritative, journal, timer) nhưng KHÔNG quét.
    internal async Task<string> LoadSoakStateAsync(string videoPath)
    {
        try
        {
            _checkpointRequest = null;
            _activeRequest = null;
            _cues = [];
            _visibleCues = [];
            _authoritative = false;
            _liveCues.Clear();
            _nextCueRenderAt = default;
            _userAtEnd = true;
            CueList.Items.Clear();
            CueCountText.Text = "0 câu";
            CancelButton.IsEnabled = false;
            ResetManualEdits();
            _path = videoPath;
            PathText.Text = videoPath;
            _media = await _application.Media.ProbeAsync(videoPath, CancellationToken.None);
            Timeline.Maximum = Math.Max(0.1, _media.Duration);
            Timeline.Value = 0;
            await PreparePlayerAsync(videoPath, _media.DirectCompatible);
            MediaText.Text = $"{_media.Width}×{_media.Height} · {_media.Duration:0.0}s · {_media.Codec}";
            await UpdateFrameAsync();
            ApplyRegionVisual();
            Timeline.IsEnabled = true;
            var sameVideo = _application.FindOcrVideoCheckpoints(videoPath);
            var newest = sameVideo.FirstOrDefault();
            if (newest is null) return "SOAK-LOAD: no-checkpoint";
            _region = newest.Region;
            SelectValue(ScanModeBox, newest.Mode);
            SelectValue(DeviceBox, newest.Device);
            SelectValue(LanesBox, newest.Parallelism);
            ApplyRegionVisual();
            var checkpointRequest = BuildRequest();
            var checkpoint = await _application.InspectOcrCheckpointAsync(checkpointRequest, CancellationToken.None);
            if (!checkpoint.Exists) return "SOAK-LOAD: inspect-miss";
            _checkpointRequest = checkpointRequest;
            ApplyRecoverableCheckpointCues(checkpoint);
            ApplyCheckpointUi();
            await Task.Delay(3000);
            var summary = $"SOAK-LOAD: cues={_cues.Count} authoritative={_authoritative} progress={checkpoint.ProgressPercent:0.0}";
            var altPlayer = Services.StartupDiagnostics.SoakPlayerPath;
            if (!string.IsNullOrWhiteSpace(altPlayer) && System.IO.File.Exists(altPlayer))
            {
                await PreparePlayerAsync(altPlayer, directCompatible: true);
                summary += " +altplayer";
            }
            return summary;
        }
        catch (Exception error)
        {
            return "SOAK-LOAD-FAIL: " + error.Message + " | arglen=" + videoPath.Length
                + " | exists=" + System.IO.File.Exists(videoPath)
                + " | arg=" + videoPath;
        }
    }

    // Test-only: quét Fresh thật trên video soak đã load (chế độ nhanh/cpu/4 lane
    // cho xong trong vài phút). Trả Task để harness đảo tab giữa chừng.
    internal Task StartSoakScanAsync()
    {
        SelectValue(ScanModeBox, "fast");
        SelectValue(DeviceBox, "cpu");
        SelectValue(LanesBox, "4");
        return StartScanAsync(BuildRequest(), OcrScanStartMode.Fresh);
    }

    internal string LastSoakStatus() => StatusText.Text;

    private void AppendStressCues(int add)
    {
        var start = _cues.Count == 0 ? 100.0 : _cues[^1].End + 0.5;
        var extra = Enumerable.Range(0, add)
            .Select(i => new OcrCue(start + i * 2.0, start + i * 2.0 + 1.5, $"Cue {_cues.Count + i + 1} 测试字幕内容", .9))
            .ToList();
        _cues = _cues.Concat(extra).ToList();
        RenderCues();
    }

    private bool IsNearCueListEnd()
    {
        if (_cueScrollViewer is null) return false;
        return OcrLiveViewPolicy.IsNearEnd(
            _cueScrollViewer.VerticalOffset,
            _cueScrollViewer.ViewportHeight,
            _cueScrollViewer.ScrollableHeight);
    }

    private async Task ScrollCueListTo(double offset)
    {
        if (_cueScrollViewer is null) return;
        _cueScrollViewer.ChangeView(null, Math.Max(0, offset), null, true);
        var last = -1.0;
        var stable = 0;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100);
            var current = _cueScrollViewer.VerticalOffset;
            if (Math.Abs(current - last) < 0.5 && ++stable >= 2) return;
            stable = 0;
            last = current;
        }
    }

    private int CountRealizedCueItems()
    {
        var found = 0;
        CountListViewItems(CueList, ref found);
        return found;
    }

    private static void CountListViewItems(DependencyObject root, ref int found)
    {
        if (root is ListViewItem) found++;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
            CountListViewItems(VisualTreeHelper.GetChild(root, i), ref found);
    }
    private void AttachCueListScroll()
    {
        DetachCueListScroll();
        _cueScrollViewer = FindDescendantScrollViewer(CueList);
        if (_cueScrollViewer is not null)
            _cueScrollViewer.ViewChanged += CueListViewChanged;
    }

    private void DetachCueListScroll()
    {
        if (_cueScrollViewer is not null)
            _cueScrollViewer.ViewChanged -= CueListViewChanged;
        _cueScrollViewer = null;
    }

    private static ScrollViewer? FindDescendantScrollViewer(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer) return viewer;
            var nested = FindDescendantScrollViewer(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private void CueListViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_cueScrollViewer is null) return;
        _userAtEnd = OcrLiveViewPolicy.IsNearEnd(
            _cueScrollViewer.VerticalOffset,
            _cueScrollViewer.ViewportHeight,
            _cueScrollViewer.ScrollableHeight);
    }

    public void ApplyConfiguration()
    {
        var left = _application.Config.OcrLeft / 100d;
        var top = _application.Config.OcrTop / 100d;
        var right = _application.Config.OcrRight / 100d;
        var bottom = _application.Config.OcrBottom / 100d;
        _region = OcrCheckpointStoreProxy.Normalize(new OcrRegion(left, top, right - left, bottom - top));
        SetOcrOutputDirectory(_application.Config.OcrOutputDirectory);
        for (var index = 0; index < DeviceBox.Items.Count; index++)
        {
            if (string.Equals(DeviceBox.Items[index]?.ToString(), _application.Config.OcrDevice, StringComparison.OrdinalIgnoreCase))
            {
                DeviceBox.SelectedIndex = index;
                break;
            }
        }
        RefreshRegionActions();
        ApplyRegionVisual();
    }

    private async void PickVideo_Click(object sender, RoutedEventArgs e)
    {
        if (_jobId is not null || _queueRunning)
        {
            StatusText.Text = "Đang quét OCR; hãy Tạm dừng hoặc Hủy trước khi đổi video.";
            return;
        }
        var selected = await _picker.PickVideoAsync();
        if (selected is null) return;
        try
        {
            _checkpointRequest = null;
            _activeRequest = null;
            _cues = [];
            _visibleCues = [];
            _authoritative = false;
            _liveCues.Clear();
            _nextCueRenderAt = default;
            _cueViewDirty = false;
            _userAtEnd = true;
            CueList.Items.Clear();
            CueCountText.Text = "0 câu";
            CancelButton.IsEnabled = false;
            ResetManualEdits();
            ScanButton.Content = "Quét từ đầu";
            ScanButton.IsEnabled = false;
            RestartButton.Visibility = Visibility.Collapsed;
            ExportButton.IsEnabled = false;
            Progress.Value = 0;
            TelemetryText.Text = "Chưa có telemetry.";
            OcrResultText.Text = string.Empty;
            _path = selected;
            PathText.Text = selected;
            PublishPresenceContext(Path.GetFileName(selected), null);
            StatusText.Text = "Đang đọc video...";
            _media = await _application.Media.ProbeAsync(selected, CancellationToken.None);
            Timeline.Maximum = Math.Max(0.1, _media.Duration);
            Timeline.Value = 0;
            await PreparePlayerAsync(selected, _media.DirectCompatible);
            MediaText.Text = $"{_media.Width}×{_media.Height} · {_media.Duration:0.0}s · {_media.Codec} · {(_media.DirectCompatible ? "player native + frame FFmpeg" : "frame FFmpeg fallback")}";
            await UpdateFrameAsync();
            ApplyRegionVisual();
            StatusText.Text = "Video đã sẵn sàng. Bật Chỉnh ROI để tạo, di chuyển hoặc đổi kích thước vùng phụ đề.";
            Timeline.IsEnabled = true;
            SelectRegionButton.IsEnabled = true;
            RefreshFrameButton.IsEnabled = true;
            var checkpointRequest = BuildRequest();
            StatusText.Text = "Đang kiểm tra checkpoint OCR; chưa thể bắt đầu quét mới...";
            var checkpoint = await _application.InspectOcrCheckpointAsync(checkpointRequest, CancellationToken.None);
            var restoredNote = string.Empty;
            if (!checkpoint.Exists)
            {
                // Vụ 06/10: ROI/mode mặc định sau khởi động lệch với lúc quét
                // nên inspect miss dù checkpoint còn. Tra hồ sơ cùng video,
                // khôi phục đúng ROI/mode đã quét rồi inspect lại bằng key
                // chuẩn — resume lại chạy, user không phải vẽ tay.
                var sameVideo = _application.FindOcrVideoCheckpoints(selected);
                var newest = sameVideo.FirstOrDefault();
                if (newest is not null)
                {
                    _region = newest.Region;
                    SelectValue(ScanModeBox, newest.Mode);
                    SelectValue(DeviceBox, newest.Device);
                    SelectValue(LanesBox, newest.Parallelism);
                    ApplyRegionVisual();
                    checkpointRequest = BuildRequest();
                    checkpoint = await _application.InspectOcrCheckpointAsync(checkpointRequest, CancellationToken.None);
                    if (checkpoint.Exists)
                        restoredNote = $"Đã khôi phục khung quét + chế độ của lần quét {newest.SavedUtc:dd/MM HH:mm} (chế độ {newest.Mode}). ";
                }
            }
            if (checkpoint.Exists)
            {
                _checkpointRequest = checkpointRequest;
                ApplyRecoverableCheckpointCues(checkpoint);
                ApplyCheckpointUi();
                StatusText.Text = restoredNote + $"Có checkpoint schema {checkpoint.Schema}: {checkpoint.ProgressPercent:0.0}% · {checkpoint.CueCount} câu; có thể Tiếp tục hoặc Xuất SRT phục hồi.";
            }
            else
            {
                RefreshRegionActions();
                StatusText.Text = "Video đã sẵn sàng. Không có checkpoint; có thể Quét từ đầu.";
            }
        }
        catch (Exception error)
        {
            StatusText.Text = error.Message;
        }
    }

    private async void RefreshFrame_Click(object sender, RoutedEventArgs e)
    {
        try { await UpdateFrameAsync(); }
        catch (Exception error) { StatusText.Text = error.Message; }
    }

    private void Timeline_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_playerMode && !_syncingTimeline && _player is not null)
        {
            _player.PlaybackSession.Position = TimeSpan.FromSeconds(Math.Max(0, Timeline.Value));
        }
        UpdateClock();
        SyncCueSelection(Timeline.Value);
    }

    private async Task UpdateFrameAsync()
    {
        if (_path is null || _media is null) return;
        var bytes = await _application.Media.GetFrameJpegAsync(_path, Timeline.Value, CancellationToken.None);
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
        }
        stream.Seek(0);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        PreviewImage.Source = bitmap;
        UpdateClock();
    }

    private void UpdateClock() => ClockText.Text = $"{FormatClock(Timeline.Value)} / {FormatClock(_media?.Duration ?? 0)}";

    private async void Playback_Click(object sender, RoutedEventArgs e)
    {
        try { await SetPlaybackModeAsync(!_playerMode, play: true); }
        catch (Exception error) { StatusText.Text = "Player native: " + error.Message; }
    }

    private async void SelectRegion_Toggled(object sender, RoutedEventArgs e)
    {
        if (_media is null)
        {
            SelectRegionButton.IsOn = false;
            return;
        }
        var selecting = SelectRegionButton.IsOn;
        try
        {
            if (selecting && _playerMode) await SetPlaybackModeAsync(false, play: false);
            PreviewCanvas.IsHitTestVisible = selecting;
            StatusText.Text = selecting
                ? "Chỉnh ROI: kéo giữa khung để di chuyển · kéo mép/góc để resize · kéo ngoài khung để tạo mới."
                : "Đã thoát chế độ chỉnh ROI.";
        }
        catch (Exception error)
        {
            SelectRegionButton.IsOn = false;
            PreviewCanvas.IsHitTestVisible = false;
            StatusText.Text = error.Message;
        }
    }

    private async void Fullscreen_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SetPlaybackModeAsync(true, play: false);
            PreviewPlayer.IsFullWindow = true;
        }
        catch (Exception error)
        {
            StatusText.Text = "Toàn màn hình: " + error.Message;
        }
    }

    private async void Prepare_Click(object sender, RoutedEventArgs e)
    {
        if (_jobId is not null)
        {
            StatusText.Text = "Đang quét OCR; không thể khởi tạo lại engine lúc này.";
            return;
        }
        try
        {
            StatusText.Text = "Đang chuẩn bị private PaddleOCR runtime...";
            var status = await _application.PrepareOcrAsync(Selected(DeviceBox), CancellationToken.None);
            StatusText.Text = $"OCR Ready · {status.ActiveMode} · {status.Workers} worker";
        }
        catch (Exception error)
        {
            StatusText.Text = error.Message;
        }
    }

    private async void TestFrame_Click(object sender, RoutedEventArgs e)
    {
        if (_path is null) return;
        try
        {
            StatusText.Text = "Đang nhận diện frame...";
            var result = await _application.RecognizeFrameAsync(_path, Timeline.Value, ReadRegion(), Selected(DeviceBox), CancellationToken.None);
            OcrResultText.Text = result.Detected ? $"{result.Text} · {result.Confidence:P0}" : result.Error ?? "Không phát hiện phụ đề.";
            StatusText.Text = "Test frame hoàn tất.";
        }
        catch (Exception error)
        {
            StatusText.Text = error.Message;
        }
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_path is null || _media is null) return;
        if (_queueRunning)
        {
            StatusText.Text = "Hàng chờ đang chạy; dùng nút Tạm dừng/Hủy hoặc Dừng sau mục này.";
            return;
        }
        if (string.Equals(Selected(SourceBox), "listen", StringComparison.Ordinal))
        {
            await StartListenAsync();
            return;
        }
        var request = _checkpointRequest ?? BuildRequest();
        var startMode = _checkpointRequest is null ? OcrScanStartMode.Fresh : OcrScanStartMode.Resume;
        // Tiep tuc sau khi sua ROI luc tam dung: giu request goc (giu checkpoint key),
        // truyen ROI moi rieng de quet. Khong sua -> null -> chay nhu cu.
        OcrRegion? regionOverride = null;
        if (_checkpointRequest is not null)
        {
            var edited = ReadRegion();
            if (!edited.Equals(_checkpointRequest.Region))
            {
                regionOverride = edited;
                StatusText.Text = "Tiếp tục với ROI mới sửa: phần đã quét giữ nguyên, phần còn lại đọc theo khung mới.";
            }
        }
        _ = await StartScanAsync(request, startMode, regionOverride);
    }

    // ---- Hàng chờ OCR (09/10): quét nhiều video nối tiếp, mỗi video giữ
    // setup riêng lúc thêm (đường dẫn + ROI + chế độ + thiết bị + luồng).
    // Lỗi bỏ qua chạy tiếp, chỉ sống trong phiên app, chỉ quét OCR.
    private void SyncQueueView()
    {
        var selectedId = (QueueList.SelectedItem as OcrQueueItem)?.Id;
        var same = _ocrQueueView.Count == _ocrQueue.Items.Count;
        if (same)
        {
            for (var index = 0; index < _ocrQueueView.Count; index++)
            {
                if (!ReferenceEquals(_ocrQueueView[index], _ocrQueue.Items[index])) { same = false; break; }
            }
        }
        if (!same)
        {
            _ocrQueueView.Clear();
            foreach (var item in _ocrQueue.Items) _ocrQueueView.Add(item);
            if (selectedId is { } id)
            {
                foreach (var item in _ocrQueueView)
                {
                    if (item.Id == id) { QueueList.SelectedItem = item; break; }
                }
            }
        }
        var waiting = _ocrQueue.WaitingCount;
        var active = _ocrQueue.ActiveItem;
        var pausedCount = 0;
        foreach (var item in _ocrQueue.Items)
            if (item.Status == OcrQueueItemStatus.Paused) pausedCount++;
        QueueCountText.Text = _ocrQueue.Items.Count == 0 ? "Trống"
            : active is not null ? $"Đang quét · chờ {waiting}"
            : waiting > 0 ? $"{waiting} đang chờ"
            : pausedCount > 0 ? $"{pausedCount} tạm dừng" : "Hết việc";
        QueueRunButton.IsEnabled = !_queueRunning && (waiting > 0 || pausedCount > 0);
        QueueStopButton.IsEnabled = _queueRunning;
    }

    private OcrQueueItem? SelectedQueueItem() => QueueList.SelectedItem as OcrQueueItem;

    private void QueueAdd_Click(object sender, RoutedEventArgs e)
    {
        if (_queueRunning)
        {
            StatusText.Text = "Hàng chờ đang chạy; không thêm lúc này để khỏi nhầm video đang quét.";
            return;
        }
        if (_jobId is not null)
        {
            StatusText.Text = "Đang quét OCR; hãy Tạm dừng hoặc Hủy trước khi thêm vào hàng.";
            return;
        }
        if (string.Equals(Selected(SourceBox), "listen", StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = "Hàng chờ chỉ nhận quét OCR (mắt đọc), không nhận chế độ Nghe.";
            return;
        }
        if (_path is null || _media is null)
        {
            StatusText.Text = "Chọn video + chỉnh ROI/chế độ trước, rồi bấm Thêm video đang chọn.";
            return;
        }
        var item = _ocrQueue.Enqueue(
            _path, ReadRegion(), Selected(ScanModeBox), Selected(DeviceBox), Selected(LanesBox), _media.Duration);
        StatusText.Text = $"Đã thêm vào hàng chờ ({_ocrQueue.WaitingCount} chờ): {item.DisplayName} · {item.SetupSummary}.";
    }

    private async void QueueRun_Click(object sender, RoutedEventArgs e)
    {
        if (_queueRunning) return;
        if (_jobId is not null)
        {
            StatusText.Text = "Đang quét dở; Tạm dừng/Hủy mục hiện tại trước khi chạy hàng chờ.";
            return;
        }
        var hasPaused = false;
        foreach (var existing in _ocrQueue.Items)
        {
            if (existing.Status == OcrQueueItemStatus.Paused) { hasPaused = true; break; }
        }
        if (_ocrQueue.WaitingCount == 0 && !hasPaused)
        {
            StatusText.Text = "Hàng chờ trống; thêm video rồi bấm Chạy hàng chờ.";
            return;
        }
        _queueRunning = true;
        ScanButton.IsEnabled = false;
        PickVideoButton.IsEnabled = false;
        QueueAddButton.IsEnabled = false;
        try
        {
            await RunQueueAsync();
        }
        catch (Exception error)
        {
            StatusText.Text = "Hàng chờ lỗi: " + error.Message;
        }
        finally
        {
            _queueRunning = false;
            PickVideoButton.IsEnabled = true;
            QueueAddButton.IsEnabled = true;
            if (_jobId is null)
            {
                RestoreIdleControls();
                ScanButton.IsEnabled = _path is not null && _media is not null;
            }
            SyncQueueView();
        }
    }

    private void QueueStop_Click(object sender, RoutedEventArgs e)
    {
        if (!_queueRunning)
        {
            QueueStatusText.Text = "Hàng chờ không chạy.";
            return;
        }
        _queueRunning = false;
        QueueStatusText.Text = "Sẽ dừng sau khi mục đang chạy xong (Tạm dừng/Hủy để dừng ngay).";
    }

    private void QueueClear_Click(object sender, RoutedEventArgs e)
    {
        var removed = _ocrQueue.ClearFinished();
        QueueStatusText.Text = removed > 0 ? $"Đã dọn {removed} mục xong/lỗi/hủy." : "Không có mục xong để dọn.";
    }

    private void QueueRemove_Click(object sender, RoutedEventArgs e)
    {
        var item = SelectedQueueItem();
        if (item is null)
        {
            QueueStatusText.Text = "Chọn 1 mục trong hàng chờ rồi bấm Xóa.";
            return;
        }
        if (!_ocrQueue.Remove(item.Id))
            QueueStatusText.Text = "Không xóa được mục đang quét (dùng Hủy để dừng nó).";
    }

    private void QueueUp_Click(object sender, RoutedEventArgs e)
    {
        var item = SelectedQueueItem();
        if (item is null || !_ocrQueue.MoveUp(item.Id))
            QueueStatusText.Text = "Chỉ dời được mục đang chờ.";
    }

    private void QueueDown_Click(object sender, RoutedEventArgs e)
    {
        var item = SelectedQueueItem();
        if (item is null || !_ocrQueue.MoveDown(item.Id))
            QueueStatusText.Text = "Chỉ dời được mục đang chờ.";
    }

    private async Task RunQueueAsync()
    {
        while (_queueRunning)
        {
            var item = _ocrQueue.TakeNext();
            if (item is null) break;
            var position = 0;
            var total = _ocrQueue.Items.Count;
            foreach (var done in _ocrQueue.Items)
            {
                if (done.Status is OcrQueueItemStatus.Done
                    or OcrQueueItemStatus.Failed
                    or OcrQueueItemStatus.Cancelled) position++;
            }
            position++;
            QueueStatusText.Text = $"Đang quét ({position}/{total}): {item.DisplayName}.";
            MediaPreviewInfo media;
            try
            {
                media = await _application.Media.ProbeAsync(item.VideoPath, CancellationToken.None);
            }
            catch (Exception error)
            {
                _ocrQueue.MarkFailed(item.Id, "Không đọc được video: " + error.Message);
                continue;
            }
            item.Duration = media.Duration;
            _path = item.VideoPath;
            PathText.Text = _path;
            _media = media;
            _region = item.Region;
            Timeline.Maximum = Math.Max(0.1, media.Duration);
            Timeline.Value = 0;
            try
            {
                await PreparePlayerAsync(_path, media.DirectCompatible);
                MediaText.Text = $"{media.Width}×{media.Height} · {media.Duration:0.0}s · {media.Codec}";
                await UpdateFrameAsync();
                ApplyRegionVisual();
            }
            catch (Exception error)
            {
                item.Message = "Preview lỗi (vẫn quét): " + error.Message;
            }
            Timeline.IsEnabled = true;
            var request = item.ToRequest();
            var startMode = OcrScanStartMode.Fresh;
            try
            {
                var checkpoint = await _application.InspectOcrCheckpointAsync(request, CancellationToken.None);
                if (checkpoint.Exists) startMode = OcrScanStartMode.Resume;
            }
            catch { }
            if (!_queueRunning)
            {
                item.Status = OcrQueueItemStatus.Waiting;
                break;
            }
            var (end, exportedPath, detail) = await StartScanAsync(request, startMode, null, item);
            switch (end)
            {
                case QueueScanEnd.Done:
                    _ocrQueue.MarkDone(item.Id, exportedPath, string.IsNullOrWhiteSpace(detail) ? "Xong." : detail);
                    break;
                case QueueScanEnd.Failed:
                    _ocrQueue.MarkFailed(item.Id, string.IsNullOrWhiteSpace(detail) ? "Quét lỗi (đã bỏ qua, chạy tiếp)." : detail);
                    break;
                case QueueScanEnd.Cancelled:
                    _ocrQueue.MarkCancelled(item.Id, "Đã hủy.");
                    _queueRunning = false;
                    break;
                default:
                    _ocrQueue.MarkPaused(item.Id);
                    item.Message = "Tạm dừng, checkpoint còn; bấm Chạy hàng chờ để tiếp tục.";
                    _queueRunning = false;
                    break;
            }
        }
        var doneCount = 0;
        var failedCount = 0;
        foreach (var done in _ocrQueue.Items)
        {
            if (done.Status == OcrQueueItemStatus.Done) doneCount++;
            else if (done.Status == OcrQueueItemStatus.Failed) failedCount++;
        }
        QueueStatusText.Text = $"Hàng chờ xong: {doneCount} video xong"
            + (failedCount > 0 ? $" · {failedCount} lỗi đã bỏ qua" : "") + ".";
        StatusText.Text = QueueStatusText.Text;
    }

    // Nghe riêng (Whisper-only): hiện trực tiếp % + giờ + chữ từ checkpoint,
    // dừng giữ sổ dở, nút Nghe lại từ đầu (RestartButton) xóa sổ nghe lại;
    // Hủy và xóa khi idle cũng xóa sạch. Xong thì copy sub ra chỗ sub OCR.
    private async Task StartListenAsync(bool fromScratch = false)
    {
        if (_path is null || _media is null || !(_media.Duration > 0) || _jobId is not null) return;
        var projectId = TryListenProjectId();
        if (projectId is null) { StatusText.Text = "Video chưa hợp lệ để nghe."; return; }
        if (fromScratch) _application.RemoveOcrListenCheckpoint(projectId);
        var heardBefore = fromScratch ? [] : _application.ReadOcrListenCues(projectId);
        var resuming = !fromScratch
            && string.Equals(_listenProjectId, projectId, StringComparison.Ordinal)
            && _cues.Count > 0 && heardBefore.Count > 0;
        if (!resuming)
        {
            _cues = [];
            _visibleCues = [];
            _authoritative = false;
            _liveCues.Clear();
            _manualDeletes.Clear();
            _cueViewDirty = false;
            CueList.Items.Clear();
            CueCountText.Text = "0 đoạn";
            Progress.Value = 0;
            TelemetryText.Text = "Đang chuẩn bị tai nghe...";
            ExportButton.IsEnabled = false;
        }
        else
        {
            _authoritative = false;
            _liveCues.Seed(_cues);
            _cueViewDirty = true;
        }
        _nextCueRenderAt = default;
        _userAtEnd = true;
        _listening = true;
        _listenProjectId = projectId;
        AutoSaveFolderButton.IsEnabled = false;
        PickVideoButton.IsEnabled = false;
        DeviceBox.IsEnabled = false;
        ScanModeBox.IsEnabled = false;
        SourceBox.IsEnabled = false;
        LanesBox.IsEnabled = false;
        PrepareOcrButton.IsEnabled = false;
        ScanButton.IsEnabled = false;
        RestartButton.Visibility = Visibility.Collapsed;
        TestFrameButton.IsEnabled = false;
        SelectRegionButton.IsEnabled = false;
        PauseButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        CancelButton.Content = "Dừng";
        string runningId;
        try
        {
            (runningId, _) = _application.StartOcrListen(_path, _media.Duration);
        }
        catch (Exception error)
        {
            StatusText.Text = error.Message;
            _listening = false;
            _jobId = null;
            ApplyListenIdleControls();
            return;
        }
        _jobId = runningId;
        var duration = _media.Duration;
        while (_jobId == runningId)
        {
            JobSnapshot snapshot;
            try
            {
                snapshot = _application.Jobs.GetSnapshot(runningId);
            }
            catch
            {
                break;
            }
            Progress.Value = snapshot.Progress;
            StatusText.Text = snapshot.Message;
            var heard = _application.ReadOcrListenCues(projectId);
            if (heard.Count > 0)
            {
                MergeLiveCueSnapshot(heard);
                RefreshLiveCueView(force: snapshot.Done);
                TelemetryText.Text = $"Đang nghe · tới {FormatClock(heard[^1].End)} / {FormatClock(duration)} · {heard.Count} đoạn";
            }
            if (snapshot.Done)
            {
                var cancelled = string.Equals(snapshot.Status, "cancelled", StringComparison.OrdinalIgnoreCase);
                var failed = string.Equals(snapshot.Status, "error", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(snapshot.Error);
                _jobId = null;
                _listening = false;
                if (cancelled)
                {
                    ScanButton.Content = "Nghe tiếp";
                    StatusText.Text = "Đã dừng nghe, chữ đã nghe được giữ lại. Bấm Nghe tiếp để nối, Nghe lại từ đầu để nghe mới.";
                }
                else if (failed)
                {
                    ScanButton.Content = "Nghe tiếp";
                    StatusText.Text = snapshot.Error ?? snapshot.Message;
                }
                else
                {
                    var final = _application.ReadOcrListenCues(projectId);
                    _liveCues.Clear();
                    _cues = final.OrderBy(cue => cue.Start).ToArray();
                    _authoritative = true;
                    RebuildDisplayRows();
                    RenderCues();
                    CueCountText.Text = $"{_cues.Count} đoạn";
                    try
                    {
                        var name = BiliSubStudio.Core.IO.FileNamePolicy.Sanitize(
                            Path.GetFileNameWithoutExtension(_path) + "_Whisper_Chinese.srt",
                            "BiliSub_Whisper_Chinese.srt");
                        Directory.CreateDirectory(_ocrOutputDirectory);
                        var dest = Path.Combine(_ocrOutputDirectory, name);
                        File.Copy(_application.OcrListenSrtPath(projectId), dest, overwrite: true);
                        StatusText.Text = "Đã nghe xong và lưu SRT: " + dest;
                    }
                    catch (Exception error)
                    {
                        StatusText.Text = "Nghe xong nhưng lưu SRT lỗi: " + error.Message;
                    }
                    ScanButton.Content = "Nghe tiếp";
                }
                ApplyListenIdleControls();
                break;
            }
            await Task.Delay(400);
        }
        if (_jobId == runningId)
        {
            // Job mất khỏi sổ mà vòng poll chưa dọn: coi như dừng, giữ chữ đã nghe.
            _jobId = null;
            _listening = false;
            ScanButton.Content = "Nghe tiếp";
            StatusText.Text = "Phiên nghe mất khỏi sổ tác vụ; chữ đã nghe được giữ lại.";
            ApplyListenIdleControls();
        }
    }

    private void ApplyListenIdleControls()
    {
        PickVideoButton.IsEnabled = true;
        AutoSaveFolderButton.IsEnabled = true;
        PauseButton.IsEnabled = false;
        SourceBox.IsEnabled = true;
        DeviceBox.IsEnabled = false;
        ScanModeBox.IsEnabled = false;
        LanesBox.IsEnabled = false;
        PrepareOcrButton.IsEnabled = false;
        TestFrameButton.IsEnabled = false;
        SelectRegionButton.IsEnabled = false;
        ScanButton.IsEnabled = true;
        // Nút nghe chế độ nghe luôn hiện: Nghe lại từ đầu (xóa sổ) + Hủy và xóa.
        RestartButton.Content = "Nghe lại từ đầu";
        RestartButton.Visibility = Visibility.Visible;
        RestartButton.IsEnabled = true;
        CancelButton.IsEnabled = _cues.Count > 0;
        CancelButton.Content = _cues.Count > 0 ? "Hủy và xóa" : "Hủy";
    }

    private async Task CancelListenAsync()
    {
        if (_cancelInProgress || !_listening || _jobId is null) return;
        try
        {
            _cancelInProgress = true;
            CancelButton.Content = "Đang dừng...";
            CancelButton.IsEnabled = true;
            _application.CancelJob(_jobId);
            var waited = 0;
            while (_jobId is not null && waited < 150)
            {
                await Task.Delay(200);
                waited++;
            }
        }
        catch (Exception error)
        {
            StatusText.Text = error.Message;
        }
        finally
        {
            _cancelInProgress = false;
        }
    }

    private string? TryListenProjectId()
    {
        try
        {
            return _path is not null && _media is not null && _media.Duration > 0
                ? BiliSubApplication.OcrListenProjectId(_path, _media.Duration)
                : null;
        }
        catch
        {
            return null;
        }
    }

    // Hủy và xóa (idle): xóa sổ nghe + sub đã lưu, dọn bảng, về trạng thái chưa nghe.
    private async Task CancelListenDeleteAsync()
    {
        var projectId = TryListenProjectId();
        if (projectId is null) return;
        try
        {
            CancelButton.IsEnabled = false;
            _application.RemoveOcrListenCheckpoint(projectId);
            _liveCues.Clear();
            _cues = [];
            _visibleCues = [];
            _authoritative = false;
            _manualDeletes.Clear();
            _cueViewDirty = false;
            CueList.Items.Clear();
            CueCountText.Text = "0 đoạn";
            Progress.Value = 0;
            TelemetryText.Text = string.Empty;
            ExportButton.IsEnabled = false;
            _listenProjectId = projectId;
            ScanButton.Content = "Nghe từ đầu";
            RestartButton.IsEnabled = true;
            CancelButton.Content = "Hủy";
            StatusText.Text = "Đã xóa sạch chữ đã nghe và sub đã lưu. Bấm Nghe từ đầu để nghe lại.";
        }
        catch (Exception error)
        {
            StatusText.Text = error.Message;
            CancelButton.IsEnabled = true;
        }
        await Task.CompletedTask;
    }

    // Một nguồn sự thật cho nút chế độ mắt (27/09: vụ nghe->mắt kẹt nút
    // "Nghe lại từ đầu" vì nhánh về mắt chỉ dựng ScanButton, quên Restart/
    // Cancel/telemetry — nhãn một đằng hành động một nẻo, bấm là quét OCR
    // trong khi nhãn ghi Nghe). Mọi đường về mắt idle đều qua đây.
    private void ApplyEyeModeControls()
    {
        RestoreIdleControls();
        RefreshRegionActions();
        var recovery = _checkpointRequest is not null;
        ScanButton.IsEnabled = true;
        ScanButton.Content = recovery ? "Tiếp tục" : (_cues.Count > 0 ? "Quét lại từ đầu" : "Quét / Tiếp tục");
        RestartButton.Content = "Quét lại từ đầu";
        RestartButton.Visibility = recovery ? Visibility.Visible : Visibility.Collapsed;
        RestartButton.IsEnabled = true;
        CancelButton.Content = recovery ? "Hủy và xóa" : "Hủy";
        CancelButton.IsEnabled = recovery;
        TelemetryText.Text = recovery
            ? "Có checkpoint OCR; có thể Tiếp tục hoặc Quét lại từ đầu."
            : (_cues.Count > 0 ? $"{_cues.Count} câu; bấm Quét lại từ đầu để quét mới." : "Chưa có telemetry.");
    }

    private void SourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Sự kiện nổ ngay trong lúc trang đang dựng (SelectedIndex mặc định):
        // nút bấm chưa có nên bỏ qua, mặc định vốn là mắt đọc.
        if (ScanButton is null || TelemetryText is null) return;
        if (_jobId is not null) return;
        if (!string.Equals(Selected(SourceBox), "listen", StringComparison.Ordinal))
        {
            // Về mắt đọc: dựng lại TOÀN BỘ nút theo trạng thái OCR hiện tại
            // (không chỉ ScanButton — bài học vụ kẹt nhãn Nghe ở chế độ Mắt).
            ApplyEyeModeControls();
            return;
        }
        DeviceBox.IsEnabled = false;
        ScanModeBox.IsEnabled = false;
        LanesBox.IsEnabled = false;
        PrepareOcrButton.IsEnabled = false;
        TestFrameButton.IsEnabled = false;
        SelectRegionButton.IsEnabled = false;
        PauseButton.IsEnabled = false;
        var projectId = TryListenProjectId();
        _listenProjectId = projectId;
        var heard = projectId is not null && _application.ReadOcrListenCues(projectId).Count > 0;
        ScanButton.Content = heard ? "Nghe tiếp" : "Nghe từ đầu";
        RestartButton.Content = "Nghe lại từ đầu";
        RestartButton.Visibility = Visibility.Visible;
        RestartButton.IsEnabled = true;
        CancelButton.IsEnabled = heard;
        CancelButton.Content = heard ? "Hủy và xóa" : "Hủy";
        TelemetryText.Text = "Chế độ tai nghe: chỉ chạy Whisper lấy lời thoại rồi xuất SRT, không quét mắt.";
    }

    private async void Restart_Click(object sender, RoutedEventArgs e)
    {
        if (_path is null || _media is null || _jobId is not null || _queueRunning) return;
        if (string.Equals(Selected(SourceBox), "listen", StringComparison.Ordinal))
        {
            // Nghe lại từ đầu: xóa sổ nghe + sub đã lưu, nghe lại thật sự mới.
            var projectId = TryListenProjectId();
            if (projectId is null) return;
            try
            {
                RestartButton.IsEnabled = false;
                ScanButton.IsEnabled = false;
                _application.RemoveOcrListenCheckpoint(projectId);
                await StartListenAsync(fromScratch: true);
            }
            catch (Exception error)
            {
                RestartButton.IsEnabled = true;
                StatusText.Text = error.Message;
            }
            return;
        }
        var request = _checkpointRequest ?? BuildRequest();
        try
        {
            RestartButton.IsEnabled = false;
            ScanButton.IsEnabled = false;
            await _application.RemoveOcrCheckpointAsync(request, CancellationToken.None);
            var checkpoint = await _application.InspectOcrCheckpointAsync(request, CancellationToken.None);
            if (checkpoint.Exists) throw new IOException("Checkpoint OCR vẫn còn; chưa thể Quét lại từ đầu.");
            _checkpointRequest = null;
            _ = await StartScanAsync(BuildRequest(), OcrScanStartMode.Fresh);
        }
        catch (Exception error)
        {
            RestartButton.IsEnabled = true;
            StatusText.Text = error.Message;
            RefreshRegionActions();
        }
    }

    // Kết thúc 1 lượt quét (dùng cho cả quét đơn và driver hàng chờ).
    private enum QueueScanEnd
    {
        Done,
        Failed,
        Cancelled,
        Paused,
    }

    private async Task<(QueueScanEnd End, string? ExportedPath, string? Detail)> StartScanAsync(
        OcrScanRequest request, OcrScanStartMode startMode, OcrRegion? regionOverride = null, OcrQueueItem? queueItem = null)
    {
        var autoSaveDirectory = _ocrOutputDirectory;
        var resuming = startMode == OcrScanStartMode.Resume;
        if (!resuming)
        {
            _cues = [];
            _visibleCues = [];
            _authoritative = false;
            _liveCues.Clear();
            _cueViewDirty = false;
            CueList.Items.Clear();
            CueCountText.Text = "0 câu";
            Progress.Value = 0;
            TelemetryText.Text = "Đang chờ kết quả OCR...";
            ExportButton.IsEnabled = false;
            ResetManualEdits();
        }
        else
        {
            // Resume: giữ nguyên danh sách pause-authoritative đang hiển thị và
            // seed accumulator từ nó. Snapshot rolling sau đó reconcile tiếp về
            // phía trước, thay vì xóa sạch list rồi chờ telemetry refill.
            // Quét chạy lại -> list về chế độ tạm (preview) cho tới khi xong.
            _authoritative = false;
            _liveCues.Seed(_cues);
            _cueViewDirty = true;
        }
        _nextCueRenderAt = default;
        _userAtEnd = true;
        AutoSaveFolderButton.IsEnabled = false;
        _checkpointRequest = null;
        _activeRequest = request;
        var runningId = _application.StartOcrScan(request, startMode, regionOverride);
        _jobId = runningId;
        OcrScanResult? latestResult = null;
        PickVideoButton.IsEnabled = false;
        DeviceBox.IsEnabled = false;
        ScanModeBox.IsEnabled = false;
        SourceBox.IsEnabled = false;
        LanesBox.IsEnabled = false;
        PrepareOcrButton.IsEnabled = false;
        ScanButton.IsEnabled = false;
        RestartButton.Visibility = Visibility.Collapsed;
        TestFrameButton.IsEnabled = false;
        SelectRegionButton.IsOn = false;
        SelectRegionButton.IsEnabled = false;
        PauseButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        CancelButton.Content = "Hủy";
        while (_jobId == runningId)
        {
            var snapshot = _application.Jobs.GetSnapshot(runningId);
            Progress.Value = snapshot.Progress;
            StatusText.Text = snapshot.Message;
            if (queueItem is not null)
            {
                queueItem.Progress = snapshot.Progress;
                queueItem.Message = snapshot.Message;
            }
            if (snapshot.Result is OcrScanResult result)
            {
                var safeProgress = SafeFrontierProgress(request, result.SafeFrontierSeconds, snapshot.Done);
                Progress.Value = string.Equals(snapshot.Status, "finalizing", StringComparison.OrdinalIgnoreCase)
                    ? snapshot.Progress
                    : safeProgress;
                latestResult = result;
                var authoritative = snapshot.Done
                    && !string.Equals(snapshot.Status, "cancelled", StringComparison.OrdinalIgnoreCase)
                    && string.IsNullOrWhiteSpace(snapshot.Error);
                if (authoritative)
                    ApplyAuthoritativeCues(result.Cues);
                else
                {
                    MergeLiveCueSnapshot(result.Cues);
                    RefreshLiveCueView(force: snapshot.Done);
                }
                TelemetryText.Text = $"SRT liên tục {safeProgress:0.0}% ({FormatClock(result.SafeFrontierSeconds)}) · tổng xử lý {snapshot.Progress:0.0}% · {result.ParallelismSelected} FFmpeg lane · {result.WorkerCount} worker ({result.WorkerKinds}) · {result.CompletedLanes}/{result.ParallelismSelected} lane xong · {result.Frames} frames · {result.OcrImages} OCR · {result.RealtimeSpeed:0.00}×";
            }
            else if (snapshot.Result is OcrScanTelemetry telemetry)
            {
                Progress.Value = SafeFrontierProgress(request, telemetry.SafeFrontierSeconds, snapshot.Done);
                TelemetryText.Text = $"SRT liên tục {Progress.Value:0.0}% ({FormatClock(telemetry.SafeFrontierSeconds)}) · tổng xử lý {snapshot.Progress:0.0}% · {telemetry.SegmentLanes} FFmpeg lane · {telemetry.WorkerCount} worker ({telemetry.WorkerKinds}) · {telemetry.ActiveLanes} đang chạy · {telemetry.CompletedLanes} xong · {telemetry.Frames} frames · {telemetry.OcrImages} OCR";
            }
            else if (snapshot.Result is OcrBenchmarkTelemetry benchmark)
            {
                var stable = benchmark.LastStable > 0 ? benchmark.LastStable.ToString() : "chưa có";
                var resources = string.IsNullOrWhiteSpace(benchmark.ResourceSummary) ? string.Empty : " · " + benchmark.ResourceSummary;
                TelemetryText.Text = $"Benchmark {benchmark.Candidate}/{benchmark.Maximum} · PASS gần nhất {stable} · {benchmark.WorkerCount} Python worker ({benchmark.WorkerKinds}) · {benchmark.Phase}{resources}";
            }
            else
            {
                var ocrStatus = _application.OcrStatus;
                TelemetryText.Text = $"Đang chuẩn bị benchmark · {ocrStatus.Workers} Python worker ({ocrStatus.WorkerKinds}) · {ocrStatus.ActiveMode} · {snapshot.Status}";
            }
            if (!_cancelInProgress && !snapshot.PauseRequested)
                PauseButton.IsEnabled = string.Equals(snapshot.Status, "scanning", StringComparison.OrdinalIgnoreCase);
            if (snapshot.Done)
            {
                var paused = latestResult?.Paused == true;
                var cancelled = string.Equals(snapshot.Status, "cancelled", StringComparison.OrdinalIgnoreCase);
                var failed = string.Equals(snapshot.Status, "error", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(snapshot.Error);
                _checkpointRequest = paused ? request : null;
                _activeRequest = null;
                _jobId = null;
                RestoreIdleControls();
                var end = QueueScanEnd.Failed;
                string? exportedPath = null;
                string? detail = snapshot.Error ?? snapshot.Message;
                if (cancelled)
                {
                    end = QueueScanEnd.Cancelled;
                    detail = "Đã hủy sạch: 0 Python worker · 0 FFmpeg/process tree · checkpoint đã xóa.";
                    ResetFreshUi(detail);
                }
                else if (paused)
                {
                    end = QueueScanEnd.Paused;
                    detail = null;
                    ApplyCheckpointUi();
                }
                else if (failed)
                {
                    var checkpoint = await _application.InspectOcrCheckpointAsync(request, CancellationToken.None);
                    if (checkpoint.Exists)
                    {
                        _checkpointRequest = request;
                        ApplyRecoverableCheckpointCues(checkpoint);
                        ApplyCheckpointUi();
                        StatusText.Text = (snapshot.Error ?? snapshot.Message)
                            + " Checkpoint vẫn còn; có thể Tiếp tục hoặc Xuất SRT phục hồi.";
                    }
                    else
                    {
                        ResetFreshUi(snapshot.Error ?? snapshot.Message);
                    }
                }
                else
                {
                    CancelButton.IsEnabled = false;
                    CancelButton.Content = "Hủy";
                    RestartButton.Visibility = Visibility.Collapsed;
                    ScanButton.Content = "Quét lại từ đầu";
                    ExportButton.IsEnabled = _cues.Count > 0;
                    try
                    {
                        var exported = await ExportCurrentSrtAsync(autoSaveDirectory, recovery: false);
                        exportedPath = exported.Path;
                        var message = "Đã quét xong và tự lưu SRT: " + exported.Path;
                        if (exported.Warnings.Count > 0)
                            message += " · lưu ý: " + string.Join(" · ", exported.Warnings.Take(3));
                        StatusText.Text = message;
                        detail = message;
                        end = QueueScanEnd.Done;
                    }
                    catch (Exception error)
                    {
                        StatusText.Text = "OCR đã hoàn tất nhưng tự lưu SRT lỗi: " + error.Message;
                        detail = StatusText.Text;
                        end = QueueScanEnd.Done;
                    }
                }
                if (queueItem is not null)
                {
                    queueItem.Progress = end == QueueScanEnd.Done ? 100 : queueItem.Progress;
                    if (!string.IsNullOrWhiteSpace(detail)) queueItem.Message = detail;
                }
                return (end, exportedPath, detail);
            }
            await Task.Delay(400);
        }
        return (QueueScanEnd.Cancelled, null, "Vòng quét kết thúc ngoài dự kiến.");
    }

    private static double SafeFrontierProgress(OcrScanRequest request, double frontierSeconds, bool done)
    {
        if (done) return 100;
        if (!(request.Duration > 0) || !double.IsFinite(frontierSeconds)) return 0;
        return Math.Clamp((frontierSeconds - request.ScanStartOffset) / request.Duration * 100, 0, 99.5);
    }

    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (_jobId is null || _listening) return;
        try
        {
            PauseButton.IsEnabled = false;
            await _application.PauseJobAsync(_jobId, CancellationToken.None);
        }
        catch (Exception error)
        {
            StatusText.Text = error.Message;
            PauseButton.IsEnabled = _jobId is not null && !_cancelInProgress;
        }
    }

    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_cancelInProgress) return;
        if (_listening)
        {
            await CancelListenAsync();
            return;
        }
        if (string.Equals(Selected(SourceBox), "listen", StringComparison.Ordinal))
        {
            await CancelListenDeleteAsync();
            return;
        }
        var request = _activeRequest ?? _checkpointRequest;
        if (request is null) return;

        try
        {
            _cancelInProgress = true;
            PauseButton.IsEnabled = false;
            ScanButton.IsEnabled = false;
            RestartButton.IsEnabled = false;
            CancelButton.Content = "Đang hủy...";
            CancelButton.IsEnabled = true;
            if (_jobId is { } runningId)
                await _application.CancelOcrScanAsync(runningId, request, CancellationToken.None);
            else
            {
                await _application.RemoveOcrCheckpointAsync(request, CancellationToken.None);
                var checkpoint = await _application.InspectOcrCheckpointAsync(request, CancellationToken.None);
                if (checkpoint.Exists) throw new IOException("Checkpoint OCR vẫn còn sau khi Hủy.");
            }
            _jobId = null;
            _activeRequest = null;
            _checkpointRequest = null;
            RestoreIdleControls();
            ResetFreshUi("Đã hủy sạch: 0 Python worker · 0 FFmpeg/process tree · checkpoint đã xóa. Lần bấm sau sẽ Quét từ đầu.");
        }
        catch (Exception error)
        {
            CancelButton.IsEnabled = true;
            StatusText.Text = error.Message;
        }
        finally
        {
            _cancelInProgress = false;
            RestartButton.IsEnabled = true;
            RefreshRegionActions();
        }
    }

    private void RestoreIdleControls()
    {
        PickVideoButton.IsEnabled = true;
        DeviceBox.IsEnabled = true;
        ScanModeBox.IsEnabled = true;
        SourceBox.IsEnabled = true;
        LanesBox.IsEnabled = true;
        PrepareOcrButton.IsEnabled = true;
        AutoSaveFolderButton.IsEnabled = true;
        PauseButton.IsEnabled = false;
        RefreshRegionActions();
    }

    private void ApplyCheckpointUi()
    {
        DeviceBox.IsEnabled = false;
        ScanModeBox.IsEnabled = false;
        SourceBox.IsEnabled = false;
        LanesBox.IsEnabled = false;
        // Tam dung van cho sua ROI: Scan_Click so ReadRegion() voi _checkpointRequest.Region
        // va truyen regionOverride (giu checkpoint key). Device/mode/lane khoa vi doi la doi key.
        SelectRegionButton.IsEnabled = _media is not null;
        ScanButton.Content = "Tiếp tục";
        CancelButton.Content = "Hủy và xóa";
        CancelButton.IsEnabled = true;
        RestartButton.Content = "Quét lại từ đầu";
        RestartButton.Visibility = Visibility.Visible;
        RestartButton.IsEnabled = true;
        ScanButton.IsEnabled = true;
        ExportButton.IsEnabled = _cues.Count > 0;
    }

    private void ResetFreshUi(string message)
    {
        _cues = [];
        _visibleCues = [];
        _liveCues.Clear();
        _nextCueRenderAt = default;
        _cueViewDirty = false;
        _userAtEnd = true;
        CueList.Items.Clear();
        CueCountText.Text = "0 câu";
        Progress.Value = 0;
        TelemetryText.Text = "Chưa có telemetry.";
        OcrResultText.Text = string.Empty;
        ExportButton.IsEnabled = false;
        ResetManualEdits();
        ScanButton.Content = "Quét từ đầu";
        CancelButton.Content = "Hủy";
        CancelButton.IsEnabled = false;
        RestartButton.Visibility = Visibility.Collapsed;
        StatusText.Text = message;
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var recovery = _checkpointRequest is not null;
            var exported = await ExportCurrentSrtAsync(_ocrOutputDirectory, recovery);
            var message = recovery ? "Đã xuất SRT phục hồi chưa hoàn chỉnh: " + exported.Path : "Đã xuất: " + exported.Path;
            if (exported.Warnings.Count > 0)
                message += " · lưu ý: " + string.Join(" · ", exported.Warnings.Take(3));
            StatusText.Text = message;
        }
        catch (Exception error)
        {
            StatusText.Text = error.Message;
        }
    }

    private OcrScanRequest BuildRequest() => new(
        _path ?? throw new InvalidOperationException("Chưa chọn video."),
        ReadRegion(),
        Selected(ScanModeBox),
        Selected(DeviceBox),
        Selected(LanesBox),
        1,
        _media?.Duration ?? 0);

    private OcrRegion ReadRegion() => OcrCheckpointStoreProxy.Normalize(_region);

    private static string Selected(ComboBox box)
    {
        if (box.SelectedItem is ComboBoxItem item)
        {
            return item.Tag?.ToString() ?? item.Content?.ToString() ?? string.Empty;
        }
        return box.SelectedItem?.ToString() ?? string.Empty;
    }

    private static void SelectValue(ComboBox box, string value)
    {
        for (var index = 0; index < box.Items.Count; index++)
        {
            if (box.Items[index] is ComboBoxItem item)
            {
                if (string.Equals(item.Tag?.ToString(), value, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase))
                {
                    box.SelectedIndex = index;
                    return;
                }
            }
            else if (string.Equals(box.Items[index]?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedIndex = index;
                return;
            }
        }
    }

    private void PublishPresenceContext(string? video, string? project)
    {
        try
        {
            if (Microsoft.UI.Xaml.Application.Current is BiliSubStudio.App.App app
                && app.MainWindow is BiliSubStudio.App.MainWindow window)
                window.SetPresenceContext(video, project);
        }
        catch { }
    }

    private async void ChooseAutoSaveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_jobId is not null) return;
        try
        {
            var selected = await _folderPicker.PickFolderAsync(_ocrOutputDirectory);
            if (selected is null) return;
            var snapshot = await _application.Settings.SetOcrOutputDirectoryAsync(selected, CancellationToken.None);
            SetOcrOutputDirectory(snapshot.Config.OcrOutputDirectory);
            StatusText.Text = "Đã chọn thư mục tự lưu SRT: " + _ocrOutputDirectory;
        }
        catch (Exception error)
        {
            StatusText.Text = error.Message;
        }
    }

    private async Task<BiliSubStudio.Core.Application.BiliSubApplication.OcrExportResult> ExportCurrentSrtAsync(string outputDirectory, bool recovery)
    {
        var suffix = recovery ? "_Chinese.partial.srt" : "_Chinese.srt";
        // Danh sách loại trừ câu lọc xóa + file fix (best-effort tuyệt đối:
        // thiếu/hỏng là xuất như cũ, không bao giờ chặn export).
        IReadOnlyList<BiliSubStudio.Core.Ocr.OcrCueExclusion>? exclusions = null;
        IReadOnlyList<BiliSubStudio.Core.Ocr.OcrCueFix>? fixes = null;
        try
        {
            var request = _checkpointRequest ?? BuildRequest();
            exclusions = await _application.LoadOcrExclusionsAsync(request, CancellationToken.None);
            fixes = await _application.LoadOcrFixesAsync(request, CancellationToken.None);
        }
        catch { }
        var exported = await _application.ExportOcrAsync(
            _cues, outputDirectory, Path.GetFileNameWithoutExtension(_path) + suffix, CancellationToken.None, exclusions, fixes);
        // Đồng bộ display sau xuất (18/09): list hiển thị nạp lại đúng sổ vừa
        // dùng để xuất — file sổ có thể đổi dưới chân app đang chạy (judge ghi
        // tiếp, thao tác tay ngoài) mà journal không đổi nên không trigger
        // reload; không đồng bộ thì live và SRT lệch nhau oan.
        if (_authoritative) await ReloadDisplayRowsAsync();
        return exported;
    }

    private void SetOcrOutputDirectory(string path)
    {
        _ocrOutputDirectory = path;
        AutoSaveDirectoryBox.Text = path;
        ToolTipService.SetToolTip(AutoSaveDirectoryBox, path);
    }

    private bool MergeLiveCueSnapshot(IReadOnlyList<OcrCue> incoming)
    {
        // Snapshot lúc đang quét là tạm -> list về chế độ preview.
        _authoritative = false;
        var changed = _liveCues.Merge(incoming);
        _cueViewDirty |= changed;
        return changed;
    }

    private void RefreshLiveCueView(bool force)
    {
        var now = DateTimeOffset.UtcNow;
        if (!_cueViewDirty || !force && now < _nextCueRenderAt) return;
        _cues = _liveCues.Cues;
        RenderCues();
        _cueViewDirty = false;
        _nextCueRenderAt = now.AddSeconds(2);
    }

    private void ApplyAuthoritativeCues(IReadOnlyList<OcrCue> cues)
    {
        // Final cues đã qua ReconcileAsync (pixel-gated) trong OcrScanner. List
        // hiển thị đi qua pipeline chung (OcrExportPipeline) với cùng sổ
        // loại/fix như export -> live-quét-xong = SRT theo cấu trúc.
        _liveCues.Clear();
        _cues = cues.OrderBy(cue => cue.Start).ToArray();
        _authoritative = true;
        RebuildDisplayRows();
        RenderCues();
        _cueViewDirty = false;
        _nextCueRenderAt = DateTimeOffset.UtcNow.AddSeconds(2);
        _ = ReloadDisplayRowsAsync();
    }

    private void ApplyRecoverableCheckpointCues(OcrCheckpointInfo checkpoint)
    {
        var cues = checkpoint.RecoverableCues ?? checkpoint.RecentCues;
        ApplyAuthoritativeCues(cues);
    }

    // Nạp sổ loại/fix (best-effort như export) rồi dựng lại dòng hiển thị từ
    // cùng pipeline với export. Không đụng checkpoint/judgment — chỉ đọc.
    private async Task ReloadDisplayRowsAsync()
    {
        try
        {
            var request = _checkpointRequest ?? BuildRequest();
            _displayExclusions = await _application.LoadOcrExclusionsAsync(request, CancellationToken.None);
            _displayFixes = await _application.LoadOcrFixesAsync(request, CancellationToken.None);
        }
        catch { _displayExclusions = null; _displayFixes = null; }
        if (!_authoritative) return;
        RebuildDisplayRows();
        _visibleCues = [];
        RenderCues();
    }

    private void RebuildDisplayRows()
    {
        var built = OcrExportPipeline.Build(_cues, _displayExclusions, _displayFixes);
        _displayRows = built.Rows;
        var dropped = built.Excluded + built.NormalizeDropped;
        _displayDropNote = dropped > 0 ? $" (bỏ {dropped})" : "";
    }

    private void RefreshJudgeMarks()
    {
        // Đọc nhật ký lọc thời gian (ghi live lúc quét), đổi là vẽ lại bảng
        // để dòng bị drop biến mất ngay. Best-effort tuyệt đối: lỗi là thôi,
        // bảng cũ giữ.
        try
        {
            if (_path is null)
            {
                if (_judgeMarks.Count > 0)
                {
                    _judgeMarks = new Dictionary<string, OcrCueVerdict>(StringComparer.Ordinal);
                    _visibleCues = [];
                    RenderCues();
                }
                return;
            }
            var journalPath = OcrVerdictJournal.JournalPath(_path, _application.Paths.Data);
            var map = OcrJudgeBadge.BuildMap(OcrVerdictJournal.LoadAny(
                OcrVerdictJournal.AppDataPath(_path, _application.Paths.Data), journalPath));
            if (map.Count == _judgeMarks.Count) return;
            _judgeMarks = map;
            _visibleCues = [];
            RenderCues();
            // Chế độ chốt: verdict mới luôn đi kèm file loại/fix đã ghi trước
            // (judge ghi file TRƯỚC journal) -> nạp lại để list = SRT mới nhất.
            if (_authoritative) _ = ReloadDisplayRowsAsync();
        }
        catch { }
    }

    // Danh sách live = SRT 100%: dòng nào bộ lọc quyết drop (có verdict drop
    // trong journal — tương đương exclusion đã ghi, export sẽ loại) thì ẨN
    // khỏi list luôn, không gắn badge. Dòng nào có verdict fix thì hiện CHỮ
    // ĐÃ SỬA (export đắp chữ đó). Số thứ tự = số thứ tự trong SRT.
    private bool IsDroppedByFilter(OcrCue cue) =>
        _judgeMarks.TryGetValue(OcrJudgeBadge.Key(cue.Start, cue.Text), out var verdict)
        && verdict is not null
        && verdict.Decision == "drop";

    private static string TruncateReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return string.Empty;
        var runes = reason.EnumerateRunes().Take(48).ToArray();
        var kept = string.Concat(runes);
        return kept.Length < reason.Length ? kept + "…" : kept;
    }

    private string DisplayTextFor(OcrCue cue)
    {
        if (_judgeMarks.TryGetValue(OcrJudgeBadge.Key(cue.Start, cue.Text), out var verdict)
            && verdict is not null
            && verdict.Decision == "fix"
            && !string.IsNullOrWhiteSpace(verdict.FixedText))
            return verdict.FixedText;
        return cue.Text;
    }

    private void RenderCues()
    {
        // Incremental render: chỉ rebuild rows từ first-changed index (tail merge/append),
        // prefix giữ nguyên nên ListView virtualization không realize lại hàng nghìn containers.
        // Mirror invariant cũ: CueList.Items[i] <-> _visibleCues[i] (selection/edit giữ nguyên).
        // HAI CHẾ ĐỘ (18/09): quét xong -> dòng pipeline chung (bằng SRT theo
        // cấu trúc); đang quét -> dòng tạm như cũ (journal + phantom).
        IReadOnlyList<OcrExportRow> rows = _authoritative ? _displayRows : BuildProvisionalRows();
        var common = OcrLiveViewPolicy.CommonRowPrefixLength(_visibleCues, rows);
        var appended = rows.Count > _visibleCues.Count;
        _visibleCues = rows;
        for (var index = common; index < rows.Count; index++)
        {
            var cue = rows[index];
            // Giờ dùng chung hàm format với SRT (làm tròn ms) — trước đây live
            // cắt ngọn fff nên lệch 1ms ở biên.
            var start = OcrExportPipeline.FormatSrtTime(cue.Start);
            var end = OcrExportPipeline.FormatSrtTime(Math.Max(cue.Start, cue.End));
            var text = string.Join(" / ", cue.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
            var row = $"{index + 1}.  {start} → {end}  ·  {text}{(cue.NeedsReview ? "  ⚠" : "")}";
            // Lý do review (19/09): dòng ⚠️ mà có verdict fix giữ-chữ trong
            // journal (F2 tai-im / F5 tai-phát-hiện-sai-chữ) thì hiện gợi ý
            // ngay trên dòng để người duyệt khỏi mò — tra key Start|Text
            // (fix giữ chữ nên key khớp chữ đang hiện).
            if (cue.NeedsReview
                && _judgeMarks.TryGetValue(OcrJudgeBadge.Key(cue.Start, cue.Text), out var mark)
                && mark is not null
                && mark.Decision == "fix"
                && !string.IsNullOrWhiteSpace(mark.Reason))
                row += $" ({TruncateReason(mark.Reason)})";
            if (index < CueList.Items.Count)
            {
                if (!string.Equals(CueList.Items[index]?.ToString(), row, StringComparison.Ordinal))
                    CueList.Items[index] = row;
            }
            else
            {
                CueList.Items.Add(row);
            }
        }
        while (CueList.Items.Count > rows.Count)
            CueList.Items.RemoveAt(CueList.Items.Count - 1);
        CueCountText.Text = _authoritative && _displayDropNote.Length > 0
            ? $"{rows.Count} câu{_displayDropNote}"
            : $"{rows.Count} câu";
        // Auto-follow: chỉ scroll khi có cue mới VÀ user đang ở cuối. User scroll lên xem
        // cue cũ => giữ nguyên vị trí; quay lại gần cuối => ViewChanged bật follow, resume.
        // Cuộn là việc trang trí: kẹt layout thì bỏ qua, không được gãy app.
        if (appended && _userAtEnd && CueList.Items.Count > 0)
        {
            try
            {
                CueList.ScrollIntoView(CueList.Items[CueList.Items.Count - 1]);
            }
            catch
            {
            }
        }
        SyncCueSelection(Timeline.Value);
        UpdateManualEditButtons();
    }

    // Dòng tạm lúc đang quét (bản chất chưa chốt): giữ nguyên logic cũ — ẩn cue
    // journal-drop, hiện chữ journal-fix, lọc phantom như Cleanup. Chữ/giờ/STT
    // ở đây là preview, file SRT cuối vẫn do pipeline chốt.
    private IReadOnlyList<OcrExportRow> BuildProvisionalRows()
    {
        var ordered = _cues.OrderBy(cue => cue.Start).ToArray();
        return ordered.Where((cue, index) =>
            !IsDroppedByFilter(cue) && !OcrTextCleanup.IsPhantom(cue,
                index > 0 ? ordered[index - 1] : null,
                index < ordered.Length - 1 ? ordered[index + 1] : null))
            .Select(cue => new OcrExportRow(cue.Start, cue.End, DisplayTextFor(cue), cue.NeedsReview))
            .ToArray();
    }

    private async void CueList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateManualEditButtons();
        if (_syncingCue || CueList.SelectedIndex < 0 || CueList.SelectedIndex >= _visibleCues.Count) return;
        var cue = _visibleCues[CueList.SelectedIndex];
        try
        {
            _syncingTimeline = true;
            Timeline.Value = Math.Clamp(cue.Start, Timeline.Minimum, Timeline.Maximum);
            if (_player is not null) _player.PlaybackSession.Position = TimeSpan.FromSeconds(Timeline.Value);
            if (!_playerMode) await UpdateFrameAsync();
        }
        catch (Exception error)
        {
            StatusText.Text = error.Message;
        }
        finally
        {
            _syncingTimeline = false;
        }
    }

    // Xoá/hoàn-tác tay trên danh sách ĐÃ CHỐT (21/09): thao tác chỉ ghi/gỡ sổ
    // loại trừ (.exclude.json) — checkpoint giữ nguyên, list & SRT đi qua cùng
    // OcrExportPipeline nên câu biến mất/trở lại và số thứ tự tự dồn ngay.
    // Chỉ cho phép ở chế độ authoritative (quét xong/tạm dừng): list đang quét
    // là tạm, text trên dòng chưa chắc khớp cue gốc để ghi exclusion đúng.
    private void UpdateManualEditButtons()
    {
        DeleteCueButton.IsEnabled = _authoritative
            && CueList.SelectedIndex >= 0
            && CueList.SelectedIndex < _visibleCues.Count;
        UndoDeleteCueButton.IsEnabled = _manualDeletes.Count > 0;
    }

    private void ResetManualEdits()
    {
        _manualDeletes.Clear();
        UpdateManualEditButtons();
    }

    private async void DeleteCue_Click(object sender, RoutedEventArgs e) =>
        await DeleteSelectedCueAsync();

    private void CueList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Delete || !DeleteCueButton.IsEnabled) return;
        e.Handled = true;
        _ = DeleteSelectedCueAsync();
    }

    private async Task DeleteSelectedCueAsync()
    {
        if (!_authoritative || CueList.SelectedIndex < 0 || CueList.SelectedIndex >= _visibleCues.Count)
        {
            StatusText.Text = "Chỉ xóa tay khi danh sách đã chốt (quét xong/tạm dừng).";
            return;
        }
        var row = _visibleCues[CueList.SelectedIndex];
        var rowNumber = CueList.SelectedIndex + 1;
        // Dòng hiển thị có thể đang là chữ ĐÃ FIX — exclusion phải ghi chữ GỐC
        // của cue trong checkpoint thì export mới khớp (match Start ±1ms + Text).
        var cue = _cues.LastOrDefault(candidate =>
            Math.Abs(candidate.Start - row.Start) <= OcrExportExclusions.StartToleranceSeconds);
        if (cue is null)
        {
            StatusText.Text = "Không tìm thấy cue gốc khớp dòng này; không ghi gì.";
            return;
        }
        try
        {
            var request = _checkpointRequest ?? BuildRequest();
            var entry = new OcrCueExclusion(cue.Start, cue.Text);
            var added = await _application.AppendOcrExclusionsAsync(request,
                [entry], CancellationToken.None);
            if (added > 0)
            {
                _manualDeletes.Push((entry, rowNumber));
                StatusText.Text = $"Đã xóa câu {rowNumber} — danh sách và SRT tự dồn số. Bấm \"Hoàn tác xóa\" để khôi phục.";
            }
            else
            {
                StatusText.Text = $"Câu {rowNumber} đã nằm trong sổ loại trừ từ trước.";
            }
            await ReloadDisplayRowsAsync();
        }
        catch (Exception error)
        {
            StatusText.Text = error.Message;
        }
        UpdateManualEditButtons();
    }

    private async void UndoDeleteCue_Click(object sender, RoutedEventArgs e) =>
        await UndoDeleteAsync();

    private async Task UndoDeleteAsync()
    {
        if (_manualDeletes.Count == 0) return;
        var (entry, rowNumber) = _manualDeletes.Pop();
        try
        {
            var request = _checkpointRequest ?? BuildRequest();
            var removed = await _application.RemoveOcrExclusionsAsync(request, [entry], CancellationToken.None);
            StatusText.Text = removed > 0
                ? $"Đã khôi phục câu {rowNumber} — danh sách và SRT tự dồn lại."
                : "Sổ loại trừ không còn entry này (có thể đã gỡ ngoài app).";
            await ReloadDisplayRowsAsync();
        }
        catch (Exception error)
        {
            _manualDeletes.Push((entry, rowNumber));
            StatusText.Text = error.Message;
        }
        UpdateManualEditButtons();
    }

    private void RefreshRegionActions()
    {
        var ready = false;
        try
        {
            _ = ReadRegion();
            ready = _media is not null && _jobId is null;
        }
        catch (ArgumentException)
        {
            ready = false;
        }
        TestFrameButton.IsEnabled = ready;
        ScanButton.IsEnabled = ready;
        SelectRegionButton.IsEnabled = _media is not null && _jobId is null;
    }

    private void PreviewCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_media is null) return;
        var point = e.GetCurrentPoint(PreviewCanvas);
        if (!point.Properties.IsLeftButtonPressed) return;
        _dragStart = point.Position;
        _dragOriginRegion = _region;
        _roiDragMode = HitTestRoi(point.Position);
        PreviewCanvas.CapturePointer(e.Pointer);
        StatusText.Text = _roiDragMode switch
        {
            RoiDragMode.Move => "Đang di chuyển vùng OCR...",
            RoiDragMode.Create => "Đang tạo vùng OCR mới...",
            _ => "Đang thay đổi kích thước vùng OCR...",
        };
    }

    private void PreviewCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragStart is null || !e.GetCurrentPoint(PreviewCanvas).Properties.IsLeftButtonPressed) return;
        UpdateDrag(_dragStart.Value, e.GetCurrentPoint(PreviewCanvas).Position, commit: false);
    }

    private async void PreviewCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragStart is null) return;
        try
        {
            UpdateDrag(_dragStart.Value, e.GetCurrentPoint(PreviewCanvas).Position, commit: true);
            await _application.SetOcrRegionAsync(ReadRegion(), CancellationToken.None);
            StatusText.Text = _roiDragMode switch
            {
                RoiDragMode.Move => "Đã di chuyển và lưu vùng OCR.",
                RoiDragMode.Create => "Đã tạo và lưu vùng OCR.",
                _ => "Đã đổi kích thước và lưu vùng OCR.",
            };
        }
        catch (Exception error)
        {
            _region = _dragOriginRegion;
            ApplyRegionVisual();
            StatusText.Text = error.Message;
        }
        finally
        {
            _dragStart = null;
            _roiDragMode = RoiDragMode.None;
            PreviewCanvas.ReleasePointerCapture(e.Pointer);
            RefreshRegionActions();
        }
    }

    private void UpdateDrag(Point start, Point end, bool commit)
    {
        var video = VideoRect();
        if (video.Width <= 0 || video.Height <= 0) return;

        if (_roiDragMode == RoiDragMode.Move)
        {
            var dx = (end.X - start.X) / video.Width;
            var dy = (end.Y - start.Y) / video.Height;
            _region = OcrCheckpointStoreProxy.Normalize(new OcrRegion(
                Math.Clamp(_dragOriginRegion.X + dx, 0, 1 - _dragOriginRegion.Width),
                Math.Clamp(_dragOriginRegion.Y + dy, 0, 1 - _dragOriginRegion.Height),
                _dragOriginRegion.Width,
                _dragOriginRegion.Height));
            ApplyRegionVisual();
            return;
        }

        if (_roiDragMode == RoiDragMode.Create)
        {
            var x1 = Math.Clamp(Math.Min(start.X, end.X), video.X, video.X + video.Width);
            var x2 = Math.Clamp(Math.Max(start.X, end.X), video.X, video.X + video.Width);
            var y1 = Math.Clamp(Math.Min(start.Y, end.Y), video.Y, video.Y + video.Height);
            var y2 = Math.Clamp(Math.Max(start.Y, end.Y), video.Y, video.Y + video.Height);
            if (x2 - x1 < 6 || y2 - y1 < 6)
            {
                if (commit)
                {
                    _region = _dragOriginRegion;
                    ApplyRegionVisual();
                }
                return;
            }
            _region = OcrCheckpointStoreProxy.Normalize(new OcrRegion(
                (x1 - video.X) / video.Width,
                (y1 - video.Y) / video.Height,
                (x2 - x1) / video.Width,
                (y2 - y1) / video.Height));
            ApplyRegionVisual();
            return;
        }

        var left = _dragOriginRegion.X;
        var top = _dragOriginRegion.Y;
        var right = left + _dragOriginRegion.Width;
        var bottom = top + _dragOriginRegion.Height;
        var pointerX = Math.Clamp((end.X - video.X) / video.Width, 0, 1);
        var pointerY = Math.Clamp((end.Y - video.Y) / video.Height, 0, 1);
        var minimumWidth = Math.Min(0.25, 6 / video.Width);
        var minimumHeight = Math.Min(0.25, 6 / video.Height);

        if (_roiDragMode is RoiDragMode.ResizeLeft or RoiDragMode.ResizeTopLeft or RoiDragMode.ResizeBottomLeft)
            left = Math.Clamp(pointerX, 0, right - minimumWidth);
        if (_roiDragMode is RoiDragMode.ResizeRight or RoiDragMode.ResizeTopRight or RoiDragMode.ResizeBottomRight)
            right = Math.Clamp(pointerX, left + minimumWidth, 1);
        if (_roiDragMode is RoiDragMode.ResizeTop or RoiDragMode.ResizeTopLeft or RoiDragMode.ResizeTopRight)
            top = Math.Clamp(pointerY, 0, bottom - minimumHeight);
        if (_roiDragMode is RoiDragMode.ResizeBottom or RoiDragMode.ResizeBottomLeft or RoiDragMode.ResizeBottomRight)
            bottom = Math.Clamp(pointerY, top + minimumHeight, 1);

        _region = OcrCheckpointStoreProxy.Normalize(new OcrRegion(left, top, right - left, bottom - top));
        ApplyRegionVisual();
    }

    private RoiDragMode HitTestRoi(Point point)
    {
        var video = VideoRect();
        if (video.Width <= 0 || video.Height <= 0) return RoiDragMode.Create;
        var left = video.X + _region.X * video.Width;
        var top = video.Y + _region.Y * video.Height;
        var right = left + _region.Width * video.Width;
        var bottom = top + _region.Height * video.Height;
        const double tolerance = 10;
        var nearLeft = Math.Abs(point.X - left) <= tolerance && point.Y >= top - tolerance && point.Y <= bottom + tolerance;
        var nearRight = Math.Abs(point.X - right) <= tolerance && point.Y >= top - tolerance && point.Y <= bottom + tolerance;
        var nearTop = Math.Abs(point.Y - top) <= tolerance && point.X >= left - tolerance && point.X <= right + tolerance;
        var nearBottom = Math.Abs(point.Y - bottom) <= tolerance && point.X >= left - tolerance && point.X <= right + tolerance;

        if (nearLeft && nearTop) return RoiDragMode.ResizeTopLeft;
        if (nearRight && nearTop) return RoiDragMode.ResizeTopRight;
        if (nearLeft && nearBottom) return RoiDragMode.ResizeBottomLeft;
        if (nearRight && nearBottom) return RoiDragMode.ResizeBottomRight;
        if (nearLeft) return RoiDragMode.ResizeLeft;
        if (nearRight) return RoiDragMode.ResizeRight;
        if (nearTop) return RoiDragMode.ResizeTop;
        if (nearBottom) return RoiDragMode.ResizeBottom;
        if (point.X >= left && point.X <= right && point.Y >= top && point.Y <= bottom) return RoiDragMode.Move;
        return RoiDragMode.Create;
    }

    private void ApplyRegionVisual()
    {
        if (_media is null || PreviewCanvas.ActualWidth <= 0 || PreviewCanvas.ActualHeight <= 0) return;
        var region = ReadRegion();
        var rect = VideoRect();
        Canvas.SetLeft(RoiRectangle, rect.X + region.X * rect.Width);
        Canvas.SetTop(RoiRectangle, rect.Y + region.Y * rect.Height);
        RoiRectangle.Width = region.Width * rect.Width;
        RoiRectangle.Height = region.Height * rect.Height;
    }

    private Rect VideoRect()
    {
        if (_media is null || PreviewCanvas.ActualWidth <= 0 || PreviewCanvas.ActualHeight <= 0)
        {
            return new Rect(0, 0, PreviewCanvas.ActualWidth, PreviewCanvas.ActualHeight);
        }
        var source = _media.Width / (double)_media.Height;
        var host = PreviewCanvas.ActualWidth / PreviewCanvas.ActualHeight;
        return host > source
            ? new Rect((PreviewCanvas.ActualWidth - PreviewCanvas.ActualHeight * source) / 2, 0, PreviewCanvas.ActualHeight * source, PreviewCanvas.ActualHeight)
            : new Rect(0, (PreviewCanvas.ActualHeight - PreviewCanvas.ActualWidth / source) / 2, PreviewCanvas.ActualWidth, PreviewCanvas.ActualWidth / source);
    }

    private async Task PreparePlayerAsync(string path, bool directCompatible)
    {
        if (_player is not null)
        {
            _player.PlaybackSession.PositionChanged -= PlayerPositionChanged;
            _player.Dispose();
            _player = null;
        }
        _playerMode = false;
        PreviewPlayer.Visibility = Visibility.Collapsed;
        PreviewImage.Visibility = Visibility.Visible;
        PreviewCanvas.Visibility = Visibility.Visible;
        PreviewCanvas.IsHitTestVisible = false;
        SelectRegionButton.IsOn = false;
        PlaybackButton.Content = "Phát video";
        PlaybackButton.IsEnabled = directCompatible;
        FullscreenButton.IsEnabled = directCompatible;
        if (!directCompatible) return;

        var file = await StorageFile.GetFileFromPathAsync(path);
        var player = new MediaPlayer { AutoPlay = false };
        player.Source = MediaSource.CreateFromStorageFile(file);
        player.PlaybackSession.PositionChanged += PlayerPositionChanged;
        player.MediaFailed += (_, args) => DispatcherQueue.TryEnqueue(() =>
            StatusText.Text = "Player native lỗi; vẫn có thể dùng frame FFmpeg: " + args.ErrorMessage);
        _player = player;
        PreviewPlayer.SetMediaPlayer(player);
    }

    private async Task SetPlaybackModeAsync(bool enabled, bool play)
    {
        if (enabled && _player is null) throw new InvalidOperationException("Codec/container này dùng preview frame FFmpeg.");
        _playerMode = enabled;
        if (enabled)
        {
            SelectRegionButton.IsOn = false;
            PreviewCanvas.IsHitTestVisible = false;
        }
        PreviewPlayer.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        PreviewImage.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        PreviewCanvas.Visibility = Visibility.Visible;
        PlaybackButton.Content = enabled ? "Dùng frame" : "Phát video";
        if (_player is null) return;
        if (enabled)
        {
            _player.PlaybackSession.Position = TimeSpan.FromSeconds(Math.Max(0, Timeline.Value));
            if (play) _player.Play();
        }
        else
        {
            _player.Pause();
            _syncingTimeline = true;
            Timeline.Value = Math.Clamp(_player.PlaybackSession.Position.TotalSeconds, Timeline.Minimum, Timeline.Maximum);
            _syncingTimeline = false;
            await UpdateFrameAsync();
            ApplyRegionVisual();
        }
    }

    private void PlayerPositionChanged(MediaPlaybackSession sender, object args)
    {
        if (!_playerMode) return;
        var seconds = sender.Position.TotalSeconds;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_playerMode || _media is null) return;
            _syncingTimeline = true;
            Timeline.Value = Math.Clamp(seconds, Timeline.Minimum, Timeline.Maximum);
            _syncingTimeline = false;
            UpdateClock();
            SyncCueSelection(Timeline.Value);
        });
    }

    private void SyncCueSelection(double seconds)
    {
        // Đồng bộ highlight theo timeline là việc trang trí: kẹt layout thì bỏ
        // qua, không được gãy app.
        try
        {
            if (_visibleCues.Count == 0) return;
            var index = -1;
            for (var i = 0; i < _visibleCues.Count; i++)
            {
                if (seconds >= _visibleCues[i].Start && seconds <= _visibleCues[i].End)
                {
                    index = i;
                    break;
                }
            }
            if (CueList.SelectedIndex == index) return;
            _syncingCue = true;
            try
            {
                CueList.SelectedIndex = index;
                // Selection vẫn bám timeline, nhưng chỉ scroll khi đang follow cuối —
                // user scroll lên xem cue cũ thì highlight di chuyển mà không bị kéo xuống.
                if (index >= 0 && _userAtEnd) CueList.ScrollIntoView(CueList.Items[index]);
            }
            finally
            {
                _syncingCue = false;
            }
        }
        catch
        {
        }
    }

    private static string FormatClock(double seconds)
    {
        var value = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return value.TotalHours >= 1 ? value.ToString(@"hh\:mm\:ss") : value.ToString(@"mm\:ss");
    }

    private enum RoiDragMode
    {
        None,
        Create,
        Move,
        ResizeLeft,
        ResizeRight,
        ResizeTop,
        ResizeBottom,
        ResizeTopLeft,
        ResizeTopRight,
        ResizeBottomLeft,
        ResizeBottomRight,
    }

    private static class OcrCheckpointStoreProxy
    {
        public static OcrRegion Normalize(OcrRegion region)
        {
            if (region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0 || region.X + region.Width > 1.0001 || region.Y + region.Height > 1.0001)
            {
                throw new ArgumentException("Vùng OCR không hợp lệ.");
            }
            return region;
        }
    }
}
