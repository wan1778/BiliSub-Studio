using System.Text.Json;
using System.Text.Json.Nodes;
using BiliSubStudio.Core.Timeline;

namespace BiliSubStudio.Core.ContractTests;

internal static class TimelineAudioSyncContract
{
    public static async Task RunAsync()
    {
        AudioTimeAndToleranceAreDeterministic();
        CompilerResolvesSourceVoiceAndMusicExactly();
        MusicLimitAndGainValidationStayBounded();
        await SchemaFourRoundtripAndSchemaThreeUpgradeAsync();
    }

    private static void AudioTimeAndToleranceAreDeterministic()
    {
        Check(TimelineAudioTime.FramesToMicroseconds(22_050, 22_050) == 1_000_000,
            "22.05 kHz frame-to-microsecond mapping drifted");
        Check(TimelineAudioTime.MicrosecondsToFrames(1_000_000, 44_100) == 44_100,
            "44.1 kHz microsecond-to-frame mapping drifted");
        Check(TimelineAudioTime.FramesToMicroseconds(48_000, 48_000) == 1_000_000,
            "48 kHz frame-to-microsecond mapping drifted");
        Check(TimelineAudioTime.MaximumOutputDriftUs(30) == 16_667,
            "DEC-019 30 FPS half-frame tolerance drifted");
        Check(TimelineAudioTime.MaximumOutputDriftUs(60) == 8_334,
            "DEC-019 60 FPS half-frame tolerance drifted");

        var random = new Random(808);
        foreach (var rate in new[] { 22_050, 44_100, 48_000 })
        {
            for (var index = 0; index < 300; index++)
            {
                var frames = random.NextInt64(0, 10_000_000);
                var microseconds = TimelineAudioTime.FramesToMicroseconds(frames, rate);
                var restored = TimelineAudioTime.MicrosecondsToFrames(microseconds, rate);
                Check(Math.Abs(restored - frames) <= 1,
                    "sample/microsecond roundtrip exceeded one frame");
            }
        }
    }

    private static void CompilerResolvesSourceVoiceAndMusicExactly()
    {
        var audible = TimelineMediaAsset.Create(
            "C:/video/a.mp4", 1920, 1080, 60_000_000, 30, hasAudio: true);
        var silent = TimelineMediaAsset.Create(
            "C:/video/b.mp4", 1280, 720, 60_000_000, 25, hasAudio: false);
        var first = new TimelineClip(TimelineClip.NewId(), audible.Id, 0, 0, 8_000_000, 1);
        var second = new TimelineClip(TimelineClip.NewId(), silent.Id, 8_000_000, 10_000_000, 16_000_000, 2);
        var subtitle = TimelineItemOps.AttachSubtitleToClip(
            second.Id, 500_000, 2_500_000, "C:/subs/b.srt");
        var cue = TimelineSubtitleCue.Create(
            subtitle.Id, 1_000_000, 2_000_000, "Voice trên clip không có source audio");
        var voiceAsset = TimelineAudioContractFixture.VoiceAsset();
        var voice = new TimelineVoiceCue(
            TimelineVoiceCue.NewId(),
            cue.Id,
            voiceAsset.Id,
            0,
            TimelineAudioTime.MicrosecondsToFrames(cue.DurationUs, voiceAsset.SampleRate),
            .8,
            cue.SpeechRevisionSha256);
        var musicAsset = TimelineAudioContractFixture.MusicAsset();
        var music = new TimelineMusicTrack(
            TimelineMusicTrack.NewId(), musicAsset.Id, 2_000_000, 0, 480_000, .4);
        var timeline = TimelineEditState.Create([first, second], [subtitle]);
        var state = TimelineAudioEditState.Create(
            timeline,
            [cue],
            [voiceAsset, musicAsset],
            [new TimelineSourceAudioState(first.Id, .5), new TimelineSourceAudioState(second.Id, 1)],
            [voice],
            music);
        var project = TimelineAudioContractFixture.Project(state, [audible, silent]);
        var plan = TimelineCompiler.Compile(project);

        Check(plan.TotalDurationUs == 11_000_000,
            "non-1x clip duration drifted the sequence");
        var firstSource = plan.Audio.SourceAudio.Single(source => source.ClipId == first.Id);
        Check(firstSource.SequenceStartUs == 0 && firstSource.SequenceEndUs == 8_000_000
            && firstSource.SourceInUs == 0 && firstSource.SourceOutUs == 8_000_000
            && firstSource.PlaybackRate == 1 && firstSource.Gain == .5
            && firstSource.HasSourceStream,
            "first clip source-audio mapping drifted");
        var secondSource = plan.Audio.SourceAudio.Single(source => source.ClipId == second.Id);
        Check(secondSource.SequenceStartUs == 8_000_000 && secondSource.SequenceEndUs == 11_000_000
            && secondSource.SourceInUs == 10_000_000 && secondSource.SourceOutUs == 16_000_000
            && secondSource.PlaybackRate == 2 && !secondSource.HasSourceStream,
            "silent/non-1x source-audio mapping drifted");
        var resolvedVoice = plan.Audio.VoiceCues.Single();
        var resolvedCue = plan.SubtitleCues.Single();
        Check(resolvedVoice.SequenceStartUs == 9_000_000
            && resolvedVoice.SequenceEndUs == 10_000_000
            && resolvedVoice.FrameCount == 22_050,
            "voice did not resolve exactly from its linked cue");
        Check(resolvedCue.CueId == resolvedVoice.SubtitleCueId
            && resolvedCue.SequenceStartUs == resolvedVoice.SequenceStartUs
            && resolvedCue.SequenceEndUs == resolvedVoice.SequenceEndUs
            && resolvedCue.SpokenText == cue.SpokenText,
            "compiled subtitle state diverged from its linked voice");
        Check(plan.Audio.Music is
            { SequenceStartUs: 2_000_000, SequenceEndUs: 11_000_000, FrameCount: 432_000 },
            "compiler did not clamp sequence-global music to video duration");
        Check(plan.Audio.OutputSampleRate == 48_000
            && plan.Audio.SequenceFrameRate == 30
            && plan.Audio.MaximumOutputDriftUs == 16_667
            && plan.Audio.HasAudibleAudio,
            "compiled audio plan lost 48 kHz normalization or audible state");

        var reordered = TimelineAudioDependencyOps.Reorder(state, first.Id, 1);
        var reorderedPlan = TimelineCompiler.Compile(
            TimelineAudioContractFixture.Project(reordered, [audible, silent]));
        Check(reorderedPlan.Audio.VoiceCues.Single().SequenceStartUs == 1_000_000,
            "voice did not follow its cue after non-1x clip reorder");
        Check(reorderedPlan.Audio.Music!.SequenceStartUs == 2_000_000,
            "music moved during non-1x clip reorder");

        var muted = TimelineAudioEditState.Create(
            timeline,
            [cue],
            [voiceAsset, musicAsset],
            [new TimelineSourceAudioState(first.Id, 0), new TimelineSourceAudioState(second.Id, 0)],
            [voice with { Gain = 0 }],
            music with { Gain = 0 });
        Check(!TimelineCompiler.Compile(
                TimelineAudioContractFixture.Project(muted, [audible, silent])).Audio.HasAudibleAudio,
            "all-muted audio plan remained audible");
    }

    private static void MusicLimitAndGainValidationStayBounded()
    {
        var fixture = TimelineAudioContractFixture.CreateLocalVoiceState();
        var legacyMusic = new TimelineItem(
            TimelineItem.NewId(), TimelineItemKind.Music, null,
            0, 0, 0, 5_000_000, "C:/audio/legacy.mp3");
        Throws<InvalidDataException>(
            () => TimelineAudioEditState.Create(
                TimelineEditState.Create(fixture.Clips, [legacyMusic]),
                [],
                fixture.State.AudioAssets,
                TimelineSourceAudioState.Defaults(fixture.Clips),
                [],
                fixture.State.Music),
            "DEC-018 allowed two music tracks");
        Throws<InvalidDataException>(
            () => TimelineAudioEditState.Create(
                fixture.State.Timeline,
                fixture.State.SubtitleCues,
                fixture.State.AudioAssets,
                [new TimelineSourceAudioState(fixture.Clips[0].Id, double.NaN),
                 new TimelineSourceAudioState(fixture.Clips[1].Id, 1)],
                fixture.State.VoiceCues,
                fixture.State.Music),
            "non-finite source gain was accepted");
        Throws<InvalidDataException>(
            () => (fixture.State.Music! with { Gain = 2.01 }).ValidateShape(),
            "music gain above 2 was accepted");
        var outsideMusic = TimelineAudioContractFixture.Project(
            fixture.State with
            {
                Music = fixture.State.Music! with { SequenceStartUs = long.MaxValue },
            },
            fixture.MediaAssets);
        outsideMusic.Validate();
        Check(TimelineCompiler.Compile(outsideMusic).Audio.Music is null,
            "non-overlapping global music overflowed or leaked into the audio plan");
        var tinyFps = TimelineAudioContractFixture.Project(fixture.State, fixture.MediaAssets) with
        {
            Sequence = new TimelineSequence(fixture.Clips, 1920, 1080, double.Epsilon),
        };
        Throws<InvalidDataException>(
            tinyFps.Validate,
            "FPS whose DEC-019 tolerance exceeds int64 was accepted");

        var legacyState = TimelineAudioEditState.Create(
            TimelineEditState.Create(fixture.Clips, [legacyMusic]),
            [],
            [],
            TimelineSourceAudioState.Defaults(fixture.Clips),
            [],
            null);
        var legacyPlan = TimelineCompiler.Compile(
            TimelineAudioContractFixture.Project(legacyState, fixture.MediaAssets));
        Check(legacyPlan.Audio.Music is { IsVerified: false }
            && legacyPlan.Audio.HasUnverifiedAudio
            && legacyPlan.Audio.HasAudibleAudio,
            "schema-3 legacy music disappeared outside the compiled audio plan");
    }

    private static async Task SchemaFourRoundtripAndSchemaThreeUpgradeAsync()
    {
        var fixture = TimelineAudioContractFixture.CreateLocalVoiceState();
        var root = Path.Combine(Path.GetTempPath(), "bilisub-timeline-g8-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new TimelineProjectStoreV2(root);
            var project = TimelineAudioContractFixture.Project(fixture.State, fixture.MediaAssets);
            await store.SaveAsync(project, CancellationToken.None);
            var reopened = await store.LoadAsync(project.Id, CancellationToken.None);
            Check(reopened.SubtitleCues.SequenceEqual(project.SubtitleCues)
                && reopened.AudioAssets.SequenceEqual(project.AudioAssets)
                && reopened.SourceAudio.SequenceEqual(project.SourceAudio)
                && reopened.VoiceCues.SequenceEqual(project.VoiceCues)
                && reopened.Music == project.Music,
                "schema-4 audio state did not survive reopen");
            var originalPlan = TimelineCompiler.Compile(project);
            var reopenedPlan = TimelineCompiler.Compile(reopened);
            Check(reopenedPlan.Audio.VoiceCues.SequenceEqual(originalPlan.Audio.VoiceCues)
                && reopenedPlan.Audio.SourceAudio.SequenceEqual(originalPlan.Audio.SourceAudio)
                && reopenedPlan.Audio.Music == originalPlan.Audio.Music,
                "save/reopen/recompile drifted audio timing");
            Check(reopenedPlan.SubtitleCues.SequenceEqual(originalPlan.SubtitleCues),
                "save/reopen/recompile drifted resolved subtitle timing or text");

            var path = Path.Combine(root, project.Id + ".json");
            var storedJson = await File.ReadAllTextAsync(path);
            var mixed = JsonNode.Parse(storedJson)!.AsObject();
            mixed["schema"] = 3;
            await File.WriteAllTextAsync(path, mixed.ToJsonString());
            await ThrowsAsync<InvalidDataException>(
                () => store.LoadAsync(project.Id, CancellationToken.None),
                "schema 3 containing G8 state was silently truncated");

            var schema3 = JsonNode.Parse(storedJson)!.AsObject();
            schema3["schema"] = 3;
            schema3.Remove("subtitle_cues");
            schema3.Remove("audio_assets");
            schema3.Remove("source_audio");
            schema3.Remove("voice_cues");
            schema3.Remove("music");
            await File.WriteAllTextAsync(
                path,
                schema3.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            var upgraded = await store.LoadAsync(project.Id, CancellationToken.None);
            Check(upgraded.Items.SequenceEqual(project.Items)
                && upgraded.SubtitleCues.Count == 0
                && upgraded.AudioAssets.Count == 0
                && upgraded.VoiceCues.Count == 0
                && upgraded.SourceAudio.Count == fixture.Clips.Count
                && upgraded.SourceAudio.All(source => source.Gain == 1),
                "schema-3 upgrade did not preserve items and apply explicit G8 defaults");
            await store.SaveAsync(upgraded, CancellationToken.None);
            var saved = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            Check(saved["schema"]!.GetValue<int>() == TimelineProjectV2.Schema
                && saved["source_audio"] is JsonArray,
                "schema-3 upgrade did not save back as the current V2 schema");

            var legacyMusic = new TimelineItem(
                TimelineItem.NewId(), TimelineItemKind.Music, null,
                0, 0, 1_000_000, 6_000_000, "C:/audio/schema3-music.mp3");
            var legacyProject = upgraded with { Items = [legacyMusic] };
            await store.SaveAsync(legacyProject, CancellationToken.None);
            var legacySchema3 = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            legacySchema3["schema"] = 3;
            legacySchema3.Remove("subtitle_cues");
            legacySchema3.Remove("audio_assets");
            legacySchema3.Remove("source_audio");
            legacySchema3.Remove("voice_cues");
            legacySchema3.Remove("music");
            await File.WriteAllTextAsync(path, legacySchema3.ToJsonString());
            var reopenedLegacy = await store.LoadAsync(project.Id, CancellationToken.None);
            var legacyPlan = TimelineCompiler.Compile(reopenedLegacy);
            Check(legacyPlan.Audio.Music is { IsVerified: false }
                && legacyPlan.Audio.HasUnverifiedAudio,
                "schema-3 legacy music did not survive upgrade into the audio plan");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
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

    private static async Task ThrowsAsync<TException>(Func<Task> action, string message)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }
}
