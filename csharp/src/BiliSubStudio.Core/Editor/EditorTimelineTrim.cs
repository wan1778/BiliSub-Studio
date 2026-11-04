namespace BiliSubStudio.Core.Editor;

/// <summary>Edge of a clip on the timeline that the user drags to trim.</summary>
public enum EditorTimelineTrimEdge
{
    /// <summary>In edge: trims the start of the range in the source file.</summary>
    In,
    /// <summary>Out edge: trims the end of the range in the source file.</summary>
    Out,
}

/// <summary>
/// Math for trimming clip edges on the timeline (following FilmCraft): dragging the In edge
/// moves the in point in the source file by delta while the duration shrinks by exactly delta,
/// dragging the Out edge grows/shrinks the duration by delta. Every clamp (media handle,
/// adjacent clip, minimum one frame) also lives here so UI only calls into it.
///
/// The timeline is a contiguous sequence: each clip's position is the total duration of the clips
/// before it, so trimming an edge shifts the following clips back accordingly (no gaps are opened).
/// </summary>
public static class EditorTimelineTrim
{
    /// <summary>Width of the clip edge grab region, in screen pixels.</summary>
    public const double EdgeGrabPixels = 10;

    /// <summary>Minimum drag threshold, in screen pixels.</summary>
    public const double MinimumDragPixels = 3;

    /// <summary>
    /// Which clip edge is grabbed when clicking at the given X coordinate: null
    /// means a click inside the clip (selects the clip only, no edge trim).
    /// </summary>
    /// <param name="positionX">Pointer X coordinate within the clip (px).</param>
    /// <param name="clipWidth">Clip width on screen (px).</param>
    /// <param name="grabPixels">Edge grab width (px).</param>
    public static EditorTimelineTrimEdge? EdgeAt(
        double positionX,
        double clipWidth,
        double grabPixels = EdgeGrabPixels)
    {
        if (!double.IsFinite(positionX) || !double.IsFinite(clipWidth)) return null;
        var grab = double.IsFinite(grabPixels) && grabPixels > 0 ? grabPixels : EdgeGrabPixels;
        if (!CanGrabEdge(clipWidth, grab)) return null;
        if (positionX <= grab) return EditorTimelineTrimEdge.In;
        if (positionX >= clipWidth - grab) return EditorTimelineTrimEdge.Out;
        return null;
    }

    /// <summary>
    /// Whether a clip is wide enough to grab an edge: a too-narrow clip only selects on click,
    /// it does not drag an edge (the two grab zones would overlap).
    /// </summary>
    /// <param name="clipWidth">Clip width on screen (px).</param>
    /// <param name="grabPixels">Edge grab width (px).</param>
    public static bool CanGrabEdge(double clipWidth, double grabPixels = EdgeGrabPixels)
    {
        if (!double.IsFinite(clipWidth)) return false;
        var grab = double.IsFinite(grabPixels) && grabPixels > 0 ? grabPixels : EdgeGrabPixels;
        return clipWidth >= grab * 3;
    }

    /// <summary>
    /// A drag only takes effect once the pointer has passed the minimum drag threshold:
    /// a light click must not produce junk trims.
    /// </summary>
    /// <param name="movedPixels">Distance the pointer has travelled (px).</param>
    /// <param name="thresholdPixels">Minimum drag threshold (px).</param>
    public static bool ReachedDragThreshold(double movedPixels, double thresholdPixels = MinimumDragPixels)
    {
        if (!double.IsFinite(movedPixels)) return false;
        var threshold = double.IsFinite(thresholdPixels) && thresholdPixels > 0 ? thresholdPixels : MinimumDragPixels;
        return Math.Abs(movedPixels) >= threshold;
    }

    /// <summary>Shortest duration of a clip after trimming: one frame.</summary>
    /// <param name="frameRate">Frames per second of the media being trimmed.</param>
    public static double MinimumDuration(double frameRate)
        => double.IsFinite(frameRate) && frameRate > 0 ? 1d / frameRate : 1d / 30d;

    /// <summary>
    /// Clamps a trim delta under Regular rules: it may not exceed the media handle (the start/end
    /// of the source file), may not cut into the media of an adjacent clip from the same
    /// source file, and may not trim below one frame.
    /// </summary>
    /// <param name="clips">Clip list of the timeline.</param>
    /// <param name="clipIndex">Position of the clip being trimmed.</param>
    /// <param name="edge">Edge being trimmed.</param>
    /// <param name="delta">Shift amount in timeline time (seconds).</param>
    /// <param name="mediaDuration">Duration of the clip's source file (0 = unknown).</param>
    /// <param name="minDuration">Minimum duration (one frame).</param>
    /// <returns>The clamped delta; 0 means the clip cannot be trimmed.</returns>
    public static double ClampTrim(
        IReadOnlyList<EditorTimelineClip> clips,
        int clipIndex,
        EditorTimelineTrimEdge edge,
        double delta,
        double mediaDuration,
        double minDuration)
    {
        if (clips is null || clipIndex < 0 || clipIndex >= clips.Count) return 0;
        if (!double.IsFinite(delta)) return 0;
        var clip = clips[clipIndex];
        var duration = Math.Max(0, clip.Duration);
        var minimum = double.IsFinite(minDuration) && minDuration > 0 ? minDuration : 0;
        var media = double.IsFinite(mediaDuration) && mediaDuration > 0 ? mediaDuration : double.PositiveInfinity;

        if (edge == EditorTimelineTrimEdge.In)
        {
            // Leading media handle: may not take media before the current in point.
            var low = -Math.Max(0, clip.SourceIn);
            // May not trim below one frame.
            var high = duration - minimum;
            // Regular trim: the In edge may not cut into the media of the previous clip.
            // Only applied when the previous clip lies entirely before this clip in the same
            // source file; if the two clips overlap, the user picks the media themselves.
            if (clipIndex > 0 && SameSource(clips[clipIndex - 1], clip))
            {
                var previousEnd = clips[clipIndex - 1].End;
                if (previousEnd <= clip.SourceIn)
                    low = Math.Max(low, previousEnd - clip.SourceIn);
            }
            return Math.Clamp(delta, low, Math.Max(low, high));
        }

        // Out edge: may not trim below one frame and may not exceed
        // the trailing media handle of the file.
        var outLow = -(duration - minimum);
        var outHigh = Math.Max(0, media - clip.End);
        // Regular trim: the Out edge may not cut into the media of the following clip.
        if (clipIndex + 1 < clips.Count && SameSource(clips[clipIndex + 1], clip))
        {
            var nextSourceIn = clips[clipIndex + 1].SourceIn;
            if (nextSourceIn >= clip.End)
                outHigh = Math.Min(outHigh, nextSourceIn - clip.End);
        }
        return Math.Clamp(delta, outLow, Math.Max(outLow, outHigh));
    }

    /// <summary>
    /// Shifts one edge of a clip by delta: the In edge moves the in point in the source file
    /// and shortens the duration, the Out edge only changes the duration. The remaining media
    /// is always one contiguous stretch of the source file.
    /// </summary>
    /// <param name="clip">Clip being trimmed.</param>
    /// <param name="edge">Edge being trimmed.</param>
    /// <param name="delta">Shift amount in timeline time (seconds).</param>
    public static EditorTimelineClip MoveEdge(EditorTimelineClip clip, EditorTimelineTrimEdge edge, double delta)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (!double.IsFinite(delta) || delta == 0) return clip;
        return edge == EditorTimelineTrimEdge.In
            ? clip with { SourceIn = clip.SourceIn + delta, Duration = Math.Max(0, clip.Duration - delta) }
            : clip with { Duration = Math.Max(0, clip.Duration + delta) };
    }

    /// <summary>
    /// Trims one edge of a clip on the timeline: clamp the delta under Regular rules then
    /// apply it to the clip, returning a new clip (the old clip is kept if no trim is possible).
    /// </summary>
    /// <param name="clips">Clip list of the timeline.</param>
    /// <param name="clipIndex">Position of the clip being trimmed.</param>
    /// <param name="edge">Edge being trimmed.</param>
    /// <param name="delta">Shift amount in timeline time (seconds).</param>
    /// <param name="mediaDuration">Duration of the clip's source file (0 = unknown).</param>
    /// <param name="minDuration">Minimum duration (one frame).</param>
    public static EditorTimelineClip Trim(
        IReadOnlyList<EditorTimelineClip> clips,
        int clipIndex,
        EditorTimelineTrimEdge edge,
        double delta,
        double mediaDuration,
        double minDuration)
    {
        ArgumentNullException.ThrowIfNull(clips);
        if (clipIndex < 0 || clipIndex >= clips.Count)
            throw new ArgumentOutOfRangeException(nameof(clipIndex));
        var clamped = ClampTrim(clips, clipIndex, edge, delta, mediaDuration, minDuration);
        return clamped == 0 ? clips[clipIndex] : MoveEdge(clips[clipIndex], edge, clamped);
    }

    private static bool SameSource(EditorTimelineClip left, EditorTimelineClip right)
        => string.Equals(left.SourcePath, right.SourcePath, StringComparison.OrdinalIgnoreCase);
}
