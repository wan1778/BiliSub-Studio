namespace BiliSubStudio.Core.Editor;

// Bố cục mặc định cho ảnh/logo vừa thêm vào editor. Ảnh chuẩn bị đúng tỉ lệ
// khung video (16:9, 9:16…) phải phủ vừa khung ngay từ lần thêm đầu; ảnh logo
// khác tỉ lệ vẫn giữ mặc định nhỏ ở góc phải như trước.
public static class EditorImageOverlayLayout
{
    private const double AspectMatchTolerance = .005;
    private const double LogoWidth = .18;
    private const double LogoMaxHeight = .34;
    private const double LogoMinSize = .03;
    private const double LogoMaxWidth = .45;
    private const double Margin = .025;

    public static (double X, double Y, double Width, double Height) DefaultPlacement(
        uint imageWidth, uint imageHeight, double videoWidth, double videoHeight)
    {
        RequireValid(imageWidth, imageHeight, videoWidth, videoHeight);
        var imageAspect = imageWidth / (double)imageHeight;
        var videoAspect = videoWidth / videoHeight;
        if (Math.Abs(imageAspect - videoAspect) <= videoAspect * AspectMatchTolerance)
            return FitInside(imageWidth, imageHeight, videoWidth, videoHeight);

        var width = LogoWidth;
        var height = width * videoWidth / (imageAspect * videoHeight);
        if (height > LogoMaxHeight)
        {
            height = LogoMaxHeight;
            width = height * imageAspect * videoHeight / videoWidth;
        }

        width = Math.Clamp(width, LogoMinSize, LogoMaxWidth);
        height = Math.Clamp(height, LogoMinSize, LogoMaxWidth);
        return (Math.Max(0, 1 - width - Margin), Margin, width, height);
    }

    // Ảnh lớn nhất có thể mà không méo và không tràn khung, canh giữa.
    public static (double X, double Y, double Width, double Height) FitInside(
        uint imageWidth, uint imageHeight, double videoWidth, double videoHeight)
    {
        RequireValid(imageWidth, imageHeight, videoWidth, videoHeight);
        var imageAspect = imageWidth / (double)imageHeight;
        var videoAspect = videoWidth / videoHeight;
        double width;
        double height;
        if (imageAspect >= videoAspect)
        {
            width = 1;
            height = videoAspect / imageAspect;
        }
        else
        {
            height = 1;
            width = imageAspect / videoAspect;
        }

        return ((1 - width) / 2, (1 - height) / 2, width, height);
    }

    private static void RequireValid(uint imageWidth, uint imageHeight, double videoWidth, double videoHeight)
    {
        if (imageWidth == 0 || imageHeight == 0)
            throw new ArgumentOutOfRangeException(nameof(imageWidth), "Ảnh/logo phải có kích thước thật.");
        if (!double.IsFinite(videoWidth) || !double.IsFinite(videoHeight) || videoWidth <= 0 || videoHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(videoWidth), "Khung video phải có kích thước hợp lệ.");
    }
}
