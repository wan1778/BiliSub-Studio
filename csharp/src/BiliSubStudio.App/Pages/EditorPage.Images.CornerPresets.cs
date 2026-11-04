using BiliSubStudio.Core.Editor;
using Microsoft.UI.Xaml;

namespace BiliSubStudio.App.Pages;

public sealed partial class EditorPage
{
    private async void ImageCornerPreset_Click(object sender, RoutedEventArgs e)
    {
        try
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
            await SaveImageSidecarSafeAsync();
            LoadSelectedImageIntoInputs();
            RenderImageOverlays();
            NotifyEditorCompositeChanged();
            if (_imageStatusText is not null)
                _imageStatusText.Text = right ? "Đã đặt ảnh/logo vào góc phải." : "Đã đặt ảnh/logo vào góc trái.";
        }
        catch (Exception error) { ReportEditorCommandFailure("đặt góc ảnh/logo", error); }
    }

    private async void ImageFitFrame_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!TryGetSelectedImage(out var image) || EditorBusy || _media is null) return;
            var placement = EditorImageOverlayLayout.FitInside(image.PixelWidth, image.PixelHeight, _media.Width, _media.Height);
            image = image with
            {
                X = placement.X,
                Y = placement.Y,
                Width = placement.Width,
                Height = placement.Height,
            };
            _imageOverlays[_selectedImageIndex] = image;
            var saveError = await SaveImageSidecarSafeAsync();
            LoadSelectedImageIntoInputs();
            RenderImageOverlays();
            NotifyEditorCompositeChanged();
            if (_imageStatusText is not null)
                _imageStatusText.Text = saveError is null
                    ? "Đã đưa ảnh/logo vừa khung video."
                    : "Đã đưa ảnh/logo vừa khung nhưng chưa lưu được project: " + saveError.Message;
        }
        catch (Exception error) { ReportEditorCommandFailure("đưa ảnh vừa khung", error); }
    }
}
