namespace BiliSubStudio.Core.Timeline;

// G3 clip (MASTER_PLAN section 7). One sequential primary-track clip with a
// hard cut. Clip-local effects/subtitle/voice/logos anchor to the owning
// clip (DEC-009); move/reorder dependency enforcement arrives in G6.
public sealed record TimelineClip(
    string Id,
    string AssetId,
    long TimelineStartUs,
    long SourceInUs,
    long SourceOutUs,
    double PlaybackRate)
{
    public static string NewId() => Guid.NewGuid().ToString("N");

    public long DurationUs => TimelineTime.ClipDurationUs(SourceInUs, SourceOutUs, PlaybackRate);

    public long TimelineEndUs => checked(TimelineStartUs + DurationUs);

    public static TimelineClip Create(
        string assetId,
        long timelineStartUs,
        long sourceInUs,
        long sourceOutUs,
        double playbackRate = 1.0)
    {
        var clip = new TimelineClip(NewId(), assetId, timelineStartUs, sourceInUs, sourceOutUs, playbackRate);
        clip.ValidateShape();
        return clip;
    }

    public void ValidateShape()
    {
        if (Id is not { Length: 32 } || !Id.All(character => char.IsAsciiHexDigit(character)))
            throw new InvalidDataException("Timeline clip ID không hợp lệ.");
        if (AssetId is not { Length: 32 } || !AssetId.All(character => char.IsAsciiHexDigit(character)))
            throw new InvalidDataException("Timeline clip AssetId không hợp lệ.");
        if (TimelineStartUs < 0 || SourceInUs < 0 || SourceOutUs <= SourceInUs)
            throw new InvalidDataException("Timeline clip range không hợp lệ.");
        if (!double.IsFinite(PlaybackRate) || PlaybackRate <= 0)
            throw new InvalidDataException("Timeline clip PlaybackRate phải là số dương hữu hạn.");
        _ = DurationUs;
    }

    public void ValidateAgainst(TimelineMediaAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ValidateShape();
        asset.Validate();
        if (!string.Equals(AssetId, asset.Id, StringComparison.Ordinal))
            throw new InvalidDataException("Timeline clip trỏ sai asset.");
        if (SourceOutUs > asset.DurationUs)
            throw new InvalidDataException("Timeline clip source range vượt quá asset duration.");
    }
}
