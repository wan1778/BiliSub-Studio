using System.Globalization;
using System.Diagnostics;
using BiliSubStudio.Core.Configuration;
using BiliSubStudio.Core.Processes;

namespace BiliSubStudio.Core.Timeline;

public sealed record TimelineProcessedPreviewRequest(
    long Revision,
    TimelineRenderPlan Plan,
    long SequenceStartUs,
    long DurationUs);

public sealed record TimelineProcessedPreviewArtifact(
    long Revision,
    string Path,
    long SequenceStartUs,
    long DurationUs);

public interface ITimelineProcessedPreviewRenderer
{
    Task RenderAsync(
        TimelineProcessedPreviewRequest request,
        string renderingPath,
        OwnedProcessGroup processes,
        CancellationToken cancellationToken);
}

public interface ITimelineProcessedPreviewGenerator
{
    Task<TimelineProcessedPreviewArtifact> GenerateAsync(
        TimelineRenderPlan plan,
        long sequenceStartUs,
        long durationUs,
        long revision,
        CancellationToken cancellationToken);

    Task DeleteAsync(
        TimelineProcessedPreviewArtifact? artifact,
        CancellationToken cancellationToken = default);
}

// G9 owns proxy IO and child processes under Temp/TimelineV2/Preview. The
// renderer seam is intentional: G10 supplies the multi-clip FFmpeg graph,
// while this gate proves publication, cancellation and cleanup ownership.
public sealed class TimelineProcessedPreviewService : ITimelineProcessedPreviewGenerator
{
    private const string ManagedPrefix = "timeline-preview-";
    private static readonly TimeSpan LegacyStaleAge = TimeSpan.FromDays(1);
    private static readonly long ProcessStartUtcTicks = GetProcessStartUtcTicks();
    private static readonly string ProcessOwnerPrefix = ManagedPrefix
        + Environment.ProcessId.ToString(CultureInfo.InvariantCulture)
        + "-"
        + ProcessStartUtcTicks.ToString(CultureInfo.InvariantCulture)
        + "-";
    private readonly ITimelineProcessedPreviewRenderer _renderer;
    private readonly string _previewDirectory;
    private readonly string _previewPrefix;

    public TimelineProcessedPreviewService(
        AppPaths paths,
        ITimelineProcessedPreviewRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _previewDirectory = Path.GetFullPath(Path.Combine(
            paths.Temp,
            TimelineProjectStoreV2.TestRootFolderName,
            "Preview"));
        _previewPrefix = _previewDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
    }

    public string PreviewDirectory => _previewDirectory;

    public async Task<TimelineProcessedPreviewArtifact> GenerateAsync(
        TimelineRenderPlan plan,
        long sequenceStartUs,
        long durationUs,
        long revision,
        CancellationToken cancellationToken)
    {
        ValidateRequest(plan, sequenceStartUs, durationUs, revision);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_previewDirectory);

        var identity = ProcessOwnerPrefix
            + plan.ProjectId
            + "-"
            + revision.ToString("D20", CultureInfo.InvariantCulture)
            + "-"
            + Guid.NewGuid().ToString("N");
        var finalPath = EnsureManagedPath(Path.Combine(_previewDirectory, identity + ".mp4"));
        var renderingPath = EnsureManagedPath(
            Path.Combine(_previewDirectory, identity + ".rendering.mp4"));
        var request = new TimelineProcessedPreviewRequest(
            revision, plan, sequenceStartUs, durationUs);
        var published = false;

        await using var processes = new OwnedProcessGroup();
        try
        {
            await _renderer.RenderAsync(request, renderingPath, processes, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (processes.ActiveCount != 0)
                throw new InvalidOperationException("Timeline preview renderer trả về khi process vẫn còn chạy.");
            if (!File.Exists(renderingPath) || new FileInfo(renderingPath).Length == 0)
                throw new InvalidDataException("Timeline preview renderer không tạo proxy hợp lệ.");

            File.Move(renderingPath, finalPath);
            cancellationToken.ThrowIfCancellationRequested();
            published = true;
            return new TimelineProcessedPreviewArtifact(
                revision, finalPath, sequenceStartUs, durationUs);
        }
        finally
        {
            await processes.StopAsync().ConfigureAwait(false);
            await DeleteManagedFileAsync(renderingPath, CancellationToken.None).ConfigureAwait(false);
            if (!published)
                await DeleteManagedFileAsync(finalPath, CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async Task DeleteAsync(
        TimelineProcessedPreviewArtifact? artifact,
        CancellationToken cancellationToken = default)
    {
        if (artifact is null) return;
        await DeleteManagedFileAsync(
            EnsureManagedPath(artifact.Path), cancellationToken).ConfigureAwait(false);
    }

    public async Task CleanupCacheAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_previewDirectory)) return;
        var paths = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(_previewDirectory)) return Array.Empty<string>();
            var abandoned = new List<string>();
            foreach (var path in Directory.EnumerateFiles(
                         _previewDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsManagedFileName(Path.GetFileName(path))
                    && !HasLiveOwnerOrRecentLegacy(path))
                    abandoned.Add(path);
            }
            return abandoned.ToArray();
        }, cancellationToken).ConfigureAwait(false);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DeleteManagedFileAsync(path, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ValidateRequest(
        TimelineRenderPlan plan,
        long sequenceStartUs,
        long durationUs,
        long revision)
    {
        ArgumentNullException.ThrowIfNull(plan);
        TimelineProjectV2.ValidateId(plan.ProjectId);
        if (revision <= 0)
            throw new InvalidDataException("Timeline preview revision không hợp lệ.");
        if (sequenceStartUs < 0 || durationUs <= 0
            || sequenceStartUs >= plan.TotalDurationUs
            || durationUs > plan.TotalDurationUs - sequenceStartUs)
            throw new InvalidDataException("Timeline processed-preview window không hợp lệ.");
    }

    private string EnsureManagedPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(_previewPrefix, StringComparison.OrdinalIgnoreCase)
            || !IsManagedFileName(Path.GetFileName(full)))
            throw new InvalidDataException("Timeline preview artifact nằm ngoài V2 preview root.");
        return full;
    }

    private static bool IsManagedFileName(string fileName) =>
        fileName.StartsWith(ManagedPrefix, StringComparison.Ordinal)
        && fileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);

    private static bool HasLiveOwnerOrRecentLegacy(string path)
    {
        var fileName = Path.GetFileName(path);
        var owner = fileName.AsSpan(ManagedPrefix.Length);
        var firstDash = owner.IndexOf('-');
        var secondDash = firstDash < 0 ? -1 : owner[(firstDash + 1)..].IndexOf('-');
        if (firstDash > 0 && secondDash >= 0
            && int.TryParse(owner[..firstDash], NumberStyles.None, CultureInfo.InvariantCulture, out var processId)
            && long.TryParse(owner.Slice(firstDash + 1, secondDash), NumberStyles.None,
                CultureInfo.InvariantCulture, out var processStartUtcTicks))
            return IsProcessOwnerAlive(processId, processStartUtcTicks);

        return File.GetLastWriteTimeUtc(path) > DateTime.UtcNow - LegacyStaleAge;
    }

    private static bool IsProcessOwnerAlive(int processId, long processStartUtcTicks)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited
                && Math.Abs(process.StartTime.ToUniversalTime().Ticks - processStartUtcTicks)
                    <= TimeSpan.TicksPerSecond;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch
        {
            // Access-denied/process-query failures are preserved rather than
            // risking deletion of a proxy another app instance is playing.
            return true;
        }
    }

    private static long GetProcessStartUtcTicks()
    {
        using var process = Process.GetCurrentProcess();
        return process.StartTime.ToUniversalTime().Ticks;
    }

    private static async Task DeleteManagedFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!File.Exists(path)) return;
                File.Delete(path);
                if (!File.Exists(path)) return;
            }
            catch (IOException) when (attempt < 5)
            {
            }
            catch (UnauthorizedAccessException) when (attempt < 5)
            {
            }
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
        throw new IOException("Không xóa được Timeline preview artifact do request sở hữu.");
    }
}
