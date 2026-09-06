using System.Globalization;

namespace BiliSubStudio.Core.Timeline;

// G3 time model (MASTER_PLAN section 6, DEC-008). All persisted timeline
// positions are signed 64-bit microseconds. Seconds appear only at
// FFmpeg/UI boundaries with invariant culture. Editing snaps to the
// sequence frame grid. Source-local and sequence-global times are explicit.
public static class TimelineTime
{
    public const long MicrosecondsPerSecond = 1_000_000L;

    public static long FromSeconds(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
            throw new InvalidDataException("Timeline thời gian giây không hợp lệ.");
        var microseconds = Math.Round(seconds * MicrosecondsPerSecond, MidpointRounding.AwayFromZero);
        if (microseconds > long.MaxValue)
            throw new InvalidDataException("Timeline thời gian vượt phạm vi int64 microseconds.");
        return (long)microseconds;
    }

    public static double ToSeconds(long microseconds)
    {
        if (microseconds < 0)
            throw new InvalidDataException("Timeline microseconds không được âm.");
        return microseconds / (double)MicrosecondsPerSecond;
    }

    // Invariant boundary format shared by future FFmpeg/UI writers.
    public static string FormatSeconds(long microseconds) =>
        ToSeconds(microseconds).ToString("0.000", CultureInfo.InvariantCulture);

    // Snap to the nearest frame boundary of an explicit sequence frame rate.
    public static long SnapToFrameGrid(long microseconds, double frameRate)
    {
        if (microseconds < 0)
            throw new InvalidDataException("Timeline microseconds không được âm.");
        if (!double.IsFinite(frameRate) || frameRate <= 0)
            throw new InvalidDataException("Sequence FPS phải là số dương hữu hạn.");
        var frame = Math.Round(microseconds * frameRate / MicrosecondsPerSecond, MidpointRounding.AwayFromZero);
        var snapped = Math.Round(frame * MicrosecondsPerSecond / frameRate, MidpointRounding.AwayFromZero);
        if (snapped < 0 || snapped > long.MaxValue)
            throw new InvalidDataException("Snap frame grid vượt phạm vi int64 microseconds.");
        return (long)snapped;
    }

    // ClipDurationUs = (SourceOutUs - SourceInUs) / PlaybackRate.
    public static long ClipDurationUs(long sourceInUs, long sourceOutUs, double playbackRate)
    {
        if (sourceInUs < 0 || sourceOutUs <= sourceInUs)
            throw new InvalidDataException("Clip source range không hợp lệ.");
        if (!double.IsFinite(playbackRate) || playbackRate <= 0)
            throw new InvalidDataException("PlaybackRate phải là số dương hữu hạn.");
        var duration = Math.Round((sourceOutUs - sourceInUs) / playbackRate, MidpointRounding.AwayFromZero);
        if (duration <= 0 || duration > long.MaxValue)
            throw new InvalidDataException("Clip duration vượt phạm vi int64 microseconds.");
        return (long)duration;
    }

    // SourceTimeUs = SourceInUs + (SequenceTimeUs - TimelineStartUs) * PlaybackRate.
    public static long SequenceToSourceUs(
        long sequenceTimeUs, long timelineStartUs, long sourceInUs, double playbackRate)
    {
        if (sequenceTimeUs < timelineStartUs)
            throw new InvalidDataException("Sequence time nằm trước clip.");
        if (sourceInUs < 0)
            throw new InvalidDataException("SourceInUs không được âm.");
        if (!double.IsFinite(playbackRate) || playbackRate <= 0)
            throw new InvalidDataException("PlaybackRate phải là số dương hữu hạn.");
        var source = Math.Round(sourceInUs + (sequenceTimeUs - timelineStartUs) * playbackRate,
            MidpointRounding.AwayFromZero);
        if (source < 0 || source > long.MaxValue)
            throw new InvalidDataException("Source time vượt phạm vi int64 microseconds.");
        return (long)source;
    }

    public static long SourceToSequenceUs(
        long sourceTimeUs, long timelineStartUs, long sourceInUs, double playbackRate)
    {
        if (sourceTimeUs < sourceInUs)
            throw new InvalidDataException("Source time nằm trước clip range.");
        if (timelineStartUs < 0 || sourceInUs < 0)
            throw new InvalidDataException("Timeline microseconds không được âm.");
        if (!double.IsFinite(playbackRate) || playbackRate <= 0)
            throw new InvalidDataException("PlaybackRate phải là số dương hữu hạn.");
        var sequence = Math.Round(timelineStartUs + (sourceTimeUs - sourceInUs) / playbackRate,
            MidpointRounding.AwayFromZero);
        if (sequence < 0 || sequence > long.MaxValue)
            throw new InvalidDataException("Sequence time vượt phạm vi int64 microseconds.");
        return (long)sequence;
    }
}
