using BiliSubStudio.Core.Editor;

namespace BiliSubStudio.Core.ContractTests;

// Field bug 23/10: ảnh chuẩn bị đúng 16:9 bị thu về 18% khung khi thêm vào
// video 16:9. Ảnh cùng tỉ lệ khung phải phủ vừa khung ngay; ảnh logo khác tỉ lệ
// vẫn giữ mặc định nhỏ ở góc phải. Nút "Vừa khung" luôn contain, không méo ảnh.
internal static class EditorImageOverlayLayoutContract
{
    public static Task RunAsync()
    {
        var full = EditorImageOverlayLayout.DefaultPlacement(1920, 1080, 1920, 1080);
        Require(Close(full.X, 0) && Close(full.Y, 0) && Close(full.Width, 1) && Close(full.Height, 1),
            "16:9 image on a 16:9 video must fill the frame");

        var near = EditorImageOverlayLayout.DefaultPlacement(1918, 1080, 1920, 1080);
        Require(Close(near.Height, 1) && Close(near.Width, 1918 / 1920d) && Close(near.X, (1 - 1918 / 1920d) / 2),
            "near-16:9 image must still fill the frame without distortion");

        var portrait = EditorImageOverlayLayout.DefaultPlacement(1080, 1920, 1080, 1920);
        Require(Close(portrait.Width, 1) && Close(portrait.Height, 1), "9:16 image on a 9:16 video must fill the frame");

        var square = EditorImageOverlayLayout.DefaultPlacement(1024, 1024, 1920, 1080);
        Require(Close(square.Width, .18) && Close(square.X, 1 - .18 - .025) && Close(square.Y, .025) && Close(square.Height, .32),
            "square logo must keep the small top-right default");

        var squareFit = EditorImageOverlayLayout.FitInside(1024, 1024, 1920, 1080);
        Require(Close(squareFit.Height, 1) && Close(squareFit.Width, .5625) && Close(squareFit.X, .21875) && Close(squareFit.Y, 0),
            "fit must contain a square image without distortion and center it");

        var wide = EditorImageOverlayLayout.FitInside(1920, 816, 1920, 1080);
        Require(Close(wide.Width, 1) && Close(wide.Height, 816 / 1080d) && Close(wide.X, 0) && Close(wide.Y, (1 - 816 / 1080d) / 2),
            "cinematic image must keep its aspect inside the frame");

        var thrown = false;
        try { EditorImageOverlayLayout.DefaultPlacement(0, 1080, 1920, 1080); }
        catch (ArgumentOutOfRangeException) { thrown = true; }
        Require(thrown, "zero-size image must be rejected");

        return Task.CompletedTask;
    }

    private static bool Close(double actual, double expected) => Math.Abs(actual - expected) <= 1e-9;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
