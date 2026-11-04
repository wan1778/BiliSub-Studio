namespace BiliSubStudio.Core.Editor;

/// <summary>
/// Preview start point of the composition: pressing play must run FROM THE CURRENT PLAYHEAD
/// POSITION, not from the beginning of the timeline. Anything trimmed away from the exported file is
/// still a floor: the preview may not play content the exported file does not keep.
///
/// Every function here is pure: UI only calls into it, never recomputes the value itself.
/// </summary>
public static class EditorTimelinePlaybackStart
{
    /// <summary>
    /// Preview start point (timeline time) when the user presses play.
    /// </summary>
    /// <param name="playheadTime">Current playhead position (seconds).</param>
    /// <param name="earliestAllowedTime">Earliest time allowed to play (seconds).</param>
    /// <param name="duration">Total duration of the composition (seconds).</param>
    public static double Resolve(double playheadTime, double earliestAllowedTime, double duration)
    {
        var total = double.IsFinite(duration) && duration > 0 ? duration : double.MaxValue;
        var earliest = double.IsFinite(earliestAllowedTime) ? Math.Max(0, earliestAllowedTime) : 0;
        var playhead = double.IsFinite(playheadTime) ? Math.Clamp(playheadTime, 0, total) : earliest;
        return Math.Clamp(Math.Max(playhead, earliest), 0, total);
    }
}
