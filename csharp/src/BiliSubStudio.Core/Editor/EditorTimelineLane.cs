namespace BiliSubStudio.Core.Editor;

/// <summary>
/// Standard lane order of the timeline from top to bottom: EFFECTS on top,
/// then SUBTITLES (they are drawn over the video), then VIDEO, and the AUDIO
/// (voice) track at the BOTTOM - the audio-under-video stacking of the edit
/// tools (Premiere, Resolve, CapCut).
///
/// The enum values are the real lane indices (0 = top), so
/// <see cref="EditorTimelineLayout.LaneTop(EditorTimelineLane)"/> maps
/// straight onto a row of the timeline without a second lookup table.
/// </summary>
public enum EditorTimelineLane
{
    /// <summary>Effect track: the blur, mosaic, and clean regions.</summary>
    Effect = 0,

    /// <summary>Subtitle track: the subtitle lines of the project.</summary>
    Subtitle = 1,

    /// <summary>Video track: the clips of the composition.</summary>
    Video = 2,

    /// <summary>Audio track: voice and the sound of the composition.</summary>
    Voice = 3,
}
