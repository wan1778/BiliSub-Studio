namespace BiliSubStudio.Core.Timeline;

// G2 immutable render-plan shell, resolved in G3. Preview and Export share
// this one plan type, distinct from the classic VideoEditRequest path
// (DEC-013). G3 resolves the ordered clip segments; segment rendering
// arrives in G10.
public sealed record TimelineResolvedClip(
    string ClipId,
    string AssetId,
    long SequenceStartUs,
    long SequenceEndUs,
    long SourceInUs,
    long SourceOutUs)
{
    public long SequenceDurationUs => checked(SequenceEndUs - SequenceStartUs);
}

public sealed record TimelineRenderPlan(
    string ProjectId,
    long CreatedUtcTicks,
    IReadOnlyList<TimelineResolvedClip> Clips)
{
    public long TotalDurationUs => Clips.Count == 0 ? 0 : Clips.Max(clip => clip.SequenceEndUs);

    public static TimelineRenderPlan Empty(string projectId)
    {
        TimelineProjectV2.ValidateId(projectId);
        return new TimelineRenderPlan(projectId, DateTimeOffset.UtcNow.Ticks, []);
    }
}

public static class TimelineCompiler
{
    public static TimelineRenderPlan Compile(TimelineProjectV2 project)
    {
        ArgumentNullException.ThrowIfNull(project);
        project.Validate();
        if (project.Sequence is null)
            return TimelineRenderPlan.Empty(project.Id);
        var resolved = project.Sequence.VideoClips
            .OrderBy(clip => clip.TimelineStartUs)
            .Select(clip => new TimelineResolvedClip(
                clip.Id, clip.AssetId, clip.TimelineStartUs, clip.TimelineEndUs,
                clip.SourceInUs, clip.SourceOutUs))
            .ToArray();
        return new TimelineRenderPlan(project.Id, DateTimeOffset.UtcNow.Ticks, resolved);
    }
}
