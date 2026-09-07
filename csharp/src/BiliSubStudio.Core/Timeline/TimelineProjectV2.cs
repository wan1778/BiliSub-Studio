namespace BiliSubStudio.Core.Timeline;

// G3 project (MASTER_PLAN section 7), extended with items in G7, typed
// cue/audio ownership in G8, multi-track/transitions/text in G21-G23
// (schema 5), and speed envelopes/keyframes/color grades in G24-G26
// (schema 6, user-approved Concat parity scope 2026-09-08). Older V2
// schemas receive only explicit defaults; schema 1 remains rejected because
// output geometry/FPS cannot be inferred.
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
    IReadOnlyList<TimelineTextItem> TextItems,
    IReadOnlyList<TimelineSpeedEnvelope> SpeedEnvelopes,
    IReadOnlyList<TimelineKeyframes> KeyframeSets,
    IReadOnlyList<TimelineColorGradeSpec> ColorGrades)
{
    public const int Schema = 6;

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
              AudioAssets, SourceAudio, VoiceCues, Music, [], [], [], [], [], [])
    {
    }

    // G24-G26 backward-compatible constructor: schema-5 era call sites pass
    // multi-track state without speed/keyframe/grade state and receive empty
    // G24-G26 collections.
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
        TimelineMusicTrack? Music,
        IReadOnlyList<TimelineTrackV2> Tracks,
        IReadOnlyList<TimelineTransition> Transitions,
        IReadOnlyList<TimelineTextItem> TextItems)
        : this(Id, Name, UpdatedUtc, Assets, Sequence, Items, SubtitleCues,
              AudioAssets, SourceAudio, VoiceCues, Music, Tracks, Transitions,
              TextItems, [], [], [])
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
        ValidateSpeedEnvelopes();
        ValidateKeyframeSets();
        ValidateColorGrades();
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

    // G24 speed envelopes: one per clip maximum, exact tiling of the clip's
    // stored sequence window and source range. Works for primary clips and
    // track clips alike.
    private void ValidateSpeedEnvelopes()
    {
        ArgumentNullException.ThrowIfNull(SpeedEnvelopes);
        var envelopeClipIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var envelope in SpeedEnvelopes)
        {
            if (!envelopeClipIds.Add(envelope.ClipId))
                throw new InvalidDataException("Timeline project chứa speed envelope trùng clip.");
            var primary = Sequence?.VideoClips.FirstOrDefault(clip =>
                string.Equals(clip.Id, envelope.ClipId, StringComparison.Ordinal));
            long sequenceDuration;
            long sourceDuration;
            if (primary is not null)
            {
                sequenceDuration = primary.DurationUs;
                sourceDuration = primary.SourceOutUs - primary.SourceInUs;
            }
            else
            {
                var trackClip = Tracks.SelectMany(track => track.Clips).FirstOrDefault(clip =>
                    string.Equals(clip.Id, envelope.ClipId, StringComparison.Ordinal))
                    ?? throw new InvalidDataException("Timeline speed envelope trỏ clip không tồn tại.");
                sequenceDuration = trackClip.DurationUs;
                sourceDuration = trackClip.SourceOutUs - trackClip.SourceInUs;
            }
            envelope.Validate(sequenceDuration, sourceDuration);
        }
    }

    // G25 keyframe sets: one per clip maximum, offsets bounded to the owning
    // clip's sequence window.
    private void ValidateKeyframeSets()
    {
        ArgumentNullException.ThrowIfNull(KeyframeSets);
        var setClipIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var set in KeyframeSets)
        {
            if (!setClipIds.Add(set.ClipId))
                throw new InvalidDataException("Timeline project chứa keyframes trùng clip.");
            var primary = Sequence?.VideoClips.FirstOrDefault(clip =>
                string.Equals(clip.Id, set.ClipId, StringComparison.Ordinal));
            var durationUs = primary?.DurationUs
                ?? Tracks.SelectMany(track => track.Clips).FirstOrDefault(clip =>
                    string.Equals(clip.Id, set.ClipId, StringComparison.Ordinal))?.DurationUs
                ?? throw new InvalidDataException("Timeline keyframes trỏ clip không tồn tại.");
            set.Validate(durationUs);
        }
    }

    // G26 color grades: one per clip maximum.
    private void ValidateColorGrades()
    {
        ArgumentNullException.ThrowIfNull(ColorGrades);
        var gradeClipIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var spec in ColorGrades)
        {
            spec.Grade.Validate();
            if (!gradeClipIds.Add(spec.ClipId))
                throw new InvalidDataException("Timeline project chứa color grade trùng clip.");
            var primaryExists = (Sequence?.VideoClips ?? []).Any(clip =>
                string.Equals(clip.Id, spec.ClipId, StringComparison.Ordinal));
            var trackExists = Tracks.SelectMany(track => track.Clips).Any(clip =>
                string.Equals(clip.Id, spec.ClipId, StringComparison.Ordinal));
            if (!primaryExists && !trackExists)
                throw new InvalidDataException("Timeline color grade trỏ clip không tồn tại.");
        }
    }
}
