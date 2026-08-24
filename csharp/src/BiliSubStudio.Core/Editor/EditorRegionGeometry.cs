namespace BiliSubStudio.Core.Editor;

public enum EditorRegionResizeHandle
{
    North,
    South,
    East,
    West,
    NorthEast,
    NorthWest,
    SouthEast,
    SouthWest,
}

public static class EditorRegionGeometry
{
    public static EditRegion? FromPercentInputs(
        EditRegion settings,
        double xPercent,
        double yPercent,
        double widthPercent,
        double heightPercent,
        int sourceWidth,
        int sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0
            || !double.IsFinite(xPercent) || !double.IsFinite(yPercent)
            || !double.IsFinite(widthPercent) || !double.IsFinite(heightPercent))
            return null;

        var region = settings with
        {
            X = xPercent / 100,
            Y = yPercent / 100,
            Width = widthPercent / 100,
            Height = heightPercent / 100,
        };
        return IsPixelValid(region, sourceWidth, sourceHeight) ? region : null;
    }

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

    public static EditRegion ResizeBy(
        EditRegion original,
        double deltaX,
        double deltaY,
        EditorRegionResizeHandle handle,
        int sourceWidth,
        int sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0
            || !double.IsFinite(deltaX) || !double.IsFinite(deltaY)
            || !IsPixelValid(original, sourceWidth, sourceHeight)
            || handle is not (EditorRegionResizeHandle.North
                or EditorRegionResizeHandle.South
                or EditorRegionResizeHandle.East
                or EditorRegionResizeHandle.West
                or EditorRegionResizeHandle.NorthEast
                or EditorRegionResizeHandle.NorthWest
                or EditorRegionResizeHandle.SouthEast
                or EditorRegionResizeHandle.SouthWest))
            return original;

        var x1 = original.X;
        var y1 = original.Y;
        var x2 = original.X + original.Width;
        var y2 = original.Y + original.Height;
        if (handle is EditorRegionResizeHandle.West or EditorRegionResizeHandle.NorthWest or EditorRegionResizeHandle.SouthWest)
        {
            var rightPixel = (int)(x2 * sourceWidth);
            var maximumLeft = Math.Max(0, rightPixel - 2) / (double)sourceWidth;
            x1 = Math.Clamp(x1 + deltaX, 0, maximumLeft);
        }
        if (handle is EditorRegionResizeHandle.East or EditorRegionResizeHandle.NorthEast or EditorRegionResizeHandle.SouthEast)
        {
            var leftPixel = (int)(x1 * sourceWidth);
            x2 = Math.Clamp(x2 + deltaX, MinimumEdge(leftPixel + 2, sourceWidth), 1);
        }
        if (handle is EditorRegionResizeHandle.North or EditorRegionResizeHandle.NorthEast or EditorRegionResizeHandle.NorthWest)
        {
            var bottomPixel = (int)(y2 * sourceHeight);
            var maximumTop = Math.Max(0, bottomPixel - 2) / (double)sourceHeight;
            y1 = Math.Clamp(y1 + deltaY, 0, maximumTop);
        }
        if (handle is EditorRegionResizeHandle.South or EditorRegionResizeHandle.SouthEast or EditorRegionResizeHandle.SouthWest)
        {
            var topPixel = (int)(y1 * sourceHeight);
            y2 = Math.Clamp(y2 + deltaY, MinimumEdge(topPixel + 2, sourceHeight), 1);
        }

        var resized = original with { X = x1, Y = y1, Width = x2 - x1, Height = y2 - y1 };
        return IsPixelValid(resized, sourceWidth, sourceHeight) ? resized : original;
    }

    private static double MinimumEdge(int requiredPixel, int sourceSize)
    {
        if (requiredPixel >= sourceSize) return 1;
        return Math.BitIncrement(requiredPixel / (double)sourceSize);
    }

    private static bool IsPixelValid(EditRegion region, int sourceWidth, int sourceHeight)
    {
        if (!double.IsFinite(region.X) || !double.IsFinite(region.Y)
            || !double.IsFinite(region.Width) || !double.IsFinite(region.Height)
            || region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0
            || region.X >= 1 || region.Y >= 1
            || region.X + region.Width > 1.000_000_1 || region.Y + region.Height > 1.000_000_1)
            return false;
        var x = (int)(region.X * sourceWidth);
        var y = (int)(region.Y * sourceHeight);
        var right = (int)(Math.Min(1, region.X + region.Width) * sourceWidth);
        var bottom = (int)(Math.Min(1, region.Y + region.Height) * sourceHeight);
        return right - x >= 2 && bottom - y >= 2;
    }
}
