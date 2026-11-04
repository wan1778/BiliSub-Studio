namespace BiliSubStudio.Core.Editor;

/// <summary>
/// Timeline layout while dragging a clip body: the new order of the clips (by original
/// clip index), the start time of each clip in that order, and the
/// insertion marker time. The spot of the dragged clip is a gap, so the insertion marker
/// sits exactly on the boundary the clip will occupy after the drop.
/// </summary>
/// <param name="Order">Original clip index in the new timeline order.</param>
/// <param name="Starts">Start time of each clip in the new order.</param>
/// <param name="InsertionStart">Start time of the clip being dragged.</param>
public sealed record EditorTimelineClipMoveLayout(
    IReadOnlyList<int> Order,
    IReadOnlyList<double> Starts,
    double InsertionStart);

/// <summary>
/// Dragging a clip body on the timeline to reorder it. The timeline is a contiguous
/// sequence of clips, so "changing the start time" of a clip is precisely changing its
/// position in the sequence: the clip is inserted before or after an adjacent clip. The math lives in Core
/// so UI only calls into it, exactly like edge trimming and snapping.
/// </summary>
public static class EditorTimelineClipMove
{
    /// <summary>
    /// Clip body drag threshold, in screen pixels. Below this threshold a
    /// click only selects the clip, it does not drag.
    /// </summary>
    public const double DragThresholdPixels = 5;

    /// <summary>
    /// Insertion position of the dragged clip at the pointer X coordinate: the number of clips (not
    /// counting the dragged clip) whose midpoints the pointer has passed. The insertion position
    /// is computed on the currently displayed timeline layout: the pointer must pass the midpoint of an
    /// adjacent clip for the dragged clip to swap places, so dragging back to exactly its old slot
    /// changes nothing and records no history.
    /// </summary>
    /// <param name="clips">Clip list of the timeline.</param>
    /// <param name="clipIndex">Position of the dragged clip.</param>
    /// <param name="contentX">Pointer X coordinate within the timeline content (px).</param>
    /// <param name="pixelsPerSecond">Current pixel density of the timeline.</param>
    public static int InsertionIndex(
        IReadOnlyList<EditorTimelineClip> clips,
        int clipIndex,
        double contentX,
        double pixelsPerSecond)
    {
        if (clips is null || clips.Count == 0 || clipIndex < 0 || clipIndex >= clips.Count) return 0;
        if (!double.IsFinite(contentX)) return clipIndex;
        var time = EditorTimelineLayout.TimeForX(contentX, pixelsPerSecond);
        var cursor = 0d;
        var insertion = 0;
        for (var index = 0; index < clips.Count; index++)
        {
            var duration = Math.Max(0, clips[index].Duration);
            if (index == clipIndex)
            {
                cursor += duration;
                continue;
            }
            if (time >= cursor + duration / 2) insertion++;
            cursor += duration;
        }
        return insertion;
    }

    /// <summary>
    /// Timeline time (seconds) of the insertion marker: the insertion boundary in the
    /// layout AFTER inserting, i.e. the position the dragged clip will occupy in the new
    /// sequence. The insertion marker therefore always sits exactly where the clip will
    /// end up after the drop, not where the collapsed layout put it.
    /// </summary>
    /// <param name="clips">Clip list of the timeline.</param>
    /// <param name="clipIndex">Position of the dragged clip.</param>
    /// <param name="insertionIndex">Insertion position in the sequence with the dragged clip removed.</param>
    public static double InsertionBoundaryTime(
        IReadOnlyList<EditorTimelineClip> clips,
        int clipIndex,
        int insertionIndex)
        => Preview(clips, clipIndex, insertionIndex).InsertionStart;

    /// <summary>
    /// Timeline layout after applying the new insertion position: the new order of the clips and
    /// the start time of each clip. The dragged clip's spot is a gap, so
    /// dropping into it leaves the layout unchanged relative to the layout the user was just
    /// seeing.
    /// </summary>
    /// <param name="clips">Clip list of the timeline.</param>
    /// <param name="clipIndex">Position of the dragged clip.</param>
    /// <param name="insertionIndex">Insertion position in the sequence with the dragged clip removed.</param>
    public static EditorTimelineClipMoveLayout Preview(
        IReadOnlyList<EditorTimelineClip> clips,
        int clipIndex,
        int insertionIndex)
    {
        if (clips is null || clips.Count == 0 || clipIndex < 0 || clipIndex >= clips.Count)
            return new EditorTimelineClipMoveLayout([], [], 0);
        var order = MovedOrder(clips.Count, clipIndex, insertionIndex);
        var moved = new List<EditorTimelineClip>(clips.Count);
        foreach (var index in order) moved.Add(clips[index]);
        var starts = EditorTimelineComposition.Starts(moved);
        var target = Math.Clamp(insertionIndex, 0, clips.Count - 1);
        return new EditorTimelineClipMoveLayout(order, starts, starts[target]);
    }

    /// <summary>
    /// Moves a clip to the new insertion position in the sequence. The insertion position is
    /// counted on the sequence with the dragged clip removed, so the original list is
    /// returned when the clip does not change places.
    /// </summary>
    /// <param name="clips">Clip list of the timeline.</param>
    /// <param name="clipIndex">Position of the dragged clip.</param>
    /// <param name="insertionIndex">Insertion position in the sequence with the dragged clip removed.</param>
    public static IReadOnlyList<EditorTimelineClip> Move(
        IReadOnlyList<EditorTimelineClip> clips,
        int clipIndex,
        int insertionIndex)
    {
        ArgumentNullException.ThrowIfNull(clips);
        if (clipIndex < 0 || clipIndex >= clips.Count) return clips;
        var order = MovedOrder(clips.Count, clipIndex, insertionIndex);
        if (order[clipIndex] == clipIndex) return clips;
        var result = new List<EditorTimelineClip>(clips.Count);
        foreach (var index in order) result.Add(clips[index]);
        return result;
    }

    /// <summary>
    /// New order of the clips after applying the insertion position: the old
    /// sequence minus the dragged clip, then that clip inserted at the new position.
    /// </summary>
    private static IReadOnlyList<int> MovedOrder(int clipCount, int clipIndex, int insertionIndex)
    {
        var order = new List<int>(clipCount);
        for (var index = 0; index < clipCount; index++)
        {
            if (index != clipIndex) order.Add(index);
        }
        var target = Math.Clamp(insertionIndex, 0, Math.Max(0, clipCount - 1));
        order.Insert(target, clipIndex);
        return order;
    }
}
