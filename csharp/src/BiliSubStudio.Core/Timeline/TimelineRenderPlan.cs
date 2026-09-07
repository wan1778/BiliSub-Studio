namespace BiliSubStudio.Core.Timeline;

// G2 immutable render-plan shell, resolved in G3. Preview and Export share
// this one plan type, distinct from the classic VideoEditRequest path
// (DEC-013). G3 resolves the ordered clip segments; segment rendering
// arrives in G10.
public sealed record TimelineResolvedClip(
    string ClipId,
    string AssetId,
    long SequenceStartUs,
    long SequenceEndUs,
    long SourceInUs,
    long SourceOutUs,
    double PlaybackRate = 1)
{
    public long SequenceDurationUs => checked(SequenceEndUs - SequenceStartUs);
}

public sealed record TimelineResolvedItem(
    string ItemId,
    TimelineItemKind Kind,
    string? ClipId,
    long SequenceStartUs,
    long SequenceEndUs,
    string Payload);

public sealed record TimelineResolvedSubtitleCue(
    string CueId,
    string SubtitleItemId,
    string? ClipId,
    long SequenceStartUs,
    long SequenceEndUs,
    string SpokenText,
    string SpeechRevisionSha256);

public sealed record TimelineResolvedSourceAudio(
    string ClipId,
    string AssetId,
    long SequenceStartUs,
    long SequenceEndUs,
    long SourceInUs,
    long SourceOutUs,
    double PlaybackRate,
    double Gain,
    bool HasSourceStream);

public sealed record TimelineResolvedVoiceCue(
    string VoiceCueId,
    string SubtitleCueId,
    string AudioAssetId,
    string Path,
    string Sha256,
    long SequenceStartUs,
    long SequenceEndUs,
    long SourceStartFrame,
    long FrameCount,
    int SampleRate,
    int Channels,
    double Gain);

public sealed record TimelineResolvedMusic(
    string MusicId,
    string? AudioAssetId,
    string Path,
    string? Sha256,
    long SequenceStartUs,
    long SequenceEndUs,
    long SourceStartFrame,
    long FrameCount,
    int SampleRate,
    int Channels,
    double Gain,
    bool IsVerified);

public sealed record TimelineResolvedAudioPlan(
    int OutputSampleRate,
    double SequenceFrameRate,
    long MaximumOutputDriftUs,
    IReadOnlyList<TimelineResolvedSourceAudio> SourceAudio,
    IReadOnlyList<TimelineResolvedVoiceCue> VoiceCues,
    TimelineResolvedMusic? Music)
{
    public static TimelineResolvedAudioPlan Empty { get; } =
        new(TimelineAudioTime.OutputSampleRate, 0, 0, [], [], null);

    public bool HasAudibleAudio => SourceAudio.Any(source => source.HasSourceStream && source.Gain > 0)
        || VoiceCues.Any(voice => voice.Gain > 0)
        || Music is { Gain: > 0 };

    public bool HasUnverifiedAudio => Music is { IsVerified: false };
}

public sealed record TimelineRenderPlan(
    string ProjectId,
    long CreatedUtcTicks,
    int OutputWidth,
    int OutputHeight,
    double OutputFrameRate,
    IReadOnlyList<TimelineResolvedClip> Clips,
    IReadOnlyList<TimelineResolvedItem> Items,
    IReadOnlyList<TimelineResolvedSubtitleCue> SubtitleCues,
    TimelineResolvedAudioPlan Audio)
{
    public long TotalDurationUs => Clips.Count == 0 ? 0 : Clips.Max(clip => clip.SequenceEndUs);

    public static TimelineRenderPlan Empty(string projectId)
    {
        TimelineProjectV2.ValidateId(projectId);
        return new TimelineRenderPlan(
            projectId, DateTimeOffset.UtcNow.Ticks,
            OutputWidth: 0, OutputHeight: 0, OutputFrameRate: 0,
            [], [], [], TimelineResolvedAudioPlan.Empty);
    }

    public void ValidateOutputGeometry()
    {
        if (OutputWidth <= 0 || OutputHeight <= 0)
            throw new InvalidDataException("Timeline render plan thiếu output geometry.");
        if (!double.IsFinite(OutputFrameRate) || OutputFrameRate <= 0)
            throw new InvalidDataException("Timeline render plan thiếu sequence FPS hữu hạn dương.");
        _ = TimelineAudioTime.MaximumOutputDriftUs(OutputFrameRate);
    }
}

public static class TimelineCompiler
{
    public static TimelineRenderPlan Compile(TimelineProjectV2 project)
    {
        ArgumentNullException.ThrowIfNull(project);
        project.Validate();
        if (project.Sequence is null)
            return TimelineRenderPlan.Empty(project.Id);
        var resolvedClips = project.Sequence.VideoClips
            .OrderBy(clip => clip.TimelineStartUs)
            .Select(clip => new TimelineResolvedClip(
                clip.Id, clip.AssetId, clip.TimelineStartUs, clip.TimelineEndUs,
                clip.SourceInUs, clip.SourceOutUs, clip.PlaybackRate))
            .ToArray();
        var clipsById = project.Sequence.VideoClips.ToDictionary(clip => clip.Id, StringComparer.Ordinal);
        var resolvedItems = project.Items
            .Select(item =>
            {
                if (item.IsSequenceGlobal)
                    return new TimelineResolvedItem(
                        item.Id, item.Kind, null, item.SequenceStartUs, item.SequenceEndUs, item.Payload);
                var clip = clipsById[item.ClipId!];
                return new TimelineResolvedItem(
                    item.Id,
                    item.Kind,
                    item.ClipId,
                    checked(clip.TimelineStartUs + item.ClipStartUs),
                    checked(clip.TimelineStartUs + item.ClipEndUs),
                    item.Payload);
            })
            .OrderBy(item => item.SequenceStartUs)
            .ThenBy(item => item.Kind)
            .ThenBy(item => item.ItemId, StringComparer.Ordinal)
            .ToArray();
        var itemsById = project.Items.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var resolvedSubtitleCueItems = new List<TimelineResolvedSubtitleCue>(project.SubtitleCues.Count);
        foreach (var cue in project.SubtitleCues)
        {
            var owner = itemsById[cue.SubtitleItemId];
            var clip = owner.IsSequenceGlobal ? null : clipsById[owner.ClipId!];
            var startUs = clip is null
                ? cue.StartUs
                : checked(clip.TimelineStartUs + cue.StartUs);
            var endUs = clip is null
                ? cue.EndUs
                : checked(clip.TimelineStartUs + cue.EndUs);
            if (project.Sequence.TotalDurationUs == 0
                || startUs >= project.Sequence.TotalDurationUs)
                continue;
            endUs = Math.Min(endUs, project.Sequence.TotalDurationUs);
            resolvedSubtitleCueItems.Add(new TimelineResolvedSubtitleCue(
                cue.Id,
                cue.SubtitleItemId,
                owner.ClipId,
                startUs,
                endUs,
                cue.SpokenText,
                cue.SpeechRevisionSha256));
        }
        var resolvedSubtitleCues = resolvedSubtitleCueItems
            .OrderBy(cue => cue.SequenceStartUs)
            .ThenBy(cue => cue.CueId, StringComparer.Ordinal)
            .ToArray();
        var mediaAssetsById = project.Assets.ToDictionary(asset => asset.Id, StringComparer.Ordinal);
        var sourceAudioByClipId = project.SourceAudio.ToDictionary(source => source.ClipId, StringComparer.Ordinal);
        var resolvedSourceAudio = project.Sequence.VideoClips
            .OrderBy(clip => clip.TimelineStartUs)
            .Select(clip =>
            {
                var asset = mediaAssetsById[clip.AssetId];
                var source = sourceAudioByClipId[clip.Id];
                return new TimelineResolvedSourceAudio(
                    clip.Id,
                    clip.AssetId,
                    clip.TimelineStartUs,
                    clip.TimelineEndUs,
                    clip.SourceInUs,
                    clip.SourceOutUs,
                    clip.PlaybackRate,
                    source.Gain,
                    asset.HasAudio);
            })
            .ToArray();
        var audioAssetsById = project.AudioAssets.ToDictionary(asset => asset.Id, StringComparer.Ordinal);
        var cuesById = project.SubtitleCues.ToDictionary(cue => cue.Id, StringComparer.Ordinal);
        var resolvedCuesById = resolvedSubtitleCues.ToDictionary(cue => cue.CueId, StringComparer.Ordinal);
        var resolvedVoiceItems = new List<TimelineResolvedVoiceCue>(project.VoiceCues.Count);
        foreach (var voice in project.VoiceCues)
        {
            var cue = cuesById[voice.SubtitleCueId];
            if (!resolvedCuesById.TryGetValue(cue.Id, out var resolvedCue))
                continue;
            var startUs = resolvedCue.SequenceStartUs;
            var endUs = resolvedCue.SequenceEndUs;
            var asset = audioAssetsById[voice.AudioAssetId];
            var frameCount = voice.FrameCount;
            if (checked(endUs - startUs) < cue.DurationUs)
            {
                frameCount = Math.Min(
                    frameCount,
                    TimelineAudioTime.MicrosecondsToFramesFloor(
                        checked(endUs - startUs), asset.SampleRate));
                if (frameCount == 0)
                    continue;
            }
            resolvedVoiceItems.Add(new TimelineResolvedVoiceCue(
                voice.Id,
                cue.Id,
                asset.Id,
                asset.Path,
                asset.Sha256,
                startUs,
                endUs,
                voice.SourceStartFrame,
                frameCount,
                asset.SampleRate,
                asset.Channels,
                voice.Gain));
        }
        var resolvedVoice = resolvedVoiceItems
            .OrderBy(voice => voice.SequenceStartUs)
            .ThenBy(voice => voice.VoiceCueId, StringComparer.Ordinal)
            .ToArray();
        TimelineResolvedMusic? resolvedMusic = null;
        if (project.Music is not null)
        {
            var asset = audioAssetsById[project.Music.AudioAssetId];
            if (project.Music.SequenceStartUs < project.Sequence.TotalDurationUs)
            {
                var availableUs = checked(project.Sequence.TotalDurationUs - project.Music.SequenceStartUs);
                var frameCount = Math.Min(
                    project.Music.FrameCount,
                    TimelineAudioTime.MicrosecondsToFramesFloor(availableUs, asset.SampleRate));
                if (frameCount > 0)
                {
                    var endUs = checked(project.Music.SequenceStartUs
                        + TimelineAudioTime.FramesToMicroseconds(frameCount, asset.SampleRate));
                    resolvedMusic = new TimelineResolvedMusic(
                        project.Music.Id,
                        asset.Id,
                        asset.Path,
                        asset.Sha256,
                        project.Music.SequenceStartUs,
                        endUs,
                        project.Music.SourceStartFrame,
                        frameCount,
                        asset.SampleRate,
                        asset.Channels,
                        project.Music.Gain,
                        IsVerified: true);
                }
            }
        }
        else if (project.Items.SingleOrDefault(item => item.Kind == TimelineItemKind.Music) is { } legacyMusic)
        {
            if (legacyMusic.SequenceStartUs < project.Sequence.TotalDurationUs)
            {
                resolvedMusic = new TimelineResolvedMusic(
                    legacyMusic.Id,
                    null,
                    legacyMusic.Payload,
                    null,
                    legacyMusic.SequenceStartUs,
                    Math.Min(legacyMusic.SequenceEndUs, project.Sequence.TotalDurationUs),
                    0,
                    0,
                    0,
                    0,
                    1,
                    IsVerified: false);
            }
        }
        var audio = new TimelineResolvedAudioPlan(
            TimelineAudioTime.OutputSampleRate,
            project.Sequence.OutputFrameRate,
            TimelineAudioTime.MaximumOutputDriftUs(project.Sequence.OutputFrameRate),
            resolvedSourceAudio,
            resolvedVoice,
            resolvedMusic);
        return new TimelineRenderPlan(
            project.Id, DateTimeOffset.UtcNow.Ticks,
            project.Sequence.OutputWidth,
            project.Sequence.OutputHeight,
            project.Sequence.OutputFrameRate,
            resolvedClips, resolvedItems,
            resolvedSubtitleCues, audio);
    }
}
