namespace BiliSubStudio.Core.Timeline;

// G3 project (MASTER_PLAN section 7), extended with items in G7, typed
// cue/audio ownership in G8, and multi-track/transitions/text in G21-G23
// (schema 5, user-approved Concat parity scope 2026-09-08). Older V2 schemas
// receive only explicit defaults; schema 1 remains rejected because output
// geometry/FPS cannot be inferred.
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
    TimelineMusicTrack? Music,
    IReadOnlyList<TimelineTrackV2> Tracks,
    IReadOnlyList<TimelineTransition> Transitions,
    IReadOnlyList<TimelineTextItem> TextItems)
{
    public const int Schema = 5;

    // G21 backward-compatible constructor: pre-multi-track call sites (schema-4
    // era tests, legacy migration fixtures) construct with the classic 11
    // fields and receive empty multi-track state.
    public TimelineProjectV2(
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
        : this(Id, Name, UpdatedUtc, Assets, Sequence, Items, SubtitleCues,
              AudioAssets, SourceAudio, VoiceCues, Music, [], [], [])
    {
    }

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
        ValidateTracks(Sequence);
        ValidateTransitions();
        ValidateTextItems();
    }

    // G21 track ownership and cross-references. Tracks are optional for legacy
    // schema-4 payloads but once present must be internally consistent.
    private void ValidateTracks(TimelineSequence? sequence)
    {
        ArgumentNullException.ThrowIfNull(Tracks);
        var trackIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var track in Tracks)
        {
            track.Validate();
            if (!trackIds.Add(track.Id))
                throw new InvalidDataException("Timeline project chứa track trùng ID.");
        }
        var knownAssetIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var asset in Assets)
            _ = knownAssetIds.Add(asset.Id);
        foreach (var track in Tracks)
        {
            foreach (var clip in track.Clips)
            {
                if (!knownAssetIds.Contains(clip.AssetId))
                    throw new InvalidDataException("Timeline track clip trỏ asset không tồn tại.");
                var asset = Assets.Single(item => string.Equals(item.Id, clip.AssetId, StringComparison.Ordinal));
                if (clip.SourceOutUs > asset.DurationUs)
                    throw new InvalidDataException("Timeline track clip source range vượt quá asset duration.");
            }
        }
        // Overlay text tracks must exist for text items.
        var textTrackIds = Tracks
            .Where(track => track.Kind == TimelineTrackKind.Text)
            .Select(track => track.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var text in TextItems)
        {
            if (!textTrackIds.Contains(text.TrackId))
                throw new InvalidDataException("Timeline text item trỏ track text không tồn tại.");
        }
        _ = sequence; // primary sequence validation happens in Sequence.Validate.
    }

    private void ValidateTransitions()
    {
        ArgumentNullException.ThrowIfNull(Transitions);
        var clipIds = new HashSet<string>(StringComparer.Ordinal);
        if (Sequence is not null)
            foreach (var clip in Sequence.VideoClips)
                _ = clipIds.Add(clip.Id);
        foreach (var track in Tracks)
            foreach (var clip in track.Clips)
                _ = clipIds.Add(clip.Id);
        var transitionIds = new HashSet<string>(StringComparer.Ordinal);
        var seenPairs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var transition in Transitions)
        {
            transition.Validate();
            if (!transitionIds.Add(transition.Id))
                throw new InvalidDataException("Timeline project chứa transition trùng ID.");
            if (!clipIds.Contains(transition.FromClipId)
                || !clipIds.Contains(transition.ToClipId))
                throw new InvalidDataException("Timeline transition trỏ clip không tồn tại.");
            var pairKey = transition.FromClipId + ">" + transition.ToClipId;
            if (!seenPairs.Add(pairKey))
                throw new InvalidDataException("Timeline project chứa transition trùng cặp clip.");
        }
    }

    private void ValidateTextItems()
    {
        ArgumentNullException.ThrowIfNull(TextItems);
        var textIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in TextItems)
        {
            text.Validate();
            if (!textIds.Add(text.Id))
                throw new InvalidDataException("Timeline project chứa text item trùng ID.");
            if (text.ClipId is not null)
            {
                var clipExists = (Sequence?.VideoClips ?? [])
                    .Any(clip => string.Equals(clip.Id, text.ClipId, StringComparison.Ordinal))
                    || Tracks.SelectMany(track => track.Clips)
                        .Any(clip => string.Equals(clip.Id, text.ClipId, StringComparison.Ordinal));
                if (!clipExists)
                    throw new InvalidDataException("Timeline text item trỏ clip không tồn tại.");
            }
        }
    }
}
