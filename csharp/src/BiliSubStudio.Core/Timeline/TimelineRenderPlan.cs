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

public sealed record TimelineResolvedItem(
    string ItemId,
    TimelineItemKind Kind,
    string? ClipId,
    long SequenceStartUs,
    long SequenceEndUs,
    string Payload);

public sealed record TimelineRenderPlan(
    string ProjectId,
    long CreatedUtcTicks,
    IReadOnlyList<TimelineResolvedClip> Clips,
    IReadOnlyList<TimelineResolvedItem> Items)
{
    public long TotalDurationUs => Clips.Count == 0 ? 0 : Clips.Max(clip => clip.SequenceEndUs);

    public static TimelineRenderPlan Empty(string projectId)
    {
        TimelineProjectV2.ValidateId(projectId);
        return new TimelineRenderPlan(projectId, DateTimeOffset.UtcNow.Ticks, [], []);
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
        var resolvedClips = project.Sequence.VideoClips
            .OrderBy(clip => clip.TimelineStartUs)
            .Select(clip => new TimelineResolvedClip(
                clip.Id, clip.AssetId, clip.TimelineStartUs, clip.TimelineEndUs,
                clip.SourceInUs, clip.SourceOutUs))
            .ToArray();
        var clipsById = project.Sequence.VideoClips.ToDictionary(clip => clip.Id, StringComparer.Ordinal);
        var resolvedItems = project.Items
            .Select(item =>
            {
                if (item.IsSequenceGlobal)
                    return new TimelineResolvedItem(
                        item.Id, item.Kind, null, item.SequenceStartUs, item.SequenceEndUs, item.Payload);
                var clip = clipsById[item.ClipId!];
                return new TimelineResolvedItem(
                    item.Id,
                    item.Kind,
                    item.ClipId,
                    checked(clip.TimelineStartUs + item.ClipStartUs),
                    checked(clip.TimelineStartUs + item.ClipEndUs),
                    item.Payload);
            })
            .OrderBy(item => item.SequenceStartUs)
            .ThenBy(item => item.Kind)
            .ThenBy(item => item.ItemId, StringComparer.Ordinal)
            .ToArray();
        return new TimelineRenderPlan(project.Id, DateTimeOffset.UtcNow.Ticks, resolvedClips, resolvedItems);
    }
}
