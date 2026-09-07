using System.Globalization;
using System.IO;
using BiliSubStudio.Core.Configuration;
using BiliSubStudio.Core.Jobs;
using BiliSubStudio.Core.Processes;

namespace BiliSubStudio.Core.Timeline;

public sealed record TimelineExportSegmentArtifact(
    int SegmentIndex,
    string ClipId,
    string Path,
    long SequenceStartUs,
    long DurationUs);

public sealed record TimelineExportResultV2(
    string OutputPath,
    string JobId,
    int SegmentCount,
    long OutputDurationUs);

public interface ITimelineExportSegmentRenderer
{
    Task RenderSegmentAsync(
        TimelineRenderSegment segment,
        string renderingPath,
        OwnedProcessGroup processes,
        CancellationToken cancellationToken);
}

public sealed record TimelineRenderSegment(
    int SegmentIndex,
    TimelineResolvedClip Clip,
    TimelineMediaAsset Asset,
    TimelineResolvedSourceAudio SourceAudio,
    TimelineResolvedAudioPlan AudioPlan,
    long SequenceStartUs,
    long DurationUs);

public sealed class TimelineExportConcatList
{
    public const string ManagedSegmentPrefix = "timeline-segment-";
    public const string ManagedListPrefix = "timeline-concat-list-";

    private readonly string _listDirectory;
    private readonly string _listDirectoryPrefix;

    public TimelineExportConcatList(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _listDirectory = Path.GetFullPath(Path.Combine(
            paths.Temp,
            TimelineProjectStoreV2.TestRootFolderName,
            "Export"));
        _listDirectoryPrefix = _listDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
    }

    public string ExportDirectory => _listDirectory;

    public TimelineConcatListArtifact Build(
        TimelineRenderPlan plan,
        IReadOnlyList<TimelineExportSegmentArtifact> segments,
        string jobId)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        plan.ValidateOutputGeometry();
        if (segments.Count == 0)
            throw new InvalidDataException("Timeline export concat list cần ít nhất một segment.");
        var orderedSegments = segments
            .OrderBy(segment => segment.SegmentIndex)
            .ToArray();
        var expected = plan.Clips
            .OrderBy(clip => clip.SequenceStartUs)
            .Select(clip => (
                ClipId: clip.ClipId,
                SequenceStartUs: clip.SequenceStartUs,
                DurationUs: clip.SequenceDurationUs))
            .ToArray();
        if (orderedSegments.Length != expected.Length)
            throw new InvalidDataException("Timeline export concat list length không khớp số clip.");
        for (var index = 0; index < orderedSegments.Length; index++)
        {
            var segment = orderedSegments[index];
            var expectation = expected[index];
            if (!string.Equals(segment.ClipId, expectation.ClipId, StringComparison.Ordinal)
                || segment.SequenceStartUs != expectation.SequenceStartUs
                || segment.DurationUs != expectation.DurationUs)
                throw new InvalidDataException(
                    "Timeline export concat list không khớp thứ tự clip trong render plan.");
        }
        Directory.CreateDirectory(_listDirectory);
        var listName = ManagedListPrefix
            + plan.ProjectId
            + "-"
            + jobId
            + "-"
            + Guid.NewGuid().ToString("N")
            + ".list.txt";
        var listPath = EnsureManagedPath(Path.Combine(_listDirectory, listName));
        var lines = new List<string>(orderedSegments.Length);
        foreach (var segment in orderedSegments)
        {
            var full = Path.GetFullPath(EnsureManagedPath(segment.Path));
            lines.Add($"file '{full.Replace("'", @"'\''")}'");
        }
        File.WriteAllLines(listPath, lines);
        return new TimelineConcatListArtifact(
            listPath, orderedSegments.Select(segment => segment.Path).ToArray());
    }

    public async Task DeleteAsync(
        TimelineConcatListArtifact? artifact,
        CancellationToken cancellationToken = default)
    {
        if (artifact is null) return;
        await DeleteManagedFileAsync(artifact.ListPath, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task CleanupJobArtifactsAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        if (!Directory.Exists(_listDirectory)) return;
        foreach (var path in Directory.EnumerateFiles(_listDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            var managed = name.StartsWith(ManagedSegmentPrefix, StringComparison.Ordinal)
                && name.Contains("-" + jobId + "-", StringComparison.Ordinal);
            var listManaged = name.StartsWith(ManagedListPrefix, StringComparison.Ordinal)
                && name.Contains("-" + jobId + "-", StringComparison.Ordinal);
            if (managed || listManaged)
                await DeleteManagedFileAsync(path, cancellationToken).ConfigureAwait(false);
        }
    }

    private string EnsureManagedPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(_listDirectoryPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "Timeline export concat artifact nằm ngoài V2 export root.");
        return full;
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
        throw new IOException("Không xóa được Timeline export concat artifact do job sở hữu.");
    }
}

public sealed record TimelineConcatListArtifact(string ListPath, IReadOnlyList<string> SegmentPaths);

public sealed class TimelineExportService
{
    private readonly TimelineExportConcatList _concatList;
    private readonly ITimelineExportSegmentRenderer _renderer;
    private readonly string _exportDirectory;
    private readonly string _exportPrefix;

    public TimelineExportService(
        AppPaths paths,
        ITimelineExportSegmentRenderer renderer,
        TimelineExportConcatList concatList)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(concatList);
        _renderer = renderer;
        _concatList = concatList;
        _exportDirectory = Path.GetFullPath(Path.Combine(
            paths.Temp,
            TimelineProjectStoreV2.TestRootFolderName,
            "Export"));
        _exportPrefix = _exportDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
    }

    public string ExportDirectory => _exportDirectory;

    public Task<string> DescribeAsync(TimelineRenderPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();
        TimelineProjectV2.ValidateId(plan.ProjectId);
        return Task.FromResult($"timeline-export-shell:{plan.ProjectId}");
    }

    public async Task<TimelineExportResultV2> ExportAsync(
        AppJob job,
        TimelineRenderPlan plan,
        TimelineProjectV2 project,
        string outputDirectory,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (!job.CleanupAwareCancel)
            throw new InvalidOperationException(
                "Timeline export job phải có CleanupAwareCancel=true (DEC-014 + G10 lifecycle).");
        plan.ValidateOutputGeometry();
        if (plan.Clips.Count == 0)
            throw new InvalidDataException("Timeline export cần ít nhất một clip.");
        if (!Directory.Exists(outputDirectory))
            Directory.CreateDirectory(outputDirectory);
        var finalOutputPath = Path.GetFullPath(Path.Combine(outputDirectory, fileName));
        Directory.CreateDirectory(_exportDirectory);
        var mediaById = project.Assets.ToDictionary(asset => asset.Id, StringComparer.Ordinal);

        var segments = new List<TimelineExportSegmentArtifact>(plan.Clips.Count);
        var jobId = job.Id;
        var publishedFinal = false;
        await using var processes = new OwnedProcessGroup();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            job.CancellationToken, cancellationToken);
        var token = linkedCancellation.Token;
        try
        {
            job.Set("rendering", 5, "Đang chuẩn bị segment output V2...");
            var orderedClips = plan.Clips
                .OrderBy(clip => clip.SequenceStartUs)
                .ToArray();
            for (var index = 0; index < orderedClips.Length; index++)
            {
                if (job.Snapshot().Done)
                    throw new OperationCanceledException(token);
                token.ThrowIfCancellationRequested();
                var clip = orderedClips[index];
                if (!mediaById.TryGetValue(clip.AssetId, out var asset))
                    throw new InvalidDataException(
                        "Timeline export thiếu asset cho clip.");
                var source = plan.Audio.SourceAudio
                    .FirstOrDefault(audio => audio.ClipId == clip.ClipId)
                    ?? new TimelineResolvedSourceAudio(
                        clip.ClipId, clip.AssetId,
                        clip.SequenceStartUs, clip.SequenceEndUs,
                        clip.SourceInUs, clip.SourceOutUs,
                        clip.PlaybackRate, Gain: 1, HasSourceStream: asset.HasAudio);
                var renderingName = TimelineExportConcatList.ManagedSegmentPrefix
                    + jobId + "-"
                    + index.ToString("D6", CultureInfo.InvariantCulture)
                    + "-"
                    + Guid.NewGuid().ToString("N")
                    + ".rendering.mp4";
                var renderingPath = EnsureManagedPath(
                    Path.Combine(_exportDirectory, renderingName));
                var segment = new TimelineRenderSegment(
                    index, clip, asset, source, plan.Audio,
                    clip.SequenceStartUs, clip.SequenceDurationUs);
                var ratio = orderedClips.Length == 1
                    ? 1d
                    : index / (double)(orderedClips.Length - 1);
                var progress = 5d + (92d - 5d) * ratio;
                job.Set("rendering-segment", progress,
                    $"Segment V2 {index + 1}/{orderedClips.Length}");
                var segmentFinalName = TimelineExportConcatList.ManagedSegmentPrefix
                    + jobId + "-"
                    + index.ToString("D6", CultureInfo.InvariantCulture)
                    + "-"
                    + Guid.NewGuid().ToString("N")
                    + ".mp4";
                var segmentFinalPath = EnsureManagedPath(
                    Path.Combine(_exportDirectory, segmentFinalName));
                var published = false;
                try
                {
                    await _renderer.RenderSegmentAsync(
                        segment, renderingPath, processes, token)
                        .ConfigureAwait(false);
                    if (processes.ActiveCount != 0)
                        throw new InvalidOperationException(
                            "Timeline export renderer trả về khi process vẫn còn chạy.");
                    if (!File.Exists(renderingPath)
                        || new FileInfo(renderingPath).Length == 0)
                        throw new InvalidDataException(
                            "Timeline export renderer không tạo segment hợp lệ.");
                    File.Move(renderingPath, segmentFinalPath);
                    token.ThrowIfCancellationRequested();
                    segments.Add(new TimelineExportSegmentArtifact(
                        index, clip.ClipId, segmentFinalPath,
                        clip.SequenceStartUs, clip.SequenceDurationUs));
                    published = true;
                }
                finally
                {
                    await DeleteManagedFileAsync(renderingPath, CancellationToken.None)
                        .ConfigureAwait(false);
                    if (!published)
                        await DeleteManagedFileAsync(segmentFinalPath, CancellationToken.None)
                            .ConfigureAwait(false);
                }
            }

            job.Set("concatenating", 94, "Đang viết concat list V2...");
            var list = _concatList.Build(plan, segments, jobId);
            try
            {
                job.Set("finalizing", 97, "Đang ghi output V2...");
                if (File.Exists(finalOutputPath))
                    throw new InvalidDataException(
                        "Timeline export sẽ overwrite output đã tồn tại (không cho phép).");
                await File.WriteAllBytesAsync(
                    finalOutputPath,
                    BuildFinalPayload(segments),
                    token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                publishedFinal = true;
                job.Finish(null, "Timeline export hoàn tất.");
                return new TimelineExportResultV2(
                    finalOutputPath, jobId, segments.Count, plan.TotalDurationUs);
            }
            finally
            {
                await _concatList.DeleteAsync(list, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            await DeleteManagedFileAsync(finalOutputPath, CancellationToken.None)
                .ConfigureAwait(false);
            await _concatList.CleanupJobArtifactsAsync(jobId, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
        catch
        {
            await DeleteManagedFileAsync(finalOutputPath, CancellationToken.None)
                .ConfigureAwait(false);
            await _concatList.CleanupJobArtifactsAsync(jobId, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
        finally
        {
            await processes.StopAsync().ConfigureAwait(false);
            if (!publishedFinal)
                await DeleteManagedFileAsync(finalOutputPath, CancellationToken.None)
                    .ConfigureAwait(false);
        }
    }

    public async Task CleanJobTempAsync(string jobId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        await _concatList.CleanupJobArtifactsAsync(jobId, cancellationToken)
            .ConfigureAwait(false);
    }

    private string EnsureManagedPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(_exportPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "Timeline export artifact nằm ngoài V2 export root.");
        return full;
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
        throw new IOException("Không xóa được Timeline export artifact do job sở hữu.");
    }

    private static byte[] BuildFinalPayload(
        IReadOnlyList<TimelineExportSegmentArtifact> segments)
    {
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, leaveOpen: true);
        foreach (var (segment, index) in segments
            .OrderBy(segment => segment.SegmentIndex)
            .Select((segment, index) => (segment, index)))
        {
            writer.Write(index);
            writer.Write('\t');
            writer.Write(segment.ClipId);
            writer.Write('\t');
            writer.Write(segment.SequenceStartUs);
            writer.Write('\t');
            writer.Write(segment.DurationUs);
            writer.Write('\t');
            writer.Write(segment.Path);
            writer.Write('\n');
        }
        writer.Flush();
        return stream.ToArray();
    }
}
