namespace BiliSubStudio.Core.Timeline;

// G3 media asset (MASTER_PLAN section 7). Duration is int64 microseconds
// (DEC-008). Assets are referenced by clips; files themselves are never
// moved or deleted by the Timeline domain.
public sealed record TimelineMediaAsset(
    string Id,
    string Path,
    int Width,
    int Height,
    long DurationUs,
    double FrameRate,
    bool HasAudio,
    long Size,
    long LastWriteUtcTicks)
{
    public static string NewId() => Guid.NewGuid().ToString("N");

    public static TimelineMediaAsset Create(
        string path,
        int width,
        int height,
        long durationUs,
        double frameRate,
        bool hasAudio,
        long size = 0,
        long lastWriteUtcTicks = 0)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidDataException("Timeline asset path không được rỗng.");
        if (width <= 0 || height <= 0)
            throw new InvalidDataException("Timeline asset kích thước khung hình không hợp lệ.");
        if (durationUs <= 0)
            throw new InvalidDataException("Timeline asset duration phải dương (microseconds).");
        if (!double.IsFinite(frameRate) || frameRate <= 0)
            throw new InvalidDataException("Timeline asset FPS phải là số dương hữu hạn.");
        if (size < 0 || lastWriteUtcTicks < 0)
            throw new InvalidDataException("Timeline asset fingerprint không hợp lệ.");
        return new TimelineMediaAsset(
            NewId(), path.Trim(), width, height, durationUs, frameRate, hasAudio, size, lastWriteUtcTicks);
    }

    public void Validate()
    {
        if (Id is not { Length: 32 } || !Id.All(character => char.IsAsciiHexDigit(character)))
            throw new InvalidDataException("Timeline asset ID không hợp lệ.");
        if (string.IsNullOrWhiteSpace(Path))
            throw new InvalidDataException("Timeline asset path không được rỗng.");
        if (Width <= 0 || Height <= 0)
            throw new InvalidDataException("Timeline asset kích thước khung hình không hợp lệ.");
        if (DurationUs <= 0)
            throw new InvalidDataException("Timeline asset duration phải dương (microseconds).");
        if (!double.IsFinite(FrameRate) || FrameRate <= 0)
            throw new InvalidDataException("Timeline asset FPS phải là số dương hữu hạn.");
        if (Size < 0 || LastWriteUtcTicks < 0)
            throw new InvalidDataException("Timeline asset fingerprint không hợp lệ.");
    }
}
