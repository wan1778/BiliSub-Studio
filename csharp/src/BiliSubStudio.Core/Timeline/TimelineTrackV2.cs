namespace BiliSubStudio.Core.Timeline;

// G21 multi-track (user-approved full Concat parity scope, 2026-09-08).
// Schema 5 introduces overlapping tracks: the primary video track keeps the
// legacy TimelineSequence.VideoClips list so existing plan/preview/export
// contracts stay valid, while overlay video tracks, extra music tracks and
// text tracks live in Tracks with explicit ZIndex. Time remains microseconds;
// transform values are output-frame fractions so they never depend on
// resolution.
public enum TimelineTrackKind
{
    Video = 0,
    VideoOverlay = 1,
    Text = 2,
    Music = 3,
    Audio = 4,
}

// G21 transform for overlay clips (PiP). Fractions are relative to output
// frame; scale 1 = full output size; opacity 1 = fully opaque.
public sealed record TimelineClipTransform(
    double PositionX,
    double PositionY,
    double ScaleX,
    double ScaleY,
    double RotationDegrees,
    double Opacity)
{
    public static TimelineClipTransform FullFrame { get; } =
        new(0.5, 0.5, 1, 1, 0, 1);

    public void Validate()
    {
        if (!double.IsFinite(PositionX) || PositionX < 0 || PositionX > 1)
            throw new InvalidDataException("Timeline clip PositionX phải là phân số 0..1.");
        if (!double.IsFinite(PositionY) || PositionY < 0 || PositionY > 1)
            throw new InvalidDataException("Timeline clip PositionY phải là phân số 0..1.");
        if (!double.IsFinite(ScaleX) || ScaleX <= 0 || ScaleX > 16)
            throw new InvalidDataException("Timeline clip ScaleX phải trong 0..16.");
        if (!double.IsFinite(ScaleY) || ScaleY <= 0 || ScaleY > 16)
            throw new InvalidDataException("Timeline clip ScaleY phải trong 0..16.");
        if (!double.IsFinite(RotationDegrees) || Math.Abs(RotationDegrees) > 360)
            throw new InvalidDataException("Timeline clip Rotation phải trong -360..360 độ.");
        if (!double.IsFinite(Opacity) || Opacity < 0 || Opacity > 1)
            throw new InvalidDataException("Timeline clip Opacity phải trong 0..1.");
    }
}

// G21 base clip for overlay/secondary tracks. Primary video keeps
// TimelineClip; this record extends it with track ownership and transform.
public sealed record TimelineTrackClip(
    string Id,
    string AssetId,
    string TrackId,
    long TimelineStartUs,
    long SourceInUs,
    long SourceOutUs,
    double PlaybackRate,
    TimelineClipTransform Transform,
    bool IsVisible,
    bool IsMuted)
{
    public static string NewId() => Guid.NewGuid().ToString("N");

    public long DurationUs => TimelineTime.ClipDurationUs(SourceInUs, SourceOutUs, PlaybackRate);

    public long TimelineEndUs => checked(TimelineStartUs + DurationUs);

    public static TimelineTrackClip Create(
        string assetId,
        string trackId,
        long timelineStartUs,
        long sourceInUs,
        long sourceOutUs,
        double playbackRate = 1.0,
        TimelineClipTransform? transform = null,
        bool isVisible = true,
        bool isMuted = false)
    {
        var clip = new TimelineTrackClip(
            NewId(), assetId, trackId, timelineStartUs, sourceInUs, sourceOutUs,
            playbackRate, transform ?? TimelineClipTransform.FullFrame,
            isVisible, isMuted);
        clip.ValidateShape();
        return clip;
    }

    public void ValidateShape()
    {
        if (Id is not { Length: 32 } || !Id.All(character => char.IsAsciiHexDigit(character)))
            throw new InvalidDataException("Timeline track clip ID không hợp lệ.");
        if (AssetId is not { Length: 32 } || !AssetId.All(character => char.IsAsciiHexDigit(character)))
            throw new InvalidDataException("Timeline track clip AssetId không hợp lệ.");
        if (string.IsNullOrWhiteSpace(TrackId))
            throw new InvalidDataException("Timeline track clip thiếu TrackId.");
        if (TimelineStartUs < 0 || SourceInUs < 0 || SourceOutUs <= SourceInUs)
            throw new InvalidDataException("Timeline track clip range không hợp lệ.");
        if (!double.IsFinite(PlaybackRate) || PlaybackRate <= 0)
            throw new InvalidDataException("Timeline track clip PlaybackRate phải dương hữu hạn.");
        Transform.Validate();
    }
}

// G21 track container. ZIndex orders visual composition (higher = on top);
// audio and music tracks ignore ZIndex and mix additively.
public sealed record TimelineTrackV2(
    string Id,
    TimelineTrackKind Kind,
    string Name,
    int ZIndex,
    bool IsVisible,
    bool IsMuted,
    bool IsLocked,
    double GainDb,
    IReadOnlyList<TimelineTrackClip> Clips)
{
    public static string NewId() => Guid.NewGuid().ToString("N");

    public void Validate()
    {
        if (Id is not { Length: 32 } || !Id.All(character => char.IsAsciiHexDigit(character)))
            throw new InvalidDataException("Timeline track ID không hợp lệ.");
        if (!Enum.IsDefined(typeof(TimelineTrackKind), Kind))
            throw new InvalidDataException("Timeline track kind không hợp lệ.");
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 80)
            throw new InvalidDataException("Timeline track name phải 1..80 ký tự.");
        if (ZIndex is < 0 or > 64)
            throw new InvalidDataException("Timeline track ZIndex phải trong 0..64.");
        if (!double.IsFinite(GainDb) || GainDb < -60 || GainDb > 24)
            throw new InvalidDataException("Timeline track GainDb phải trong -60..24.");
        ArgumentNullException.ThrowIfNull(Clips);
        foreach (var clip in Clips)
        {
            clip.ValidateShape();
            if (!string.Equals(clip.TrackId, Id, StringComparison.Ordinal))
                throw new InvalidDataException("Timeline track clip không thuộc track này.");
        }
        // Visual tracks: same-track clips must not overlap (sequential like the
        // primary track). Music/audio may stack (mixing) so overlap is allowed.
        if (Kind is TimelineTrackKind.Video or TimelineTrackKind.VideoOverlay)
        {
            var ordered = Clips.OrderBy(clip => clip.TimelineStartUs).ToArray();
            for (var index = 1; index < ordered.Length; index++)
            {
                if (ordered[index].TimelineStartUs < ordered[index - 1].TimelineEndUs)
                    throw new InvalidDataException(
                        $"Timeline track {Name} chứa clip chồng lấn trong cùng track visual.");
            }
        }
    }
}
