using BiliSubStudio.Core.Editor;

namespace BiliSubStudio.Core.ContractTests;

// Trimming clip edges on the timeline: the In edge moves the source-file in point by delta and
// shrinks duration, the Out edge grows/shrinks duration; every clamp (media handle, neighbouring
// clip, one frame) and the 9 px screen snapping live in Core so the UI only calls into it.
internal static class EditorTimelineEditingContract
{
    public static Task RunAsync()
    {
        var clips = new List<EditorTimelineClip>
        {
            new("a", "E:\\video\\a.mp4", 10, 30, "A"),
            new("b", "E:\\video\\b.mp4", 0, 20, "B"),
        };
        const double mediaDuration = 60;
        const double minDuration = 1d / 30d;

        // In edge: the in point moves by delta, duration shrinks by exactly delta, the out point in the
        // source file stays put so the remaining media stays continuous.
        var head = EditorTimelineTrim.Trim(clips, 0, EditorTimelineTrimEdge.In, 2.5, mediaDuration, minDuration);
        Require(Close(head.SourceIn, 12.5), "trimming the In edge must move the source in point by the delta");
        Require(Close(head.Duration, 27.5), "trimming the In edge must shorten the clip by the delta");
        Require(Close(head.End, 40), "trimming the In edge must keep the source out point so the media stays continuous");

        // Out edge: only duration changes, the in point in the source file stays put.
        var tail = EditorTimelineTrim.Trim(clips, 0, EditorTimelineTrimEdge.Out, 5, mediaDuration, minDuration);
        Require(Close(tail.SourceIn, 10), "trimming the Out edge must keep the source in point");
        Require(Close(tail.Duration, 35), "trimming the Out edge must grow the clip by the delta");
        Require(Close(tail.End, 45), "trimming the Out edge must move the source out point");

        // Media handle: media before the start of the source file must not be taken.
        var beforeFile = EditorTimelineTrim.Trim(clips, 0, EditorTimelineTrimEdge.In, -40, mediaDuration, minDuration);
        Require(Close(beforeFile.SourceIn, 0), "the In edge must not reach before the start of the source file");
        Require(Close(beforeFile.Duration, 40), "the In edge may grow into the head handle of the source file");

        // End-of-file media handle: it must not go past the end of the source file.
        var pastFile = EditorTimelineTrim.Trim(clips, 0, EditorTimelineTrimEdge.Out, 100, mediaDuration, minDuration);
        Require(Close(pastFile.End, 60), "the Out edge must not reach past the end of the source file");
        Require(Close(pastFile.Duration, 50), "the Out edge must stop at the end of the source file");
        var atFileEnd = new List<EditorTimelineClip> { new("a", "E:\\video\\a.mp4", 40, 20, "A") };
        var blockedByFile = EditorTimelineTrim.Trim(atFileEnd, 0, EditorTimelineTrimEdge.Out, 5, mediaDuration, minDuration);
        Require(Close(blockedByFile.Duration, 20), "a clip that already reaches the end of the file must not grow");

        // Minimum one frame: trimming too deep stops at exactly one frame.
        var minimum = EditorTimelineTrim.Trim(clips, 0, EditorTimelineTrimEdge.In, 40, mediaDuration, minDuration);
        Require(Close(minimum.Duration, minDuration), "a clip must keep at least one frame");
        Require(Close(minimum.SourceIn, 10 + 30 - minDuration), "the In edge must stop at the one-frame limit");

        // Regular trim: the Out edge must not cut into the media of a neighbouring clip using the
        // same source file (two clips adjacent inside one file).
        var contiguous = new List<EditorTimelineClip>
        {
            new("a", "E:\\video\\a.mp4", 0, 10, "A"),
            new("b", "E:\\video\\a.mp4", 10, 10, "A2"),
        };
        var blocked = EditorTimelineTrim.Trim(contiguous, 0, EditorTimelineTrimEdge.Out, 4, 60, minDuration);
        Require(Close(blocked.Duration, 10), "the Out edge must not cut into the media of the next clip");
        var blockedHead = EditorTimelineTrim.Trim(contiguous, 1, EditorTimelineTrimEdge.In, -4, 60, minDuration);
        Require(Close(blockedHead.SourceIn, 10), "the In edge must not cut into the media of the previous clip");
        Require(Close(blockedHead.Duration, 10), "a blocked In edge must leave the clip untouched");

        // A neighbouring clip from a different source file has no shared media constraint.
        var otherSource = EditorTimelineTrim.Trim(clips, 0, EditorTimelineTrimEdge.Out, 4, 60, minDuration);
        Require(Close(otherSource.Duration, 34), "a neighbour from another file must not block the Out edge");

        // The same source file but two clips sharing media: the user chooses the media range freely,
        // so edge trimming must still work.
        var duplicated = new List<EditorTimelineClip>
        {
            new("a", "E:\\video\\a.mp4", 0, 30, "A"),
            new("a2", "E:\\video\\a.mp4", 0, 30, "A2"),
        };
        var duplicatedTrim = EditorTimelineTrim.Trim(duplicated, 0, EditorTimelineTrimEdge.Out, 5, 60, minDuration);
        Require(Close(duplicatedTrim.Duration, 35), "a clip that shares its media with the next one must still be trimmable");
        var duplicatedHead = EditorTimelineTrim.Trim(duplicated, 1, EditorTimelineTrimEdge.In, 5, 60, minDuration);
        Require(Close(duplicatedHead.SourceIn, 5), "a clip that shares its media with the previous one must still be trimmable");

        // An invalid delta trims nothing.
        Require(Close(EditorTimelineTrim.Trim(clips, 0, EditorTimelineTrimEdge.Out, double.NaN, 60, minDuration).Duration, 30),
            "a NaN delta must leave the clip untouched");
        Require(Close(EditorTimelineTrim.MinimumDuration(0), 1d / 30d),
            "an unknown frame rate must fall back to 30 fps");
        Require(Close(EditorTimelineTrim.MinimumDuration(60), 1d / 60d),
            "the minimum duration must be one frame of the media");

        // The clip edge grab zone is 10 px wide: clicking close to the edge grabs the edge, clicking
        // in the middle of the clip only selects the clip.
        Require(EditorTimelineTrim.EdgeGrabPixels == 10d, "the clip edge grab zone must be 10 screen pixels");
        Require(EditorTimelineTrim.EdgeAt(0, 200) == EditorTimelineTrimEdge.In,
            "the left edge of a clip must be grabbable");
        Require(EditorTimelineTrim.EdgeAt(9.9, 200) == EditorTimelineTrimEdge.In,
            "the left grab zone must cover the first 10 pixels");
        Require(EditorTimelineTrim.EdgeAt(10.1, 200) is null,
            "a press in the middle of a clip must not grab an edge");
        Require(EditorTimelineTrim.EdgeAt(190.1, 200) == EditorTimelineTrimEdge.Out,
            "the right edge of a clip must be grabbable");
        Require(EditorTimelineTrim.EdgeAt(190, 200) == EditorTimelineTrimEdge.Out,
            "the right grab zone must cover the last 10 pixels");
        Require(EditorTimelineTrim.EdgeAt(5, 20) is null,
            "a clip too narrow for two grab zones must only be selected");
        Require(EditorTimelineTrim.CanGrabEdge(30), "a clip of 30 pixels must still be trimmable");
        Require(!EditorTimelineTrim.CanGrabEdge(29), "a clip narrower than the grab zones must not be trimmable");

        // Minimum drag threshold: a light click must not create a junk trim.
        Require(EditorTimelineTrim.MinimumDragPixels == 3d, "the minimum drag threshold must be 3 screen pixels");
        Require(!EditorTimelineTrim.ReachedDragThreshold(2.9),
            "a nudge below the threshold must not trim");
        Require(EditorTimelineTrim.ReachedDragThreshold(3),
            "a drag of the threshold must trim");
        Require(!EditorTimelineTrim.ReachedDragThreshold(double.NaN),
            "an unknown drag distance must not trim");

        // Screen-pixel snapping: 9 px, not a time threshold.
        Require(EditorTimelineSnapping.SnapPixels == 9d, "the snap threshold must be 9 screen pixels");
        var near = EditorTimelineSnapping.SnapTime(5, [5.5], 10);
        Require(Close(near, 5.5), "a candidate 5 screen pixels away must snap");
        var far = EditorTimelineSnapping.SnapTime(5, [5.9], 10);
        Require(Close(far, 5), "a candidate 9 screen pixels away must not snap");
        var sameTimeOtherZoom = EditorTimelineSnapping.SnapTime(5, [5.5], 100);
        Require(Close(sameTimeOtherZoom, 5), "the same time offset must not snap when it is 50 screen pixels away");
        var samePixelsOtherZoom = EditorTimelineSnapping.SnapTime(5, [5.05], 100);
        Require(Close(samePixelsOtherZoom, 5.05), "a candidate 5 screen pixels away must snap at any zoom");

        // The nearest mark wins; on a tie the mark that comes first is taken.
        Require(Close(EditorTimelineSnapping.SnapTime(5, [5.8, 5.2], 10), 5.2), "the closest candidate must win");
        Require(Close(EditorTimelineSnapping.SnapTime(5, [5.2, 5.8], 10), 5.2), "the first candidate must win a tie");

        // snap mark list: another clip's start/end, the playhead, markers and the trim mark.
        var candidates = EditorTimelineSnapping.Candidates(clips, 12, [7], [3], excludedClipIndex: 1);
        Require(candidates.Contains(0) && candidates.Contains(30),
            "the other clips must contribute their start and end as snap candidates");
        Require(!candidates.Contains(50), "the dragged clip must not contribute its own edges");
        Require(candidates.Contains(12), "the playhead must be a snap candidate");
        Require(candidates.Contains(7) && candidates.Contains(3),
            "the markers and the trim marks must be snap candidates");
        Require(candidates.Count == 5, "the snap candidate list must only hold the expected markers");

        return Task.CompletedTask;
    }

    private static bool Close(double left, double right) => Math.Abs(left - right) < .0005;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
