using BiliSubStudio.App.Services;
using BiliSubStudio.Core.Application;
using BiliSubStudio.Core.Editor;
using BiliSubStudio.Core.Media;
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

public sealed partial class EditorPage : Page
{
    private readonly BiliSubApplication _application;
    private readonly IFilePickerService _picker;
    private readonly List<EditRegion> _regions = [];
    private string? _path;
    private MediaPreviewInfo? _media;
    private EditRegion? _pending;
    private Point? _dragStart;
    private string? _jobId;
    private MediaPlayer? _player;
    private bool _playerMode;
    private bool _syncingTimeline;
    private bool _syncingRegionCoordinates;

    public EditorPage(BiliSubApplication application, IFilePickerService picker)
    {
        _application = application; _picker = picker; InitializeComponent();
        Unloaded += (_, _) => _player?.Pause();
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
        ApplyPendingVisual();
    }

    private async void Pick_Click(object sender, RoutedEventArgs e)
    {
        var path = await _picker.PickVideoAsync(); if (path is null) return;
        try
        {
            _path = path;
            _media = await _application.Media.ProbeAsync(path, CancellationToken.None);
            _regions.Clear(); _pending = null; RegionList.Items.Clear(); SelectionRectangle.Visibility = Visibility.Collapsed;
            SetCoordinateBoxes(0, 0, 0, 0);
            Timeline.Maximum = Math.Max(0.1, _media.Duration); Timeline.Value = 0; EndBox.Value = _media.Duration;
            PathText.Text = path;
            await PreparePlayerAsync(path, _media.DirectCompatible);
            MediaText.Text = $"{_media.Width}×{_media.Height} · {_media.Duration:0.0}s · {_media.Codec} · {(_media.DirectCompatible ? "player native + frame FFmpeg" : "frame FFmpeg fallback")}";
            FileNameBox.Text = Path.GetFileNameWithoutExtension(path) + "_edited.mp4";
            Timeline.IsEnabled = true; SelectRegionButton.IsEnabled = true; RefreshFrameButton.IsEnabled = true;
            await UpdateFrameAsync();
            StatusText.Text = "Bật Khoanh vùng để kéo trên frame, hoặc nhập tọa độ bằng bàn phím.";
            RefreshEditorActions();
        }
        catch (Exception error) { StatusText.Text = error.Message; }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) { try { await UpdateFrameAsync(); } catch (Exception error) { StatusText.Text = error.Message; } }
    private void Timeline_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_playerMode && !_syncingTimeline && _player is not null) _player.PlaybackSession.Position = TimeSpan.FromSeconds(Math.Max(0, Timeline.Value));
        UpdateClock();
    }
    private async Task UpdateFrameAsync() { if (_path is null || _media is null) return; var bytes = await _application.Media.GetFrameJpegAsync(_path, Timeline.Value, CancellationToken.None); using var stream = new InMemoryRandomAccessStream(); using (var writer = new DataWriter(stream.GetOutputStreamAt(0))) { writer.WriteBytes(bytes); await writer.StoreAsync(); } stream.Seek(0); var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(stream); PreviewImage.Source = bitmap; UpdateClock(); }
    private void UpdateClock() => ClockText.Text = $"{FormatClock(Timeline.Value)} / {FormatClock(_media?.Duration ?? 0)}";

    private async void Playback_Click(object sender, RoutedEventArgs e)
    {
        try { await SetPlaybackModeAsync(!_playerMode, play: true); }
        catch (Exception error) { StatusText.Text = "Player native: " + error.Message; }
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

    private async void SelectRegion_Toggled(object sender, RoutedEventArgs e)
    {
        if (_media is null) { SelectRegionButton.IsOn = false; return; }
        var selecting = SelectRegionButton.IsOn;
        try
        {
            if (selecting && _playerMode) await SetPlaybackModeAsync(false, play: false);
            Overlay.IsHitTestVisible = selecting;
            StatusText.Text = selecting ? "Kéo trên frame để đặt vùng; nhấn lại để dùng điều khiển player." : "Đã thoát chế độ khoanh vùng.";
        }
        catch (Exception error)
        {
            SelectRegionButton.IsOn = false;
            Overlay.IsHitTestVisible = false;
            StatusText.Text = error.Message;
        }
    }

    private void WholeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        StartBox.IsEnabled = EndBox.IsEnabled = !WholeToggle.IsOn;
        if (!WholeToggle.IsOn) { StartBox.Value = Timeline.Value; EndBox.Value = Math.Min(_media?.Duration ?? Timeline.Value + 5, Timeline.Value + 5); }
        RefreshPendingFromInputs();
    }

    private void EditInput_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (IsLoaded) RefreshPendingFromInputs();
    }

    private void RegionCoordinates_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (IsLoaded && !_syncingRegionCoordinates) RefreshPendingFromInputs();
    }

    private void AddRegion_Click(object sender, RoutedEventArgs e)
    {
        RefreshPendingFromInputs();
        if (_pending is null) { StatusText.Text = "Hãy khoanh hoặc nhập một vùng hợp lệ trước."; return; }
        var region = _pending with { Effect = EffectBox.SelectedItem?.ToString() ?? "blur", Strength = (int)StrengthBox.Value, WholeVideo = WholeToggle.IsOn, Start = double.IsNaN(StartBox.Value) ? 0 : StartBox.Value, End = double.IsNaN(EndBox.Value) ? (_media?.Duration ?? 0) : EndBox.Value };
        try { _ = VideoEditorService.BuildFilter(new VideoEditRequest(_path ?? "x", ".", "x.mp4", _media?.Width ?? 1, _media?.Height ?? 1, _media?.Duration ?? 0, [region])); _regions.Add(region); RenderRegions(); StatusText.Text = $"Đã thêm vùng {_regions.Count}."; }
        catch (Exception error) { StatusText.Text = error.Message; }
    }

    private void RemoveRegion_Click(object sender, RoutedEventArgs e) { if (RegionList.SelectedIndex >= 0) { _regions.RemoveAt(RegionList.SelectedIndex); RenderRegions(); } }
    private void RegionList_SelectionChanged(object sender, SelectionChangedEventArgs e) => RemoveRegionButton.IsEnabled = RegionList.SelectedIndex >= 0 && _jobId is null;
    private void FileNameBox_TextChanged(object sender, TextChangedEventArgs e) { if (IsLoaded) RefreshEditorActions(); }
    private void RenderRegions() { RegionList.Items.Clear(); for (var i = 0; i < _regions.Count; i++) { var r = _regions[i]; RegionList.Items.Add($"{i + 1}. {r.Effect} · x={r.X:P0} y={r.Y:P0} w={r.Width:P0} h={r.Height:P0} · {(r.WholeVideo ? "toàn video" : $"{r.Start:0.0}-{r.End:0.0}s")}"); } RefreshEditorActions(); }

    private async void Render_Click(object sender, RoutedEventArgs e)
    {
        if (_path is null || _media is null || _regions.Count == 0) { StatusText.Text = "Cần video và ít nhất một vùng."; return; }
        _jobId = _application.StartEditor(new VideoEditRequest(_path, _application.Config.OutputDirectory, FileNameBox.Text, _media.Width, _media.Height, _media.Duration, _regions.ToArray())); RefreshEditorActions();
        while (_jobId is not null) { var snapshot = _application.Jobs.GetSnapshot(_jobId); Progress.Value = snapshot.Progress; StatusText.Text = snapshot.Message; if (snapshot.Done) { _jobId = null; RefreshEditorActions(); break; } await Task.Delay(400); }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) { if (_jobId is not null) _application.CancelJob(_jobId); }
    private void Overlay_PointerPressed(object sender, PointerRoutedEventArgs e) { _dragStart = e.GetCurrentPoint(Overlay).Position; Overlay.CapturePointer(e.Pointer); }
    private void Overlay_PointerMoved(object sender, PointerRoutedEventArgs e) { if (_dragStart is null || !e.GetCurrentPoint(Overlay).Properties.IsLeftButtonPressed) return; UpdateSelection(_dragStart.Value, e.GetCurrentPoint(Overlay).Position, false); }
    private void Overlay_PointerReleased(object sender, PointerRoutedEventArgs e) { if (_dragStart is null) return; UpdateSelection(_dragStart.Value, e.GetCurrentPoint(Overlay).Position, true); _dragStart = null; Overlay.ReleasePointerCapture(e.Pointer); }

    private void UpdateSelection(Point start, Point end, bool commit)
    {
        var rect = VideoRect();
        if (rect.Width <= 0 || rect.Height <= 0) return;
        var x1 = Math.Clamp(Math.Min(start.X, end.X), rect.X, rect.X + rect.Width); var x2 = Math.Clamp(Math.Max(start.X, end.X), rect.X, rect.X + rect.Width); var y1 = Math.Clamp(Math.Min(start.Y, end.Y), rect.Y, rect.Y + rect.Height); var y2 = Math.Clamp(Math.Max(start.Y, end.Y), rect.Y, rect.Y + rect.Height);
        SelectionRectangle.Visibility = Visibility.Visible; Canvas.SetLeft(SelectionRectangle, x1); Canvas.SetTop(SelectionRectangle, y1); SelectionRectangle.Width = x2 - x1; SelectionRectangle.Height = y2 - y1;
        if (commit && x2 - x1 >= 2 && y2 - y1 >= 2)
        {
            SetCoordinateBoxes((x1 - rect.X) / rect.Width, (y1 - rect.Y) / rect.Height, (x2 - x1) / rect.Width, (y2 - y1) / rect.Height);
            RefreshPendingFromInputs();
        }
    }
    private Rect VideoRect() { if (_media is null || Overlay.ActualWidth <= 0 || Overlay.ActualHeight <= 0) return new Rect(0, 0, Overlay.ActualWidth, Overlay.ActualHeight); var source = _media.Width / (double)_media.Height; var host = Overlay.ActualWidth / Overlay.ActualHeight; return host > source ? new Rect((Overlay.ActualWidth - Overlay.ActualHeight * source) / 2, 0, Overlay.ActualHeight * source, Overlay.ActualHeight) : new Rect(0, (Overlay.ActualHeight - Overlay.ActualWidth / source) / 2, Overlay.ActualWidth, Overlay.ActualWidth / source); }

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
        Overlay.Visibility = Visibility.Visible;
        Overlay.IsHitTestVisible = false;
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
            Overlay.IsHitTestVisible = false;
        }
        PreviewPlayer.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        PreviewImage.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        Overlay.Visibility = Visibility.Visible;
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
        });
    }

    private void SetCoordinateBoxes(double x, double y, double width, double height)
    {
        _syncingRegionCoordinates = true;
        try
        {
            RegionXBox.Value = x * 100; RegionYBox.Value = y * 100;
            RegionWidthBox.Value = width * 100; RegionHeightBox.Value = height * 100;
        }
        finally { _syncingRegionCoordinates = false; }
    }

    private void RefreshPendingFromInputs()
    {
        _pending = null;
        if (_media is null)
        {
            RegionValidationText.Text = "Chọn video rồi khoanh hoặc nhập vùng.";
            RefreshEditorActions();
            return;
        }
        var x = RegionXBox.Value / 100; var y = RegionYBox.Value / 100;
        var width = RegionWidthBox.Value / 100; var height = RegionHeightBox.Value / 100;
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) || !double.IsFinite(height) || x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > 1.0001 || y + height > 1.0001)
        {
            RegionValidationText.Text = "Vùng phải có kích thước lớn hơn 0 và nằm trọn trong video.";
            RefreshEditorActions();
            return;
        }
        var start = WholeToggle.IsOn || double.IsNaN(StartBox.Value) ? 0 : StartBox.Value;
        var end = WholeToggle.IsOn || double.IsNaN(EndBox.Value) ? _media.Duration : EndBox.Value;
        _pending = new EditRegion(x, y, width, height, EffectBox.SelectedItem?.ToString() ?? "blur", (int)StrengthBox.Value, WholeToggle.IsOn, start, end);
        try
        {
            _ = VideoEditorService.BuildFilter(new VideoEditRequest(_path ?? "x", ".", "x.mp4", _media.Width, _media.Height, _media.Duration, [_pending]));
            RegionValidationText.Text = "Vùng hợp lệ và sẵn sàng thêm.";
            ApplyPendingVisual();
        }
        catch (Exception error)
        {
            _pending = null;
            RegionValidationText.Text = error.Message;
        }
        RefreshEditorActions();
    }

    private void ApplyPendingVisual()
    {
        if (_pending is null || Overlay.ActualWidth <= 0 || Overlay.ActualHeight <= 0) return;
        var rect = VideoRect();
        SelectionRectangle.Visibility = Visibility.Visible;
        Canvas.SetLeft(SelectionRectangle, rect.X + _pending.X * rect.Width);
        Canvas.SetTop(SelectionRectangle, rect.Y + _pending.Y * rect.Height);
        SelectionRectangle.Width = _pending.Width * rect.Width;
        SelectionRectangle.Height = _pending.Height * rect.Height;
    }

    private void RefreshEditorActions()
    {
        var idle = _jobId is null;
        AddRegionButton.IsEnabled = idle && _pending is not null;
        RemoveRegionButton.IsEnabled = idle && RegionList.SelectedIndex >= 0;
        RenderButton.IsEnabled = idle && _path is not null && _media is not null && _regions.Count > 0 && !string.IsNullOrWhiteSpace(FileNameBox.Text);
        CancelButton.IsEnabled = !idle;
    }

    private static string FormatClock(double seconds)
    {
        var value = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return value.TotalHours >= 1 ? value.ToString(@"hh\:mm\:ss") : value.ToString(@"mm\:ss");
    }
}
