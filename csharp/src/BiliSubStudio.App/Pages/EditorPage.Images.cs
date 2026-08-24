using System.Text.Json;
using BiliSubStudio.App.Services;
using BiliSubStudio.Core.Editor;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace BiliSubStudio.App.Pages;

internal sealed record EditorImageOverlayState(
    string Path,
    double X,
    double Y,
    double Width,
    double Height,
    double Opacity,
    uint PixelWidth,
    uint PixelHeight);

public sealed partial class EditorPage
{
    private const int MaxEditorImages = 8;
    private readonly List<EditorImageOverlayState> _imageOverlays = [];
    private readonly Dictionary<string, BitmapImage> _imageBitmaps = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _imageMissingReconcileGate = new(1, 1);
    private bool _imageFeatureInitialized;
    private bool _syncingImageInputs;
    private bool _imageMissingReconcileInProgress;
    private string? _imageProjectId;
    private int _selectedImageIndex = -1;
    private Point? _imageDragStart;
    private EditorImageOverlayState? _imageDragOriginal;
    private DragKind _imageDragKind = DragKind.None;

    private ToggleButton? _imageModeButton;
    private StackPanel? _imageInspectorPanel;
    private Canvas? _imageOverlayCanvas;
    private Button? _addImageButton;
    private Button? _removeImageButton;
    private Button? _imageTopRightButton;
    private Button? _imageTopLeftButton;
    private ListView? _imageList;
    private TextBlock? _imageStatusText;
    private NumberBox? _imageXBox;
    private NumberBox? _imageYBox;
    private NumberBox? _imageWidthBox;
    private NumberBox? _imageHeightBox;
    private Slider? _imageOpacitySlider;



    // Static UI SHELL declares Image/Logo controls in XAML. Initialization
    // is state-only and must never insert or reparent visual elements.
    private void EnsureImageFeatureInitialized()
    {
        if (_imageFeatureInitialized) return;
        _imageFeatureInitialized = true;
        RefreshImageControls();
    }

    private async Task EnsureImageProjectLoadedAsync()
    {
        if (_project is null)
        {
            _imageProjectId = null;
            _imageOverlays.Clear();
            _imageBitmaps.Clear();
            _selectedImageIndex = -1;
            return;
        }
        if (string.Equals(_imageProjectId, _project.Id, StringComparison.Ordinal)) return;

        _imageProjectId = _project.Id;
        _imageOverlays.Clear();
        _imageBitmaps.Clear();
        _selectedImageIndex = -1;
        var missingImageCount = 0;
        var path = ImageSidecarPath(_project.Id);
        if (File.Exists(path))
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 32 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var loaded = await JsonSerializer.DeserializeAsync<List<EditorImageOverlayState>>(stream) ?? [];
            foreach (var image in loaded.Take(MaxEditorImages))
            {
                if (IsMissingImageFile(image))
                {
                    missingImageCount++;
                    continue;
                }
                if (TryNormalizeImageState(image, out var normalized)) _imageOverlays.Add(normalized);
            }
        }
        for (var index = 0; index < _imageOverlays.Count; index++)
            await EnsureBitmapLoadedAsync(_imageOverlays[index].Path);
        if (_imageOverlays.Count > 0) _selectedImageIndex = 0;
        if (missingImageCount > 0)
            await SaveImageSidecarStrictAsync();
        RenderImageList();
        LoadSelectedImageIntoInputs();
        if (missingImageCount > 0 && _imageStatusText is not null)
            _imageStatusText.Text = $"Đã gỡ {missingImageCount} ảnh/logo bị mất khỏi project đã lưu.";
    }

    private async void AddImage_Click(object sender, RoutedEventArgs e)
    {
        try { await AddImageAsync(); }
        catch (Exception error) { if (_imageStatusText is not null) _imageStatusText.Text = error.Message; }
    }

    private async Task AddImageAsync()
    {
        if (_media is null || _project is null || EditorBusy || _playback.IsPreviewMode || _imageOverlays.Count >= MaxEditorImages) return;
        var path = await _picker.PickImageAsync();
        if (path is null) return;
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg"))
            throw new InvalidDataException("Chỉ hỗ trợ PNG, JPG hoặc JPEG.");
        var file = await StorageFile.GetFileFromPathAsync(path);
        uint pixelWidth;
        uint pixelHeight;
        using (var stream = await file.OpenReadAsync())
        {
            var decoder = await BitmapDecoder.CreateAsync(stream);
            pixelWidth = decoder.PixelWidth;
            pixelHeight = decoder.PixelHeight;
        }
        if (pixelWidth == 0 || pixelHeight == 0) throw new InvalidDataException("Không đọc được kích thước ảnh/logo.");
        var width = .18;
        var aspect = pixelWidth / (double)pixelHeight;
        var height = width * _media.Width / (aspect * _media.Height);
        if (height > .34)
        {
            height = .34;
            width = height * aspect * _media.Height / _media.Width;
        }
        width = Math.Clamp(width, .03, .45);
        height = Math.Clamp(height, .03, .45);
        var state = new EditorImageOverlayState(
            Path.GetFullPath(path), Math.Max(0, 1 - width - .025), .025, width, height, 1, pixelWidth, pixelHeight);
        _imageOverlays.Add(state);
        _selectedImageIndex = _imageOverlays.Count - 1;
        await EnsureBitmapLoadedAsync(state.Path);
        await SaveImageSidecarAsync();
        RenderImageList();
        LoadSelectedImageIntoInputs();
        RenderImageOverlays();
        NotifyEditorCompositeChanged();
        if (_imageStatusText is not null) _imageStatusText.Text = $"Đã thêm {Path.GetFileName(state.Path)}. Kéo trực tiếp trên preview để đặt logo.";
        RefreshEditorActions();
    }

    private async void RemoveImage_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedImageIndex < 0 || _selectedImageIndex >= _imageOverlays.Count || EditorBusy) return;
        var removed = _imageOverlays[_selectedImageIndex];
        _imageOverlays.RemoveAt(_selectedImageIndex);
        _imageBitmaps.Remove(removed.Path);
        _selectedImageIndex = Math.Min(_selectedImageIndex, _imageOverlays.Count - 1);
        await SaveImageSidecarAsync();
        RenderImageList();
        LoadSelectedImageIntoInputs();
        RenderImageOverlays();
        if (_imageStatusText is not null) _imageStatusText.Text = _imageOverlays.Count == 0 ? "Đã xóa hết ảnh/logo." : "Đã xóa ảnh/logo đang chọn.";
        RefreshImageControls();
    }

    private void ImageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_imageList is null) return;
        _selectedImageIndex = _imageList.SelectedIndex;
        LoadSelectedImageIntoInputs();
        RenderImageOverlays();
        RefreshImageControls();
    }

    private async void ImageTopRight_Click(object sender, RoutedEventArgs e) => await MoveSelectedImageToCornerAsync(right: true);
    private async void ImageTopLeft_Click(object sender, RoutedEventArgs e) => await MoveSelectedImageToCornerAsync(right: false);

    private async Task MoveSelectedImageToCornerAsync(bool right)
    {
        if (!TryGetSelectedImage(out var image) || EditorBusy) return;
        image = image with
        {
            X = right ? Math.Max(0, 1 - image.Width - .025) : .025,
            Y = .025,
        };
        _imageOverlays[_selectedImageIndex] = image;
        await SaveImageSidecarAsync();
        LoadSelectedImageIntoInputs();
        RenderImageOverlays();
    }

    private async void ImageGeometry_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_syncingImageInputs || !TryGetSelectedImage(out var image) || _imageXBox is null || _imageYBox is null || _imageWidthBox is null || _imageHeightBox is null) return;
        var x = _imageXBox.Value / 100;
        var y = _imageYBox.Value / 100;
        var width = _imageWidthBox.Value / 100;
        var height = _imageHeightBox.Value / 100;
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) || !double.IsFinite(height)
            || width < .02 || height < .02 || x < 0 || y < 0 || x + width > 1.0001 || y + height > 1.0001)
        {
            if (_imageStatusText is not null) _imageStatusText.Text = "Tọa độ ảnh/logo phải nằm hoàn toàn trong video và kích thước tối thiểu 2%.";
            return;
        }
        _imageOverlays[_selectedImageIndex] = image with { X = x, Y = y, Width = width, Height = height };
        await SaveImageSidecarAsync();
        RenderImageOverlays();
        NotifyEditorCompositeChanged();
        if (_imageStatusText is not null) _imageStatusText.Text = "Đã cập nhật vị trí/kích thước ảnh/logo.";
    }

    private async void ImageOpacity_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_syncingImageInputs || !TryGetSelectedImage(out var image) || _imageOpacitySlider is null) return;
        _imageOverlays[_selectedImageIndex] = image with { Opacity = Math.Clamp(_imageOpacitySlider.Value / 100, .05, 1) };
        await SaveImageSidecarAsync();
        RenderImageOverlays();
        NotifyEditorCompositeChanged();
    }

    private void ImageOverlay_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_inspectorMode != InspectorMode.Image || _media is null || EditorBusy || _playback.IsPreviewMode || _imageOverlayCanvas is null) return;
        var point = e.GetCurrentPoint(_imageOverlayCanvas).Position;
        var hit = HitTestImage(point);
        if (hit.Index < 0) return;
        _selectedImageIndex = hit.Index;
        _imageDragKind = hit.Kind;
        _imageDragOriginal = _imageOverlays[hit.Index];
        if (!TryNormalize(point, out var normalized)) return;
        _imageDragStart = normalized;
        _imageOverlayCanvas.CapturePointer(e.Pointer);
        RenderImageList();
        LoadSelectedImageIntoInputs();
        RenderImageOverlays();
        e.Handled = true;
    }

    private void ImageOverlay_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_imageDragStart is null || _imageDragOriginal is null || _imageOverlayCanvas is null || !e.GetCurrentPoint(_imageOverlayCanvas).Properties.IsLeftButtonPressed) return;
        if (!TryNormalize(e.GetCurrentPoint(_imageOverlayCanvas).Position, out var current)) return;
        var original = _imageDragOriginal;
        var placement = ResizeImagePlacement(original, _imageDragStart.Value, current, _imageDragKind);
        _imageOverlays[_selectedImageIndex] = original with
        {
            X = placement.X,
            Y = placement.Y,
            Width = placement.Width,
            Height = placement.Height,
        };
        LoadSelectedImageIntoInputs();
        RenderImageOverlays();
    }

    private void ImageOverlay_PointerReleased(object sender, PointerRoutedEventArgs e) => FinishImageDrag(e, commit: true);
    private void ImageOverlay_PointerCanceled(object sender, PointerRoutedEventArgs e) => FinishImageDrag(e, commit: false);

    private async void FinishImageDrag(PointerRoutedEventArgs e, bool commit)
    {
        if (_imageDragStart is null || _imageOverlayCanvas is null) return;
        if (!commit && _imageDragOriginal is not null && _selectedImageIndex >= 0 && _selectedImageIndex < _imageOverlays.Count)
            _imageOverlays[_selectedImageIndex] = _imageDragOriginal;
        _imageOverlayCanvas.ReleasePointerCapture(e.Pointer);
        _imageDragStart = null;
        _imageDragOriginal = null;
        _imageDragKind = DragKind.None;
        if (commit) await SaveImageSidecarAsync();
        LoadSelectedImageIntoInputs();
        RenderImageOverlays();
        if (_imageStatusText is not null) _imageStatusText.Text = commit ? "Đã lưu vị trí/kích thước ảnh/logo." : "Đã hủy thay đổi ảnh/logo.";
    }

    private (int Index, DragKind Kind) HitTestImage(Point point)
    {
        var video = VideoRect();
        if (video.Width <= 0 || video.Height <= 0) return (-1, DragKind.None);
        if (_selectedImageIndex >= 0 && _selectedImageIndex < _imageOverlays.Count)
        {
            var selected = _imageOverlays[_selectedImageIndex];
            var handle = HitImageHandles(point, selected, video);
            if (handle != DragKind.None) return (_selectedImageIndex, handle);
        }
        for (var index = _imageOverlays.Count - 1; index >= 0; index--)
        {
            var image = _imageOverlays[index];
            var left = video.X + image.X * video.Width;
            var top = video.Y + image.Y * video.Height;
            var right = left + image.Width * video.Width;
            var bottom = top + image.Height * video.Height;
            if (point.X >= left && point.X <= right && point.Y >= top && point.Y <= bottom)
                return (index, DragKind.Move);
        }
        return (-1, DragKind.None);
    }

    private static DragKind HitImageHandles(Point point, EditorImageOverlayState image, Rect video)
    {
        var left = video.X + image.X * video.Width;
        var top = video.Y + image.Y * video.Height;
        var right = left + image.Width * video.Width;
        var bottom = top + image.Height * video.Height;
        const double tolerance = 10;
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

    private static EditorSubtitlePlacement ResizeImagePlacement(EditorImageOverlayState original, Point start, Point current, DragKind kind)
    {
        var dx = current.X - start.X;
        var dy = current.Y - start.Y;
        if (kind == DragKind.Move)
        {
            return new EditorSubtitlePlacement(
                Math.Clamp(original.X + dx, 0, 1 - original.Width),
                Math.Clamp(original.Y + dy, 0, 1 - original.Height),
                original.Width,
                original.Height);
        }

        const double minimum = .02;
        var x1 = original.X;
        var y1 = original.Y;
        var x2 = original.X + original.Width;
        var y2 = original.Y + original.Height;
        if (kind is DragKind.West or DragKind.NorthWest or DragKind.SouthWest) x1 = Math.Clamp(x1 + dx, 0, x2 - minimum);
        if (kind is DragKind.East or DragKind.NorthEast or DragKind.SouthEast) x2 = Math.Clamp(x2 + dx, x1 + minimum, 1);
        if (kind is DragKind.North or DragKind.NorthEast or DragKind.NorthWest) y1 = Math.Clamp(y1 + dy, 0, y2 - minimum);
        if (kind is DragKind.South or DragKind.SouthEast or DragKind.SouthWest) y2 = Math.Clamp(y2 + dy, y1 + minimum, 1);
        return new EditorSubtitlePlacement(x1, y1, x2 - x1, y2 - y1);
    }

    private void RenderImageOverlays()
    {
        if (_imageOverlayCanvas is null) return;
        _imageOverlayCanvas.Children.Clear();
        if (_media is null || _imageOverlays.Count == 0) return;
        if (_imageOverlays.Any(IsMissingImageFile))
        {
            if (!_imageMissingReconcileInProgress) _ = ReconcileMissingImageFilesFromRenderAsync();
            return;
        }
        var video = VideoRect();
        if (video.Width <= 0 || video.Height <= 0) return;

        for (var index = 0; index < _imageOverlays.Count; index++)
        {
            var state = _imageOverlays[index];
            if (!_imageBitmaps.TryGetValue(state.Path, out var bitmap))
            {
                _ = LoadBitmapAndRefreshAsync(state.Path);
                continue;
            }
            var image = new Image
            {
                Source = bitmap,
                Stretch = Stretch.Fill,
                Opacity = state.Opacity,
                Width = Math.Max(1, state.Width * video.Width),
                Height = Math.Max(1, state.Height * video.Height),
            };
            Canvas.SetLeft(image, video.X + state.X * video.Width);
            Canvas.SetTop(image, video.Y + state.Y * video.Height);
            _imageOverlayCanvas.Children.Add(image);

            if (_inspectorMode == InspectorMode.Image && index == _selectedImageIndex && !EditorBusy && !_playback.IsPreviewMode)
                RenderImageSelection(state, video);
        }
    }

    private void RenderImageSelection(EditorImageOverlayState state, Rect video)
    {
        if (_imageOverlayCanvas is null) return;
        var left = video.X + state.X * video.Width;
        var top = video.Y + state.Y * video.Height;
        var width = state.Width * video.Width;
        var height = state.Height * video.Height;
        var stroke = ColorHelper.FromArgb(255, 255, 194, 72);
        var rectangle = new Rectangle
        {
            Width = width,
            Height = height,
            Fill = new SolidColorBrush(ColorHelper.FromArgb(12, 255, 194, 72)),
            Stroke = new SolidColorBrush(stroke),
            StrokeThickness = 2,
            StrokeDashArray = new DoubleCollection { 5, 3 },
        };
        Canvas.SetLeft(rectangle, left);
        Canvas.SetTop(rectangle, top);
        _imageOverlayCanvas.Children.Add(rectangle);
        var right = left + width;
        var bottom = top + height;
        var middleX = (left + right) / 2;
        var middleY = (top + bottom) / 2;
        foreach (var point in new[]
        {
            new Point(left, top), new Point(middleX, top), new Point(right, top), new Point(right, middleY),
            new Point(right, bottom), new Point(middleX, bottom), new Point(left, bottom), new Point(left, middleY),
        })
        {
            var handle = new Rectangle
            {
                Width = 9,
                Height = 9,
                Fill = new SolidColorBrush(Colors.White),
                Stroke = new SolidColorBrush(stroke),
                StrokeThickness = 1.5,
                RadiusX = 2,
                RadiusY = 2,
            };
            Canvas.SetLeft(handle, point.X - 4.5);
            Canvas.SetTop(handle, point.Y - 4.5);
            _imageOverlayCanvas.Children.Add(handle);
        }
    }

    private void ImageOverlay_SizeChanged(object sender, SizeChangedEventArgs e) => RenderImageOverlays();

    private async Task EnsureBitmapLoadedAsync(string path)
    {
        if (_imageBitmaps.ContainsKey(path)) return;
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenReadAsync();
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        _imageBitmaps[path] = bitmap;
    }

    private async Task LoadBitmapAndRefreshAsync(string path)
    {
        try
        {
            await EnsureBitmapLoadedAsync(path);
            DispatcherQueue.TryEnqueue(RenderImageOverlays);
        }
        catch { }
    }

    private void RenderImageList()
    {
        if (_imageList is null) return;
        _imageList.Items.Clear();
        for (var index = 0; index < _imageOverlays.Count; index++)
        {
            var image = _imageOverlays[index];
            _imageList.Items.Add($"{index + 1}. {Path.GetFileName(image.Path)} · {image.Opacity:P0}");
        }
        _imageList.SelectedIndex = _selectedImageIndex;
    }

    private void LoadSelectedImageIntoInputs()
    {
        if (_imageXBox is null || _imageYBox is null || _imageWidthBox is null || _imageHeightBox is null || _imageOpacitySlider is null) return;
        _syncingImageInputs = true;
        try
        {
            if (!TryGetSelectedImage(out var image))
            {
                _imageXBox.Value = _imageYBox.Value = _imageWidthBox.Value = _imageHeightBox.Value = 0;
                _imageOpacitySlider.Value = 100;
                return;
            }
            _imageXBox.Value = image.X * 100;
            _imageYBox.Value = image.Y * 100;
            _imageWidthBox.Value = image.Width * 100;
            _imageHeightBox.Value = image.Height * 100;
            _imageOpacitySlider.Value = image.Opacity * 100;
        }
        finally { _syncingImageInputs = false; }
    }

    private bool TryGetSelectedImage(out EditorImageOverlayState image)
    {
        if (_selectedImageIndex >= 0 && _selectedImageIndex < _imageOverlays.Count)
        {
            image = _imageOverlays[_selectedImageIndex];
            return true;
        }
        image = null!;
        return false;
    }

    private static bool IsMissingImageFile(EditorImageOverlayState image)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(image.Path)) return false;
            var path = Path.GetFullPath(image.Path.Trim());
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension is not (".png" or ".jpg" or ".jpeg")) return false;
            return !File.Exists(path);
        }
        catch { return false; }
    }

    private async Task<int> ReconcileMissingImageFilesAsync()
    {
        if (_project is null || !string.Equals(_imageProjectId, _project.Id, StringComparison.Ordinal)) return 0;
        await _imageMissingReconcileGate.WaitAsync();
        try
        {
            var missing = _imageOverlays.Where(IsMissingImageFile).ToArray();
            if (missing.Length == 0) return 0;

            var paths = missing.Select(image => image.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _imageOverlays.RemoveAll(image => paths.Contains(image.Path));
            foreach (var missingPath in paths) _imageBitmaps.Remove(missingPath);
            _selectedImageIndex = Math.Min(_selectedImageIndex, _imageOverlays.Count - 1);
            RenderImageList();
            LoadSelectedImageIntoInputs();
            RenderImageOverlays();
            RefreshImageControls();
            await SaveImageSidecarStrictAsync();
            if (_imageStatusText is not null)
                _imageStatusText.Text = $"Đã gỡ {missing.Length} ảnh/logo bị mất khỏi project.";
            return missing.Length;
        }
        finally { _imageMissingReconcileGate.Release(); }
    }

    private async Task ReconcileMissingImageFilesFromRenderAsync()
    {
        if (_imageMissingReconcileInProgress) return;
        _imageMissingReconcileInProgress = true;
        try { await ReconcileMissingImageFilesAsync(); }
        catch (Exception error)
        {
            if (_imageStatusText is not null)
                _imageStatusText.Text = "Không thể cập nhật project sau khi ảnh/logo bị mất: " + error.Message;
        }
        finally { _imageMissingReconcileInProgress = false; }
    }

    private bool TryNormalizeImageState(EditorImageOverlayState image, out EditorImageOverlayState normalized)
    {
        normalized = image;
        try
        {
            var path = Path.GetFullPath(image.Path.Trim());
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension is not (".png" or ".jpg" or ".jpeg") || !File.Exists(path)) return false;
            if (!double.IsFinite(image.X) || !double.IsFinite(image.Y) || !double.IsFinite(image.Width) || !double.IsFinite(image.Height)
                || image.X < 0 || image.Y < 0 || image.Width < .02 || image.Height < .02
                || image.X + image.Width > 1.0001 || image.Y + image.Height > 1.0001) return false;
            if (!double.IsFinite(image.Opacity)) return false;
            normalized = image with { Path = path, Opacity = Math.Clamp(image.Opacity, .05, 1) };
            return true;
        }
        catch { return false; }
    }

    private async Task SaveImageSidecarAsync()
    {
        if (_project is null || !string.Equals(_imageProjectId, _project.Id, StringComparison.Ordinal)) return;
        // Never rewrite a source-specific sidecar after the underlying video was replaced.
        if (!CurrentSourceFingerprintMatches()) return;
        var path = ImageSidecarPath(_project.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (_imageOverlays.Count == 0)
        {
            try { File.Delete(path); } catch { }
            return;
        }
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 32 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await JsonSerializer.SerializeAsync(stream, _imageOverlays, cancellationToken: CancellationToken.None);
            await stream.FlushAsync();
            stream.Flush(flushToDisk: true);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); } catch { }
        }
    }

    private async Task SaveImageSidecarStrictAsync()
    {
        if (_project is null || !string.Equals(_imageProjectId, _project.Id, StringComparison.Ordinal)) return;
        if (!CurrentSourceFingerprintMatches()) return;
        if (_imageOverlays.Count > 0)
        {
            await SaveImageSidecarAsync();
            return;
        }
        var path = ImageSidecarPath(_project.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Delete(path);
    }

    private string ImageSidecarPath(string projectId) => Path.Combine(_application.Paths.Data, "Projects", projectId + ".images.json");

    private void ArchiveImageSidecarForSourceChange(string projectId)
    {
        var path = ImageSidecarPath(projectId);
        if (File.Exists(path))
        {
            var archive = path + ".source-changed-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "-" + Guid.NewGuid().ToString("N");
            Exception? last = null;
            try
            {
                File.Move(path, archive, overwrite: false);
            }
            catch (Exception error)
            {
                last = error;
                try
                {
                    File.Copy(path, archive, overwrite: false);
                    File.Delete(path);
                }
                catch (Exception copyError)
                {
                    last = copyError;
                }
            }
            if (File.Exists(path))
                throw new IOException("Không thể lưu trữ ảnh/logo của video nguồn cũ.", last);
        }

        _imageProjectId = null;
        _imageOverlays.Clear();
        _imageBitmaps.Clear();
        _selectedImageIndex = -1;
        RenderImageList();
        LoadSelectedImageIntoInputs();
        RenderImageOverlays();
    }

    private async Task RenderProjectAsync()
    {
        try { await EnsureImageProjectLoadedAsync(); }
        catch (Exception error)
        {
            StatusText.Text = error.Message;
            return;
        }
        if (_path is null || _media is null || _project is null)
        {
            StatusText.Text = "Chưa chọn video để xuất.";
            return;
        }
        if (EditorBusy) return;
        try { EnsureCurrentSourceFingerprint(); }
        catch (Exception error)
        {
            StatusText.Text = error.Message;
            return;
        }
        try { await ReconcileMissingImageFilesAsync(); }
        catch (Exception error)
        {
            StatusText.Text = "Không thể cập nhật project sau khi ảnh/logo bị mất: " + error.Message;
            return;
        }

        var subtitle = CompletedSubtitleBurn();
        var hasBaseEdit = _document.Regions.Count > 0 || subtitle is not null || _audioSettings.SourceMode != "keep" || _voiceTrack is not null;
        var hasImages = _imageOverlays.Count > 0;
        if (!hasBaseEdit && !hasImages)
        {
            StatusText.Text = "Cần ít nhất một thay đổi trước khi xuất video.";
            return;
        }

        string? baseOutput = null;
        AppJob? imageJob = null;
        try
        {
            await SaveProjectNowAsync();
            await SaveImageSidecarAsync();

            if (!hasImages)
            {
                _jobId = _application.StartEditor(CurrentEditRequest(subtitle));
                RefreshEditorActions();
                while (_jobId is not null)
                {
                    var snapshot = _application.Jobs.GetSnapshot(_jobId);
                    Progress.Value = snapshot.Progress;
                    StatusText.Text = snapshot.Message;
                    if (!snapshot.Done)
                    {
                        await Task.Delay(350);
                        continue;
                    }
                    _jobId = null;
                    if (snapshot.Result is VideoEditResult result)
                    {
                        Progress.Value = 100;
                        StatusText.Text = "Đã xuất: " + result.OutputPath;
                        break;
                    }
                    if (snapshot.Message.Contains("hủy", StringComparison.OrdinalIgnoreCase))
                        throw new OperationCanceledException("Đã hủy xuất video.");
                    throw new InvalidOperationException(snapshot.Error ?? snapshot.Message);
                }
                return;
            }

            var composerInput = _path;
            if (hasBaseEdit)
            {
                var temporaryDirectory = Path.Combine(_application.Paths.Temp, "Editor", "ImageBase");
                Directory.CreateDirectory(temporaryDirectory);
                var temporaryName = "editor-image-base-" + Guid.NewGuid().ToString("N") + ".mp4";
                var request = CurrentEditRequest(subtitle) with { OutputDirectory = temporaryDirectory, FileName = temporaryName };
                _jobId = _application.StartEditor(request);
                RefreshEditorActions();
                while (_jobId is not null)
                {
                    var snapshot = _application.Jobs.GetSnapshot(_jobId);
                    Progress.Value = Math.Clamp(snapshot.Progress * .68, 0, 68);
                    StatusText.Text = "Đang dựng bản chỉnh trước khi ghép ảnh/logo · " + snapshot.Message;
                    if (!snapshot.Done)
                    {
                        await Task.Delay(250);
                        continue;
                    }
                    var completedJobId = _jobId ?? throw new InvalidOperationException("Mất job render trung gian.");
                    _jobId = null;
                    if (snapshot.Result is VideoEditResult result)
                    {
                        baseOutput = result.OutputPath;
                        composerInput = result.OutputPath;
                        if (_application.Jobs.TryGet(completedJobId, out var completedJob)
                            && completedJob is not null
                            && completedJob.CancellationToken.IsCancellationRequested)
                            throw new OperationCanceledException("Đã hủy xuất video.");
                        break;
                    }
                    if (snapshot.Message.Contains("hủy", StringComparison.OrdinalIgnoreCase))
                        throw new OperationCanceledException("Đã hủy xuất video.");
                    throw new InvalidOperationException(snapshot.Error ?? "Không tạo được bản chỉnh trung gian để ghép ảnh/logo.");
                }
            }

            imageJob = _application.Jobs.Create("editor-image", cleanupAwareCancel: true);
            _jobId = imageJob.Id;
            RefreshEditorActions();
            var composer = new EditorImageOverlayComposer(_application.Tools, _application.Processes);
            var specs = _imageOverlays.Select(image => new EditorImageOverlaySpec(
                image.Path, image.X, image.Y, image.Width, image.Height, image.Opacity)).ToArray();
            var output = await composer.RenderAsync(
                imageJob, composerInput, _application.Config.OutputDirectory, FileNameBox.Text,
                _media.Width, _media.Height, _media.Duration, specs, copyAudio: hasBaseEdit);
            imageJob.Finish(null, "Đã xuất: " + output, new VideoEditResult(output));
            Progress.Value = 100;
            StatusText.Text = $"Đã xuất video với {_imageOverlays.Count} ảnh/logo: {output}";
        }
        catch (OperationCanceledException)
        {
            imageJob?.CancelComplete("Đã hủy xuất và dọn file ảnh/logo dở.");
            StatusText.Text = "Đã hủy xuất video và dọn file dở.";
        }
        catch (Exception error)
        {
            imageJob?.Finish(error, error.Message);
            StatusText.Text = error.Message;
        }
        finally
        {
            if (baseOutput is not null)
            {
                try { File.Delete(baseOutput); } catch { }
            }
            _jobId = null;
            RefreshEditorActions();
            RenderImageOverlays();
        }
    }

    private void RefreshImageControls()
    {
        if (!_imageFeatureInitialized) return;
        var editable = _media is not null && !EditorBusy && !_playback.IsPreviewMode;
        var selected = _selectedImageIndex >= 0 && _selectedImageIndex < _imageOverlays.Count;
        if (_imageModeButton is not null) _imageModeButton.IsEnabled = !EditorBusy;
        if (_addImageButton is not null) _addImageButton.IsEnabled = editable && _imageOverlays.Count < MaxEditorImages;
        if (_removeImageButton is not null) _removeImageButton.IsEnabled = editable && selected;
        if (_imageTopLeftButton is not null) _imageTopLeftButton.IsEnabled = editable && selected;
        if (_imageTopRightButton is not null) _imageTopRightButton.IsEnabled = editable && selected;
        if (_imageList is not null) _imageList.IsEnabled = editable;
        if (_imageXBox is not null) _imageXBox.IsEnabled = editable && selected;
        if (_imageYBox is not null) _imageYBox.IsEnabled = editable && selected;
        if (_imageWidthBox is not null) _imageWidthBox.IsEnabled = editable && selected;
        if (_imageHeightBox is not null) _imageHeightBox.IsEnabled = editable && selected;
        if (_imageOpacitySlider is not null) _imageOpacitySlider.IsEnabled = editable && selected;
        if (_imageOverlayCanvas is not null) _imageOverlayCanvas.IsHitTestVisible = editable && _inspectorMode == InspectorMode.Image;
    }
}
