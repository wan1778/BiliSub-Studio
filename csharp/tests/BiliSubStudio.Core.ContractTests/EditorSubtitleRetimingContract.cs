using BiliSubStudio.Core.Editor;

namespace BiliSubStudio.Core.ContractTests;

// Kéo mép cue trên dải thời gian: timecode phải định dạng đúng chuẩn SRT và
// mép không được vượt qua cue liền kề (không chồng lấn, không rỗng).
internal static class EditorSubtitleRetimingContract
{
    public static Task RunAsync()
    {
        Require(EditorSubtitleDocument.FormatTimestamp(0) == "00:00:00,000",
            "zero timestamp must render as SRT midnight");
        Require(EditorSubtitleDocument.FormatTimestamp(3661.5) == "01:01:01,500",
            "timestamp must keep hours minutes seconds milliseconds");
        Require(EditorSubtitleDocument.FormatTimestamp(13032.108889) == "03:37:12,109",
            "long video timestamp must round to milliseconds");
        Require(EditorSubtitleDocument.FormatTiming(0, 1.5) == "00:00:00,000 --> 00:00:01,500",
            "timing line must use the SRT arrow format");

        var cues = new[]
        {
            new EditorSubtitleCue("a", "1", "00:00:00,000 --> 00:00:02,000", 0, 2, "Mot"),
            new EditorSubtitleCue("b", "2", "00:00:02,000 --> 00:00:05,000", 2, 5, "Hai"),
            new EditorSubtitleCue("c", "3", "00:00:05,000 --> 00:00:09,000", 5, 9, "Ba"),
        };

        var movedEnd = EditorSubtitleDocument.RetimeCue(cues, 1, EditorCueEdge.End, 4.25);
        Require(Close(movedEnd.End, 4.25) && Close(movedEnd.Start, 2),
            "dragging the right edge must move only the end");
        Require(movedEnd.Timing == "00:00:02,000 --> 00:00:04,250",
            "retimed cue must rebuild its timing line");
        Require(movedEnd.Id == "b" && movedEnd.Number == "2" && movedEnd.SourceText == "Hai",
            "retiming must not touch identity or text");

        var clampedEnd = EditorSubtitleDocument.RetimeCue(cues, 1, EditorCueEdge.End, 8);
        Require(Close(clampedEnd.End, 5),
            "right edge must not cross the next cue start");
        var clampedStart = EditorSubtitleDocument.RetimeCue(cues, 1, EditorCueEdge.Start, 4.9);
        Require(Close(clampedStart.Start, 4.8) && Close(clampedStart.End, 5),
            "left edge must keep the minimum duration");
        var boundedStart = EditorSubtitleDocument.RetimeCue(cues, 1, EditorCueEdge.Start, .5);
        Require(Close(boundedStart.Start, 2),
            "left edge must not cross the previous cue end");

        var firstStart = EditorSubtitleDocument.RetimeCue(cues, 0, EditorCueEdge.Start, -4);
        Require(Close(firstStart.Start, 0),
            "first cue must not start before zero");
        var lastEnd = EditorSubtitleDocument.RetimeCue(cues, 2, EditorCueEdge.End, 99);
        Require(Close(lastEnd.End, 99),
            "last cue has no following neighbor to clamp against");

        Require(Throws(() => EditorSubtitleDocument.RetimeCue(cues, 9, EditorCueEdge.End, 5)),
            "index outside the cue list must be rejected");
        Require(Throws(() => EditorSubtitleDocument.RetimeCue(cues, 0, EditorCueEdge.End, double.NaN)),
            "non finite timing must be rejected");
        Require(Throws(() => EditorSubtitleDocument.FormatTimestamp(double.PositiveInfinity)),
            "non finite timestamp must be rejected");

        return Task.CompletedTask;
    }

    private static bool Close(double actual, double expected) => Math.Abs(actual - expected) <= 1e-6;

    private static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return true;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
