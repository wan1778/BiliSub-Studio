using System.Drawing;
using System.Drawing.Imaging;

namespace BiliSubStudio.Core.Ocr;

// Crop a JPEG by a relative region (probe style: crop exactly the ROI so the worker measures the text outline).
// Windows-only (GDI+), fail-open: on corruption return null and the caller uses the original image.
// Never throws (except for the standard Dispose pattern).
public static class OcrImageCrop
{
    public static byte[]? CropJpeg(byte[]? jpeg, double x, double y, double width, double height)
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (jpeg is null || jpeg.Length == 0 || jpeg.Length > 32 * 1024 * 1024) return null;
        if (!(x >= 0) || !(y >= 0) || !(width > 0) || !(height > 0)) return null;
        if (x >= 1 || y >= 1 || x + width <= 0 || y + height <= 0) return null;
        try
        {
            using var stream = new MemoryStream(jpeg, writable: false);
            using var bitmap = new Bitmap(stream);
            var bitmapWidth = bitmap.Width;
            var bitmapHeight = bitmap.Height;
            if (bitmapWidth <= 0 || bitmapHeight <= 0 || bitmapWidth > 4096 || bitmapHeight > 4096)
                return null;
            var left = Math.Clamp((int)(x * bitmapWidth), 0, bitmapWidth - 1);
            var top = Math.Clamp((int)(y * bitmapHeight), 0, bitmapHeight - 1);
            var right = Math.Clamp((int)Math.Ceiling((x + width) * bitmapWidth), left + 1, bitmapWidth);
            var bottom = Math.Clamp((int)Math.Ceiling((y + height) * bitmapHeight), top + 1, bitmapHeight);
            using var cropped = bitmap.Clone(new Rectangle(left, top, right - left, bottom - top), bitmap.PixelFormat);
            using var output = new MemoryStream();
            cropped.Save(output, ImageFormat.Jpeg);
            return output.Length > 0 ? output.ToArray() : null;
        }
        catch
        {
            return null;
        }
    }
}
