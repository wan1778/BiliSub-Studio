namespace BiliSubStudio.Core.Timeline;

// G19 resilience helpers (MASTER_PLAN section 1: missing-media reporting and
// relink). Detection is read-only; relink is a pure function that keeps the
// asset and project identity stable (DEC-012) and only swaps the source path
// plus fingerprint. Validation mirrors the classic Editor relink gate: block
// files that do not match the original geometry and duration, never compare
// size/mtime because a legitimate copy changes them.
public static class TimelineMediaRelink
{
    public const double DurationToleranceSeconds = 0.5;

    public static IReadOnlyList<TimelineMediaAsset> FindMissingMediaAssets(
        TimelineProjectV2 project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return project.Assets
            .Where(asset => !File.Exists(asset.Path))
            .ToArray();
    }

    public static TimelineProjectV2 RelinkMediaAsset(
        TimelineProjectV2 project,
        string assetId,
        string newPath,
        int width,
        int height,
        double durationSeconds,
        bool hasAudio,
        long size,
        long lastWriteUtcTicks)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (string.IsNullOrWhiteSpace(assetId))
            throw new InvalidDataException("Timeline relink cần asset ID.");
        if (string.IsNullOrWhiteSpace(newPath))
            throw new InvalidDataException("Timeline relink cần đường dẫn nguồn mới.");
        if (width <= 0 || height <= 0
            || !double.IsFinite(durationSeconds) || durationSeconds <= 0)
            throw new InvalidDataException("Timeline relink cần media probe hợp lệ.");
        if (size <= 0 || lastWriteUtcTicks < 0)
            throw new InvalidDataException("Timeline relink fingerprint không hợp lệ.");

        var index = project.Assets.ToList().FindIndex(asset =>
            string.Equals(asset.Id, assetId, StringComparison.Ordinal));
        if (index < 0)
            throw new InvalidDataException("Timeline relink asset không tồn tại.");
        var original = project.Assets[index];
        if (original.Width != width || original.Height != height
            || Math.Abs(original.DurationUs / (double)TimelineTime.MicrosecondsPerSecond - durationSeconds)
                > DurationToleranceSeconds)
            throw new InvalidDataException(
                $"File được chọn không khớp nguồn gốc ({original.Width}×{original.Height}, " +
                $"{original.DurationUs / (double)TimelineTime.MicrosecondsPerSecond:0.0}s) — " +
                $"file này là {width}×{height}, {durationSeconds:0.0}s. Hãy chọn đúng file video đã di chuyển.");

        var relinked = original with
        {
            Path = Path.GetFullPath(newPath.Trim()),
            Width = width,
            Height = height,
            DurationUs = TimelineTime.FromSeconds(durationSeconds),
            HasAudio = hasAudio,
            Size = size,
            LastWriteUtcTicks = lastWriteUtcTicks,
        };
        relinked.Validate();
        var assets = project.Assets.ToArray();
        assets[index] = relinked;
        var updated = project with
        {
            Assets = assets,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };
        // Every clip still points at the same stable asset ID; sequence
        // validation must keep passing against the relinked fingerprint.
        updated.Validate();
        return updated;
    }
}
