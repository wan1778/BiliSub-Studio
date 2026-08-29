using BiliSubStudio.App.Services;
using BiliSubStudio.Core.Application;
using BiliSubStudio.Core.Media;
using BiliSubStudio.Core.Ocr;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
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
    private string? _path;
    private MediaPreviewInfo? _media;
    private string? _jobId;
    private OcrScanRequest? _checkpointRequest;
    private OcrScanRequest? _activeRequest;
    private bool _cancelInProgress;
    private IReadOnlyList<OcrCue> _cues = [];
    private IReadOnlyList<OcrCue> _visibleCues = [];
    private readonly SortedDictionary<long, OcrCue> _liveCuesByStart = [];
    private DateTimeOffset _nextCueRenderAt;
    private bool _cueViewDirty;
    private OcrRegion _region = new(0.05, 0.65, 0.90, 0.29);
    private OcrRegion _dragOriginRegion = new(0.05, 0.65, 0.90, 0.29);
    private Point? _dragStart;
    private RoiDragMode _roiDragMode;
    private MediaPlayer? _player;
    private bool _playerMode;
    private bool _syncingTimeline;
    private bool _syncingCue;

    public OcrPage(BiliSubApplication application, IFilePickerService picker)
    {
        _application = application;
        _picker = picker;
        InitializeComponent();
        PreviewCanvas.SizeChanged += (_, _) => ApplyRegionVisual();

        ApplyConfiguration();
        Unloaded += (_, _) => _player?.Pause();
    }

    public void ApplyConfiguration()
    {
        var left = _application.Config.OcrLeft / 100d;
        var top = _application.Config.OcrTop / 100d;
        var right = _application.Config.OcrRight / 100d;
        var bottom = _application.Config.OcrBottom / 100d;
        _region = OcrCheckpointStoreProxy.Normalize(new OcrRegion(left, top, right - left, bottom - top));
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
        if (_jobId is not null)
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
            _liveCuesByStart.Clear();
            _nextCueRenderAt = default;
            _cueViewDirty = false;
            CueList.Items.Clear();
            CueCountText.Text = "0 câu";
            CancelButton.IsEnabled = false;
            ScanButton.Content = "Quét từ đầu";
            RestartButton.Visibility = Visibility.Collapsed;
            ExportButton.IsEnabled = false;
            Progress.Value = 0;
            TelemetryText.Text = "Chưa có telemetry.";
            OcrResultText.Text = string.Empty;
            _path = selected;
            PathText.Text = selected;
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
            RefreshRegionActions();
            var checkpointRequest = BuildRequest();
            var checkpoint = await _application.InspectOcrCheckpointAsync(checkpointRequest, CancellationToken.None);
            if (checkpoint.Exists)
            {
                _checkpointRequest = checkpointRequest;
                ApplyCheckpointUi();
                StatusText.Text = $"Có checkpoint schema {checkpoint.Schema}: {checkpoint.ProgressPercent:0.0}% · {checkpoint.CueCount} câu.";
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
        var request = _checkpointRequest ?? BuildRequest();
        var startMode = _checkpointRequest is null ? OcrScanStartMode.Fresh : OcrScanStartMode.Resume;
        await StartScanAsync(request, startMode);
    }

    private async void Restart_Click(object sender, RoutedEventArgs e)
    {
        if (_path is null || _media is null || _jobId is not null) return;
        var request = _checkpointRequest ?? BuildRequest();
        try
        {
            RestartButton.IsEnabled = false;
            ScanButton.IsEnabled = false;
            await _application.RemoveOcrCheckpointAsync(request, CancellationToken.None);
            var checkpoint = await _application.InspectOcrCheckpointAsync(request, CancellationToken.None);
            if (checkpoint.Exists) throw new IOException("Checkpoint OCR vẫn còn; chưa thể Quét lại từ đầu.");
            _checkpointRequest = null;
            await StartScanAsync(BuildRequest(), OcrScanStartMode.Fresh);
        }
        catch (Exception error)
        {
            RestartButton.IsEnabled = true;
            StatusText.Text = error.Message;
            RefreshRegionActions();
        }
    }

    private async Task StartScanAsync(OcrScanRequest request, OcrScanStartMode startMode)
    {
        _cues = [];
        _visibleCues = [];
        _liveCuesByStart.Clear();
        _nextCueRenderAt = default;
        _cueViewDirty = false;
        CueList.Items.Clear();
        CueCountText.Text = "0 câu";
        ExportButton.IsEnabled = false;
        Progress.Value = 0;
        TelemetryText.Text = "Đang chờ kết quả OCR...";
        _checkpointRequest = null;
        _activeRequest = request;
        var runningId = _application.StartOcrScan(request, startMode);
        _jobId = runningId;
        OcrScanResult? latestResult = null;
        PickVideoButton.IsEnabled = false;
        DeviceBox.IsEnabled = false;
        ScanModeBox.IsEnabled = false;
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
            if (snapshot.Result is OcrScanResult result)
            {
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
                TelemetryText.Text = $"{result.ParallelismSelected} FFmpeg lane · {result.WorkerCount} worker ({result.WorkerKinds}) · {result.CompletedLanes}/{result.ParallelismSelected} lane xong · {result.Frames} frames · {result.OcrImages} OCR · {result.RealtimeSpeed:0.00}× · frontier {FormatClock(result.SafeFrontierSeconds)}";
            }
            else if (snapshot.Result is OcrScanTelemetry telemetry)
            {
                TelemetryText.Text = $"{telemetry.SegmentLanes} FFmpeg lane · {telemetry.WorkerCount} worker ({telemetry.WorkerKinds}) · {telemetry.ActiveLanes} đang chạy · {telemetry.CompletedLanes} xong · {telemetry.Frames} frames · {telemetry.OcrImages} OCR · frontier {FormatClock(telemetry.SafeFrontierSeconds)}";
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
                if (cancelled)
                    ResetFreshUi("Đã hủy sạch: 0 Python worker · 0 FFmpeg/process tree · checkpoint đã xóa.");
                else if (paused)
                    ApplyCheckpointUi();
                else if (failed)
                {
                    var checkpoint = await _application.InspectOcrCheckpointAsync(request, CancellationToken.None);
                    if (checkpoint.Exists)
                    {
                        _checkpointRequest = request;
                        ApplyCheckpointUi();
                        StatusText.Text = snapshot.Error ?? snapshot.Message;
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
                }
                break;
            }
            await Task.Delay(400);
        }
    }

    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (_jobId is null) return;
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
        LanesBox.IsEnabled = true;
        PrepareOcrButton.IsEnabled = true;
        PauseButton.IsEnabled = false;
        RefreshRegionActions();
    }

    private void ApplyCheckpointUi()
    {
        DeviceBox.IsEnabled = false;
        ScanModeBox.IsEnabled = false;
        LanesBox.IsEnabled = false;
        SelectRegionButton.IsEnabled = false;
        ScanButton.Content = "Tiếp tục";
        CancelButton.Content = "Hủy và xóa";
        CancelButton.IsEnabled = true;
        RestartButton.Visibility = Visibility.Visible;
        RestartButton.IsEnabled = true;
        ExportButton.IsEnabled = false;
    }

    private void ResetFreshUi(string message)
    {
        _cues = [];
        _visibleCues = [];
        _liveCuesByStart.Clear();
        _nextCueRenderAt = default;
        _cueViewDirty = false;
        CueList.Items.Clear();
        CueCountText.Text = "0 câu";
        Progress.Value = 0;
        TelemetryText.Text = "Chưa có telemetry.";
        OcrResultText.Text = string.Empty;
        ExportButton.IsEnabled = false;
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
            var path = await _application.ExportOcrAsync(_cues, null, Path.GetFileNameWithoutExtension(_path) + "_Chinese.srt", CancellationToken.None);
            StatusText.Text = "Đã xuất: " + path;
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

    private bool MergeLiveCueSnapshot(IReadOnlyList<OcrCue> incoming)
    {
        var changed = false;
        foreach (var cue in incoming)
        {
            var key = checked((long)Math.Round(cue.Start * 1000, MidpointRounding.AwayFromZero));
            if (!_liveCuesByStart.TryGetValue(key, out var existing))
            {
                _liveCuesByStart[key] = cue;
                changed = true;
                continue;
            }
            var preferred = PreferLiveCue(existing, cue);
            if (preferred == existing) continue;
            _liveCuesByStart[key] = preferred;
            changed = true;
        }
        _cueViewDirty |= changed;
        return changed;
    }

    private void RefreshLiveCueView(bool force)
    {
        var now = DateTimeOffset.UtcNow;
        if (!_cueViewDirty || !force && now < _nextCueRenderAt) return;
        _cues = OcrCueReconciler.MergeTouchingIdentical(_liveCuesByStart.Values.ToArray());
        RenderCues();
        _cueViewDirty = false;
        _nextCueRenderAt = now.AddSeconds(2);
    }

    private void ApplyAuthoritativeCues(IReadOnlyList<OcrCue> cues)
    {
        _liveCuesByStart.Clear();
        foreach (var cue in cues.OrderBy(cue => cue.Start))
        {
            var key = checked((long)Math.Round(cue.Start * 1000, MidpointRounding.AwayFromZero));
            _liveCuesByStart[key] = _liveCuesByStart.TryGetValue(key, out var existing)
                ? PreferLiveCue(existing, cue)
                : cue;
        }
        _cues = OcrCueReconciler.MergeTouchingIdentical(_liveCuesByStart.Values.ToArray());
        RenderCues();
        _cueViewDirty = false;
        _nextCueRenderAt = DateTimeOffset.UtcNow.AddSeconds(2);
    }

    private static OcrCue PreferLiveCue(OcrCue current, OcrCue candidate)
    {
        var currentRunes = current.Text.EnumerateRunes().Count();
        var candidateRunes = candidate.Text.EnumerateRunes().Count();
        if (candidateRunes != currentRunes) return candidateRunes > currentRunes ? candidate : current;
        if (Math.Abs(candidate.Confidence - current.Confidence) > .000001)
            return candidate.Confidence > current.Confidence ? candidate : current;
        return candidate.End > current.End ? candidate : current;
    }

    private void RenderCues()
    {
        _visibleCues = _cues.OrderBy(cue => cue.Start).ToArray();
        for (var index = 0; index < _visibleCues.Count; index++)
        {
            var cue = _visibleCues[index];
            var start = TimeSpan.FromSeconds(Math.Max(0, cue.Start));
            var end = TimeSpan.FromSeconds(Math.Max(cue.Start, cue.End));
            var text = string.Join(" / ", cue.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
            var row = $"{index + 1}.  {start.ToString(@"hh\:mm\:ss\,fff")} → {end.ToString(@"hh\:mm\:ss\,fff")}  ·  {text}";
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
        while (CueList.Items.Count > _visibleCues.Count)
            CueList.Items.RemoveAt(CueList.Items.Count - 1);
        CueCountText.Text = $"{_visibleCues.Count} câu";
        SyncCueSelection(Timeline.Value);
    }

    private async void CueList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
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
        CueList.SelectedIndex = index;
        if (index >= 0) CueList.ScrollIntoView(CueList.Items[index]);
        _syncingCue = false;
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
