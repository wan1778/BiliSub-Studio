using BiliSubStudio.App.Services;
using BiliSubStudio.Core.Application;
using BiliSubStudio.Core.Editor;
using BiliSubStudio.Core.Media;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;

namespace BiliSubStudio.App.Pages;

public sealed partial class EditorPage : Page
{
    private enum DragKind { None, Create, Move, North, South, East, West, NorthEast, NorthWest, SouthEast, SouthWest }

    private readonly BiliSubApplication _application;
    private readonly IFilePickerService _picker;
    private readonly EditorRegionDocument _document = new();
    private string? _path;
    private MediaPreviewInfo? _media;
    private EditorProject? _project;
    private EditRegion? _draftRegion;
    private string? _jobId;
    private MediaPlayer? _player;
    private bool _playerMode;
    private bool _syncingTimeline;
    private bool _syncingInputs;
    private bool _syncingList;
    private Point? _dragStartNormalized;
    private EditRegion? _dragOriginal;
    private DragKind _dragKind;
    private bool _dragHistoryCaptured;
    private CancellationTokenSource? _previewCancellation;
    private CancellationTokenSource? _saveCancellation;
    private int _previewRevision;
    private double _lastOverlayWidth = -1;
    private double _lastOverlayHeight = -1;
    private double _lastTimelineWidth = -1;

    public EditorPage(BiliSubApplication application, IFilePickerService picker)
    {
        _application = application;
        _picker = picker;
        InitializeComponent();
        LayoutUpdated += EditorPage_LayoutUpdated;
        Unloaded += EditorPage_Unloaded;
    }

    private void EditorPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _player?.Pause();
        _previewCancellation?.Cancel();
        _ = SaveProjectNowAsync();
    }

    private async void Pick_Click(object sender, RoutedEventArgs e)
    {
        var path = await _picker.PickVideoAsync();
        if (path is null) return;
        try
        {
            await SaveProjectNowAsync();
            _path = path;
            _media = await _application.Media.ProbeAsync(path, CancellationToken.None);
            _project = await _application.LoadEditorProjectAsync(path, _media, CancellationToken.None);
            _document.Reset(_project.Regions);
            _draftRegion = null;
            Timeline.Maximum = Math.Max(0.1, _media.Duration);
            Timeline.Value = 0;
            PathText.Text = path;
            MediaText.Text = $"{_media.Width}×{_media.Height} · {_media.Duration:0.0}s · {_media.Codec} · {(_media.DirectCompatible ? "player native + preview hiệu ứng FFmpeg" : "preview hiệu ứng FFmpeg")}";
            _syncingInputs = true;
            try
            {
                FileNameBox.Text = _project.FileName;
                EndBox.Value = _media.Duration;
            }
            finally { _syncingInputs = false; }
            await PreparePlayerAsync(path, _media.DirectCompatible);
            Timeline.IsEnabled = true;
            RefreshFrameButton.IsEnabled = true;
            if (_document.Selected is not null) LoadSelectedIntoInputs();
            else SetCoordinateBoxes(0, 0, 0, 0);
            RenderDocument();
            await UpdateFrameAsync();
            StatusText.Text = _document.Regions.Count > 0
                ? $"Đã mở lại project với {_document.Regions.Count} vùng."
                : "Kéo trực tiếp trên frame để tạo vùng đầu tiên.";
            RefreshEditorActions();
        }
        catch (Exception error) { StatusText.Text = error.Message; }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        try { await UpdateFrameAsync(); }
        catch (Exception error) { StatusText.Text = error.Message; }
    }

    private void Timeline_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_playerMode && !_syncingTimeline && _player is not null)
            _player.PlaybackSession.Position = TimeSpan.FromSeconds(Math.Max(0, Timeline.Value));
        UpdateClock();
        RenderOverlays();
        RenderTimelineRegions();
        if (!_playerMode && _media is not null) QueuePreviewRefresh();
    }

    private async Task UpdateFrameAsync()
    {
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _previewCancellation = cancellation;
        var revision = ++_previewRevision;
        await RefreshPreviewAsync(revision, TimeSpan.Zero, cancellation.Token);
    }

    private void QueuePreviewRefresh()
    {
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _previewCancellation = cancellation;
        var revision = ++_previewRevision;
        _ = RefreshPreviewAsync(revision, TimeSpan.FromMilliseconds(140), cancellation.Token);
    }

    private async Task RefreshPreviewAsync(int revision, TimeSpan delay, CancellationToken cancellationToken)
    {
        if (_path is null || _media is null || _playerMode) return;
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
            var regions = CurrentPreviewRegions();
            var bytes = await _application.GetEditorPreviewFrameJpegAsync(_path, Timeline.Value, _media, regions, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (revision != _previewRevision) return;
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
            }
            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            if (revision == _previewRevision) PreviewImage.Source = bitmap;
            UpdateClock();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (revision == _previewRevision) StatusText.Text = "Preview hiệu ứng: " + error.Message;
        }
    }

    private IReadOnlyList<EditRegion> CurrentPreviewRegions()
    {
        if (_draftRegion is null) return _document.Regions.ToArray();
        return [.. _document.Regions, _draftRegion];
    }

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

    private void WholeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _syncingInputs) return;
        StartBox.IsEnabled = EndBox.IsEnabled = !WholeToggle.IsOn && _jobId is null;
        if (!WholeToggle.IsOn && _media is not null && _document.Selected is null)
        {
            _syncingInputs = true;
            try
            {
                StartBox.Value = Timeline.Value;
                EndBox.Value = Math.Min(_media.Duration, Timeline.Value + 5);
            }
            finally { _syncingInputs = false; }
        }
        ApplyInputsToDocument();
    }

    private void EffectBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && !_syncingInputs) ApplyInputsToDocument();
    }

    private void EditInput_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (IsLoaded && !_syncingInputs) ApplyInputsToDocument();
    }

    private void RegionCoordinates_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (IsLoaded && !_syncingInputs) ApplyInputsToDocument();
    }

    private void ApplyInputsToDocument()
    {
        var region = ReadRegionFromInputs(_document.Selected?.Id ?? string.Empty);
        if (region is null)
        {
            _draftRegion = null;
            RegionValidationText.Text = _media is null
                ? "Chọn video rồi kéo để tạo vùng."
                : "Vùng phải lớn hơn 0, nằm trong video và có thời gian hợp lệ.";
            RefreshEditorActions();
            return;
        }
        try
        {
            ValidateRegion(region);
            if (_document.Selected is not null)
            {
                _document.ReplaceSelected(region);
                _draftRegion = null;
                RegionValidationText.Text = "Đã cập nhật vùng đang chọn.";
                RenderDocument();
                QueueProjectSave();
                QueuePreviewRefresh();
            }
            else
            {
                _draftRegion = region;
                RegionValidationText.Text = "Tọa độ hợp lệ; bấm Thêm để lưu vùng.";
                RenderOverlays();
            }
        }
        catch (Exception error)
        {
            _draftRegion = null;
            RegionValidationText.Text = error.Message;
        }
        RefreshEditorActions();
    }

    private void AddRegion_Click(object sender, RoutedEventArgs e)
    {
        var region = ReadRegionFromInputs(string.Empty);
        if (region is null) { StatusText.Text = "Hãy nhập một vùng hợp lệ trước."; return; }
        try
        {
            ValidateRegion(region);
            _document.Add(region);
            _draftRegion = null;
            LoadSelectedIntoInputs();
            DocumentChanged($"Đã thêm vùng {_document.Regions.Count}.");
        }
        catch (Exception error) { StatusText.Text = error.Message; }
    }

    private void RemoveRegion_Click(object sender, RoutedEventArgs e)
    {
        if (_document.RemoveSelected())
        {
            if (_document.Selected is not null) LoadSelectedIntoInputs();
            else SetCoordinateBoxes(0, 0, 0, 0);
            DocumentChanged("Đã xóa vùng chọn. Có thể Hoàn tác.");
        }
    }

    private void RegionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingList) return;
        _document.Select(RegionList.SelectedIndex);
        _draftRegion = null;
        if (_document.Selected is not null) LoadSelectedIntoInputs();
        RenderDocument();
        RefreshEditorActions();
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_document.Undo())
        {
            if (_document.Selected is not null) LoadSelectedIntoInputs();
            DocumentChanged("Đã hoàn tác.");
        }
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        if (_document.Redo())
        {
            if (_document.Selected is not null) LoadSelectedIntoInputs();
            DocumentChanged("Đã làm lại.");
        }
    }

    private void SubtitlePreset_Click(object sender, RoutedEventArgs e)
    {
        if (_media is null) return;
        _document.Add(new EditRegion(.08, .72, .84, .18, "blur", 18, true, 0, _media.Duration));
        LoadSelectedIntoInputs();
        DocumentChanged("Đã thêm preset vùng phụ đề dưới.");
    }

    private void WatermarkPreset_Click(object sender, RoutedEventArgs e)
    {
        if (_media is null) return;
        _document.Add(new EditRegion(.78, .04, .18, .10, "mosaic", 12, true, 0, _media.Duration));
        LoadSelectedIntoInputs();
        DocumentChanged("Đã thêm preset watermark góc phải.");
    }

    private void FileNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded && !_syncingInputs)
        {
            QueueProjectSave();
            RefreshEditorActions();
        }
    }

    private async void Render_Click(object sender, RoutedEventArgs e)
    {
        if (_path is null || _media is null || _document.Regions.Count == 0)
        {
            StatusText.Text = "Cần video và ít nhất một vùng.";
            return;
        }
        try
        {
            await SaveProjectNowAsync();
            _jobId = _application.StartEditor(new VideoEditRequest(
                _path,
                _application.Config.OutputDirectory,
                FileNameBox.Text,
                _media.Width,
                _media.Height,
                _media.Duration,
                _document.Regions.ToArray()));
            RefreshEditorActions();
            while (_jobId is not null)
            {
                var snapshot = _application.Jobs.GetSnapshot(_jobId);
                Progress.Value = snapshot.Progress;
                StatusText.Text = snapshot.Message;
                if (snapshot.Done)
                {
                    _jobId = null;
                    RefreshEditorActions();
                    QueuePreviewRefresh();
                    break;
                }
                await Task.Delay(350);
            }
        }
        catch (Exception error)
        {
            _jobId = null;
            StatusText.Text = error.Message;
            RefreshEditorActions();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_jobId is null) return;
        _application.CancelJob(_jobId);
        StatusText.Text = "Đang dừng FFmpeg và xóa file render dở...";
    }

    private void Overlay_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_media is null || _jobId is not null || _playerMode) return;
        var point = e.GetCurrentPoint(Overlay).Position;
        if (!TryNormalize(point, out var normalized)) return;
        var hit = HitTestRegion(point);
        if (hit.Index >= 0)
        {
            _document.Select(hit.Index);
            _dragKind = hit.Kind;
            _dragOriginal = _document.Selected;
            LoadSelectedIntoInputs();
        }
        else
        {
            _document.Select(-1);
            _dragKind = DragKind.Create;
            _dragOriginal = null;
            _draftRegion = null;
        }
        _dragStartNormalized = normalized;
        _dragHistoryCaptured = false;
        Overlay.CapturePointer(e.Pointer);
        RenderDocument();
        e.Handled = true;
    }

    private void Overlay_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragStartNormalized is null || _media is null || !e.GetCurrentPoint(Overlay).Properties.IsLeftButtonPressed) return;
        if (!TryNormalize(e.GetCurrentPoint(Overlay).Position, out var current)) return;
        if (_dragKind == DragKind.Create)
        {
            var x = Math.Min(_dragStartNormalized.Value.X, current.X);
            var y = Math.Min(_dragStartNormalized.Value.Y, current.Y);
            var width = Math.Abs(current.X - _dragStartNormalized.Value.X);
            var height = Math.Abs(current.Y - _dragStartNormalized.Value.Y);
            _draftRegion = RegionWithCurrentSettings(x, y, width, height, string.Empty);
            RenderOverlays();
            return;
        }
        if (_dragOriginal is null || _document.Selected is null) return;
        var updated = ResizeOrMove(_dragOriginal, _dragStartNormalized.Value, current, _dragKind);
        if (!_dragHistoryCaptured)
        {
            _document.BeginChange();
            _dragHistoryCaptured = true;
        }
        _document.ReplaceSelected(updated, capture: false);
        SetCoordinateBoxes(updated.X, updated.Y, updated.Width, updated.Height);
        RenderDocument(renderInputs: false);
    }

    private void Overlay_PointerReleased(object sender, PointerRoutedEventArgs e) => FinishDrag(e, commit: true);
    private void Overlay_PointerCanceled(object sender, PointerRoutedEventArgs e) => FinishDrag(e, commit: false);

    private void FinishDrag(PointerRoutedEventArgs e, bool commit)
    {
        if (_dragStartNormalized is null) return;
        if (!commit && _dragHistoryCaptured) _document.Undo();
        if (commit && _dragKind == DragKind.Create && _draftRegion is { Width: >= .002, Height: >= .002 } created)
        {
            _document.Add(created);
            _draftRegion = null;
            LoadSelectedIntoInputs();
            DocumentChanged($"Đã tạo vùng {_document.Regions.Count}.");
        }
        else if (commit && _dragHistoryCaptured)
        {
            if (_document.Selected is not null) LoadSelectedIntoInputs();
            DocumentChanged("Đã cập nhật vị trí/kích thước vùng.");
        }
        else
        {
            _draftRegion = null;
            RenderDocument();
        }
        _dragStartNormalized = null;
        _dragOriginal = null;
        _dragKind = DragKind.None;
        _dragHistoryCaptured = false;
        Overlay.ReleasePointerCapture(e.Pointer);
        RefreshEditorActions();
    }

    private void EditorPage_LayoutUpdated(object? sender, object e)
    {
        var overlayChanged = Math.Abs(Overlay.ActualWidth - _lastOverlayWidth) >= .5
            || Math.Abs(Overlay.ActualHeight - _lastOverlayHeight) >= .5;
        if (overlayChanged)
        {
            _lastOverlayWidth = Overlay.ActualWidth;
            _lastOverlayHeight = Overlay.ActualHeight;
            RenderOverlays();
        }
        if (Math.Abs(RegionTimelineCanvas.ActualWidth - _lastTimelineWidth) >= .5)
        {
            _lastTimelineWidth = RegionTimelineCanvas.ActualWidth;
            RenderTimelineRegions();
        }
    }

    private void Page_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Delete or VirtualKey.Back) || _jobId is not null || _document.Selected is null) return;
        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox) return;
        if (_document.RemoveSelected())
        {
            e.Handled = true;
            DocumentChanged("Đã xóa vùng chọn. Có thể Hoàn tác.");
        }
    }

    private void DocumentChanged(string status)
    {
        RenderDocument();
        QueueProjectSave();
        QueuePreviewRefresh();
        StatusText.Text = status;
    }

    private void RenderDocument(bool renderInputs = true)
    {
        RenderRegionList();
        RenderOverlays();
        RenderTimelineRegions();
        if (renderInputs && _document.Selected is not null) LoadSelectedIntoInputs();
        RefreshEditorActions();
    }

    private void RenderRegionList()
    {
        _syncingList = true;
        try
        {
            RegionList.Items.Clear();
            for (var index = 0; index < _document.Regions.Count; index++)
            {
                var region = _document.Regions[index];
                RegionList.Items.Add($"{index + 1}. {EffectLabel(region.Effect)} · x={region.X:P0} y={region.Y:P0} w={region.Width:P0} h={region.Height:P0} · {(region.WholeVideo ? "toàn video" : $"{region.Start:0.0}-{region.End:0.0}s")}");
            }
            RegionList.SelectedIndex = _document.SelectedIndex;
            if (_document.SelectedIndex >= 0) RegionList.ScrollIntoView(RegionList.Items[_document.SelectedIndex]);
        }
        finally { _syncingList = false; }
    }

    private void RenderOverlays()
    {
        Overlay.Children.Clear();
        if (_media is null) return;
        var video = VideoRect();
        if (video.Width <= 0 || video.Height <= 0) return;
        for (var index = 0; index < _document.Regions.Count; index++)
        {
            var region = _document.Regions[index];
            var selected = index == _document.SelectedIndex;
            var active = VideoEditorService.IsActiveAt(region, Timeline.Value);
            var rectangle = RegionRectangle(region, video,
                selected ? ColorHelper.FromArgb(255, 49, 142, 242) : active ? ColorHelper.FromArgb(230, 70, 200, 220) : ColorHelper.FromArgb(190, 130, 140, 150),
                selected ? ColorHelper.FromArgb(52, 49, 142, 242) : active ? ColorHelper.FromArgb(30, 70, 200, 220) : ColorHelper.FromArgb(18, 130, 140, 150),
                selected ? 2.5 : 1.5);
            Overlay.Children.Add(rectangle);
        }
        if (_draftRegion is not null)
        {
            Overlay.Children.Add(RegionRectangle(_draftRegion, video,
                ColorHelper.FromArgb(255, 255, 190, 60), ColorHelper.FromArgb(38, 255, 190, 60), 2));
        }
        if (_document.Selected is { } selectedRegion) RenderHandles(selectedRegion, video);
    }

    private Rectangle RegionRectangle(EditRegion region, Rect video, Windows.UI.Color stroke, Windows.UI.Color fill, double thickness)
    {
        var rectangle = new Rectangle
        {
            Stroke = new SolidColorBrush(stroke),
            Fill = new SolidColorBrush(fill),
            StrokeThickness = thickness,
            RadiusX = 2,
            RadiusY = 2,
            Width = Math.Max(1, region.Width * video.Width),
            Height = Math.Max(1, region.Height * video.Height),
        };
        Canvas.SetLeft(rectangle, video.X + region.X * video.Width);
        Canvas.SetTop(rectangle, video.Y + region.Y * video.Height);
        return rectangle;
    }

    private void RenderHandles(EditRegion region, Rect video)
    {
        var x1 = video.X + region.X * video.Width;
        var y1 = video.Y + region.Y * video.Height;
        var x2 = x1 + region.Width * video.Width;
        var y2 = y1 + region.Height * video.Height;
        var xm = (x1 + x2) / 2;
        var ym = (y1 + y2) / 2;
        foreach (var point in new[]
        {
            new Point(x1, y1), new Point(xm, y1), new Point(x2, y1), new Point(x2, ym),
            new Point(x2, y2), new Point(xm, y2), new Point(x1, y2), new Point(x1, ym),
        })
        {
            var handle = new Rectangle
            {
                Width = 9,
                Height = 9,
                Fill = new SolidColorBrush(Colors.White),
                Stroke = new SolidColorBrush(ColorHelper.FromArgb(255, 49, 142, 242)),
                StrokeThickness = 1.5,
                RadiusX = 2,
                RadiusY = 2,
            };
            Canvas.SetLeft(handle, point.X - 4.5);
            Canvas.SetTop(handle, point.Y - 4.5);
            Overlay.Children.Add(handle);
        }
    }

    private void RenderTimelineRegions()
    {
        RegionTimelineCanvas.Children.Clear();
        if (_media is null || _media.Duration <= 0 || RegionTimelineCanvas.ActualWidth <= 0) return;
        for (var index = 0; index < _document.Regions.Count; index++)
        {
            var region = _document.Regions[index];
            var start = region.WholeVideo ? 0 : Math.Clamp(region.Start, 0, _media.Duration);
            var end = region.WholeVideo ? _media.Duration : Math.Clamp(region.End, start, _media.Duration);
            var bar = new Rectangle
            {
                Height = 3,
                Width = Math.Max(2, (end - start) / _media.Duration * RegionTimelineCanvas.ActualWidth),
                RadiusX = 1.5,
                RadiusY = 1.5,
                Fill = new SolidColorBrush(index == _document.SelectedIndex
                    ? ColorHelper.FromArgb(230, 49, 142, 242)
                    : ColorHelper.FromArgb(170, 70, 200, 220)),
            };
            Canvas.SetLeft(bar, start / _media.Duration * RegionTimelineCanvas.ActualWidth);
            Canvas.SetTop(bar, 2 + index % 3 * 4);
            RegionTimelineCanvas.Children.Add(bar);
        }
    }

    private (int Index, DragKind Kind) HitTestRegion(Point point)
    {
        var video = VideoRect();
        if (video.Width <= 0 || video.Height <= 0) return (-1, DragKind.None);
        if (_document.Selected is { } selected)
        {
            var handle = HitSelectedHandles(point, selected, video);
            if (handle != DragKind.None) return (_document.SelectedIndex, handle);
        }
        for (var index = _document.Regions.Count - 1; index >= 0; index--)
        {
            var region = _document.Regions[index];
            var left = video.X + region.X * video.Width;
            var top = video.Y + region.Y * video.Height;
            if (point.X >= left && point.X <= left + region.Width * video.Width && point.Y >= top && point.Y <= top + region.Height * video.Height)
                return (index, DragKind.Move);
        }
        return (-1, DragKind.Create);
    }

    private static DragKind HitSelectedHandles(Point point, EditRegion region, Rect video)
    {
        var left = video.X + region.X * video.Width;
        var top = video.Y + region.Y * video.Height;
        var right = left + region.Width * video.Width;
        var bottom = top + region.Height * video.Height;
        const double tolerance = 9;
        var nearLeft = Math.Abs(point.X - left) <= tolerance;
        var nearRight = Math.Abs(point.X - right) <= tolerance;
        var nearTop = Math.Abs(point.Y - top) <= tolerance;
        var nearBottom = Math.Abs(point.Y - bottom) <= tolerance;
        var withinX = point.X >= left - tolerance && point.X <= right + tolerance;
        var withinY = point.Y >= top - tolerance && point.Y <= bottom + tolerance;
        if (nearLeft && nearTop) return DragKind.NorthWest;
        if (nearRight && nearTop) return DragKind.NorthEast;
        if (nearLeft && nearBottom) return DragKind.SouthWest;
        if (nearRight && nearBottom) return DragKind.SouthEast;
        if (nearTop && withinX) return DragKind.North;
        if (nearBottom && withinX) return DragKind.South;
        if (nearLeft && withinY) return DragKind.West;
        if (nearRight && withinY) return DragKind.East;
        return DragKind.None;
    }

    private static EditRegion ResizeOrMove(EditRegion original, Point start, Point current, DragKind kind)
    {
        var dx = current.X - start.X;
        var dy = current.Y - start.Y;
        if (kind == DragKind.Move)
        {
            return original with
            {
                X = Math.Clamp(original.X + dx, 0, 1 - original.Width),
                Y = Math.Clamp(original.Y + dy, 0, 1 - original.Height),
            };
        }
        var x1 = original.X;
        var y1 = original.Y;
        var x2 = original.X + original.Width;
        var y2 = original.Y + original.Height;
        if (kind is DragKind.West or DragKind.NorthWest or DragKind.SouthWest) x1 = Math.Clamp(x1 + dx, 0, x2 - .002);
        if (kind is DragKind.East or DragKind.NorthEast or DragKind.SouthEast) x2 = Math.Clamp(x2 + dx, x1 + .002, 1);
        if (kind is DragKind.North or DragKind.NorthEast or DragKind.NorthWest) y1 = Math.Clamp(y1 + dy, 0, y2 - .002);
        if (kind is DragKind.South or DragKind.SouthEast or DragKind.SouthWest) y2 = Math.Clamp(y2 + dy, y1 + .002, 1);
        return original with { X = x1, Y = y1, Width = x2 - x1, Height = y2 - y1 };
    }

    private bool TryNormalize(Point point, out Point normalized)
    {
        var video = VideoRect();
        if (video.Width <= 0 || video.Height <= 0 || point.X < video.X || point.X > video.Right || point.Y < video.Y || point.Y > video.Bottom)
        {
            normalized = default;
            return false;
        }
        normalized = new Point((point.X - video.X) / video.Width, (point.Y - video.Y) / video.Height);
        return true;
    }

    private EditRegion RegionWithCurrentSettings(double x, double y, double width, double height, string id)
    {
        var duration = _media?.Duration ?? 0;
        var start = WholeToggle.IsOn || double.IsNaN(StartBox.Value) ? 0 : StartBox.Value;
        var end = WholeToggle.IsOn || double.IsNaN(EndBox.Value) ? duration : EndBox.Value;
        return new EditRegion(x, y, width, height, SelectedEffect(), (int)Math.Clamp(StrengthBox.Value, 2, 64), WholeToggle.IsOn, start, end, id);
    }

    private EditRegion? ReadRegionFromInputs(string id)
    {
        if (_media is null) return null;
        var x = RegionXBox.Value / 100;
        var y = RegionYBox.Value / 100;
        var width = RegionWidthBox.Value / 100;
        var height = RegionHeightBox.Value / 100;
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) || !double.IsFinite(height) ||
            x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > 1.0001 || y + height > 1.0001) return null;
        return RegionWithCurrentSettings(x, y, width, height, id);
    }

    private void ValidateRegion(EditRegion region)
    {
        if (_media is null) throw new InvalidOperationException("Chưa chọn video.");
        _ = VideoEditorService.BuildFilter(new VideoEditRequest(_path ?? "x", ".", "x.mp4", _media.Width, _media.Height, _media.Duration, [region]));
    }

    private void LoadSelectedIntoInputs()
    {
        var region = _document.Selected;
        if (region is null) return;
        _syncingInputs = true;
        try
        {
            SetCoordinateBoxes(region.X, region.Y, region.Width, region.Height);
            SelectEffect(region.Effect);
            StrengthBox.Value = region.Strength;
            WholeToggle.IsOn = region.WholeVideo;
            StartBox.Value = region.Start;
            EndBox.Value = region.End;
            StartBox.IsEnabled = EndBox.IsEnabled = !region.WholeVideo && _jobId is null;
            RegionValidationText.Text = "Vùng đang chọn có thể kéo, resize hoặc sửa bằng các ô số.";
        }
        finally { _syncingInputs = false; }
    }

    private void SetCoordinateBoxes(double x, double y, double width, double height)
    {
        var previous = _syncingInputs;
        _syncingInputs = true;
        try
        {
            RegionXBox.Value = x * 100;
            RegionYBox.Value = y * 100;
            RegionWidthBox.Value = width * 100;
            RegionHeightBox.Value = height * 100;
        }
        finally { _syncingInputs = previous; }
    }

    private string SelectedEffect() => (EffectBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "blur";

    private void SelectEffect(string effect)
    {
        for (var index = 0; index < EffectBox.Items.Count; index++)
        {
            if (EffectBox.Items[index] is ComboBoxItem item && string.Equals(item.Tag?.ToString(), effect, StringComparison.OrdinalIgnoreCase))
            {
                EffectBox.SelectedIndex = index;
                return;
            }
        }
        EffectBox.SelectedIndex = 0;
    }

    private static string EffectLabel(string effect) => effect.ToLowerInvariant() switch
    {
        "mosaic" => "Mosaic",
        "cover" => "Che đen",
        _ => "Làm mờ",
    };

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
        PlaybackButton.Content = "Phát video";
        PlaybackButton.IsEnabled = directCompatible;
        FullscreenButton.IsEnabled = directCompatible;
        if (!directCompatible) return;
        var file = await StorageFile.GetFileFromPathAsync(path);
        var player = new MediaPlayer { AutoPlay = false };
        player.Source = MediaSource.CreateFromStorageFile(file);
        player.PlaybackSession.PositionChanged += PlayerPositionChanged;
        player.MediaFailed += (_, args) => DispatcherQueue.TryEnqueue(() =>
            StatusText.Text = "Player native lỗi; vẫn có thể dùng preview FFmpeg: " + args.ErrorMessage);
        _player = player;
        PreviewPlayer.SetMediaPlayer(player);
    }

    private async Task SetPlaybackModeAsync(bool enabled, bool play)
    {
        if (enabled && _player is null) throw new InvalidOperationException("Codec/container này dùng preview frame FFmpeg.");
        _playerMode = enabled;
        PreviewPlayer.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        PreviewImage.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        Overlay.Visibility = Visibility.Visible;
        PlaybackButton.Content = enabled ? "Xem frame hiệu ứng" : "Phát video";
        if (_player is not null)
        {
            if (enabled)
            {
                _player.PlaybackSession.Position = TimeSpan.FromSeconds(Math.Max(0, Timeline.Value));
                if (play) _player.Play();
                StatusText.Text = "Đang phát video gốc; quay về frame để xem chính xác hiệu ứng.";
            }
            else
            {
                _player.Pause();
                _syncingTimeline = true;
                Timeline.Value = Math.Clamp(_player.PlaybackSession.Position.TotalSeconds, Timeline.Minimum, Timeline.Maximum);
                _syncingTimeline = false;
                await UpdateFrameAsync();
                StatusText.Text = "Preview FFmpeg đang hiển thị hiệu ứng tại frame hiện tại.";
            }
        }
        RefreshEditorActions();
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
            RenderOverlays();
            RenderTimelineRegions();
        });
    }

    private Rect VideoRect()
    {
        if (_media is null || Overlay.ActualWidth <= 0 || Overlay.ActualHeight <= 0)
            return new Rect(0, 0, Overlay.ActualWidth, Overlay.ActualHeight);
        var source = _media.Width / (double)_media.Height;
        var host = Overlay.ActualWidth / Overlay.ActualHeight;
        return host > source
            ? new Rect((Overlay.ActualWidth - Overlay.ActualHeight * source) / 2, 0, Overlay.ActualHeight * source, Overlay.ActualHeight)
            : new Rect(0, (Overlay.ActualHeight - Overlay.ActualWidth / source) / 2, Overlay.ActualWidth, Overlay.ActualWidth / source);
    }

    private void QueueProjectSave()
    {
        if (_project is null) return;
        _saveCancellation?.Cancel();
        _saveCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _saveCancellation = cancellation;
        var snapshot = ProjectSnapshot();
        _ = SaveProjectLaterAsync(snapshot, cancellation.Token);
    }

    private async Task SaveProjectLaterAsync(EditorProject project, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(450, cancellationToken);
            await _application.SaveEditorProjectAsync(project, cancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            DispatcherQueue.TryEnqueue(() => StatusText.Text = "Không tự lưu được project: " + error.Message);
        }
    }

    private async Task SaveProjectNowAsync()
    {
        if (_project is null) return;
        _saveCancellation?.Cancel();
        try { await _application.SaveEditorProjectAsync(ProjectSnapshot(), CancellationToken.None); }
        catch (Exception error) { StatusText.Text = "Không lưu được project: " + error.Message; }
    }

    private EditorProject ProjectSnapshot() => (_project ?? throw new InvalidOperationException("Project Editor chưa mở.")) with
    {
        FileName = FileNameBox.Text,
        Regions = _document.Regions.ToArray(),
        UpdatedUtc = DateTimeOffset.UtcNow,
    };

    private void RefreshEditorActions()
    {
        var idle = _jobId is null;
        var hasMedia = _media is not null;
        OpenVideoButton.IsEnabled = idle;
        Overlay.IsHitTestVisible = idle && hasMedia && !_playerMode;
        AddRegionButton.IsEnabled = idle && _draftRegion is not null;
        RemoveRegionButton.IsEnabled = idle && _document.Selected is not null;
        UndoButton.IsEnabled = idle && _document.CanUndo;
        RedoButton.IsEnabled = idle && _document.CanRedo;
        SubtitlePresetButton.IsEnabled = WatermarkPresetButton.IsEnabled = idle && hasMedia;
        RenderButton.IsEnabled = idle && _path is not null && hasMedia && _document.Regions.Count > 0 && !string.IsNullOrWhiteSpace(FileNameBox.Text);
        CancelButton.IsEnabled = !idle;
        Timeline.IsEnabled = idle && hasMedia;
        RefreshFrameButton.IsEnabled = idle && hasMedia && !_playerMode;
        PlaybackButton.IsEnabled = idle && _player is not null;
        FullscreenButton.IsEnabled = idle && _player is not null;
        RegionXBox.IsEnabled = RegionYBox.IsEnabled = RegionWidthBox.IsEnabled = RegionHeightBox.IsEnabled = idle && hasMedia;
        EffectBox.IsEnabled = StrengthBox.IsEnabled = WholeToggle.IsEnabled = idle && hasMedia;
        StartBox.IsEnabled = EndBox.IsEnabled = idle && hasMedia && !WholeToggle.IsOn;
        FileNameBox.IsEnabled = idle;
    }

    private static string FormatClock(double seconds)
    {
        var value = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return value.TotalHours >= 1 ? value.ToString(@"hh\:mm\:ss") : value.ToString(@"mm\:ss");
    }
}
