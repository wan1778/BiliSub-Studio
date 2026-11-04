using BiliSubStudio.Core.Editor;

namespace BiliSubStudio.Core.ContractTests;

// The playhead grab zone must be wide enough to hit the round knob at the head of the playhead,
// and it must only open on the ruler: presses in the lane area belong to clip-edge trimming and
// cue dragging, and must not steal them.
internal static class EditorTimelinePlayheadGrabContract
{
    public static Task RunAsync()
    {
        Require(EditorTimelinePlayhead.GrabPixels >= 16d,
            "the playhead grab zone must be at least 16 DIP on each side of the playhead");
        Require(EditorTimelinePlayhead.GrabZoneWidth == EditorTimelinePlayhead.GrabPixels * 2d,
            "the grab zone width must be twice the grab radius");
        Require(EditorTimelinePlayhead.IsWithinGrabRange(100, 100 - EditorTimelinePlayhead.GrabPixels),
            "the grab zone must cover the full radius on the left of the playhead");
        Require(EditorTimelinePlayhead.IsWithinGrabRange(100, 100 + EditorTimelinePlayhead.GrabPixels),
            "the grab zone must cover the full radius on the right of the playhead");
        Require(!EditorTimelinePlayhead.IsWithinGrabRange(100, 100 - EditorTimelinePlayhead.GrabPixels - 1),
            "outside the grab zone the ruler keeps its seek behaviour");
        Require(!EditorTimelinePlayhead.IsWithinGrabRange(double.NaN, 100),
            "an invalid pointer position must not grab the playhead");
        Require(!EditorTimelinePlayhead.IsWithinGrabRange(100, double.NaN),
            "an invalid playhead position must not be grabbed");

        // The grab zone only opens on the ruler (where the playhead knob is).
        Require(EditorTimelinePlayhead.IsInRulerBand(0, 26),
            "the top of the ruler band must grab the playhead");
        Require(EditorTimelinePlayhead.IsInRulerBand(13, 26),
            "the middle of the ruler band must grab the playhead");
        Require(EditorTimelinePlayhead.IsInRulerBand(26, 26),
            "the bottom edge of the ruler band must grab the playhead");
        Require(!EditorTimelinePlayhead.IsInRulerBand(27, 26),
            "the lane area must stay free for trim and cue drags");
        Require(!EditorTimelinePlayhead.IsInRulerBand(-1, 26),
            "a pointer above the strip must not grab the playhead");
        Require(!EditorTimelinePlayhead.IsInRulerBand(10, 0),
            "an unknown ruler height must not open the grab zone");
        Require(!EditorTimelinePlayhead.IsInRulerBand(double.NaN, 26),
            "an invalid pointer position must not open the grab zone");

        return Task.CompletedTask;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
