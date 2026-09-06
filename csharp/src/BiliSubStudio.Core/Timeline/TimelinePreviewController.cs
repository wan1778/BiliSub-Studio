namespace BiliSubStudio.Core.Timeline;

// G2 separate preview path shell. It consumes only the immutable V2 plan and
// performs no FFmpeg work and no production writes. Real processed preview
// arrives in G9.
public sealed class TimelinePreviewController
{
    public Task<string> DescribeAsync(TimelineRenderPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();
        TimelineProjectV2.ValidateId(plan.ProjectId);
        return Task.FromResult($"timeline-preview-shell:{plan.ProjectId}");
    }
}
