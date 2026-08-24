namespace BiliSubStudio.Core.Editor;

public static class EditorRegionGeometry
{
    public static EditRegion? FromNormalizedDrag(
        EditRegion settings,
        double startX,
        double startY,
        double currentX,
        double currentY,
        int sourceWidth,
        int sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0
            || !double.IsFinite(startX) || !double.IsFinite(startY)
            || !double.IsFinite(currentX) || !double.IsFinite(currentY))
            return null;

        startX = Math.Clamp(startX, 0, 1);
        startY = Math.Clamp(startY, 0, 1);
        currentX = Math.Clamp(currentX, 0, 1);
        currentY = Math.Clamp(currentY, 0, 1);
        var x = Math.Min(startX, currentX);
        var y = Math.Min(startY, currentY);
        var right = Math.Max(startX, currentX);
        var bottom = Math.Max(startY, currentY);
        var pixelX = (int)(x * sourceWidth);
        var pixelY = (int)(y * sourceHeight);
        var pixelRight = (int)(right * sourceWidth);
        var pixelBottom = (int)(bottom * sourceHeight);
        if (pixelRight - pixelX < 2 || pixelBottom - pixelY < 2) return null;

        return settings with
        {
            X = x,
            Y = y,
            Width = right - x,
            Height = bottom - y,
        };
    }

    public static int FindTopmostContaining(
        IReadOnlyList<EditRegion> regions,
        double normalizedX,
        double normalizedY)
    {
        if (!double.IsFinite(normalizedX) || !double.IsFinite(normalizedY)
            || normalizedX < 0 || normalizedX > 1
            || normalizedY < 0 || normalizedY > 1)
            return -1;

        for (var index = regions.Count - 1; index >= 0; index--)
        {
            var region = regions[index];
            var right = region.X + region.Width;
            var bottom = region.Y + region.Height;
            if (!double.IsFinite(region.X) || !double.IsFinite(region.Y)
                || !double.IsFinite(right) || !double.IsFinite(bottom)
                || region.Width <= 0 || region.Height <= 0)
                continue;
            if (normalizedX >= region.X && normalizedX <= right
                && normalizedY >= region.Y && normalizedY <= bottom)
                return index;
        }

        return -1;
    }

    public static EditRegion MoveBy(EditRegion original, double deltaX, double deltaY)
    {
        if (!double.IsFinite(deltaX) || !double.IsFinite(deltaY)
            || !double.IsFinite(original.X) || !double.IsFinite(original.Y)
            || !double.IsFinite(original.Width) || !double.IsFinite(original.Height)
            || original.Width <= 0 || original.Width > 1
            || original.Height <= 0 || original.Height > 1)
            return original;

        return original with
        {
            X = Math.Clamp(original.X + deltaX, 0, 1 - original.Width),
            Y = Math.Clamp(original.Y + deltaY, 0, 1 - original.Height),
        };
    }
}
