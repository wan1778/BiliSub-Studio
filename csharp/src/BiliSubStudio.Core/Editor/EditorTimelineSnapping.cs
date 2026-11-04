namespace BiliSubStudio.Core.Editor;

/// <summary>
/// Snapping (snap) clip edges on the timeline. Snap targets are chosen by distance
/// on SCREEN (9 px like FilmCraft) rather than by a time threshold, so
/// the snapping strength is identical at every zoom level. When several targets sit at
/// the same distance, the one appearing first wins, exactly like FilmCraft.
///
/// Snap targets live in timeline time (composed time): the start/end of other
/// clips, the playhead, the markers, and the trim marks of the project.
/// </summary>
public static class EditorTimelineSnapping
{
    /// <summary>Snap threshold in screen pixels (like FilmCraft).</summary>
    public const double SnapPixels = 9;

    /// <summary>
    /// Snaps one timeline position to the nearest target within the given screen
    /// distance.
    /// </summary>
    /// <param name="time">Timeline position being dragged (timeline time).</param>
    /// <param name="candidates">Snap targets (timeline time).</param>
    /// <param name="pixelsPerSecond">Current pixel density of the timeline.</param>
    /// <param name="thresholdPixels">Snap threshold in screen pixels.</param>
    /// <returns>The snapped position, or <paramref name="time"/> if no target is close enough.</returns>
    public static double SnapTime(
        double time,
        IEnumerable<double> candidates,
        double pixelsPerSecond,
        double thresholdPixels = SnapPixels)
    {
        if (!double.IsFinite(time) || candidates is null) return time;
        var pixelsPerSecondValue = EditorTimelineLayout.ClampPixelsPerSecond(pixelsPerSecond);
        if (pixelsPerSecondValue <= 0) return time;
        var threshold = double.IsFinite(thresholdPixels) && thresholdPixels > 0 ? thresholdPixels : SnapPixels;
        var x = EditorTimelineLayout.XForTime(time, pixelsPerSecondValue);
        var bestDistance = double.PositiveInfinity;
        var best = double.NaN;
        foreach (var candidate in candidates)
        {
            if (!double.IsFinite(candidate)) continue;
            var distance = Math.Abs(EditorTimelineLayout.XForTime(candidate, pixelsPerSecondValue) - x);
            if (distance >= threshold || distance >= bestDistance) continue;
            bestDistance = distance;
            best = candidate;
        }
        return double.IsNaN(best) ? time : best;
    }

    /// <summary>
    /// Collects the snap targets of the composition: the start/end of the other clips, the playhead,
    /// markers, and the trim marks. The clip being dragged is left out so it does not snap to
    /// its own edge.
    /// </summary>
    /// <param name="clips">Clip list of the composition.</param>
    /// <param name="playheadTime">Playhead position on the timeline.</param>
    /// <param name="markers">Markers of the timeline (timeline time).</param>
    /// <param name="trimMarks">Trim marks of the project (timeline time).</param>
    /// <param name="excludedClipIndex">Position of the clip being dragged, left out of the snap targets.</param>
    public static IReadOnlyList<double> Candidates(
        IReadOnlyList<EditorTimelineClip> clips,
        double playheadTime,
        IEnumerable<double>? markers = null,
        IEnumerable<double>? trimMarks = null,
        int excludedClipIndex = -1)
    {
        var candidates = new List<double>();
        if (clips is { Count: > 0 })
        {
            var starts = EditorTimelineComposition.Starts(clips);
            for (var index = 0; index < clips.Count; index++)
            {
                if (index == excludedClipIndex) continue;
                candidates.Add(starts[index]);
                candidates.Add(starts[index] + Math.Max(0, clips[index].Duration));
            }
        }
        if (double.IsFinite(playheadTime)) candidates.Add(playheadTime);
        if (markers is not null)
            foreach (var marker in markers)
                if (double.IsFinite(marker)) candidates.Add(marker);
        if (trimMarks is not null)
            foreach (var trimMark in trimMarks)
                if (double.IsFinite(trimMark)) candidates.Add(trimMark);
        return candidates;
    }
}
