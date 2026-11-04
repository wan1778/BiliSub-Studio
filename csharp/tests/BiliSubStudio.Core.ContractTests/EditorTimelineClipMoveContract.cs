using BiliSubStudio.Core.Editor;

namespace BiliSubStudio.Core.ContractTests;

// Dragging a clip body to reorder the timeline: the insertion position is computed from the
// layout currently displayed, the insertion line sits exactly on the insert boundary in the layout
// AFTER inserting, and dragging back to the old slot changes nothing.
internal static class EditorTimelineClipMoveContract
{
    public static Task RunAsync()
    {
        var a = new EditorTimelineClip("a", "E:\\video\\a.mp4", 0, 10, "A");
        var b = new EditorTimelineClip("b", "E:\\video\\b.mp4", 0, 10, "B");
        var c = new EditorTimelineClip("c", "E:\\video\\c.mp4", 0, 10, "C");
        var clips = new[] { a, b, c };
        const double pps = 10;

        // The clip-body drag threshold lives in Core so a light click does not drag.
        Require(EditorTimelineClipMove.DragThresholdPixels == 5d,
            "the clip body drag threshold must be 5 screen pixels");

        // Insertion position: the cursor must pass through the middle of a neighbouring clip before
        // the dragged clip swaps places, so dragging back to exactly the old slot changes nothing.
        Require(EditorTimelineClipMove.InsertionIndex(clips, 0, 5 * pps, pps) == 0,
            "a pointer before the middle of the next clip must keep the dragged clip in place");
        Require(EditorTimelineClipMove.InsertionIndex(clips, 0, 15 * pps, pps) == 1,
            "a pointer past the middle of the next clip must insert the dragged clip after it");
        Require(EditorTimelineClipMove.InsertionIndex(clips, 0, 25 * pps, pps) == 2,
            "a pointer past every other clip must insert the dragged clip at the end");
        Require(EditorTimelineClipMove.InsertionIndex(clips, 0, 1 * pps, pps) == 0,
            "a pointer inside the dragged clip must keep the dragged clip in place");

        // Drag the last clip to the front.
        Require(EditorTimelineClipMove.InsertionIndex(clips, 2, 2 * pps, pps) == 0,
            "a pointer near the start must insert the dragged clip first");
        Require(EditorTimelineClipMove.InsertionIndex(clips, 2, 12 * pps, pps) == 1,
            "a pointer past the middle of the first clip must insert after it");

        // The insertion line sits exactly on the insert boundary in the layout AFTER inserting: the
        // position the dragged clip will occupy in the new sequence.
        Require(Close(EditorTimelineClipMove.InsertionBoundaryTime(clips, 0, 1), 10),
            "inserting the first clip in the middle must draw the guide where that clip will start");
        Require(Close(EditorTimelineClipMove.InsertionBoundaryTime(clips, 0, 2), 20),
            "inserting the first clip at the end must draw the guide at the end of the other clips");
        Require(Close(EditorTimelineClipMove.InsertionBoundaryTime(clips, 2, 0), 0),
            "inserting the last clip first must draw the guide at the start of the strip");
        Require(Close(EditorTimelineClipMove.InsertionBoundaryTime(clips, 2, 1), 10),
            "inserting the last clip in the middle must draw the guide where that clip will start");
        Require(Close(EditorTimelineClipMove.InsertionBoundaryTime(clips, 1, 1), 10),
            "a clip that stays on its own slot must keep the guide on its own start");

        // Layout after inserting: the other clips shift into the new order, the dragged clip's slot is
        // empty, so the insertion line matches the front edge of that empty slot.
        var preview = EditorTimelineClipMove.Preview(clips, 0, 1);
        Require(preview.Order.Count == 3 && preview.Order[0] == 1 && preview.Order[1] == 0 && preview.Order[2] == 2,
            "the preview layout must keep the new order of the clips");
        Require(Close(preview.Starts[0], 0) && Close(preview.Starts[1], 10) && Close(preview.Starts[2], 20),
            "the preview layout must place the moved clip right after the first one");
        Require(Close(preview.InsertionStart, preview.Starts[1]),
            "the insertion guide must sit on the start of the moved clip in the new layout");
        Require(Close(preview.Starts[1] + clips[0].Duration, preview.Starts[2]),
            "the moved clip must keep its real length in the preview layout");

        var previewEnd = EditorTimelineClipMove.Preview(clips, 0, 2);
        Require(Close(previewEnd.InsertionStart, 20),
            "moving the first clip to the end must draw the guide at the end of the other clips");
        Require(Close(previewEnd.Starts[2] + clips[0].Duration, 30),
            "the preview layout must keep the composed duration");

        // A genuine reorder.
        var movedToEnd = EditorTimelineClipMove.Move(clips, 0, 2);
        Require(movedToEnd.Count == 3, "moving a clip must keep every clip");
        Require(movedToEnd[0].Id == "b" && movedToEnd[1].Id == "c" && movedToEnd[2].Id == "a",
            "moving the first clip to the end must keep the order of the other clips");
        Require(Close(EditorTimelineComposition.TotalDuration(movedToEnd), 30),
            "moving a clip must not change the composed duration");

        var movedToFront = EditorTimelineClipMove.Move(clips, 2, 0);
        Require(movedToFront[0].Id == "c" && movedToFront[1].Id == "a" && movedToFront[2].Id == "b",
            "moving the last clip to the front must keep the order of the other clips");

        var movedToMiddle = EditorTimelineClipMove.Move(clips, 0, 1);
        Require(movedToMiddle[0].Id == "b" && movedToMiddle[1].Id == "a" && movedToMiddle[2].Id == "c",
            "moving the first clip into the middle must insert it between the other clips");

        // Dropping back exactly where it was changes nothing.
        Require(EditorTimelineClipMove.Move(clips, 1, 1).Count == 3
            && EditorTimelineClipMove.Move(clips, 1, 1)[0].Id == "a"
            && EditorTimelineClipMove.Move(clips, 1, 1)[1].Id == "b",
            "dropping a clip on its own slot must keep the strip unchanged");

        // An invalid insertion position changes nothing.
        Require(EditorTimelineClipMove.Move(clips, 9, 0).Count == 3, "moving an unknown clip must be a no-op");
        Require(EditorTimelineClipMove.Move(clips, 2, -5)[0].Id == "c",
            "a negative insertion index must clamp to the start");
        Require(EditorTimelineClipMove.Move(clips, 0, 99)[2].Id == "a",
            "an insertion index past the end must clamp to the end");

        return Task.CompletedTask;
    }

    private static bool Close(double left, double right) => Math.Abs(left - right) < .0005;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
