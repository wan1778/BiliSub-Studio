namespace BiliSubStudio.Core.Editor;

/// <summary>
/// A segment of the composition that needs to be exported to a file. Each segment keeps its
/// source path and the [Start, End] range measured within that same source file;
/// <see cref="SourceDuration"/> is the full duration of the source file used to
/// normalize the cut range.
/// </summary>
public sealed record VideoEditSegment(
    string InputPath,
    double SourceDuration,
    double Start,
    double End,
    int Width,
    int Height,
    IReadOnlyList<EditRegion> Regions,
    EditorSubtitleBurn? Subtitle = null,
    EditorVoiceTrack? VoiceTrack = null)
{
    /// <summary>Duration of the segment after trimming (seconds).</summary>
    public double Duration => Math.Max(0, End - Start);
}
