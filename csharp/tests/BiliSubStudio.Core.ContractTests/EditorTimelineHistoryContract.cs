using BiliSubStudio.Core.Editor;

namespace BiliSubStudio.Core.ContractTests;

// Timeline undo/redo history: each operation records before/after states of the clip list, undo
// re-applies the before state, redo re-applies the after state, a new operation clears leftover redo
// entries, and the step count is bounded.
internal static class EditorTimelineHistoryContract
{
    public static Task RunAsync()
    {
        var a = new EditorTimelineClip("a", "E:\\video\\a.mp4", 0, 10, "A");
        var b = new EditorTimelineClip("b", "E:\\video\\b.mp4", 0, 20, "B");
        var c = new EditorTimelineClip("c", "E:\\video\\c.mp4", 0, 30, "C");

        var history = new EditorTimelineHistory();
        Require(!history.CanUndo && !history.CanRedo, "a fresh history must have nothing to undo or redo");
        Require(history.Undo() is null, "undoing an empty history must return nothing");
        Require(history.Redo() is null, "redoing an empty history must return nothing");

        // Adding a clip: before = [a], after = [a, b].
        var beforeAdd = new[] { a };
        var afterAdd = new[] { a, b };
        history.Push(beforeAdd, afterAdd);
        Require(history.CanUndo && !history.CanRedo, "a pushed action must be undoable and not redoable");
        Require(history.Count == 1, "the history must hold exactly one step");

        var undone = history.Undo();
        Require(undone is not null && undone.Count == 1 && undone[0].Id == "a",
            "undo must restore the clip list from before the action");
        Require(!history.CanUndo && history.CanRedo, "after an undo the action must be redoable");

        var redone = history.Redo();
        Require(redone is not null && redone.Count == 2 && redone[1].Id == "b",
            "redo must restore the clip list from after the action");
        Require(history.CanUndo && !history.CanRedo, "after a redo the action must be undoable again");

        // A new operation clears leftover redo entries.
        history.Undo();
        Require(history.CanRedo, "the undone action must still be redoable");
        history.Push(undone!, new[] { c });
        Require(!history.CanRedo, "a new action must drop the pending redo");
        Require(history.Count == 1, "a new action must drop the undone steps from the history");

        // An edge trim keeps the id but changes duration: it still has to be recorded.
        var trimmed = a with { Duration = 5 };
        var trimHistory = new EditorTimelineHistory();
        trimHistory.Push(new[] { a }, new[] { trimmed });
        Require(trimHistory.Count == 1, "a trim that keeps the clip id must still be recorded");
        var trimUndo = trimHistory.Undo();
        Require(trimUndo is not null && Close(trimUndo[0].Duration, 10),
            "undoing a trim must restore the original duration");
        var trimRedo = trimHistory.Redo();
        Require(trimRedo is not null && Close(trimRedo[0].Duration, 5),
            "redoing a trim must restore the trimmed duration");

        // An operation that changes nothing is not recorded in history.
        var noop = new EditorTimelineHistory();
        noop.Push(new[] { a }, new[] { a });
        Require(noop.Count == 0, "an action that changes nothing must not be recorded");

        // Bounded step count: the oldest step is pushed out.
        var bounded = new EditorTimelineHistory(maxSteps: 3);
        var list = new List<EditorTimelineClip> { a };
        for (var index = 0; index < 5; index++)
        {
            var next = new List<EditorTimelineClip>(list) { new($"x{index}", "E:\\video\\x.mp4", 0, 1, "X") };
            bounded.Push(list.ToArray(), next.ToArray());
            list = next;
        }
        Require(bounded.Count == 3, "the history must not grow past its step limit");
        Require(bounded.MaxSteps == 3, "the history must report its step limit");

        // Clear the history when opening another project.
        bounded.Clear();
        Require(!bounded.CanUndo && !bounded.CanRedo && bounded.Count == 0,
            "clearing the history must drop every step");

        // History must snapshot the list: mutating the source list afterwards must not change
        // history.
        var snapshotHistory = new EditorTimelineHistory();
        var mutable = new List<EditorTimelineClip> { a };
        snapshotHistory.Push(mutable.ToArray(), new[] { a, b });
        mutable.Add(c);
        var snapshotUndo = snapshotHistory.Undo();
        Require(snapshotUndo is not null && snapshotUndo.Count == 1,
            "the history must keep its own copy of the clip list");

        return Task.CompletedTask;
    }

    private static bool Close(double left, double right) => Math.Abs(left - right) < .0005;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
