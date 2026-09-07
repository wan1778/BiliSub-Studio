using BiliSubStudio.Core.Editor;
using BiliSubStudio.Core.Timeline;

namespace BiliSubStudio.Core.ContractTests;

internal static class TimelinePromotionContract
{
    public static async Task RunAsync()
    {
        await RealProjectCopyKeepsSourceUntouchedAsync();
        await BackupGateRejectsUnguardedPromotionAsync();
        await InvalidSourceRejectedAsync();
    }

    private static async Task RealProjectCopyKeepsSourceUntouchedAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "bilisub-g11-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = CreateRealShapedProject();
            var jsonPath = Path.Combine(root, "real-project.json");
            var mediaPath = Path.Combine(root, "real-media.mp4");
            var jsonBytes = System.Text.Encoding.UTF8.GetBytes("{\"schema\":7,\"real\":true}");
            await File.WriteAllBytesAsync(jsonPath, jsonBytes);
            await File.WriteAllBytesAsync(mediaPath, [7, 8, 9, 10]);
            var jsonHashBefore = await File.ReadAllBytesAsync(jsonPath);
            var mediaHashBefore = await File.ReadAllBytesAsync(mediaPath);

            var store = new TimelineProjectStoreV2(Path.Combine(root, "v2"));
            var service = new TimelinePromotionService(store);
            var report = await service.PromoteAsync(
                source,
                jsonPath,
                mediaPath,
                width: 1920,
                height: 1080,
                durationSeconds: 60,
                hasAudio: true,
                new TimelinePromotionRequest("Projects-G11-test", new string('d', 64)),
                CancellationToken.None);

            Check(report.Warnings.Count == 3
                && report.Warnings.Contains(TimelineMigrationWarning.RegionsFlattened)
                && report.Warnings.Contains(TimelineMigrationWarning.SubtitleDropped)
                && report.Warnings.Contains(TimelineMigrationWarning.VoiceDropped),
                "real-project copy did not warn about every flattened/dropped field");
            Check(report.SourceJsonSha256Before == report.SourceJsonSha256After
                && report.MediaSha256Before == report.MediaSha256After,
                "promotion report claims source drifted");
            Check((await File.ReadAllBytesAsync(jsonPath)).SequenceEqual(jsonHashBefore),
                "schema-7 project file changed during promotion");
            Check((await File.ReadAllBytesAsync(mediaPath)).SequenceEqual(mediaHashBefore),
                "source media changed during promotion");
            Check(TimelineProjectV2.Schema == 6, "promoted copy must use current V2 schema");
            Check(!string.Equals(report.Project.Id, source.Id, StringComparison.Ordinal),
                "promoted copy must own a fresh V2 identity");
            Check(report.Project.Name == source.Name, "promoted copy lost the project name");
            var sequence = report.Project.Sequence
                ?? throw new InvalidOperationException("promoted copy lost its sequence");
            Check(sequence.VideoClips.Count == 1,
                "promoted copy must carry exactly one clip");
            var clip = sequence.VideoClips[0];
            Check(clip.SourceInUs == 0 && clip.SourceOutUs == 60_000_000 && clip.PlaybackRate == 1.0,
                "promoted copy must span the full source at 1x");
            Check(sequence.OutputWidth == 1920
                && sequence.OutputHeight == 1080,
                "promoted copy geometry must default to the source probe");
            var asset = report.Project.Assets.Single();
            Check(asset.HasAudio && asset.DurationUs == 60_000_000,
                "promoted copy lost probed audio presence or duration");
            Check(report.Project.SourceAudio.Count == 1 && report.Project.SourceAudio[0].Gain == 1,
                "promoted copy lost explicit unity source-audio default");
            Check(report.SourceFingerprint == source.Source,
                "promotion report lost the source fingerprint");

            var reopened = await store.LoadAsync(report.Project.Id, CancellationToken.None);
            Check(reopened.Id == report.Project.Id && reopened.Sequence is not null
                && reopened.Sequence.VideoClips.Count == 1,
                "promoted copy did not survive V2 reopen");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task BackupGateRejectsUnguardedPromotionAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "bilisub-g11-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = CreateRealShapedProject();
            var jsonPath = Path.Combine(root, "p.json");
            var mediaPath = Path.Combine(root, "m.mp4");
            await File.WriteAllBytesAsync(jsonPath, [1]);
            await File.WriteAllBytesAsync(mediaPath, [2]);
            var store = new TimelineProjectStoreV2(Path.Combine(root, "v2"));
            var service = new TimelinePromotionService(store);

            await ThrowsAsync<InvalidDataException>(() => service.PromoteAsync(
                source, jsonPath, mediaPath, 1920, 1080, 60, true,
                new TimelinePromotionRequest("", new string('d', 64)), CancellationToken.None),
                "promotion without a backup tag was accepted");
            await ThrowsAsync<InvalidDataException>(() => service.PromoteAsync(
                source, jsonPath, mediaPath, 1920, 1080, 60, true,
                new TimelinePromotionRequest("tag", "not-a-hash"), CancellationToken.None),
                "promotion with a malformed manifest hash was accepted");
            await ThrowsAsync<InvalidDataException>(() => service.PromoteAsync(
                source, jsonPath, Path.Combine(root, "missing.mp4"), 1920, 1080, 60, true,
                new TimelinePromotionRequest("tag", new string('d', 64)), CancellationToken.None),
                "promotion with missing media was accepted instead of relink guidance");
            await ThrowsAsync<InvalidDataException>(() => service.PromoteAsync(
                source, Path.Combine(root, "missing.json"), mediaPath, 1920, 1080, 60, true,
                new TimelinePromotionRequest("tag", new string('d', 64)), CancellationToken.None),
                "promotion with a missing schema-7 file was accepted");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task InvalidSourceRejectedAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "bilisub-g11-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var jsonPath = Path.Combine(root, "p.json");
            var mediaPath = Path.Combine(root, "m.mp4");
            await File.WriteAllBytesAsync(jsonPath, [1]);
            await File.WriteAllBytesAsync(mediaPath, [2]);
            var store = new TimelineProjectStoreV2(Path.Combine(root, "v2"));
            var service = new TimelinePromotionService(store);
            var request = new TimelinePromotionRequest("tag", new string('d', 64));
            await ThrowsAsync<InvalidDataException>(() => service.PromoteAsync(
                CreateRealShapedProject() with { Schema = 6 },
                jsonPath, mediaPath, 1920, 1080, 60, true, request, CancellationToken.None),
                "non-schema-7 project accepted by promotion");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static EditorProject CreateRealShapedProject()
    {
        var source = new EditorSourceFingerprint(
            "C:/v/real-project.mp4", Size: 1000, LastWriteUtcTicks: 42,
            Width: 1920, Height: 1080, Duration: 60);
        var regions = new List<EditRegion>
        {
            new(.1, .1, .2, .2, "blur", 18, false, 0, 60, "blur-partial"),
            new(.5, .5, .2, .2, "mosaic", 12, false, 5, 55, "mosaic-scoped"),
            new(0, 0, 1, 1, "cover", 0, true, 0, 60, "cover-whole"),
        };
        var cues = new List<EditorSubtitleCue>
        {
            new("cue-0001", "1", "00:00:11,000 --> 00:00:14,000", 11, 14, "你好", "Xin chào"),
            new("cue-0002", "2", "00:00:20,000 --> 00:00:24,000", 20, 24, "再见", "Tạm biệt"),
        };
        var subtitle = new EditorSubtitleProject(
            "C:/subs/real.srt", 500, 43, new string('c', 64), cues,
            EditorSubtitlePlacement.Default, "skill", new string('e', 64),
            "C:/subs/real.ass");
        var tts = new EditorTtsProject(
            "complete", "nghi-tts", "1", "male", "ngoc_huyen",
            "C:/cache/manifest.json", new string('f', 64),
            new EditorVoiceTrack("C:/audio/master.wav", 0, 40, 1),
            CueCount: 2, ReviewCount: 0,
            [new EditorTtsCueWindow("cue-0001", 11, 14, "srt-fallback", "fit")]);
        return new EditorProject(
            7,
            new string('a', 24),
            "G11 real project",
            source,
            "real_edited.mp4",
            regions,
            subtitle,
            DateTimeOffset.UtcNow,
            new EditorAudioSettings("duck", 1.5),
            new EditorAsrProject("complete", "faster-whisper-small", new string('b', 40),
                "cpu", "int8", "C:/cache/asr.json", 2, .8),
            new EditorSpeechProject("complete", "model", new string('b', 40),
                "cpu", "int8", "C:/cache/analysis.json", new string('b', 64), 2, 20, .8),
            Tts: tts,
            VoiceOverrides: new Dictionary<string, string> { ["cue-0001"] = "female" },
            Trim: new EditorTrimRange(10, 50));
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
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
