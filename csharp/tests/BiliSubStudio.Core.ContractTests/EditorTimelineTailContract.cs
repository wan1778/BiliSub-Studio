using BiliSubStudio.Core.Editor;

namespace BiliSubStudio.Core.ContractTests;

// The timeline strip keeps an EMPTY WORKING TAIL behind the video, exactly as
// long as the video (10 min of video -> the strip works until minute 20; an
// hour -> until hour 2). The tail is real working time on the same second axis
// as the video, so it never stretches with zoom and the video's own duration,
// clips and export stay untouched.
internal static class EditorTimelineTailContract
{
    public static Task RunAsync()
    {
        // TAIL-01: the working extent is the video plus a tail of the same real
        // length (doubling the span).
        Require(EditorTimelineLayout.WorkingSpan(600) == 1200,
            "TAIL-01 a 10 minute video must keep the strip working until minute 20");
        Require(EditorTimelineLayout.WorkingSpan(3600) == 7200,
            "TAIL-01 an hour of video must keep the strip working until hour 2");
        Require(EditorTimelineLayout.WorkingSpan(7200) == 14400,
            "TAIL-01 two hours of video must keep the strip working until hour 4");

        // TAIL-02: the tail is exactly as long as the video (span - duration).
        Require(EditorTimelineLayout.WorkingSpan(600) - 600 == 600,
            "TAIL-02 the empty tail must be exactly the video's own length");

        // TAIL-03: no video, no tail (the strip stays empty), and corrupt
        // lengths never invent a working span.
        Require(EditorTimelineLayout.WorkingSpan(0) == 0,
            "TAIL-03 a timeline without a video must not invent a working tail");
        Require(EditorTimelineLayout.WorkingSpan(-5) == 0,
            "TAIL-03 a negative length must not invent a working tail");
        Require(EditorTimelineLayout.WorkingSpan(double.NaN) == 0,
            "TAIL-03 a NaN length must not invent a working tail");

        // TAIL-04: the doubled span still feeds the strip's pixel width, so the
        // ruler and every track render all the way to the end of the tail.
        Require(EditorTimelineLayout.ContentWidth(EditorTimelineLayout.WorkingSpan(600), 10) == 12000,
            "TAIL-04 the strip's canvas must cover the whole working span");

        return Task.CompletedTask;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
