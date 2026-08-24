using Microsoft.UI.Xaml;

namespace BiliSubStudio.App.Pages;

public sealed partial class EditorPage
{
    private async void RemoveImageSafe_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedImageIndex < 0 || _selectedImageIndex >= _imageOverlays.Count || EditorBusy || _playback.IsPreviewMode) return;

        var removedIndex = _selectedImageIndex;
        var removed = _imageOverlays[removedIndex];
        _imageOverlays.RemoveAt(removedIndex);

        var pathStillUsed = _imageOverlays.Any(image =>
            string.Equals(image.Path, removed.Path, StringComparison.OrdinalIgnoreCase));
        if (!pathStillUsed) _imageBitmaps.Remove(removed.Path);

        _selectedImageIndex = Math.Min(removedIndex, _imageOverlays.Count - 1);
        await SaveImageSidecarAsync();
        RenderImageList();
        LoadSelectedImageIntoInputs();
        RenderImageOverlays();
        NotifyEditorCompositeChanged();

        if (_imageStatusText is not null)
            _imageStatusText.Text = _imageOverlays.Count == 0
                ? "Đã xóa hết ảnh/logo."
                : "Đã xóa ảnh/logo đang chọn.";
        RefreshImageControls();
    }
}
