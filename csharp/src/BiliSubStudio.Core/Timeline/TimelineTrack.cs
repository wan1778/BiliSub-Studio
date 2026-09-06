namespace BiliSubStudio.Core.Timeline;

// G3 tracks (MASTER_PLAN section 4, DEC-007). Visual tracks composite bottom
// to top; audio tracks display below the separator. G3 locks the canonical
// order; item ownership inside tracks arrives in G6/G7.
public enum TimelineTrackType
{
    Img = 0,
    Sub = 1,
    Fx = 2,
    Video = 3,
    SourceAudio = 4,
    Voice = 5,
    Music = 6,
}

public sealed record TimelineTrack(
    string Id,
    TimelineTrackType Type,
    bool IsVisible,
    bool IsMuted,
    bool IsLocked,
    IReadOnlyList<string> ClipIds)
{
    // DEC-007 display order: IMG, SUB, FX, V1 video, separator,
    // source audio, voice, music.
    public static IReadOnlyList<TimelineTrackType> CanonicalOrder { get; } =
    [
        TimelineTrackType.Img,
        TimelineTrackType.Sub,
        TimelineTrackType.Fx,
        TimelineTrackType.Video,
        TimelineTrackType.SourceAudio,
        TimelineTrackType.Voice,
        TimelineTrackType.Music,
    ];

    public static IReadOnlyList<TimelineTrack> CreateDefaultTracks() =>
        CanonicalOrder
            .Select(type => new TimelineTrack(
                type.ToString().ToLowerInvariant() + "-" + Guid.NewGuid().ToString("N"),
                type,
                IsVisible: true,
                IsMuted: false,
                IsLocked: false,
                ClipIds: []))
            .ToArray();

    public static bool IsCanonicalOrder(IEnumerable<TimelineTrackType> types) =>
        types.SequenceEqual(CanonicalOrder);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id))
            throw new InvalidDataException("Timeline track ID không được rỗng.");
        if (!Enum.IsDefined(typeof(TimelineTrackType), Type))
            throw new InvalidDataException("Timeline track type không hợp lệ.");
        ArgumentNullException.ThrowIfNull(ClipIds);
    }
}
