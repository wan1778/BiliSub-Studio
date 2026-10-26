using BiliSubStudio.Core.Editor;

namespace BiliSubStudio.Core.ContractTests;

// Dải thời gian kiểu CapCut/Premiere trong Editor: toạ độ giây <-> pixel phải
// round-trip chính xác, zoom phải kẹp được, và chỉ vẽ block trong viewport
// (project có ~14k cue nên không thể dựng hết element).
internal static class EditorTimelineLayoutContract
{
    public static Task RunAsync()
    {
        Require(Close(EditorTimelineLayout.FitPixelsPerSecond(100, 1000), 10),
            "fit zoom must show 100s inside 1000px");
        Require(EditorTimelineLayout.FitPixelsPerSecond(0, 1000) == EditorTimelineLayout.MinPixelsPerSecond,
            "empty media must not divide by zero");
        Require(EditorTimelineLayout.FitPixelsPerSecond(100, 0) == EditorTimelineLayout.MinPixelsPerSecond,
            "zero viewport must not divide by zero");

        Require(EditorTimelineLayout.ClampPixelsPerSecond(0) == EditorTimelineLayout.MinPixelsPerSecond,
            "zero zoom must clamp to the floor");
        Require(EditorTimelineLayout.ClampPixelsPerSecond(-5) == EditorTimelineLayout.MinPixelsPerSecond,
            "negative zoom must clamp to the floor");
        Require(EditorTimelineLayout.ClampPixelsPerSecond(1e9) == EditorTimelineLayout.MaxPixelsPerSecond,
            "huge zoom must clamp to the ceiling");
        Require(EditorTimelineLayout.ClampPixelsPerSecond(double.NaN) == EditorTimelineLayout.MinPixelsPerSecond,
            "NaN zoom must clamp to the floor");

        var pps = 24.5;
        var x = EditorTimelineLayout.XForTime(123.456, pps);
        Require(Close(x, 123.456 * pps), "time -> pixel must scale linearly");
        Require(Close(EditorTimelineLayout.TimeForX(x, pps), 123.456), "pixel -> time must invert exactly");

        Require(Close(EditorTimelineLayout.ContentWidth(60, 10), 600), "content width follows duration * zoom");
        Require(EditorTimelineLayout.ContentWidth(1e9, 600) == EditorTimelineLayout.MaxContentWidth,
            "content width must stay inside the canvas limit");
        Require(EditorTimelineLayout.ContentWidth(0, 10) == 1, "empty media still needs a drawable canvas");

        Require(EditorTimelineLayout.MajorTickSeconds(10) == 10, "major tick must keep ~84px spacing");
        Require(EditorTimelineLayout.MajorTickSeconds(1) == 120, "low zoom needs a coarser tick");
        Require(Close(EditorTimelineLayout.MinorTickSeconds(10), 2), "minor ticks split the major step");
        Require(EditorTimelineLayout.MajorTickSeconds(10) >= EditorTimelineLayout.MinorTickSeconds(10),
            "major tick must never be finer than minor tick");

        var window = EditorTimelineLayout.VisibleWindow(2400, 800, 10, 3600);
        Require(Close(window.Start, 240) && Close(window.End, 320), "visible window follows scroll offset");
        var clamped = EditorTimelineLayout.VisibleWindow(0, 800, 10, 100);
        Require(Close(clamped.Start, 0) && Close(clamped.End, 80), "visible window must not exceed the media");

        Require(EditorTimelineLayout.IntersectsVisible(10, 20, 0, 30), "overlapping span must be drawn");
        Require(!EditorTimelineLayout.IntersectsVisible(40, 50, 0, 30), "span outside the window must be skipped");
        Require(EditorTimelineLayout.IntersectsVisible(30, 40, 0, 30), "touching the window edge still counts");
        Require(EditorTimelineLayout.IntersectsVisible(20, 10, 0, 30), "reversed span must still be visible");
        Require(!EditorTimelineLayout.IntersectsVisible(double.NaN, 10, 0, 30), "NaN span must be skipped");

        Require(EditorTimelineLayout.LaneTop(0) == 0, "first lane starts at the top");
        Require(EditorTimelineLayout.LaneTop(2) == 2 * (EditorTimelineLayout.LaneHeight + EditorTimelineLayout.LaneGap),
            "lane offset must account for the gap");
        Require(EditorTimelineLayout.TracksHeight(3)
            == 3 * EditorTimelineLayout.LaneHeight + 2 * EditorTimelineLayout.LaneGap,
            "tracks height must cover every lane plus gaps");
        Require(EditorTimelineLayout.TracksHeight(0) == 0, "no lanes means no track height");

        return Task.CompletedTask;
    }

    private static bool Close(double actual, double expected) => Math.Abs(actual - expected) <= 1e-9;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
