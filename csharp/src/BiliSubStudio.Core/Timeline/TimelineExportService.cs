namespace BiliSubStudio.Core.Timeline;

// G2 separate export path shell. It consumes only the immutable V2 plan and
// performs no FFmpeg work and no production writes. Real segment render and
// concat arrive in G10.
public sealed class TimelineExportService
{
    public Task<string> DescribeAsync(TimelineRenderPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();
        TimelineProjectV2.ValidateId(plan.ProjectId);
        return Task.FromResult($"timeline-export-shell:{plan.ProjectId}");
    }
}
