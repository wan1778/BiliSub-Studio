namespace BiliSubStudio.Core.Editor;

/// <summary>
/// One step in the timeline history: the clip list state before and
/// after the operation. Clips are immutable records, so capturing the list
/// is enough to undo, without needing to know which operation caused the change.
/// </summary>
/// <param name="Before">Clip list before the operation.</param>
/// <param name="After">Clip list after the operation.</param>
public sealed record EditorTimelineHistoryEntry(
    IReadOnlyList<EditorTimelineClip> Before,
    IReadOnlyList<EditorTimelineClip> After);

/// <summary>
/// Undo/redo history of the composition timeline. The history is pure (independent of
/// UI): every timeline operation records the before/after state of the clip
/// list, undo re-applies the previous state and redo re-applies the state
/// after. The number of steps is bounded, and a new operation drops the leftover redo
/// tail.
/// </summary>
public sealed class EditorTimelineHistory
{
    /// <summary>Maximum number of steps kept in the history.</summary>
    public const int DefaultMaxSteps = 50;

    private readonly List<EditorTimelineHistoryEntry> _entries = [];
    private int _cursor;

    /// <summary>
    /// Creates a history with a given maximum number of steps.
    /// </summary>
    /// <param name="maxSteps">Maximum number of steps kept (at least 1).</param>
    public EditorTimelineHistory(int maxSteps = DefaultMaxSteps)
    {
        MaxSteps = double.IsFinite(maxSteps) && maxSteps >= 1 ? (int)maxSteps : DefaultMaxSteps;
    }

    /// <summary>Maximum number of steps kept in the history.</summary>
    public int MaxSteps { get; }

    /// <summary>Number of steps currently in the history.</summary>
    public int Count => _entries.Count;

    /// <summary>Whether an operation on the timeline can be undone.</summary>
    public bool CanUndo => _cursor > 0;

    /// <summary>Whether an operation on the timeline can be redone.</summary>
    public bool CanRedo => _cursor < _entries.Count;

    /// <summary>
    /// Records an operation in the history. A new operation drops the leftover redo
    /// tail, and the oldest step is pushed out when the limit is exceeded. An operation
    /// that does not change the clip list records nothing.
    /// </summary>
    /// <param name="before">Clip list before the operation.</param>
    /// <param name="after">Clip list after the operation.</param>
    public void Push(IReadOnlyList<EditorTimelineClip> before, IReadOnlyList<EditorTimelineClip> after)
    {
        var previous = Snapshot(before);
        var next = Snapshot(after);
        if (SameClips(previous, next)) return;
        if (_cursor < _entries.Count) _entries.RemoveRange(_cursor, _entries.Count - _cursor);
        _entries.Add(new EditorTimelineHistoryEntry(previous, next));
        if (_entries.Count > MaxSteps) _entries.RemoveRange(0, _entries.Count - MaxSteps);
        _cursor = _entries.Count;
    }

    /// <summary>
    /// Undoes one operation: returns the clip list from before the most
    /// recent operation, null when there is nothing left to undo.
    /// </summary>
    public IReadOnlyList<EditorTimelineClip>? Undo()
    {
        if (!CanUndo) return null;
        _cursor--;
        return _entries[_cursor].Before;
    }

    /// <summary>
    /// Redoes an undone operation: returns the clip list from after the operation,
    /// null when there is nothing left to redo.
    /// </summary>
    public IReadOnlyList<EditorTimelineClip>? Redo()
    {
        if (!CanRedo) return null;
        var entry = _entries[_cursor];
        _cursor++;
        return entry.After;
    }

    /// <summary>Clears the whole history (for example when opening a different project).</summary>
    public void Clear()
    {
        _entries.Clear();
        _cursor = 0;
    }

    private static IReadOnlyList<EditorTimelineClip> Snapshot(IReadOnlyList<EditorTimelineClip>? clips)
    {
        if (clips is null || clips.Count == 0) return [];
        var copy = new EditorTimelineClip[clips.Count];
        for (var index = 0; index < clips.Count; index++) copy[index] = clips[index];
        return copy;
    }

    private static bool SameClips(IReadOnlyList<EditorTimelineClip> left, IReadOnlyList<EditorTimelineClip> right)
    {
        if (left.Count != right.Count) return false;
        for (var index = 0; index < left.Count; index++)
            if (!left[index].Equals(right[index])) return false;
        return true;
    }
}
