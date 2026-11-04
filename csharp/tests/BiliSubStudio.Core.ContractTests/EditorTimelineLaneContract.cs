using BiliSubStudio.Core.Editor;

namespace BiliSubStudio.Core.ContractTests;

// The timeline's lane order from top to bottom must be effects -> subtitles -> video -> voice:
// effects on top, subtitle layers over the video, video under them and the audio (voice) track
// at the BOTTOM (the stacking of the edit tools).
// The timeline panel must be tall enough that the bottommost lane is not cut off.
internal static class EditorTimelineLaneContract
{
    public static Task RunAsync()
    {
        Require(EditorTimelineLayout.LaneCount == 4,
            "the strip must own four lanes");
        Require((int)EditorTimelineLane.Effect == 0,
            "the effect lane is the top lane");
        Require((int)EditorTimelineLane.Subtitle == 1,
            "the subtitle lane sits under the effect lane");
        Require((int)EditorTimelineLane.Video == 2,
            "the video lane sits under the subtitle lane");
        Require((int)EditorTimelineLane.Voice == 3,
            "the voice lane is the bottom lane (audio under video)");

        var order = EditorTimelineLayout.LaneOrder;
        Require(order.Count == EditorTimelineLayout.LaneCount,
            "the lane order must list every lane of the strip");
        Require(order[0] == EditorTimelineLane.Effect,
            "the effect lane is drawn first (topmost)");
        Require(order[1] == EditorTimelineLane.Subtitle,
            "the subtitle lane is drawn under the effect lane");
        Require(order[2] == EditorTimelineLane.Video,
            "the video lane is drawn under the subtitle lane");
        Require(order[3] == EditorTimelineLane.Voice,
            "the voice lane is drawn last (bottom)");

        Require(EditorTimelineLayout.LaneTop(EditorTimelineLane.Voice)
            == EditorTimelineLayout.LaneTop(3),
            "the lane overload must map to the same geometry as the lane index");
        Require(EditorTimelineLayout.LaneTop(EditorTimelineLane.Video)
            < EditorTimelineLayout.LaneTop(EditorTimelineLane.Voice),
            "the video lane must sit above the voice lane (audio at the bottom)");
        Require(EditorTimelineLayout.LaneTop(EditorTimelineLane.Subtitle)
            < EditorTimelineLayout.LaneTop(EditorTimelineLane.Video),
            "the subtitle lane must sit above the video lane");
        Require(EditorTimelineLayout.LaneTop(EditorTimelineLane.Effect)
            < EditorTimelineLayout.LaneTop(EditorTimelineLane.Subtitle),
            "the effect lane must sit above the subtitle lane");

        // The panel is tall enough: the ruler + 4 lanes + horizontal scrollbar + toolbar +
        // spacing + padding. Missing the space taken by the horizontal scrollbar would cut off
        // the bottommost lane.
        var panel = EditorTimelineLayout.PanelHeight(EditorTimelineLayout.LaneCount);
        Require(Close(panel, 206),
            "the timeline panel height must stay in sync with the layout math");
        Require(panel >= EditorTimelineLayout.RulerHeight + EditorTimelineLayout.RulerGap
            + EditorTimelineLayout.TracksHeight(EditorTimelineLayout.LaneCount)
            + EditorTimelineLayout.ScrollBarReserve,
            "the panel must fit the ruler, every lane and the horizontal scroll bar");
        Require(Close(EditorTimelineLayout.TracksHeight(EditorTimelineLayout.LaneCount), 113),
            "four lanes must keep their own height");

        return Task.CompletedTask;
    }

    private static bool Close(double actual, double expected) => Math.Abs(actual - expected) <= 1e-9;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
