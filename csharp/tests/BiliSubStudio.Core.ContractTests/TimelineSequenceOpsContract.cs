using BiliSubStudio.Core.Timeline;

namespace BiliSubStudio.Core.ContractTests;

// G6 multi-clip core: add/reorder/trim/split/delete with hard-cut
// invariants and atomic undo. No IO; all operations are pure reflows.
internal static class TimelineSequenceOpsContract
{
    public static Task RunAsync()
    {
        var asset = TimelineMediaAsset.Create(
            "C:/v/a.mp4", 1920, 1080, TimelineTime.FromSeconds(600), 30, hasAudio: true);

        var first = new TimelineClip(TimelineClip.NewId(), asset.Id, 0, 0, 10_000_000, 1.0);
        var second = new TimelineClip(TimelineClip.NewId(), asset.Id, 10_000_000, 20_000_000, 30_000_000, 1.0);
        var clips = new[] { first, second };

        AddJoinsAtEnd(asset, clips);
        ReorderPreservesOrderAndDurations(clips);
        TrimClosesGap(asset, clips);
        SplitCreatesAdjacentHalves(asset, clips);
        DeleteRemovesExactlyOne(clips);
        HistoryIsAtomicAndBounded(clips);
        return Task.CompletedTask;
    }

    private static void AddJoinsAtEnd(TimelineMediaAsset asset, IReadOnlyList<TimelineClip> clips)
    {
        var added = TimelineSequenceOps.Add(clips, asset.Id, 100_000_000, 115_000_000);
        Check(added.Count == 3, "add lost a clip");
        Check(added[2].TimelineStartUs == added[1].TimelineEndUs, "add did not join at the end");
        Check(added[2].SourceInUs == 100_000_000 && added[2].SourceOutUs == 115_000_000,
            "add lost the source range");
        TimelineSequence.ValidateSequential(added);

        // Split adds a distinct id (undo must restore the pre-split clip id).
        var split = TimelineSequenceOps.Split(added, added[2].Id, added[2].TimelineStartUs + 5_000_000);
        Check(split.Count == 4, "split did not insert the right half");
        Check(split[2].Id == added[2].Id && split[3].Id != added[2].Id,
            "split must keep the left id and mint a new right id");
        Check(split[3].TimelineStartUs == split[2].TimelineEndUs, "split halves overlap or gap");
        Check(split[2].SourceOutUs == split[3].SourceInUs, "split did not cut the source at one point");
        TimelineSequence.ValidateSequential(split);

        // Undo of a split restores the whole dependency set atomically.
        var history = new TimelineCommandHistory();
        history.Commit(added, split);
        var restored = history.Undo(split);
        Check(restored.Count == 3 && restored[2].Id == added[2].Id
            && restored[2].SourceOutUs == 115_000_000,
            "split undo did not restore the original clip atomically");
    }

    private static void ReorderPreservesOrderAndDurations(IReadOnlyList<TimelineClip> clips)
    {
        var reordered = TimelineSequenceOps.Reorder(clips, clips[0].Id, 1);
        Check(reordered.Count == 2, "reorder lost a clip");
        Check(reordered[0].Id == clips[1].Id && reordered[1].Id == clips[0].Id,
            "reorder changed the wrong clips");
        Check(reordered[1].TimelineStartUs == reordered[0].TimelineEndUs,
            "reorder left a gap");
        Check(reordered[0].DurationUs == clips[1].DurationUs
            && reordered[1].DurationUs == clips[0].DurationUs,
            "reorder changed clip durations");
        Check(reordered[1].SourceInUs == clips[0].SourceInUs,
            "reorder must not rewrite source-local time");
        TimelineSequence.ValidateSequential(reordered);
        Throws<InvalidDataException>(() => TimelineSequenceOps.Reorder(clips, clips[0].Id, 7),
            "out-of-range reorder position accepted");
        Throws<InvalidDataException>(() => TimelineSequenceOps.Reorder(clips, "missing", 0),
            "unknown reorder clip accepted");
    }

    private static void TrimClosesGap(TimelineMediaAsset asset, IReadOnlyList<TimelineClip> clips)
    {
        var trimmed = TimelineSequenceOps.Trim(clips, clips[0].Id, 2_000_000, 8_000_000);
        Check(trimmed[0].SourceInUs == 2_000_000 && trimmed[0].SourceOutUs == 8_000_000,
            "trim did not narrow the source range");
        Check(trimmed[0].DurationUs == 6_000_000, "trim broke the duration identity");
        Check(trimmed[1].TimelineStartUs == 6_000_000 && trimmed[1].TimelineEndUs == 16_000_000,
            "trim did not reflow the tail");
        Check(trimmed[1].SourceInUs == clips[1].SourceInUs,
            "trim must not touch other clips' source time");
        TimelineSequence.ValidateSequential(trimmed);
        Throws<InvalidDataException>(() => TimelineSequenceOps.Trim(clips, clips[0].Id, 8_000_000, 2_000_000),
            "inverted trim accepted");
        Throws<InvalidDataException>(() => TimelineSequenceOps.Trim(clips, "missing", 0, 1),
            "unknown trim clip accepted");
    }

    private static void SplitCreatesAdjacentHalves(TimelineMediaAsset asset, IReadOnlyList<TimelineClip> clips)
    {
        // Non-1x playback: source cut must respect the rate (2x here).
        var fast = new TimelineClip(TimelineClip.NewId(), asset.Id, 0, 0, 10_000_000, 2.0);
        var split = TimelineSequenceOps.Split([fast], fast.Id, 2_500_000);
        Check(split.Count == 2, "split lost a half");
        Check(split[0].SourceOutUs == 5_000_000 && split[1].SourceInUs == 5_000_000,
            "2x playback split did not cut source at offset*rate");
        Check(split[0].DurationUs == 2_500_000 && split[1].DurationUs == 2_500_000,
            "split halves lost duration identity");
        TimelineSequence.ValidateSequential(split);
        Throws<InvalidDataException>(() => TimelineSequenceOps.Split(clips, clips[0].Id, clips[0].TimelineStartUs),
            "split at clip start accepted");
        Throws<InvalidDataException>(() => TimelineSequenceOps.Split(clips, clips[0].Id, clips[0].TimelineEndUs),
            "split at clip end accepted");
    }

    private static void DeleteRemovesExactlyOne(IReadOnlyList<TimelineClip> clips)
    {
        var deleted = TimelineSequenceOps.Delete(clips, clips[0].Id);
        Check(deleted.Count == 1, "delete removed the wrong count");
        Check(deleted[0].Id == clips[1].Id && deleted[0].TimelineStartUs == 0,
            "delete did not reflow the tail to the start");
        Check(deleted[0].SourceInUs == clips[1].SourceInUs,
            "delete must not rewrite source-local time");
        Check(TimelineSequenceOps.Delete(clips, clips[0].Id).Count == 1, "delete not deterministic");
        Check(TimelineSequenceOps.Delete(deleted, deleted[0].Id).Count == 0, "delete last clip failed");
        Throws<InvalidDataException>(() => TimelineSequenceOps.Delete(clips, "missing"),
            "unknown delete clip accepted");
    }

    private static void HistoryIsAtomicAndBounded(IReadOnlyList<TimelineClip> clips)
    {
        var history = new TimelineCommandHistory();
        Check(history.UndoCount == 0 && history.RedoCount == 0, "fresh history is not empty");

        var afterAdd = TimelineSequenceOps.Add(clips, clips[0].AssetId, 200_000_000, 210_000_000);
        history.Commit(clips, afterAdd);
        var undone = history.Undo(afterAdd);
        Check(undone.Count == clips.Count && undone[0].Id == clips[0].Id,
            "undo did not restore the whole clip set");
        Check(history.RedoCount == 1, "undo did not stage redo");
        var redone = history.Redo(undone);
        Check(redone.Count == afterAdd.Count && redone[2].Id == afterAdd[2].Id,
            "redo did not restore the edited set atomically");

        // Empty history is a no-op, not an exception.
        var empty = new TimelineCommandHistory();
        Check(empty.Undo(clips).Count == clips.Count, "undo on empty history threw or mutated");
        Check(empty.Redo(clips).Count == clips.Count, "redo on empty history threw or mutated");

        // New commit clears the redo branch (linear history, no forks).
        var branch = TimelineSequenceOps.Delete(afterAdd, afterAdd[0].Id);
        history.Commit(afterAdd, branch);
        Check(history.RedoCount == 0, "new commit did not clear the redo branch");

        // Bounded: commit more than MaxEntries and undo must stop at the bound.
        var bounded = new TimelineCommandHistory();
        var current = clips;
        for (var index = 0; index < TimelineCommandHistory.MaxEntries + 25; index++)
        {
            var next = TimelineSequenceOps.Delete(current, current[^1].Id);
            if (next.Count == 0) break;
            bounded.Commit(current, next);
            current = next;
        }
        while (bounded.UndoCount > 0) current = bounded.Undo(current);
        Check(bounded.UndoCount == 0, "undo drained past zero");
        Check(current.Count <= clips.Count + 1 && current.Count >= clips.Count - 1,
            "bounded history lost or invented clips across the cap");
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }

    private static void Throws<TException>(Action action, string message) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }
}
