namespace BiliSubStudio.Core.Timeline;

public enum TimelineItemKind
{
    Effect = 0,
    Logo = 1,
    Subtitle = 2,
    Music = 3,
}

public enum TimelineSubtitleSplitPolicy
{
    Reject = 0,
    SplitAndClamp = 1,
}

public sealed record TimelineItemSplitResult(
    IReadOnlyList<TimelineItem> Items,
    IReadOnlyDictionary<string, string> RightItemIdsByOriginalId);

// Effect/subtitle and clip-logo times are relative to their owning clip.
// Music and logos without a ClipId use absolute sequence time.
public sealed record TimelineItem(
    string Id,
    TimelineItemKind Kind,
    string? ClipId,
    long ClipStartUs,
    long ClipEndUs,
    long SequenceStartUs,
    long SequenceEndUs,
    string Payload)
{
    public static string NewId() => Guid.NewGuid().ToString("N");

    public bool IsSequenceGlobal => Kind == TimelineItemKind.Music
        || (Kind is TimelineItemKind.Logo or TimelineItemKind.Subtitle && ClipId is null);

    public void Validate()
    {
        if (!IsId(Id))
            throw new InvalidDataException("Timeline item ID không hợp lệ.");
        if (!Enum.IsDefined(Kind))
            throw new InvalidDataException("Timeline item kind không hợp lệ.");
        if (string.IsNullOrWhiteSpace(Payload) || Payload.Length > 4096)
            throw new InvalidDataException("Timeline item payload không hợp lệ.");

        if (IsSequenceGlobal)
        {
            if (ClipId is not null)
                throw new InvalidDataException("Item sequence-global không được gắn clip.");
            if (SequenceStartUs < 0 || SequenceEndUs <= SequenceStartUs)
                throw new InvalidDataException("Sequence-global range không hợp lệ.");
            if (ClipStartUs != 0 || ClipEndUs != 0)
                throw new InvalidDataException("Item sequence-global không dùng clip-local time.");
            return;
        }

        if (!IsId(ClipId))
            throw new InvalidDataException("Item clip-local phải gắn một clip.");
        if (ClipStartUs < 0 || ClipEndUs <= ClipStartUs)
            throw new InvalidDataException("Clip-local range không hợp lệ.");
        if (SequenceStartUs != 0 || SequenceEndUs != 0)
            throw new InvalidDataException("Item clip-local không dùng sequence time.");
    }

    private static bool IsId(string? value) =>
        value is { Length: 32 } && value.All(char.IsAsciiHexDigit);
}

public static class TimelineItemOps
{
    public static IReadOnlyList<TimelineItem> ValidateAll(
        IReadOnlyList<TimelineItem> items,
        IReadOnlyList<TimelineClip> clips)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(clips);
        var clipsById = clips.ToDictionary(clip => clip.Id, StringComparer.Ordinal);
        var itemIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            item.Validate();
            if (!itemIds.Add(item.Id))
                throw new InvalidDataException("Timeline item trùng ID.");
            if (item.IsSequenceGlobal)
                continue;
            if (!clipsById.TryGetValue(item.ClipId!, out var clip))
                throw new InvalidDataException("Timeline item gắn clip không tồn tại.");
            if (item.ClipEndUs > clip.DurationUs)
                throw new InvalidDataException("Timeline item vượt quá duration của clip.");
        }
        return items;
    }

    // Reorder keeps local coordinates unchanged. For a narrowed clip this
    // also clamps crossing items and drops items entirely outside the clip.
    public static IReadOnlyList<TimelineItem> RideClips(
        IReadOnlyList<TimelineItem> items,
        IReadOnlyList<TimelineClip> clips)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(clips);
        var clipsById = clips.ToDictionary(clip => clip.Id, StringComparer.Ordinal);
        var result = new List<TimelineItem>(items.Count);
        foreach (var item in items)
        {
            item.Validate();
            if (item.IsSequenceGlobal)
            {
                result.Add(item);
                continue;
            }
            if (!clipsById.TryGetValue(item.ClipId!, out var clip))
                throw new InvalidDataException("Timeline item gắn clip không tồn tại.");
            if (item.ClipStartUs >= clip.DurationUs)
                continue;
            result.Add(item with { ClipEndUs = Math.Min(item.ClipEndUs, clip.DurationUs) });
        }
        ValidateAll(result, clips);
        return result;
    }

    public static IReadOnlyList<TimelineItem> DeleteItemsOfClip(
        IReadOnlyList<TimelineItem> items,
        string clipId)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items
            .Where(item => !string.Equals(item.ClipId, clipId, StringComparison.Ordinal))
            .ToArray();
    }

    public static IReadOnlyList<TimelineItem> TrimItems(
        IReadOnlyList<TimelineItem> items,
        TimelineClip oldClip,
        TimelineClip newClip)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(oldClip);
        ArgumentNullException.ThrowIfNull(newClip);
        if (!string.Equals(oldClip.Id, newClip.Id, StringComparison.Ordinal)
            || Math.Abs(oldClip.PlaybackRate - newClip.PlaybackRate) > double.Epsilon)
            throw new InvalidDataException("Trim dependency không khớp clip nguồn.");
        var removedHead = Math.Round(
            (newClip.SourceInUs - oldClip.SourceInUs) / oldClip.PlaybackRate,
            MidpointRounding.AwayFromZero);
        if (removedHead < long.MinValue || removedHead > long.MaxValue)
            throw new InvalidDataException("Trim dependency vượt phạm vi int64 microseconds.");
        var removedHeadUs = (long)removedHead;
        var result = new List<TimelineItem>(items.Count);
        foreach (var item in items)
        {
            item.Validate();
            if (item.IsSequenceGlobal
                || !string.Equals(item.ClipId, oldClip.Id, StringComparison.Ordinal))
            {
                result.Add(item);
                continue;
            }
            if (removedHeadUs >= 0 && item.ClipEndUs <= removedHeadUs)
                continue;
            var startUs = Math.Max(0, checked(item.ClipStartUs - removedHeadUs));
            var endUs = Math.Min(newClip.DurationUs, checked(item.ClipEndUs - removedHeadUs));
            if (startUs < endUs)
                result.Add(item with { ClipStartUs = startUs, ClipEndUs = endUs });
        }
        return result;
    }

    // cutLocalUs is measured in the old clip's sequence-local time. Effects
    // and clip logos crossing the cut split into two clamped items. A subtitle
    // crossing spoken content is rejected until the caller supplies a policy.
    public static IReadOnlyList<TimelineItem> SplitItems(
        IReadOnlyList<TimelineItem> items,
        string oldClipId,
        string leftClipId,
        string rightClipId,
        long cutLocalUs,
        TimelineSubtitleSplitPolicy subtitlePolicy = TimelineSubtitleSplitPolicy.Reject) =>
        SplitItemsWithOwnership(
            items, oldClipId, leftClipId, rightClipId, cutLocalUs, subtitlePolicy).Items;

    public static TimelineItemSplitResult SplitItemsWithOwnership(
        IReadOnlyList<TimelineItem> items,
        string oldClipId,
        string leftClipId,
        string rightClipId,
        long cutLocalUs,
        TimelineSubtitleSplitPolicy subtitlePolicy = TimelineSubtitleSplitPolicy.Reject)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (cutLocalUs <= 0)
            throw new InvalidDataException("Vị trí cắt item không hợp lệ.");
        if (!Enum.IsDefined(subtitlePolicy))
            throw new InvalidDataException("Subtitle split policy không hợp lệ.");
        var result = new List<TimelineItem>(items.Count + 2);
        var rightItemIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            item.Validate();
            if (item.IsSequenceGlobal
                || !string.Equals(item.ClipId, oldClipId, StringComparison.Ordinal))
            {
                result.Add(item);
                continue;
            }

            if (item.ClipEndUs <= cutLocalUs)
            {
                result.Add(item with { ClipId = leftClipId });
            }
            else if (item.ClipStartUs >= cutLocalUs)
            {
                result.Add(item with
                {
                    ClipId = rightClipId,
                    ClipStartUs = item.ClipStartUs - cutLocalUs,
                    ClipEndUs = item.ClipEndUs - cutLocalUs,
                });
            }
            else
            {
                if (item.Kind == TimelineItemKind.Subtitle
                    && subtitlePolicy == TimelineSubtitleSplitPolicy.Reject)
                    throw new InvalidOperationException(
                        "Subtitle đi qua điểm cắt cần explicit split policy.");
                result.Add(item with { ClipId = leftClipId, ClipEndUs = cutLocalUs });
                var rightId = TimelineItem.NewId();
                rightItemIds.Add(item.Id, rightId);
                result.Add(item with
                {
                    Id = rightId,
                    ClipId = rightClipId,
                    ClipStartUs = 0,
                    ClipEndUs = item.ClipEndUs - cutLocalUs,
                });
            }
        }
        return new TimelineItemSplitResult(result, rightItemIds);
    }

    // PDEC-004 recommendation: SRT attaches to the selected clip by default.
    public static TimelineItem AttachSubtitleToClip(
        string clipId,
        long clipStartUs,
        long clipEndUs,
        string srtPath)
    {
        var item = new TimelineItem(
            TimelineItem.NewId(),
            TimelineItemKind.Subtitle,
            clipId,
            clipStartUs,
            clipEndUs,
            0,
            0,
            srtPath);
        item.Validate();
        return item;
    }

    public static TimelineItem AttachSubtitleToSequence(
        long sequenceStartUs,
        long sequenceEndUs,
        string srtPath)
    {
        var item = new TimelineItem(
            TimelineItem.NewId(),
            TimelineItemKind.Subtitle,
            null,
            0,
            0,
            sequenceStartUs,
            sequenceEndUs,
            srtPath);
        item.Validate();
        return item;
    }
}

// One immutable state is the transaction boundary for a clip edit and all
// dependencies. This prevents callers from committing clips without items.
public sealed record TimelineEditState(
    IReadOnlyList<TimelineClip> Clips,
    IReadOnlyList<TimelineItem> Items)
{
    public static TimelineEditState Create(
        IReadOnlyList<TimelineClip> clips,
        IReadOnlyList<TimelineItem> items)
    {
        ArgumentNullException.ThrowIfNull(clips);
        ArgumentNullException.ThrowIfNull(items);
        var state = new TimelineEditState(
            clips.OrderBy(clip => clip.TimelineStartUs).ToArray(),
            items.ToArray());
        TimelineSequence.ValidateSequential(state.Clips);
        TimelineItemOps.ValidateAll(state.Items, state.Clips);
        return state;
    }
}

public static class TimelineDependencyOps
{
    public static TimelineEditState Reorder(TimelineEditState state, string clipId, int newPosition)
    {
        ArgumentNullException.ThrowIfNull(state);
        return TimelineEditState.Create(
            TimelineSequenceOps.Reorder(state.Clips, clipId, newPosition),
            state.Items);
    }

    public static TimelineEditState Split(
        TimelineEditState state,
        string clipId,
        long cutSequenceUs,
        TimelineSubtitleSplitPolicy subtitlePolicy = TimelineSubtitleSplitPolicy.Reject)
    {
        ArgumentNullException.ThrowIfNull(state);
        var oldClip = state.Clips.SingleOrDefault(
            clip => string.Equals(clip.Id, clipId, StringComparison.Ordinal))
            ?? throw new InvalidDataException("Timeline clip không tồn tại để cắt dependency.");
        var clips = TimelineSequenceOps.Split(state.Clips, clipId, cutSequenceUs);
        var leftClip = clips.Single(clip => string.Equals(clip.Id, clipId, StringComparison.Ordinal));
        var rightClip = clips.Single(clip => !state.Clips.Any(
            old => string.Equals(old.Id, clip.Id, StringComparison.Ordinal)));
        var items = TimelineItemOps.SplitItems(
            state.Items, clipId, clipId, rightClip.Id, leftClip.DurationUs, subtitlePolicy);
        return TimelineEditState.Create(clips, TimelineItemOps.RideClips(items, clips));
    }

    public static TimelineEditState Trim(
        TimelineEditState state,
        string clipId,
        long newSourceInUs,
        long newSourceOutUs)
    {
        ArgumentNullException.ThrowIfNull(state);
        var oldClip = state.Clips.SingleOrDefault(
            clip => string.Equals(clip.Id, clipId, StringComparison.Ordinal))
            ?? throw new InvalidDataException("Timeline clip không tồn tại để trim dependency.");
        var clips = TimelineSequenceOps.Trim(
            state.Clips, clipId, newSourceInUs, newSourceOutUs);
        var newClip = clips.Single(clip => string.Equals(clip.Id, clipId, StringComparison.Ordinal));
        var items = TimelineItemOps.TrimItems(state.Items, oldClip, newClip);
        return TimelineEditState.Create(clips, items);
    }

    public static TimelineEditState Delete(TimelineEditState state, string clipId)
    {
        ArgumentNullException.ThrowIfNull(state);
        return TimelineEditState.Create(
            TimelineSequenceOps.Delete(state.Clips, clipId),
            TimelineItemOps.DeleteItemsOfClip(state.Items, clipId));
    }
}

public sealed class TimelineEditHistory
{
    private readonly List<TimelineEditState> _undo = [];
    private readonly List<TimelineEditState> _redo = [];
    private readonly object _gate = new();

    public const int MaxEntries = 200;

    public int UndoCount { get { lock (_gate) return _undo.Count; } }
    public int RedoCount { get { lock (_gate) return _redo.Count; } }

    public void Commit(TimelineEditState before, TimelineEditState after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        _ = TimelineEditState.Create(after.Clips, after.Items);
        lock (_gate)
        {
            _undo.Add(TimelineEditState.Create(before.Clips, before.Items));
            if (_undo.Count > MaxEntries)
                _undo.RemoveAt(0);
            _redo.Clear();
        }
    }

    public TimelineEditState Undo(TimelineEditState current)
    {
        ArgumentNullException.ThrowIfNull(current);
        lock (_gate)
        {
            if (_undo.Count == 0)
                return current;
            var previous = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);
            _redo.Add(TimelineEditState.Create(current.Clips, current.Items));
            return previous;
        }
    }

    public TimelineEditState Redo(TimelineEditState current)
    {
        ArgumentNullException.ThrowIfNull(current);
        lock (_gate)
        {
            if (_redo.Count == 0)
                return current;
            var next = _redo[^1];
            _redo.RemoveAt(_redo.Count - 1);
            _undo.Add(TimelineEditState.Create(current.Clips, current.Items));
            return next;
        }
    }
}
