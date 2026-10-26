namespace BiliSubStudio.Core.Editor;

/// <summary>
/// Toán học cho dải thời gian ghép nhiều video: ánh xạ thời gian dải về
/// (clip, thời gian trong file nguồn) và ngược lại.
/// </summary>
public static class EditorTimelineComposition
{
    public const string PrimaryClipId = "source";

    /// <summary>Clip của dự án; dự án cũ (chưa có timeline) dùng video nguồn làm một clip duy nhất.</summary>
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

    /// <summary>Vị trí (bắt đầu trên dải) của từng clip theo thứ tự.</summary>
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

    /// <summary>Clip chứa thời gian trên dải ghép, kèm thời gian tương ứng trong file nguồn.</summary>
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
            if (time >= start || index == 0) return (clip, clip.Start + (time - start), start);
        }
        var first = clips[0];
        return (first, first.Start, 0);
    }

    /// <summary>Tách clip tại thời gian nguồn, trả về danh sách clip mới.</summary>
    public static IReadOnlyList<EditorTimelineClip> SplitAtSourceTime(
        IReadOnlyList<EditorTimelineClip> clips,
        int clipIndex,
        double sourceTime)
    {
        if (clipIndex < 0 || clipIndex >= clips.Count) return clips;
        var clip = clips[clipIndex];
        var offset = sourceTime - clip.Start;
        if (offset <= .05 || offset >= clip.Duration - .05) return clips;
        var left = clip with { End = sourceTime };
        var right = clip with
        {
            Id = $"{clip.Id}-split-{Guid.NewGuid().ToString("N")[..6]}",
            Start = sourceTime,
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

    /// <summary>Bỏ một clip khỏi dải ghép.</summary>
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
}
