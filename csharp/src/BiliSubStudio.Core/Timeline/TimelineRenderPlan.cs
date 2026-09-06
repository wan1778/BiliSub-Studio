namespace BiliSubStudio.Core.Timeline;

// G2 immutable render-plan shell. G3+ resolves assets/clips/tracks into real
// media operations; this shell only proves Preview and Export can share one
// plan type that is distinct from the classic VideoEditRequest path.
public sealed record TimelineRenderPlan(string ProjectId, long CreatedUtcTicks)
{
    public static TimelineRenderPlan Empty(string projectId)
    {
        TimelineProjectV2.ValidateId(projectId);
        return new TimelineRenderPlan(projectId, DateTimeOffset.UtcNow.Ticks);
    }
}

public static class TimelineCompiler
{
    public static TimelineRenderPlan Compile(TimelineProjectV2 project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return TimelineRenderPlan.Empty(project.Id);
    }
}
