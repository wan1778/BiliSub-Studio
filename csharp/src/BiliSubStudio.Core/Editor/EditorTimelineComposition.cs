namespace BiliSubStudio.Core.Editor;

/// <summary>
/// Math for a timeline composed of multiple videos: mapping timeline time to
/// (clip, time within the source file) and back.
/// </summary>
public static class EditorTimelineComposition
{
    public const string PrimaryClipId = "source";

    /// <summary>Clips of the project; an old project (with no timeline) uses the source video as a single clip.</summary>
    public static IReadOnlyList<EditorTimelineClip> Resolve(EditorTimelineProject? timeline, EditorSourceFingerprint? source)
    {
        if (timeline?.Clips is { Count: > 0 } clips && clips.All(c => c.Duration > 0)) return clips;
        if (source is null) return [];
        var duration = Math.Max(0, source.Duration);
        var label = string.IsNullOrWhiteSpace(source.Path) ? "Video" : Path.GetFileName(source.Path);
        return [new EditorTimelineClip(PrimaryClipId, source.Path, 0, duration, label)];
    }

    public static double TotalDuration(IEnumerable<EditorTimelineClip> clips)
        => clips.Sum(clip => Math.Max(0, clip.Duration));

    /// <summary>Position (start on the timeline) of each clip, in order.</summary>
    public static IReadOnlyList<double> Starts(IEnumerable<EditorTimelineClip> clips)
    {
        var starts = new List<double>();
        var cursor = 0d;
        foreach (var clip in clips)
        {
            starts.Add(cursor);
            cursor += Math.Max(0, clip.Duration);
        }
        return starts;
    }

    /// <summary>Clip containing the timeline time, together with the corresponding time in the source file.</summary>
    public static (EditorTimelineClip Clip, double SourceTime, double TimelineStart) Locate(
        IReadOnlyList<EditorTimelineClip> clips,
        double timelineTime)
    {
        if (clips.Count == 0)
            return (new EditorTimelineClip(PrimaryClipId, string.Empty, 0, 0, "Video"), 0, 0);
        var starts = Starts(clips);
        var time = Math.Max(0, timelineTime);
        for (var index = clips.Count - 1; index >= 0; index--)
        {
            var clip = clips[index];
            var start = starts[index];
            if (time >= start || index == 0) return (clip, clip.SourceIn + (time - start), start);
        }
        var first = clips[0];
        return (first, first.SourceIn, 0);
    }

    /// <summary>Splits a clip at a source time, returning the new list of clips.</summary>
    public static IReadOnlyList<EditorTimelineClip> SplitAtSourceTime(
        IReadOnlyList<EditorTimelineClip> clips,
        int clipIndex,
        double sourceTime)
    {
        if (clipIndex < 0 || clipIndex >= clips.Count) return clips;
        var clip = clips[clipIndex];
        var offset = sourceTime - clip.SourceIn;
        if (offset <= .05 || offset >= clip.Duration - .05) return clips;
        var left = clip with { Duration = offset };
        var right = clip with
        {
            Id = $"{clip.Id}-split-{Guid.NewGuid().ToString("N")[..6]}",
            SourceIn = sourceTime,
            Duration = clip.Duration - offset,
            Label = clip.Label,
        };
        var result = new List<EditorTimelineClip>(clips.Count + 1);
        for (var index = 0; index < clips.Count; index++)
        {
            if (index != clipIndex)
            {
                result.Add(clips[index]);
                continue;
            }
            result.Add(left);
            result.Add(right);
        }
        return result;
    }

    /// <summary>Removes one clip from the composition.</summary>
    public static IReadOnlyList<EditorTimelineClip> RemoveAt(IReadOnlyList<EditorTimelineClip> clips, int clipIndex)
    {
        if (clipIndex < 0 || clipIndex >= clips.Count) return clips;
        var result = new List<EditorTimelineClip>(clips.Count - 1);
        for (var index = 0; index < clips.Count; index++)
        {
            if (index != clipIndex) result.Add(clips[index]);
        }
        return result;
    }

    /// <summary>
    /// Largest effect-region time that can be stored for the project. Region
    /// times stay anchored to the primary clip's source file, so a timed region
    /// dragged past the end of the primary video continues into the following
    /// clips in composed-timeline seconds. Keeping the source duration as the
    /// floor preserves regions that live in the trimmed-out tail of the source.
    /// </summary>
    public static double RegionTimeLimit(
        EditorTimelineProject? timeline,
        string sourcePath,
        double sourceDuration)
    {
        var duration = Math.Max(0, sourceDuration);
        if (timeline?.Clips is not { Count: > 0 } clips) return duration;
        var starts = Starts(clips);
        var total = TotalDuration(clips);
        for (var index = 0; index < clips.Count; index++)
        {
            var clip = clips[index];
            if (clip.Id != PrimaryClipId
                && !string.Equals(clip.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase)) continue;
            return Math.Max(duration, clip.SourceIn + Math.Max(0, total - starts[index]));
        }
        return duration;
    }
}
