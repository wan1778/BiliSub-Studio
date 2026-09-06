namespace BiliSubStudio.Core.Timeline;

// G6 multi-clip operations (MASTER_PLAN section 8). Every operation is a
// pure function returning the new clip list; callers persist atomically.
// Clip-local dependency items (G7) will ride the same reflow helpers; the
// video track itself only ever moves/reflows contiguous hard cuts.
public static class TimelineSequenceOps
{
    // Add appends at the end (cursor = current total duration).
    public static IReadOnlyList<TimelineClip> Add(
        IReadOnlyList<TimelineClip> clips,
        string assetId,
        long sourceInUs,
        long sourceOutUs,
        double playbackRate = 1.0)
    {
        ArgumentNullException.ThrowIfNull(clips);
        var ordered = OrderAndValidate(clips);
        var start = ordered.Count == 0 ? 0 : ordered[^1].TimelineEndUs;
        var appended = new List<TimelineClip>(ordered) { TimelineClip.Create(assetId, start, sourceInUs, sourceOutUs, playbackRate) };
        TimelineSequence.ValidateSequential(appended);
        return appended;
    }

    // Reorder moves a clip to a new zero-based position in play order and
    // reflows everything contiguously. One undoable transaction is the
    // caller's concern; this is a pure reflow.
    public static IReadOnlyList<TimelineClip> Reorder(
        IReadOnlyList<TimelineClip> clips,
        string clipId,
        int newPosition)
    {
        ArgumentNullException.ThrowIfNull(clips);
        var ordered = OrderAndValidate(clips);
        var index = ordered.FindIndex(clip => string.Equals(clip.Id, clipId, StringComparison.Ordinal));
        if (index < 0) throw new InvalidDataException("Timeline clip không tồn tại để đổi thứ tự.");
        if (newPosition < 0 || newPosition >= ordered.Count)
            throw new InvalidDataException("Vị trí đổi thứ tự clip không hợp lệ.");
        var moved = ordered[index];
        ordered.RemoveAt(index);
        ordered.Insert(newPosition, moved);
        return TimelineSequence.ReflowContiguous(ordered);
    }

    // Trim narrows the clip's source range, keeping its timeline start. The
    // remainder of the sequence reflows to close the gap.
    public static IReadOnlyList<TimelineClip> Trim(
        IReadOnlyList<TimelineClip> clips,
        string clipId,
        long newSourceInUs,
        long newSourceOutUs)
    {
        ArgumentNullException.ThrowIfNull(clips);
        var ordered = OrderAndValidate(clips);
        var index = ordered.FindIndex(clip => string.Equals(clip.Id, clipId, StringComparison.Ordinal));
        if (index < 0) throw new InvalidDataException("Timeline clip không tồn tại để trim.");
        var trimmed = ordered[index] with { SourceInUs = newSourceInUs, SourceOutUs = newSourceOutUs };
        trimmed.ValidateShape();
        ordered[index] = trimmed;
        return TimelineSequence.ReflowContiguous(ordered);
    }

    // Split cuts one clip at a timeline position inside it. The right half
    // keeps the same asset with an advanced SourceIn; both halves reflow
    // contiguously with no overlap and no gap. Undo restores both halves.
    public static IReadOnlyList<TimelineClip> Split(
        IReadOnlyList<TimelineClip> clips,
        string clipId,
        long cutSequenceUs)
    {
        ArgumentNullException.ThrowIfNull(clips);
        var ordered = OrderAndValidate(clips);
        var index = ordered.FindIndex(clip => string.Equals(clip.Id, clipId, StringComparison.Ordinal));
        if (index < 0) throw new InvalidDataException("Timeline clip không tồn tại để cắt.");
        var clip = ordered[index];
        var offset = cutSequenceUs - clip.TimelineStartUs;
        if (offset <= 0 || offset >= clip.DurationUs)
            throw new InvalidDataException("Vị trí cắt phải nằm bên trong clip.");
        var sourceCutUs = TimelineTime.SequenceToSourceUs(
            cutSequenceUs, clip.TimelineStartUs, clip.SourceInUs, clip.PlaybackRate);
        var left = clip with { SourceOutUs = sourceCutUs };
        var right = clip with
        {
            Id = TimelineClip.NewId(),
            SourceInUs = sourceCutUs,
        };
        left.ValidateShape();
        right.ValidateShape();
        ordered[index] = left;
        ordered.Insert(index + 1, right);
        return TimelineSequence.ReflowContiguous(ordered);
    }

    // Delete removes one clip; the remainder reflows to close the gap.
    public static IReadOnlyList<TimelineClip> Delete(
        IReadOnlyList<TimelineClip> clips,
        string clipId)
    {
        ArgumentNullException.ThrowIfNull(clips);
        var ordered = OrderAndValidate(clips);
        var index = ordered.FindIndex(clip => string.Equals(clip.Id, clipId, StringComparison.Ordinal));
        if (index < 0) throw new InvalidDataException("Timeline clip không tồn tại để xóa.");
        ordered.RemoveAt(index);
        if (ordered.Count == 0) return [];
        return TimelineSequence.ReflowContiguous(ordered);
    }

    private static List<TimelineClip> OrderAndValidate(IReadOnlyList<TimelineClip> clips)
    {
        var ordered = clips.OrderBy(clip => clip.TimelineStartUs).ToList();
        foreach (var clip in ordered) clip.ValidateShape();
        TimelineSequence.ValidateSequential(ordered);
        return ordered;
    }
}

// G6 bounded undo/redo history of immutable clip-list snapshots. Every edit
// commits exactly one entry (MASTER_PLAN: a move is one undoable
// transaction); undo/redo returns whole dependency sets atomically.
public sealed class TimelineCommandHistory
{
    private readonly List<IReadOnlyList<TimelineClip>> _undo = [];
    private readonly List<IReadOnlyList<TimelineClip>> _redo = [];
    private readonly object _gate = new();

    public const int MaxEntries = 200;

    public int UndoCount { get { lock (_gate) return _undo.Count; } }
    public int RedoCount { get { lock (_gate) return _redo.Count; } }

    public void Commit(IReadOnlyList<TimelineClip> before, IReadOnlyList<TimelineClip> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        lock (_gate)
        {
            _undo.Add(before);
            if (_undo.Count > MaxEntries) _undo.RemoveAt(0);
            _redo.Clear();
        }
    }

    public IReadOnlyList<TimelineClip> Undo(IReadOnlyList<TimelineClip> current)
    {
        ArgumentNullException.ThrowIfNull(current);
        lock (_gate)
        {
            if (_undo.Count == 0) return current;
            var previous = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);
            _redo.Add(current);
            return previous;
        }
    }

    public IReadOnlyList<TimelineClip> Redo(IReadOnlyList<TimelineClip> current)
    {
        ArgumentNullException.ThrowIfNull(current);
        lock (_gate)
        {
            if (_redo.Count == 0) return current;
            var next = _redo[^1];
            _redo.RemoveAt(_redo.Count - 1);
            _undo.Add(current);
            return next;
        }
    }
}
