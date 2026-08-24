using Microsoft.UI.Xaml;

namespace BiliSubStudio.App.Pages;

public sealed partial class EditorPage
{
    private async void ImageCornerPreset_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedImage(out var image) || EditorBusy) return;
        var left = ReferenceEquals(sender, ImageTopLeftButton);
        var right = ReferenceEquals(sender, ImageTopRightButton);
        if (!left && !right) return;

        var maxX = Math.Max(0, 1 - image.Width);
        var maxY = Math.Max(0, 1 - image.Height);
        var preferredX = right ? 1 - image.Width - .025 : .025;
        image = image with
        {
            X = Math.Clamp(preferredX, 0, maxX),
            Y = Math.Min(.025, maxY),
        };
        _imageOverlays[_selectedImageIndex] = image;
        await SaveImageSidecarAsync();
        LoadSelectedImageIntoInputs();
        RenderImageOverlays();
        NotifyEditorCompositeChanged();
        if (_imageStatusText is not null)
            _imageStatusText.Text = right ? "Đã đặt ảnh/logo vào góc phải." : "Đã đặt ảnh/logo vào góc trái.";
    }
}
