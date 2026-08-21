using System.Globalization;
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
    private IReadOnlyList<OcrCue> _cues = [];
    private IReadOnlyList<OcrCue> _visibleCues = [];
    private Point? _dragStart;
    private MediaPlayer? _player;
    private bool _playerMode;
    private bool _syncingTimeline;
    private bool _syncingCue;
    private bool _syncingRegion;
    private bool _regionValid;

    public OcrPage(BiliSubApplication application, IFilePickerService picker)
    {
        _application = application; _picker = picker; InitializeComponent();
        ApplyConfiguration();
        Unloaded += (_, _) => _player?.Pause();
    }

    public void ApplyConfiguration()
    {
        _syncingRegion = true;
        try
        {
            LeftBox.Text = _application.Config.OcrLeft.ToString(CultureInfo.InvariantCulture);
            TopBox.Text = _application.Config.OcrTop.ToString(CultureInfo.InvariantCulture);
            RightBox.Text = _application.Config.OcrRight.ToString(CultureInfo.InvariantCulture);
            BottomBox.Text = _application.Config.OcrBottom.ToString(CultureInfo.InvariantCulture);
            for (var index = 0; index < DeviceBox.Items.Count; index++)
                if (string.Equals(DeviceBox.Items[index]?.ToString(), _application.Config.OcrDevice, StringComparison.OrdinalIgnoreCase)) { DeviceBox.SelectedIndex = index; break; }
        }
        finally { _syncingRegion = false; }
        RefreshRegionActions();
    }

    private void Page_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width > 0 && e.NewSize.Width < 900;
        PageRoot.Padding = narrow ? new Thickness(16) : new Thickness(24);
        WorkspaceGrid.Height = narrow ? 1_100 : 600;
        WorkspaceGrid.ColumnDefinitions[0].Width = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(2.2, GridUnitType.Star);
        WorkspaceGrid.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1.1, GridUnitType.Star);
        WorkspaceGrid.RowDefinitions[0].Height = narrow ? new GridLength(520) : new GridLength(1, GridUnitType.Star);
        WorkspaceGrid.RowDefinitions[1].Height = narrow ? new GridLength(580) : new GridLength(0);
        Grid.SetColumn(InspectorScroll, narrow ? 0 : 1);
        Grid.SetRow(InspectorScroll, narrow ? 1 : 0);
        InspectorScroll.Margin = narrow ? new Thickness(0, 14, 0, 0) : new Thickness(0);
    }

    private async void PickVideo_Click(object sender, RoutedEventArgs e)
    {
        var selected = await _picker.PickVideoAsync(); if (selected is null) return;
        try
        {
            _path = selected; PathText.Text = selected; StatusText.Text = "Đang đọc video...";
            _media = await _application.Media.ProbeAsync(selected, CancellationToken.None);
            Timeline.Maximum = Math.Max(0.1, _media.Duration); Timeline.Value = 0;
            await PreparePlayerAsync(selected, _media.DirectCompatible);
            MediaText.Text = $"{_media.Width}×{_media.Height} · {_media.Duration:0.0}s · {_media.Codec} · {(_media.DirectCompatible ? "player native + frame FFmpeg" : "frame FFmpeg fallback")}";
            await UpdateFrameAsync(); ApplyRegionVisual(); StatusText.Text = "Video đã sẵn sàng.";
            Timeline.IsEnabled = true; SelectRegionButton.IsEnabled = true; RefreshFrameButton.IsEnabled = true;
            RefreshRegionActions();
            var checkpoint = await _application.InspectOcrCheckpointAsync(BuildRequest(), CancellationToken.None);
            if (checkpoint.Exists) StatusText.Text = $"Có checkpoint schema {checkpoint.Schema}: {checkpoint.ProgressPercent:0.0}% · {checkpoint.CueCount} câu.";
        }
        catch (Exception error) { StatusText.Text = error.Message; }
    }

    private async void RefreshFrame_Click(object sender, RoutedEventArgs e) { try { await UpdateFrameAsync(); } catch (Exception error) { StatusText.Text = error.Message; } }
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
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0))) { writer.WriteBytes(bytes); await writer.StoreAsync(); }
        stream.Seek(0); var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(stream); PreviewImage.Source = bitmap; UpdateClock();
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
            StatusText.Text = selecting ? "Kéo trên frame để đặt vùng OCR; nhấn lại để dùng điều khiển player." : "Đã thoát chế độ khoanh ROI.";
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
        catch (Exception error) { StatusText.Text = "Toàn màn hình: " + error.Message; }
    }

    private async void Prepare_Click(object sender, RoutedEventArgs e)
    {
        try { StatusText.Text = "Đang chuẩn bị private PaddleOCR runtime..."; var status = await _application.PrepareOcrAsync(Selected(DeviceBox), CancellationToken.None); StatusText.Text = $"OCR Ready · {status.ActiveMode} · {status.Workers} worker"; }
        catch (Exception error) { StatusText.Text = error.Message; }
    }

    private async void TestFrame_Click(object sender, RoutedEventArgs e)
    {
        if (_path is null) return;
        try { StatusText.Text = "Đang nhận diện frame..."; var result = await _application.RecognizeFrameAsync(_path, Timeline.Value, ReadRegion(), Selected(DeviceBox), CancellationToken.None); OcrResultText.Text = result.Detected ? $"{result.Text} · {result.Confidence:P0}" : result.Error ?? "Không phát hiện phụ đề."; StatusText.Text = "Test frame hoàn tất."; }
        catch (Exception error) { StatusText.Text = error.Message; }
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_path is null || _media is null) return;
        _jobId = _application.StartOcrScan(BuildRequest()); ScanButton.IsEnabled = false; TestFrameButton.IsEnabled = false; ApplyRegionButton.IsEnabled = false; PauseButton.IsEnabled = true; CancelButton.IsEnabled = true; CueList.Items.Clear();
        while (_jobId is not null)
        {
            var snapshot = _application.Jobs.GetSnapshot(_jobId); Progress.Value = snapshot.Progress; StatusText.Text = snapshot.Message;
            if (snapshot.Result is OcrScanResult result)
            {
                _cues = result.Cues; RenderCues(); TelemetryText.Text = $"{result.Frames} frames · {result.OcrImages} OCR · {result.ParallelismSelected} lanes · {result.RealtimeSpeed:0.00}× · {result.BoundaryMerges} merges";
            }
            if (snapshot.Done) { _jobId = null; PauseButton.IsEnabled = false; CancelButton.IsEnabled = false; ExportButton.IsEnabled = _cues.Count > 0; RefreshRegionActions(); break; }
            await Task.Delay(400);
        }
    }

    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (_jobId is null) return;
        try { PauseButton.IsEnabled = false; await _application.PauseJobAsync(_jobId, CancellationToken.None); }
        catch (Exception error) { StatusText.Text = error.Message; PauseButton.IsEnabled = true; }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) { if (_jobId is not null) _application.CancelJob(_jobId); }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        try { var path = await _application.ExportOcrAsync(_cues, null, Path.GetFileNameWithoutExtension(_path) + "_Chinese.srt", CancellationToken.None); StatusText.Text = "Đã xuất: " + path; }
        catch (Exception error) { StatusText.Text = error.Message; }
    }

    private OcrScanRequest BuildRequest() => new(_path ?? throw new InvalidOperationException("Chưa chọn video."), ReadRegion(), Selected(ScanModeBox), Selected(DeviceBox), Selected(LanesBox), 1, _media?.Duration ?? 0);
    private OcrRegion ReadRegion() { var left = Number(LeftBox) / 100; var top = Number(TopBox) / 100; var right = Number(RightBox) / 100; var bottom = Number(BottomBox) / 100; return OcrCheckpointStoreProxy.Normalize(new OcrRegion(left, top, right - left, bottom - top)); }
    private static double Number(TextBox box) => double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : throw new ArgumentException($"Giá trị {box.Header} không hợp lệ.");
    private static string Selected(ComboBox box) => box.SelectedItem?.ToString() ?? string.Empty;
    private void RenderCues()
    {
        _visibleCues = _cues.TakeLast(120).ToArray();
        CueList.Items.Clear();
        foreach (var cue in _visibleCues) CueList.Items.Add($"{FormatClock(cue.Start)}  {cue.Text}");
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
        catch (Exception error) { StatusText.Text = error.Message; }
        finally { _syncingTimeline = false; }
    }

    private async void ApplyRegion_Click(object sender, RoutedEventArgs e) { try { var region = ReadRegion(); await _application.SetOcrRegionAsync(region, CancellationToken.None); ApplyConfiguration(); ApplyRegionVisual(); StatusText.Text = "Đã lưu vùng OCR."; } catch (Exception error) { StatusText.Text = error.Message; } }

    private void RegionBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingRegion) return;
        RefreshRegionActions();
        if (_regionValid) ApplyRegionVisual();
    }

    private void RefreshRegionActions()
    {
        try
        {
            _ = ReadRegion();
            _regionValid = true;
            RegionValidationText.Text = _media is null ? "Vùng hợp lệ; chọn video để áp dụng." : "Vùng hợp lệ.";
        }
        catch (Exception error)
        {
            _regionValid = false;
            RegionValidationText.Text = error.Message;
        }
        var ready = _media is not null && _regionValid && _jobId is null;
        ApplyRegionButton.IsEnabled = ready;
        TestFrameButton.IsEnabled = ready;
        ScanButton.IsEnabled = ready;
    }
    private void PreviewCanvas_PointerPressed(object sender, PointerRoutedEventArgs e) { _dragStart = e.GetCurrentPoint(PreviewCanvas).Position; PreviewCanvas.CapturePointer(e.Pointer); }
    private void PreviewCanvas_PointerMoved(object sender, PointerRoutedEventArgs e) { if (_dragStart is null || !e.GetCurrentPoint(PreviewCanvas).Properties.IsLeftButtonPressed) return; UpdateDrag(_dragStart.Value, e.GetCurrentPoint(PreviewCanvas).Position, commit: false); }
    private async void PreviewCanvas_PointerReleased(object sender, PointerRoutedEventArgs e) { if (_dragStart is null) return; UpdateDrag(_dragStart.Value, e.GetCurrentPoint(PreviewCanvas).Position, commit: true); _dragStart = null; PreviewCanvas.ReleasePointerCapture(e.Pointer); try { await _application.SetOcrRegionAsync(ReadRegion(), CancellationToken.None); ApplyConfiguration(); ApplyRegionVisual(); } catch (Exception error) { StatusText.Text = error.Message; } }

    private void UpdateDrag(Point start, Point end, bool commit)
    {
        var rect = VideoRect(); var x1 = Math.Clamp(Math.Min(start.X, end.X), rect.X, rect.X + rect.Width); var x2 = Math.Clamp(Math.Max(start.X, end.X), rect.X, rect.X + rect.Width); var y1 = Math.Clamp(Math.Min(start.Y, end.Y), rect.Y, rect.Y + rect.Height); var y2 = Math.Clamp(Math.Max(start.Y, end.Y), rect.Y, rect.Y + rect.Height);
        Canvas.SetLeft(RoiRectangle, x1); Canvas.SetTop(RoiRectangle, y1); RoiRectangle.Width = x2 - x1; RoiRectangle.Height = y2 - y1;
        if (commit && rect.Width > 0 && rect.Height > 0) { LeftBox.Text = ((x1 - rect.X) / rect.Width * 100).ToString("0.0", CultureInfo.InvariantCulture); RightBox.Text = ((x2 - rect.X) / rect.Width * 100).ToString("0.0", CultureInfo.InvariantCulture); TopBox.Text = ((y1 - rect.Y) / rect.Height * 100).ToString("0.0", CultureInfo.InvariantCulture); BottomBox.Text = ((y2 - rect.Y) / rect.Height * 100).ToString("0.0", CultureInfo.InvariantCulture); }
    }

    private void ApplyRegionVisual() { if (_media is null || PreviewCanvas.ActualWidth <= 0) return; var region = ReadRegion(); var rect = VideoRect(); Canvas.SetLeft(RoiRectangle, rect.X + region.X * rect.Width); Canvas.SetTop(RoiRectangle, rect.Y + region.Y * rect.Height); RoiRectangle.Width = region.Width * rect.Width; RoiRectangle.Height = region.Height * rect.Height; }
    private Rect VideoRect() { if (_media is null || PreviewCanvas.ActualWidth <= 0 || PreviewCanvas.ActualHeight <= 0) return new Rect(0, 0, PreviewCanvas.ActualWidth, PreviewCanvas.ActualHeight); var source = _media.Width / (double)_media.Height; var host = PreviewCanvas.ActualWidth / PreviewCanvas.ActualHeight; return host > source ? new Rect((PreviewCanvas.ActualWidth - PreviewCanvas.ActualHeight * source) / 2, 0, PreviewCanvas.ActualHeight * source, PreviewCanvas.ActualHeight) : new Rect(0, (PreviewCanvas.ActualHeight - PreviewCanvas.ActualWidth / source) / 2, PreviewCanvas.ActualWidth, PreviewCanvas.ActualWidth / source); }

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
            if (seconds >= _visibleCues[i].Start && seconds <= _visibleCues[i].End) { index = i; break; }
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

    private static class OcrCheckpointStoreProxy { public static OcrRegion Normalize(OcrRegion region) { if (region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0 || region.X + region.Width > 1.0001 || region.Y + region.Height > 1.0001) throw new ArgumentException("Vùng OCR không hợp lệ."); return region; } }
}
