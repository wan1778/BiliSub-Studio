using BiliSubStudio.Core.Editor;

namespace BiliSubStudio.Core.ContractTests;

// Pressing play must run FROM THE CURRENT PLAYHEAD POSITION, but never before the start mark of
// the timeline (the part trimmed out of the export file cannot be previewed either).
// The mouse grab zone around the playhead is 16 DIP on each side so the playhead head is clickable.
internal static class EditorTimelinePlaybackStartContract
{
    public static Task RunAsync()
    {
        Require(Close(EditorTimelinePlaybackStart.Resolve(12, 0, 60), 12),
            "play must start at the current playhead");
        Require(Close(EditorTimelinePlaybackStart.Resolve(12, 5, 60), 12),
            "the playhead wins when it is already past the start marker");
        Require(Close(EditorTimelinePlaybackStart.Resolve(2, 5, 60), 5),
            "play must never start before the start marker of the strip");
        Require(Close(EditorTimelinePlaybackStart.Resolve(-5, 0, 60), 0),
            "a negative playhead must clamp to the head of the strip");
        Require(Close(EditorTimelinePlaybackStart.Resolve(90, 0, 60), 60),
            "the start must clamp to the end of the strip");
        Require(Close(EditorTimelinePlaybackStart.Resolve(12, 5, 0), 12),
            "an unknown duration must not clamp the start position");
        Require(Close(EditorTimelinePlaybackStart.Resolve(double.NaN, 5, 60), 5),
            "an invalid playhead must fall back to the start marker");
        Require(Close(EditorTimelinePlaybackStart.Resolve(double.NaN, double.NaN, 60), 0),
            "an invalid playhead and marker must fall back to the head of the strip");

        Require(EditorTimelinePlayhead.GrabPixels == 16d,
            "the playhead grab zone must stay 16 DIP wide on each side");
        Require(EditorTimelinePlayhead.IsWithinGrabRange(100, 116),
            "the grab zone covers 16 DIP on each side of the playhead");
        Require(EditorTimelinePlayhead.IsWithinGrabRange(100, 84),
            "the grab zone covers 16 DIP on the left side of the playhead");
        Require(EditorTimelinePlayhead.IsWithinGrabRange(100, 116),
            "the grab zone edge is inclusive");
        Require(!EditorTimelinePlayhead.IsWithinGrabRange(100, 117),
            "outside the grab zone the ruler keeps its seek behaviour");
        Require(!EditorTimelinePlayhead.IsWithinGrabRange(double.NaN, 100),
            "an invalid pointer position must not grab the playhead");
        Require(!EditorTimelinePlayhead.IsWithinGrabRange(100, double.NaN),
            "an invalid playhead position must not be grabbed");

        return Task.CompletedTask;
    }

    private static bool Close(double actual, double expected) => Math.Abs(actual - expected) <= 1e-9;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
