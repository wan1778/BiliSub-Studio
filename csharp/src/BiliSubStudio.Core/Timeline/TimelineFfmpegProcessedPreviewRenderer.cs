using BiliSubStudio.Core.Configuration;
using BiliSubStudio.Core.Jobs;
using BiliSubStudio.Core.Processes;

namespace BiliSubStudio.Core.Timeline;

// G18 concrete G9 renderer. The first playback gate renders one complete
// immutable sequence and delegates to the exact G13/G16 export graph instead
// of maintaining a second preview-only FFmpeg graph.
public sealed class TimelineFfmpegProcessedPreviewRenderer : ITimelineProcessedPreviewRenderer
{
    private readonly TimelineProjectV2 _project;
    private readonly TimelineExportService _export;

    public TimelineFfmpegProcessedPreviewRenderer(
        AppPaths paths,
        string ffmpegPath,
        string ffprobePath,
        TimelineProjectV2 project)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(ffprobePath);
        _project = project ?? throw new ArgumentNullException(nameof(project));
        _project.Validate();
        _export = new TimelineExportService(
            paths,
            new TimelineFfmpegSegmentRenderer(ffmpegPath),
            new TimelineExportConcatList(paths),
            new TimelineFfmpegConcatenator(ffmpegPath, ffprobePath),
            new TimelineFfmpegFinalMixer(ffmpegPath, ffprobePath));
    }

    public TimelineFfmpegProcessedPreviewRenderer(
        TimelineProjectV2 project,
        TimelineExportService export)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        _export = export ?? throw new ArgumentNullException(nameof(export));
        _project.Validate();
    }

    public async Task RenderAsync(
        TimelineProcessedPreviewRequest request,
        string renderingPath,
        OwnedProcessGroup processes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(renderingPath);
        ArgumentNullException.ThrowIfNull(processes);
        if (!string.Equals(request.Plan.ProjectId, _project.Id, StringComparison.Ordinal))
            throw new InvalidDataException("Timeline preview plan và project snapshot không cùng identity.");
        if (request.SequenceStartUs != 0 || request.DurationUs != request.Plan.TotalDurationUs)
            throw new InvalidDataException(
                "G18 chỉ render processed preview cho toàn sequence; preview window chưa được hỗ trợ.");
        if (processes.ActiveCount != 0)
            throw new InvalidOperationException("Timeline preview process owner không sạch trước khi render.");

        var outputPath = Path.GetFullPath(renderingPath);
        var outputDirectory = Path.GetDirectoryName(outputPath)
            ?? throw new InvalidDataException("Timeline preview output thiếu thư mục.");
        var operationId = "timeline-preview-"
            + request.Revision.ToString("D20", System.Globalization.CultureInfo.InvariantCulture)
            + "-"
            + Guid.NewGuid().ToString("N");
        using var job = new AppJob(
            operationId, "timeline-preview", cleanupAwareCancel: true);
        var result = await _export.ExportAsync(
            job,
            request.Plan,
            _project,
            outputDirectory,
            Path.GetFileName(outputPath),
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(
                Path.GetFullPath(result.OutputPath), outputPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Timeline export graph trả sai preview output path.");
    }
}
