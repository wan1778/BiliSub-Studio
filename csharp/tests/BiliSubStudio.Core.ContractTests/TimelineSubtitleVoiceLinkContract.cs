using BiliSubStudio.Core.Timeline;

namespace BiliSubStudio.Core.ContractTests;

internal static class TimelineSubtitleVoiceLinkContract
{
    public static Task RunAsync()
    {
        ValidationRejectsStaleOrOrphanVoice();
        CueEditKeepsOrInvalidatesVoiceBySpeechRevision();
        ReorderMovesVoiceThroughCue();
        TrimPreservesOrInvalidatesVoiceExplicitly();
        SplitRelinksCueWithoutStaleVoiceTime();
        FractionalSplitClampsCueToFinalOwner();
        DeleteAndHistoryRestoreCompleteState();
        GlobalCueAndVoiceStayAbsolute();
        return Task.CompletedTask;
    }

    private static void ValidationRejectsStaleOrOrphanVoice()
    {
        var fixture = TimelineAudioContractFixture.CreateLocalVoiceState();
        var voice = fixture.State.VoiceCues.Single();
        Throws<InvalidDataException>(
            () => TimelineAudioEditState.Create(
                fixture.State.Timeline,
                fixture.State.SubtitleCues,
                fixture.State.AudioAssets,
                fixture.State.SourceAudio,
                [voice with { SubtitleCueId = TimelineSubtitleCue.NewId() }],
                fixture.State.Music),
            "voice link to a missing subtitle cue was accepted");
        Throws<InvalidDataException>(
            () => TimelineAudioEditState.Create(
                fixture.State.Timeline,
                fixture.State.SubtitleCues,
                fixture.State.AudioAssets,
                fixture.State.SourceAudio,
                [voice with { CueRevisionSha256 = new string('a', 64) }],
                fixture.State.Music),
            "stale voice revision was accepted");
        Throws<InvalidDataException>(
            () => TimelineAudioEditState.Create(
                fixture.State.Timeline,
                fixture.State.SubtitleCues,
                fixture.State.AudioAssets,
                fixture.State.SourceAudio,
                [voice, voice with { Id = TimelineVoiceCue.NewId() }],
                fixture.State.Music),
            "multiple voice links for one subtitle cue were accepted");
    }

    private static void ReorderMovesVoiceThroughCue()
    {
        var fixture = TimelineAudioContractFixture.CreateLocalVoiceState();
        var beforePlan = TimelineCompiler.Compile(
            TimelineAudioContractFixture.Project(fixture.State, fixture.MediaAssets));
        var beforeVoice = beforePlan.Audio.VoiceCues.Single();
        var voiceLink = fixture.State.VoiceCues.Single();
        var music = fixture.State.Music;

        var reordered = TimelineAudioDependencyOps.Reorder(
            fixture.State, fixture.Clips[0].Id, 1);
        var afterPlan = TimelineCompiler.Compile(
            TimelineAudioContractFixture.Project(reordered, fixture.MediaAssets));
        var afterVoice = afterPlan.Audio.VoiceCues.Single();
        Check(afterVoice.SequenceStartUs - beforeVoice.SequenceStartUs == fixture.Clips[1].DurationUs,
            "voice did not move by the owning subtitle cue's clip delta");
        Check(reordered.VoiceCues.Single() == voiceLink,
            "reorder rewrote voice source ownership instead of moving through cue identity");
        Check(reordered.Music == music
            && afterPlan.Audio.Music!.SequenceStartUs == beforePlan.Audio.Music!.SequenceStartUs,
            "sequence-global music moved during clip reorder");
    }

    private static void CueEditKeepsOrInvalidatesVoiceBySpeechRevision()
    {
        var fixture = TimelineAudioContractFixture.CreateLocalVoiceState();
        var cue = fixture.State.SubtitleCues.Single();
        var moved = cue with { StartUs = cue.StartUs + 250_000, EndUs = cue.EndUs + 250_000 };
        var movedState = TimelineAudioDependencyOps.UpdateSubtitleCue(fixture.State, moved);
        Check(movedState.VoiceCues.Single() == fixture.State.VoiceCues.Single(),
            "cue move with unchanged spoken duration invalidated voice");
        var movedPlan = TimelineCompiler.Compile(
            TimelineAudioContractFixture.Project(movedState, fixture.MediaAssets));
        var movedResolvedCue = movedPlan.SubtitleCues.Single();
        var movedResolvedVoice = movedPlan.Audio.VoiceCues.Single();
        Check(movedResolvedCue.SequenceStartUs == moved.StartUs
            && movedResolvedCue.SequenceStartUs == movedResolvedVoice.SequenceStartUs
            && movedResolvedCue.SequenceEndUs == movedResolvedVoice.SequenceEndUs,
            "subtitle and voice did not stay synchronized after a cue move");

        const string changedText = "Nội dung voice đã thay đổi";
        var changed = cue with
        {
            SpokenText = changedText,
            SpeechRevisionSha256 = TimelineSubtitleCue.ComputeSpeechRevision(changedText, cue.DurationUs),
        };
        Throws<InvalidOperationException>(
            () => TimelineAudioDependencyOps.UpdateSubtitleCue(fixture.State, changed),
            "spoken-text edit retained stale voice without explicit policy");
        var invalidated = TimelineAudioDependencyOps.UpdateSubtitleCue(
            fixture.State, changed, TimelineVoiceBoundaryPolicy.RemoveAffectedVoice);
        Check(invalidated.VoiceCues.Count == 0,
            "explicit spoken-text edit did not remove stale voice");
    }

    private static void TrimPreservesOrInvalidatesVoiceExplicitly()
    {
        var fixture = TimelineAudioContractFixture.CreateLocalVoiceState();
        var cue = fixture.State.SubtitleCues.Single();
        var trimmed = TimelineAudioDependencyOps.Trim(
            fixture.State, fixture.Clips[0].Id, 1_000_000, 10_000_000);
        var shiftedCue = trimmed.SubtitleCues.Single();
        Check(shiftedCue.StartUs == cue.StartUs - 1_000_000
            && shiftedCue.DurationUs == cue.DurationUs
            && shiftedCue.SpeechRevisionSha256 == cue.SpeechRevisionSha256,
            "head trim before cue did not preserve spoken input identity");
        Check(trimmed.VoiceCues.Single() == fixture.State.VoiceCues.Single(),
            "head trim before cue invalidated valid generated voice");

        Throws<InvalidOperationException>(
            () => TimelineAudioDependencyOps.Trim(
                fixture.State, fixture.Clips[0].Id, 2_000_000, 10_000_000),
            "trim through voiced cue did not require an explicit invalidation policy");
        var invalidated = TimelineAudioDependencyOps.Trim(
            fixture.State,
            fixture.Clips[0].Id,
            2_000_000,
            10_000_000,
            TimelineVoiceBoundaryPolicy.RemoveAffectedVoice);
        Check(invalidated.VoiceCues.Count == 0
            && invalidated.SubtitleCues.Single().DurationUs < cue.DurationUs,
            "explicit trim policy did not remove stale voice and clamp the cue atomically");
    }

    private static void SplitRelinksCueWithoutStaleVoiceTime()
    {
        var fixture = TimelineAudioContractFixture.CreateLocalVoiceState(
            cueStartUs: 6_000_000, cueEndUs: 8_000_000);
        fixture = fixture with
        {
            State = TimelineAudioEditState.Create(
                fixture.State.Timeline,
                fixture.State.SubtitleCues,
                fixture.State.AudioAssets,
                [new TimelineSourceAudioState(fixture.Clips[0].Id, .35),
                 new TimelineSourceAudioState(fixture.Clips[1].Id, 1)],
                fixture.State.VoiceCues,
                fixture.State.Music),
        };
        var voice = fixture.State.VoiceCues.Single();
        var split = TimelineAudioDependencyOps.Split(
            fixture.State,
            fixture.Clips[0].Id,
            5_000_000,
            TimelineSubtitleSplitPolicy.SplitAndClamp);
        var right = split.Timeline.Clips.Single(
            clip => !fixture.State.Timeline.Clips.Any(old => old.Id == clip.Id));
        var cue = split.SubtitleCues.Single();
        var owner = split.Timeline.Items.Single(item => item.Id == cue.SubtitleItemId);
        Check(owner.ClipId == right.Id && cue.StartUs == 1_000_000 && cue.EndUs == 3_000_000,
            "cue after split did not rebase onto the right subtitle item");
        Check(split.VoiceCues.Single() == voice,
            "split before cue rewrote its stable voice link");
        Check(split.SourceAudio.Single(source => source.ClipId == right.Id).Gain == .35,
            "split did not clone source-audio gain onto the right child");
        var plan = TimelineCompiler.Compile(
            TimelineAudioContractFixture.Project(split, fixture.MediaAssets));
        Check(plan.Audio.VoiceCues.Single().SequenceStartUs == 6_000_000
            && plan.SubtitleCues.Single().SequenceStartUs
                == plan.Audio.VoiceCues.Single().SequenceStartUs,
            "compiled subtitle/voice retained a stale pre-split timestamp");

        var crossing = TimelineAudioContractFixture.CreateLocalVoiceState(
            cueStartUs: 4_000_000, cueEndUs: 6_000_000);
        Throws<InvalidOperationException>(
            () => TimelineAudioDependencyOps.Split(
                crossing.State,
                crossing.Clips[0].Id,
                5_000_000,
                TimelineSubtitleSplitPolicy.SplitAndClamp),
            "split through voiced cue did not require explicit invalidation");
        var splitAndInvalidated = TimelineAudioDependencyOps.Split(
            crossing.State,
            crossing.Clips[0].Id,
            5_000_000,
            TimelineSubtitleSplitPolicy.SplitAndClamp,
            TimelineVoiceBoundaryPolicy.RemoveAffectedVoice);
        Check(splitAndInvalidated.SubtitleCues.Count == 2
            && splitAndInvalidated.VoiceCues.Count == 0,
            "explicit crossing split did not create cues and remove stale voice atomically");
    }

    private static void DeleteAndHistoryRestoreCompleteState()
    {
        var fixture = TimelineAudioContractFixture.CreateLocalVoiceState();
        var deleted = TimelineAudioDependencyOps.Delete(fixture.State, fixture.Clips[0].Id);
        Check(deleted.SubtitleCues.Count == 0 && deleted.VoiceCues.Count == 0,
            "clip delete left orphan subtitle/voice state");
        Check(deleted.SourceAudio.Count == 1
            && deleted.SourceAudio[0].ClipId == fixture.Clips[1].Id,
            "clip delete removed the wrong source-audio owner");
        Check(deleted.Music == fixture.State.Music,
            "clip delete changed sequence-global music");
        var empty = TimelineAudioDependencyOps.Delete(deleted, fixture.Clips[1].Id);
        Check(empty.Timeline.Clips.Count == 0 && empty.Music == fixture.State.Music,
            "final clip delete did not preserve sequence-global music state");
        Check(TimelineCompiler.Compile(
                TimelineAudioContractFixture.Project(empty, fixture.MediaAssets)).Audio.Music is null,
            "music without video overlap leaked into the compiled plan");

        var history = new TimelineAudioEditHistory();
        history.Commit(fixture.State, deleted);
        var restored = history.Undo(deleted);
        Check(restored.Timeline.Clips.Select(clip => clip.Id)
                .SequenceEqual(fixture.State.Timeline.Clips.Select(clip => clip.Id))
            && restored.SubtitleCues.SequenceEqual(fixture.State.SubtitleCues)
            && restored.VoiceCues.SequenceEqual(fixture.State.VoiceCues)
            && restored.SourceAudio.SequenceEqual(fixture.State.SourceAudio)
            && restored.Music == fixture.State.Music,
            "undo did not restore complete cue-linked audio state");
        var redone = history.Redo(restored);
        Check(redone.SubtitleCues.Count == 0 && redone.VoiceCues.Count == 0,
            "redo did not restore dependent delete atomically");
    }

    private static void FractionalSplitClampsCueToFinalOwner()
    {
        var media = TimelineMediaAsset.Create(
            "C:/video/fractional.mp4", 1920, 1080, 1_000_000, 30, hasAudio: true);
        var clip = new TimelineClip(TimelineClip.NewId(), media.Id, 0, 0, 100, 1.5);
        var subtitle = TimelineItemOps.AttachSubtitleToClip(
            clip.Id, 0, clip.DurationUs, "fractional.srt");
        var cue = TimelineSubtitleCue.Create(
            subtitle.Id, 4, clip.DurationUs, "Fractional boundary");
        var state = TimelineAudioEditState.Create(
            TimelineEditState.Create([clip], [subtitle]), [cue]);
        var split = TimelineAudioDependencyOps.Split(
            state, clip.Id, 3, TimelineSubtitleSplitPolicy.SplitAndClamp);
        var right = split.Timeline.Clips.Single(candidate => candidate.Id != clip.Id);
        var movedCue = split.SubtitleCues.Single();
        var owner = split.Timeline.Items.Single(item => item.Id == movedCue.SubtitleItemId);
        Check(owner.ClipId == right.Id && movedCue.EndUs <= owner.ClipEndUs
            && movedCue.EndUs <= right.DurationUs,
            "fractional split left cue beyond its final clamped owner");
    }

    private static void GlobalCueAndVoiceStayAbsolute()
    {
        var fixture = TimelineAudioContractFixture.CreateLocalVoiceState();
        var globalItem = TimelineItemOps.AttachSubtitleToSequence(
            2_000_000, 6_000_000, "global.srt");
        var globalCue = TimelineSubtitleCue.Create(
            globalItem.Id, 2_500_000, 4_500_000, "Xin chào toàn sequence");
        var voiceAsset = fixture.State.AudioAssets.Single(
            asset => asset.Kind == TimelineAudioAssetKind.VoiceMaster);
        var globalVoice = new TimelineVoiceCue(
            TimelineVoiceCue.NewId(),
            globalCue.Id,
            voiceAsset.Id,
            0,
            TimelineAudioTime.MicrosecondsToFrames(globalCue.DurationUs, voiceAsset.SampleRate),
            1,
            globalCue.SpeechRevisionSha256);
        var state = TimelineAudioEditState.Create(
            TimelineEditState.Create(fixture.Clips, [globalItem]),
            [globalCue],
            fixture.State.AudioAssets,
            TimelineSourceAudioState.Defaults(fixture.Clips),
            [globalVoice],
            null);
        var before = TimelineCompiler.Compile(
            TimelineAudioContractFixture.Project(state, fixture.MediaAssets));
        var reordered = TimelineAudioDependencyOps.Reorder(state, fixture.Clips[0].Id, 1);
        var after = TimelineCompiler.Compile(
            TimelineAudioContractFixture.Project(reordered, fixture.MediaAssets));
        Check(before.Audio.VoiceCues.Single().SequenceStartUs == 2_500_000
            && after.Audio.VoiceCues.Single().SequenceStartUs == 2_500_000,
            "global subtitle voice moved with a video clip");

        var tailItem = TimelineItemOps.AttachSubtitleToSequence(
            19_000_000, 22_000_000, "tail.srt");
        var tailCue = TimelineSubtitleCue.Create(
            tailItem.Id, 19_000_000, 22_000_000, "Cue vượt cuối sequence");
        var tailVoice = globalVoice with
        {
            Id = TimelineVoiceCue.NewId(),
            SubtitleCueId = tailCue.Id,
            FrameCount = TimelineAudioTime.MicrosecondsToFrames(
                tailCue.DurationUs, voiceAsset.SampleRate),
            CueRevisionSha256 = tailCue.SpeechRevisionSha256,
        };
        var tailState = TimelineAudioEditState.Create(
            TimelineEditState.Create(fixture.Clips, [tailItem]),
            [tailCue],
            fixture.State.AudioAssets,
            TimelineSourceAudioState.Defaults(fixture.Clips),
            [tailVoice],
            null);
        var tailPlan = TimelineCompiler.Compile(
            TimelineAudioContractFixture.Project(tailState, fixture.MediaAssets));
        Check(tailPlan.SubtitleCues.Single().SequenceEndUs == 20_000_000
            && tailPlan.Audio.VoiceCues.Single().SequenceEndUs == 20_000_000
            && tailPlan.Audio.VoiceCues.Single().FrameCount == 22_050,
            "sequence-tail subtitle and voice were not clamped together");
        var noVideo = TimelineAudioDependencyOps.Delete(
            TimelineAudioDependencyOps.Delete(tailState, fixture.Clips[0].Id),
            fixture.Clips[1].Id);
        var noVideoPlan = TimelineCompiler.Compile(
            TimelineAudioContractFixture.Project(noVideo, fixture.MediaAssets));
        Check(noVideo.SubtitleCues.Count == 1 && noVideo.VoiceCues.Count == 1
            && noVideoPlan.SubtitleCues.Count == 0
            && noVideoPlan.Audio.VoiceCues.Count == 0
            && !noVideoPlan.Audio.HasAudibleAudio,
            "empty sequence emitted preserved global subtitle/voice state");
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }

    private static void Throws<TException>(Action action, string message) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }
}

internal sealed record TimelineAudioFixture(
    IReadOnlyList<TimelineMediaAsset> MediaAssets,
    IReadOnlyList<TimelineClip> Clips,
    TimelineAudioEditState State);

internal static class TimelineAudioContractFixture
{
    public static TimelineAudioFixture CreateLocalVoiceState(
        long cueStartUs = 1_500_000,
        long cueEndUs = 3_500_000)
    {
        var media = TimelineMediaAsset.Create(
            "C:/video/a.mp4", 1920, 1080, 60_000_000, 30, hasAudio: true);
        var first = new TimelineClip(TimelineClip.NewId(), media.Id, 0, 0, 10_000_000, 1);
        var second = new TimelineClip(TimelineClip.NewId(), media.Id, 10_000_000, 10_000_000, 20_000_000, 1);
        var subtitle = TimelineItemOps.AttachSubtitleToClip(
            first.Id, 0, first.DurationUs, "C:/subs/clip.srt");
        var cue = TimelineSubtitleCue.Create(
            subtitle.Id, cueStartUs, cueEndUs, "Đây là câu voice ổn định");
        var voiceAsset = VoiceAsset();
        var voice = new TimelineVoiceCue(
            TimelineVoiceCue.NewId(),
            cue.Id,
            voiceAsset.Id,
            22_050,
            TimelineAudioTime.MicrosecondsToFrames(cue.DurationUs, voiceAsset.SampleRate),
            1,
            cue.SpeechRevisionSha256);
        var musicAsset = MusicAsset();
        var music = new TimelineMusicTrack(
            TimelineMusicTrack.NewId(), musicAsset.Id, 2_000_000, 0, 480_000, .5);
        var timeline = TimelineEditState.Create([first, second], [subtitle]);
        var state = TimelineAudioEditState.Create(
            timeline,
            [cue],
            [voiceAsset, musicAsset],
            TimelineSourceAudioState.Defaults(timeline.Clips),
            [voice],
            music);
        return new TimelineAudioFixture([media], [first, second], state);
    }

    public static TimelineAudioAsset VoiceAsset() => new(
        TimelineAudioAsset.NewId(),
        TimelineAudioAssetKind.VoiceMaster,
        "C:/audio/voice-master.flac",
        220_500,
        22_050,
        1,
        10_000,
        1,
        new string('a', 64));

    public static TimelineAudioAsset MusicAsset() => new(
        TimelineAudioAsset.NewId(),
        TimelineAudioAssetKind.Music,
        "C:/audio/music.flac",
        960_000,
        48_000,
        2,
        20_000,
        2,
        new string('b', 64));

    public static TimelineProjectV2 Project(
        TimelineAudioEditState state,
        IReadOnlyList<TimelineMediaAsset> mediaAssets,
        string? id = null) =>
        new(
            id ?? TimelineProjectV2.NewId(),
            "G8 contract",
            DateTimeOffset.UtcNow,
            mediaAssets,
            new TimelineSequence(state.Timeline.Clips, 1920, 1080, 30),
            state.Timeline.Items,
            state.SubtitleCues,
            state.AudioAssets,
            state.SourceAudio,
            state.VoiceCues,
            state.Music);

    public sealed class NullSegmentRenderer : ITimelineExportSegmentRenderer
    {
        public Task RenderSegmentAsync(
            TimelineRenderSegment segment,
            string renderingPath,
            BiliSubStudio.Core.Processes.OwnedProcessGroup processes,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    public sealed class NullConcatenator : ITimelineExportConcatenator
    {
        public Task ConcatAsync(
            TimelineConcatListArtifact list,
            string renderingPath,
            TimelineMuxExpectation expectation,
            BiliSubStudio.Core.Processes.OwnedProcessGroup processes,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    public sealed class NullFinalMixer : ITimelineFinalMixer
    {
        public Task MixFinalAsync(
            string concatPath,
            TimelineRenderPlan plan,
            string renderingPath,
            BiliSubStudio.Core.Processes.OwnedProcessGroup processes,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
