using BiliSubStudio.Core.Editor;
using BiliSubStudio.Core.Jobs;
using BiliSubStudio.Core.Tools;
using BiliSubStudio.Core.Processes;
using BiliSubStudio.Core.Configuration;

namespace BiliSubStudio.Core.Timeline;

public sealed record TimelinePreviewSegmentV2(string Path, double SourceStart, double Duration);

public sealed record TimelineOneClipExportResultV2(string OutputPath);

// G5 one-clip compatibility adapter. It delegates to the proven classic
// VideoEditorService via parity mapping and therefore returns a classic-owned
// preview-cache artifact. G9 owns real V2 proxy lifecycle separately.
public sealed class TimelinePreviewExportService
{
    private readonly VideoEditorService _editor;
    private readonly ToolManager _tools;
    private readonly string _compatibilityOutputDirectory;

    public TimelinePreviewExportService(AppPaths paths, VideoEditorService editor, ToolManager tools)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _compatibilityOutputDirectory = paths.Temp;
    }

    public async Task<TimelinePreviewSegmentV2> CreatePreviewSegmentAsync(
        TimelineRenderPlan plan,
        TimelineMediaAsset asset,
        double requestedStart,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(asset);
        var request = TimelineOneClipParity.ToVideoEditRequest(
            plan, asset, outputDirectory: _compatibilityOutputDirectory, fileName: string.Empty);
        var segment = await _editor.CreatePreviewSegmentAsync(request, requestedStart, cancellationToken)
            .ConfigureAwait(false);
        return new TimelinePreviewSegmentV2(segment.Path, segment.SourceStart, segment.Duration);
    }

    public async Task<TimelineOneClipExportResultV2> ExportAsync(
        AppJob job,
        TimelineRenderPlan plan,
        TimelineMediaAsset asset,
        string outputDirectory,
        string fileName,
        EditorExportSettings? export,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(asset);
        var request = TimelineOneClipParity.ToVideoEditRequest(
            plan, asset, outputDirectory, fileName, export);
        var result = await _editor.RunAsync(job, request).ConfigureAwait(false);
        return new TimelineOneClipExportResultV2(result.OutputPath);
    }

    public async Task DeletePreviewSegmentAsync(string? path, CancellationToken cancellationToken = default)
    {
        await _editor.DeletePreviewSegmentAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public Task EnsureFfmpegAsync(CancellationToken cancellationToken) =>
        _tools.EnsureFfmpegAsync(cancellationToken);
}
