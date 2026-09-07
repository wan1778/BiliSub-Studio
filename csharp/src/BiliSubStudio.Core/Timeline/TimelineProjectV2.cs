namespace BiliSubStudio.Core.Timeline;

// G3 project (MASTER_PLAN section 7), extended with items in G7 and typed
// cue/audio ownership in G8. Older V2 schemas receive only explicit defaults;
// schema 1 remains rejected because output geometry/FPS cannot be inferred.
public sealed record TimelineProjectV2(
    string Id,
    string Name,
    DateTimeOffset UpdatedUtc,
    IReadOnlyList<TimelineMediaAsset> Assets,
    TimelineSequence? Sequence,
    IReadOnlyList<TimelineItem> Items,
    IReadOnlyList<TimelineSubtitleCue> SubtitleCues,
    IReadOnlyList<TimelineAudioAsset> AudioAssets,
    IReadOnlyList<TimelineSourceAudioState> SourceAudio,
    IReadOnlyList<TimelineVoiceCue> VoiceCues,
    TimelineMusicTrack? Music)
{
    public const int Schema = 4;

    public static string NewId() => Guid.NewGuid().ToString("N");

    public static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Trim().Length != 32
            || !id.All(character => char.IsAsciiHexDigit(character)))
            throw new InvalidDataException("Timeline V2 project ID không hợp lệ.");
    }

    public static string ValidateName(string? name)
    {
        var normalized = name?.Trim() ?? string.Empty;
        if (normalized.Length is 0 or > 120)
            throw new InvalidDataException("Tên project Timeline phải có từ 1 đến 120 ký tự.");
        return normalized;
    }

    public void Validate()
    {
        ValidateId(Id);
        ValidateName(Name);
        ArgumentNullException.ThrowIfNull(Assets);
        var assetIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var asset in Assets)
        {
            asset.Validate();
            if (!assetIds.Add(asset.Id))
                throw new InvalidDataException("Timeline project chứa asset trùng ID.");
        }
        Sequence?.Validate(Assets);
        if (Sequence is not null)
            _ = TimelineAudioTime.MaximumOutputDriftUs(Sequence.OutputFrameRate);
        ArgumentNullException.ThrowIfNull(Items);
        ArgumentNullException.ThrowIfNull(SubtitleCues);
        ArgumentNullException.ThrowIfNull(AudioAssets);
        ArgumentNullException.ThrowIfNull(SourceAudio);
        ArgumentNullException.ThrowIfNull(VoiceCues);
        if (Sequence is null)
        {
            if (Items.Count != 0 || SubtitleCues.Count != 0 || SourceAudio.Count != 0
                || VoiceCues.Count != 0 || Music is not null)
                throw new InvalidDataException("Timeline project không có sequence nhưng chứa edit state.");
        }
        else
        {
            TimelineItemOps.ValidateAll(Items, Sequence.VideoClips);
        }
        TimelineAudioValidation.ValidateProjectAudio(
            Sequence?.VideoClips ?? [],
            Items,
            SubtitleCues,
            AudioAssets,
            SourceAudio,
            VoiceCues,
            Music);
    }
}
