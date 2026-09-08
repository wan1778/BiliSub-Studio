using System.Security.Cryptography;
using System.Text;

namespace BiliSubStudio.Core.Timeline;

public enum TimelineAudioAssetKind
{
    VoiceMaster = 0,
    Music = 1,
}

public enum TimelineVoiceBoundaryPolicy
{
    Reject = 0,
    RemoveAffectedVoice = 1,
}

public static class TimelineAudioTime
{
    public const int OutputSampleRate = 48_000;

    public static long FramesToMicroseconds(long frames, int sampleRate)
    {
        Validate(frames, sampleRate);
        var result = decimal.Round(
            frames * (decimal)TimelineTime.MicrosecondsPerSecond / sampleRate,
            0,
            MidpointRounding.AwayFromZero);
        if (result > long.MaxValue)
            throw new InvalidDataException("Audio duration vượt phạm vi int64 microseconds.");
        return (long)result;
    }

    public static long MicrosecondsToFrames(long microseconds, int sampleRate)
    {
        Validate(microseconds, sampleRate);
        return checked((long)decimal.Round(
            microseconds * (decimal)sampleRate / TimelineTime.MicrosecondsPerSecond,
            0,
            MidpointRounding.AwayFromZero));
    }

    public static long MicrosecondsToFramesFloor(long microseconds, int sampleRate)
    {
        Validate(microseconds, sampleRate);
        return (long)decimal.Floor(
            microseconds * (decimal)sampleRate / TimelineTime.MicrosecondsPerSecond);
    }

    // DEC-019: exact plan timing; mux output may differ by at most the larger
    // of one 48 kHz sample or half one sequence frame.
    public static long MaximumOutputDriftUs(double frameRate)
    {
        if (!double.IsFinite(frameRate) || frameRate <= 0)
            throw new InvalidDataException("Sequence FPS không hợp lệ cho A/V sync.");
        var sampleUs = (long)Math.Ceiling(
            TimelineTime.MicrosecondsPerSecond / (double)OutputSampleRate);
        var halfFrame = TimelineTime.MicrosecondsPerSecond / frameRate / 2;
        if (!double.IsFinite(halfFrame) || halfFrame > long.MaxValue)
            throw new InvalidDataException("Sequence FPS vượt phạm vi A/V sync microseconds.");
        var halfFrameUs = checked((long)Math.Ceiling(halfFrame));
        return Math.Max(sampleUs, halfFrameUs);
    }

    private static void Validate(long value, int sampleRate)
    {
        if (value < 0)
            throw new InvalidDataException("Audio time không được âm.");
        if (sampleRate is < 8_000 or > 384_000)
            throw new InvalidDataException("Audio sample rate không hợp lệ.");
    }
}

public sealed record TimelineAudioAsset(
    string Id,
    TimelineAudioAssetKind Kind,
    string Path,
    long FrameCount,
    int SampleRate,
    int Channels,
    long Size,
    long LastWriteUtcTicks,
    string Sha256)
{
    public static string NewId() => Guid.NewGuid().ToString("N");

    public long DurationUs => TimelineAudioTime.FramesToMicroseconds(FrameCount, SampleRate);

    public void Validate()
    {
        TimelineAudioValidation.ValidateId(Id, "audio asset");
        if (!Enum.IsDefined(Kind))
            throw new InvalidDataException("Timeline audio asset kind không hợp lệ.");
        if (string.IsNullOrWhiteSpace(Path) || Path.Length > 32_767)
            throw new InvalidDataException("Timeline audio asset path không hợp lệ.");
        if (FrameCount <= 0)
            throw new InvalidDataException("Timeline audio frame count không hợp lệ.");
        _ = DurationUs;
        if (Channels is < 1 or > 8)
            throw new InvalidDataException("Timeline audio channel count không hợp lệ.");
        if (Size <= 0 || LastWriteUtcTicks < 0)
            throw new InvalidDataException("Timeline audio fingerprint không hợp lệ.");
        TimelineAudioValidation.ValidateSha256(Sha256, "audio asset");
    }
}

public sealed record TimelineSourceAudioState(string ClipId, double Gain)
{
    public static TimelineSourceAudioState Default(string clipId) => new(clipId, 1);

    public void Validate()
    {
        TimelineAudioValidation.ValidateId(ClipId, "source audio clip");
        if (!double.IsFinite(Gain) || Gain is < 0 or > 2)
            throw new InvalidDataException("Timeline source audio gain phải nằm trong 0..2.");
    }

    public static IReadOnlyList<TimelineSourceAudioState> Defaults(
        IReadOnlyList<TimelineClip> clips) =>
        clips.Select(clip => Default(clip.Id)).ToArray();

    public static void ValidateAll(
        IReadOnlyList<TimelineSourceAudioState> states,
        IReadOnlyList<TimelineClip> clips)
    {
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(clips);
        var clipIds = clips.Select(clip => clip.Id).ToHashSet(StringComparer.Ordinal);
        var stateIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var state in states)
        {
            state.Validate();
            if (!stateIds.Add(state.ClipId) || !clipIds.Contains(state.ClipId))
                throw new InvalidDataException("Timeline source audio owner không hợp lệ.");
        }
        if (!stateIds.SetEquals(clipIds))
            throw new InvalidDataException("Mỗi Timeline clip phải có đúng một source audio state.");
    }
}

// StartUs/EndUs use the same coordinate space as the owning subtitle item:
// clip-local for a clip SRT and sequence-global for a global SRT.
public sealed record TimelineSubtitleCue(
    string Id,
    string SubtitleItemId,
    long StartUs,
    long EndUs,
    string SpokenText,
    string SpeechRevisionSha256)
{
    public static string NewId() => Guid.NewGuid().ToString("N");
    public long DurationUs => checked(EndUs - StartUs);

    public static TimelineSubtitleCue Create(
        string subtitleItemId,
        long startUs,
        long endUs,
        string spokenText)
    {
        var revision = ComputeSpeechRevision(spokenText, checked(endUs - startUs));
        return new TimelineSubtitleCue(NewId(), subtitleItemId, startUs, endUs, spokenText.Trim(), revision);
    }

    public static string ComputeSpeechRevision(string spokenText, long durationUs)
    {
        if (string.IsNullOrWhiteSpace(spokenText) || durationUs <= 0)
            throw new InvalidDataException("Timeline subtitle spoken input không hợp lệ.");
        var canonical = durationUs.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "\n" + spokenText.Trim();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    public void Validate()
    {
        TimelineAudioValidation.ValidateId(Id, "subtitle cue");
        TimelineAudioValidation.ValidateId(SubtitleItemId, "subtitle item");
        if (StartUs < 0 || EndUs <= StartUs)
            throw new InvalidDataException("Timeline subtitle cue range không hợp lệ.");
        if (string.IsNullOrWhiteSpace(SpokenText) || SpokenText.Length > 16_384)
            throw new InvalidDataException("Timeline subtitle spoken text không hợp lệ.");
        TimelineAudioValidation.ValidateSha256(SpeechRevisionSha256, "subtitle speech revision");
        if (!string.Equals(
                SpeechRevisionSha256,
                ComputeSpeechRevision(SpokenText, DurationUs),
                StringComparison.Ordinal))
            throw new InvalidDataException("Timeline subtitle speech revision đã stale.");
    }
}

public sealed record TimelineVoiceCue(
    string Id,
    string SubtitleCueId,
    string AudioAssetId,
    long SourceStartFrame,
    long FrameCount,
    double Gain,
    string CueRevisionSha256,
    TimelineVoiceFilter? Filter = null)
{
    public static string NewId() => Guid.NewGuid().ToString("N");

    public void ValidateShape()
    {
        TimelineAudioValidation.ValidateId(Id, "voice cue");
        TimelineAudioValidation.ValidateId(SubtitleCueId, "voice subtitle cue");
        TimelineAudioValidation.ValidateId(AudioAssetId, "voice audio asset");
        if (SourceStartFrame < 0 || FrameCount <= 0)
            throw new InvalidDataException("Timeline voice source frame range không hợp lệ.");
        if (!double.IsFinite(Gain) || Gain is < 0 or > 4)
            throw new InvalidDataException("Timeline voice gain phải nằm trong 0..4.");
        Filter?.Validate();
        TimelineAudioValidation.ValidateSha256(CueRevisionSha256, "voice cue revision");
    }
}
public sealed record TimelineMusicTrack(
    string Id,
    string AudioAssetId,
    long SequenceStartUs,
    long SourceStartFrame,
    long FrameCount,
    double Gain)
{
    public static string NewId() => Guid.NewGuid().ToString("N");

    public void ValidateShape()
    {
        TimelineAudioValidation.ValidateId(Id, "music track");
        TimelineAudioValidation.ValidateId(AudioAssetId, "music audio asset");
        if (SequenceStartUs < 0 || SourceStartFrame < 0 || FrameCount <= 0)
            throw new InvalidDataException("Timeline music range không hợp lệ.");
        if (!double.IsFinite(Gain) || Gain is < 0 or > 2)
            throw new InvalidDataException("Timeline music gain phải nằm trong 0..2.");
    }
}

public static class TimelineAudioValidation
{
    public static void ValidateProjectAudio(
        IReadOnlyList<TimelineClip> clips,
        IReadOnlyList<TimelineItem> items,
        IReadOnlyList<TimelineSubtitleCue> subtitleCues,
        IReadOnlyList<TimelineAudioAsset> audioAssets,
        IReadOnlyList<TimelineSourceAudioState> sourceAudio,
        IReadOnlyList<TimelineVoiceCue> voiceCues,
        TimelineMusicTrack? music)
    {
        ArgumentNullException.ThrowIfNull(subtitleCues);
        ArgumentNullException.ThrowIfNull(audioAssets);
        ArgumentNullException.ThrowIfNull(voiceCues);
        TimelineSourceAudioState.ValidateAll(sourceAudio, clips);

        var itemsById = items.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var cuesById = new Dictionary<string, TimelineSubtitleCue>(StringComparer.Ordinal);
        foreach (var cue in subtitleCues)
        {
            cue.Validate();
            if (!cuesById.TryAdd(cue.Id, cue))
                throw new InvalidDataException("Timeline subtitle cue trùng ID.");
            if (!itemsById.TryGetValue(cue.SubtitleItemId, out var item)
                || item.Kind != TimelineItemKind.Subtitle)
                throw new InvalidDataException("Timeline subtitle cue không có subtitle item owner.");
            var ownerStartUs = item.IsSequenceGlobal ? item.SequenceStartUs : item.ClipStartUs;
            var ownerEndUs = item.IsSequenceGlobal ? item.SequenceEndUs : item.ClipEndUs;
            if (cue.StartUs < ownerStartUs || cue.EndUs > ownerEndUs)
                throw new InvalidDataException("Timeline subtitle cue vượt range của subtitle item.");
        }

        var assetsById = new Dictionary<string, TimelineAudioAsset>(StringComparer.Ordinal);
        foreach (var asset in audioAssets)
        {
            asset.Validate();
            if (!assetsById.TryAdd(asset.Id, asset))
                throw new InvalidDataException("Timeline audio asset trùng ID.");
        }

        var voiceIds = new HashSet<string>(StringComparer.Ordinal);
        var voicedCueIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var voice in voiceCues)
        {
            voice.ValidateShape();
            if (!voiceIds.Add(voice.Id) || !voicedCueIds.Add(voice.SubtitleCueId))
                throw new InvalidDataException("Timeline voice cue bị trùng owner.");
            if (!cuesById.TryGetValue(voice.SubtitleCueId, out var cue))
                throw new InvalidDataException("Timeline voice trỏ subtitle cue không tồn tại.");
            if (!assetsById.TryGetValue(voice.AudioAssetId, out var asset)
                || asset.Kind != TimelineAudioAssetKind.VoiceMaster)
                throw new InvalidDataException("Timeline voice trỏ audio asset không hợp lệ.");
            if (voice.FrameCount > asset.FrameCount
                || voice.SourceStartFrame > asset.FrameCount - voice.FrameCount)
                throw new InvalidDataException("Timeline voice vượt source frame range.");
            if (voice.FrameCount != TimelineAudioTime.MicrosecondsToFrames(cue.DurationUs, asset.SampleRate))
                throw new InvalidDataException("Timeline voice duration không khớp subtitle cue.");
            if (!string.Equals(voice.CueRevisionSha256, cue.SpeechRevisionSha256, StringComparison.Ordinal))
                throw new InvalidDataException("Timeline voice đã stale so với subtitle cue.");
        }

        var legacyMusicCount = items.Count(item => item.Kind == TimelineItemKind.Music);
        if (legacyMusicCount + (music is null ? 0 : 1) > 1)
            throw new InvalidDataException("G8 MVP chỉ hỗ trợ một music track.");
        if (music is not null)
        {
            music.ValidateShape();
            if (!assetsById.TryGetValue(music.AudioAssetId, out var asset)
                || asset.Kind != TimelineAudioAssetKind.Music)
                throw new InvalidDataException("Timeline music trỏ audio asset không hợp lệ.");
            if (music.FrameCount > asset.FrameCount
                || music.SourceStartFrame > asset.FrameCount - music.FrameCount)
                throw new InvalidDataException("Timeline music vượt source frame range.");
        }
    }

    public static void ValidateId(string? value, string owner)
    {
        if (value is not { Length: 32 } || !value.All(char.IsAsciiHexDigit))
            throw new InvalidDataException($"Timeline {owner} ID không hợp lệ.");
    }

    public static void ValidateSha256(string? value, string owner)
    {
        if (value is not { Length: 64 } || !value.All(char.IsAsciiHexDigit)
            || !string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal))
            throw new InvalidDataException($"Timeline {owner} SHA-256 không hợp lệ.");
    }
}

public static class TimelineSubtitleCueOps
{
    public static IReadOnlyList<TimelineSubtitleCue> Trim(
        IReadOnlyList<TimelineSubtitleCue> cues,
        IReadOnlyList<TimelineItem> oldItems,
        IReadOnlyList<TimelineItem> newItems,
        TimelineClip oldClip,
        TimelineClip newClip)
    {
        var oldItemsById = oldItems.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var newItemsById = newItems.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var removedHead = Math.Round(
            (newClip.SourceInUs - oldClip.SourceInUs) / oldClip.PlaybackRate,
            MidpointRounding.AwayFromZero);
        if (removedHead < long.MinValue || removedHead > long.MaxValue)
            throw new InvalidDataException("Subtitle trim vượt phạm vi int64 microseconds.");
        var removedHeadUs = (long)removedHead;
        var result = new List<TimelineSubtitleCue>(cues.Count);
        foreach (var cue in cues)
        {
            var oldOwner = oldItemsById[cue.SubtitleItemId];
            if (oldOwner.IsSequenceGlobal
                || !string.Equals(oldOwner.ClipId, oldClip.Id, StringComparison.Ordinal))
            {
                result.Add(cue);
                continue;
            }
            if (!newItemsById.TryGetValue(cue.SubtitleItemId, out var newOwner))
                continue;
            var startUs = Math.Max(newOwner.ClipStartUs, checked(cue.StartUs - removedHeadUs));
            var endUs = Math.Min(newOwner.ClipEndUs, checked(cue.EndUs - removedHeadUs));
            if (startUs >= endUs)
                continue;
            result.Add(WithRange(cue, cue.SubtitleItemId, startUs, endUs));
        }
        return result;
    }

    public static IReadOnlyList<TimelineSubtitleCue> Split(
        IReadOnlyList<TimelineSubtitleCue> cues,
        IReadOnlyList<TimelineItem> oldItems,
        TimelineItemSplitResult itemSplit,
        IReadOnlyList<TimelineItem> finalItems,
        string oldClipId,
        long cutLocalUs)
    {
        var oldItemsById = oldItems.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var result = new List<TimelineSubtitleCue>(cues.Count + 2);
        foreach (var cue in cues)
        {
            var owner = oldItemsById[cue.SubtitleItemId];
            if (owner.IsSequenceGlobal || !string.Equals(owner.ClipId, oldClipId, StringComparison.Ordinal))
            {
                result.Add(cue);
                continue;
            }
            if (cue.EndUs <= cutLocalUs)
            {
                result.Add(cue);
            }
            else if (cue.StartUs >= cutLocalUs)
            {
                var ownerId = itemSplit.RightItemIdsByOriginalId.TryGetValue(owner.Id, out var rightOwnerId)
                    ? rightOwnerId
                    : owner.Id;
                result.Add(WithRange(
                    cue, ownerId, cue.StartUs - cutLocalUs, cue.EndUs - cutLocalUs));
            }
            else
            {
                if (!itemSplit.RightItemIdsByOriginalId.TryGetValue(owner.Id, out var rightOwnerId))
                    throw new InvalidDataException("Subtitle cue split thiếu right item owner.");
                result.Add(WithRange(cue, owner.Id, cue.StartUs, cutLocalUs));
                var rightDurationUs = cue.EndUs - cutLocalUs;
                result.Add(new TimelineSubtitleCue(
                    TimelineSubtitleCue.NewId(),
                    rightOwnerId,
                    0,
                    rightDurationUs,
                    cue.SpokenText,
                    TimelineSubtitleCue.ComputeSpeechRevision(cue.SpokenText, rightDurationUs)));
            }
        }
        var finalItemsById = finalItems.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var clamped = new List<TimelineSubtitleCue>(result.Count);
        foreach (var cue in result)
        {
            var owner = finalItemsById[cue.SubtitleItemId];
            var ownerStartUs = owner.IsSequenceGlobal ? owner.SequenceStartUs : owner.ClipStartUs;
            var ownerEndUs = owner.IsSequenceGlobal ? owner.SequenceEndUs : owner.ClipEndUs;
            var startUs = Math.Max(cue.StartUs, ownerStartUs);
            var endUs = Math.Min(cue.EndUs, ownerEndUs);
            if (startUs < endUs)
                clamped.Add(WithRange(cue, cue.SubtitleItemId, startUs, endUs));
        }
        return clamped;
    }

    private static TimelineSubtitleCue WithRange(
        TimelineSubtitleCue cue,
        string ownerId,
        long startUs,
        long endUs)
    {
        var durationUs = checked(endUs - startUs);
        return cue with
        {
            SubtitleItemId = ownerId,
            StartUs = startUs,
            EndUs = endUs,
            SpeechRevisionSha256 = durationUs == cue.DurationUs
                ? cue.SpeechRevisionSha256
                : TimelineSubtitleCue.ComputeSpeechRevision(cue.SpokenText, durationUs),
        };
    }
}

public sealed record TimelineAudioEditState(
    TimelineEditState Timeline,
    IReadOnlyList<TimelineSubtitleCue> SubtitleCues,
    IReadOnlyList<TimelineAudioAsset> AudioAssets,
    IReadOnlyList<TimelineSourceAudioState> SourceAudio,
    IReadOnlyList<TimelineVoiceCue> VoiceCues,
    TimelineMusicTrack? Music,
    IReadOnlyList<TimelineTrackV2>? Tracks = null,
    IReadOnlyList<TimelineTextItem>? TextItems = null)
{
    public static TimelineAudioEditState Create(
        TimelineEditState timeline,
        IReadOnlyList<TimelineSubtitleCue>? subtitleCues = null,
        IReadOnlyList<TimelineAudioAsset>? audioAssets = null,
        IReadOnlyList<TimelineSourceAudioState>? sourceAudio = null,
        IReadOnlyList<TimelineVoiceCue>? voiceCues = null,
        TimelineMusicTrack? music = null,
        IReadOnlyList<TimelineTrackV2>? tracks = null,
        IReadOnlyList<TimelineTextItem>? textItems = null)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        var frozenTimeline = TimelineEditState.Create(timeline.Clips, timeline.Items);
        var frozenCues = (subtitleCues ?? []).ToArray();
        var frozenAssets = (audioAssets ?? []).ToArray();
        var frozenSource = (sourceAudio ?? TimelineSourceAudioState.Defaults(timeline.Clips)).ToArray();
        var frozenVoices = (voiceCues ?? []).ToArray();
        TimelineAudioValidation.ValidateProjectAudio(
            frozenTimeline.Clips,
            frozenTimeline.Items,
            frozenCues,
            frozenAssets,
            frozenSource,
            frozenVoices,
            music);
        var frozenTracks = (tracks ?? []).ToArray();
        var frozenTextItems = (textItems ?? []).ToArray();
        return new TimelineAudioEditState(
            frozenTimeline, frozenCues, frozenAssets, frozenSource, frozenVoices, music,
            frozenTracks, frozenTextItems);
    }
}

public static class TimelineAudioDependencyOps
{
    public static TimelineAudioEditState UpdateSubtitleCue(
        TimelineAudioEditState state,
        TimelineSubtitleCue updatedCue,
        TimelineVoiceBoundaryPolicy voicePolicy = TimelineVoiceBoundaryPolicy.Reject)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(updatedCue);
        ValidatePolicy(voicePolicy);
        updatedCue.Validate();
        var index = state.SubtitleCues.ToList().FindIndex(cue => cue.Id == updatedCue.Id);
        if (index < 0)
            throw new InvalidDataException("Timeline subtitle cue không tồn tại để cập nhật.");
        var cues = state.SubtitleCues.ToArray();
        cues[index] = updatedCue;
        var voices = ReconcileVoices(state.VoiceCues, cues, voicePolicy);
        return TimelineAudioEditState.Create(
            state.Timeline, cues, state.AudioAssets, state.SourceAudio, voices, state.Music,
            state.Tracks, state.TextItems);
    }

    public static TimelineAudioEditState Reorder(
        TimelineAudioEditState state,
        string clipId,
        int newPosition)
    {
        ArgumentNullException.ThrowIfNull(state);
        return TimelineAudioEditState.Create(
            TimelineDependencyOps.Reorder(state.Timeline, clipId, newPosition),
            state.SubtitleCues,
            state.AudioAssets,
            state.SourceAudio,
            state.VoiceCues,
            state.Music,
            state.Tracks,
            state.TextItems);
    }

    public static TimelineAudioEditState Trim(
        TimelineAudioEditState state,
        string clipId,
        long newSourceInUs,
        long newSourceOutUs,
        TimelineVoiceBoundaryPolicy voicePolicy = TimelineVoiceBoundaryPolicy.Reject)
    {
        ArgumentNullException.ThrowIfNull(state);
        ValidatePolicy(voicePolicy);
        var oldClip = state.Timeline.Clips.Single(clip => clip.Id == clipId);
        var timeline = TimelineDependencyOps.Trim(
            state.Timeline, clipId, newSourceInUs, newSourceOutUs);
        var newClip = timeline.Clips.Single(clip => clip.Id == clipId);
        var cues = TimelineSubtitleCueOps.Trim(
            state.SubtitleCues, state.Timeline.Items, timeline.Items, oldClip, newClip);
        var voices = ReconcileVoices(state.VoiceCues, cues, voicePolicy);
        return TimelineAudioEditState.Create(
            timeline, cues, state.AudioAssets, state.SourceAudio, voices, state.Music,
            state.Tracks, state.TextItems);
    }

    public static TimelineAudioEditState Split(
        TimelineAudioEditState state,
        string clipId,
        long cutSequenceUs,
        TimelineSubtitleSplitPolicy subtitlePolicy = TimelineSubtitleSplitPolicy.Reject,
        TimelineVoiceBoundaryPolicy voicePolicy = TimelineVoiceBoundaryPolicy.Reject)
    {
        ArgumentNullException.ThrowIfNull(state);
        ValidatePolicy(voicePolicy);
        var oldClip = state.Timeline.Clips.Single(clip => clip.Id == clipId);
        var clips = TimelineSequenceOps.Split(state.Timeline.Clips, clipId, cutSequenceUs);
        var leftClip = clips.Single(clip => clip.Id == clipId);
        var rightClip = clips.Single(clip => !state.Timeline.Clips.Any(old => old.Id == clip.Id));
        var itemSplit = TimelineItemOps.SplitItemsWithOwnership(
            state.Timeline.Items,
            clipId,
            clipId,
            rightClip.Id,
            leftClip.DurationUs,
            subtitlePolicy);
        var items = TimelineItemOps.RideClips(itemSplit.Items, clips);
        var timeline = TimelineEditState.Create(clips, items);
        var cues = TimelineSubtitleCueOps.Split(
            state.SubtitleCues,
            state.Timeline.Items,
            itemSplit,
            items,
            clipId,
            leftClip.DurationUs);
        var voices = ReconcileVoices(state.VoiceCues, cues, voicePolicy);
        var sourceByClipId = state.SourceAudio.ToDictionary(source => source.ClipId, StringComparer.Ordinal);
        var splitSource = sourceByClipId[clipId];
        var sourceAudio = clips
            .Select(clip => clip.Id == rightClip.Id
                ? splitSource with { ClipId = rightClip.Id }
                : sourceByClipId[clip.Id])
            .ToArray();
        return TimelineAudioEditState.Create(
            timeline, cues, state.AudioAssets, sourceAudio, voices, state.Music,
            state.Tracks, state.TextItems);
    }

    public static TimelineAudioEditState Delete(TimelineAudioEditState state, string clipId)
    {
        ArgumentNullException.ThrowIfNull(state);
        var timeline = TimelineDependencyOps.Delete(state.Timeline, clipId);
        var itemIds = timeline.Items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var cues = state.SubtitleCues
            .Where(cue => itemIds.Contains(cue.SubtitleItemId))
            .ToArray();
        var voices = ReconcileVoices(
            state.VoiceCues, cues, TimelineVoiceBoundaryPolicy.RemoveAffectedVoice);
        var sourceAudio = state.SourceAudio
            .Where(source => !string.Equals(source.ClipId, clipId, StringComparison.Ordinal))
            .ToArray();
        return TimelineAudioEditState.Create(
            timeline, cues, state.AudioAssets, sourceAudio, voices, state.Music,
            state.Tracks, state.TextItems);
    }

    private static IReadOnlyList<TimelineVoiceCue> ReconcileVoices(
        IReadOnlyList<TimelineVoiceCue> voices,
        IReadOnlyList<TimelineSubtitleCue> cues,
        TimelineVoiceBoundaryPolicy policy)
    {
        var cuesById = cues.ToDictionary(cue => cue.Id, StringComparer.Ordinal);
        var affected = voices
            .Where(voice => !cuesById.TryGetValue(voice.SubtitleCueId, out var cue)
                || !string.Equals(voice.CueRevisionSha256, cue.SpeechRevisionSha256, StringComparison.Ordinal))
            .Select(voice => voice.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (affected.Count > 0 && policy == TimelineVoiceBoundaryPolicy.Reject)
            throw new InvalidOperationException(
                "Edit thay đổi spoken cue; cần explicit policy để xóa voice stale.");
        return voices.Where(voice => !affected.Contains(voice.Id)).ToArray();
    }

    private static void ValidatePolicy(TimelineVoiceBoundaryPolicy policy)
    {
        if (!Enum.IsDefined(policy))
            throw new InvalidDataException("Timeline voice boundary policy không hợp lệ.");
    }
}

public sealed class TimelineAudioEditHistory
{
    private readonly List<TimelineAudioEditState> _undo = [];
    private readonly List<TimelineAudioEditState> _redo = [];
    private readonly object _gate = new();

    public const int MaxEntries = 200;
    public int UndoCount { get { lock (_gate) return _undo.Count; } }
    public int RedoCount { get { lock (_gate) return _redo.Count; } }

    public void Commit(TimelineAudioEditState before, TimelineAudioEditState after)
    {
        var frozenBefore = Freeze(before);
        _ = Freeze(after);
        lock (_gate)
        {
            _undo.Add(frozenBefore);
            if (_undo.Count > MaxEntries)
                _undo.RemoveAt(0);
            _redo.Clear();
        }
    }

    public TimelineAudioEditState Undo(TimelineAudioEditState current)
    {
        ArgumentNullException.ThrowIfNull(current);
        lock (_gate)
        {
            if (_undo.Count == 0)
                return current;
            var previous = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);
            _redo.Add(Freeze(current));
            return previous;
        }
    }

    public TimelineAudioEditState Redo(TimelineAudioEditState current)
    {
        ArgumentNullException.ThrowIfNull(current);
        lock (_gate)
        {
            if (_redo.Count == 0)
                return current;
            var next = _redo[^1];
            _redo.RemoveAt(_redo.Count - 1);
            _undo.Add(Freeze(current));
            return next;
        }
    }

    private static TimelineAudioEditState Freeze(TimelineAudioEditState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return TimelineAudioEditState.Create(
            state.Timeline,
            state.SubtitleCues,
            state.AudioAssets,
            state.SourceAudio,
            state.VoiceCues,
            state.Music,
            state.Tracks,
            state.TextItems);
    }
}
