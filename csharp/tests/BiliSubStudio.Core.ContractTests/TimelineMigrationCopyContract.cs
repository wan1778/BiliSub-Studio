using BiliSubStudio.Core.Editor;
using BiliSubStudio.Core.Timeline;

namespace BiliSubStudio.Core.ContractTests;

// G4: schema-7 -> one-clip V2 copy. The migration is a pure in-memory copy
// from already-loaded state, then persisted only into the isolated V2 test
// root. Source project files and media are never touched (no IO on them).
internal static class TimelineMigrationCopyContract
{
    public static async Task RunAsync()
    {
        var schema7 = CreateSchema7Project();
        var sourceJsonPath = Path.Combine(Path.GetTempPath(), "bilisub-g4-" + Guid.NewGuid().ToString("N") + ".json");
        var mediaPath = Path.Combine(Path.GetTempPath(), "bilisub-g4-" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            // Persist the schema-7 fixture bytes; after migration they must be
            // byte-identical (no in-place mutation, DEC-004).
            await File.WriteAllTextAsync(sourceJsonPath, schema7.marker, CancellationToken.None);
            await File.WriteAllBytesAsync(mediaPath, [1, 2, 3], CancellationToken.None);

            var report = TimelineMigrator.MigrateSchema7(
                schema7.project, mediaPath,
                width: 1920, height: 1080, durationSeconds: 60,
                hasAudio: true,
                size: 3, lastWriteUtcTicks: 123);

            Check(TimelineProjectV2.Schema == 5, "migrated copy must use current V2 schema");
            Check(!string.Equals(report.Project.Id, schema7.project.Id, StringComparison.Ordinal),
                "migrated copy must own a fresh V2 identity, not the schema-7 id");
            var sequence = report.Project.Sequence
                ?? throw new InvalidOperationException("migrated copy lost its one-clip sequence");
            Check(sequence.VideoClips.Count == 1, "G4 copy must carry exactly one clip");
            var clip = sequence.VideoClips[0];
            Check(clip.SourceInUs == 0 && clip.SourceOutUs == 60_000_000 && clip.PlaybackRate == 1.0,
                "one-clip copy must span the full source at 1x");
            Check(sequence.OutputWidth == 1920 && sequence.OutputHeight == 1080,
                "copy geometry must default to the first clip");
            var asset = report.Project.Assets.Single();
            Check(string.Equals(asset.Path, mediaPath, StringComparison.Ordinal),
                "copy asset must point at the source video path");
            Check(asset.DurationUs == 60_000_000, "copy asset duration lost microsecond precision");
            var plan = TimelineCompiler.Compile(report.Project);
            Check(asset.HasAudio && plan.Audio.SourceAudio.Single().HasSourceStream,
                "migration lost probed source-audio presence");
            var missing = new[]
            {
                (TimelineMigrationWarning.RegionsFlattened, schema7.project.Regions.Count > 0),
                (TimelineMigrationWarning.SubtitleDropped, schema7.project.Subtitle is not null),
                (TimelineMigrationWarning.VoiceDropped, schema7.project.Tts is not null),
            }.Where(pair => pair.Item2 && !report.Warnings.Contains(pair.Item1))
                .Select(pair => pair.Item1).ToArray();
            Check(missing.Length == 0,
                "copy must explicitly report flattened/dropped schema-7 state: " + string.Join(",", missing));

            Check(File.ReadAllText(sourceJsonPath) == schema7.marker,
                "schema-7 fixture file changed during migration");
            Check(File.ReadAllBytes(mediaPath).SequenceEqual<byte>([1, 2, 3]),
                "source media changed during migration");

            // Persist the copy into the isolated V2 store and reload it.
            var root = Path.Combine(Path.GetTempPath(), "bilisub-g4-store-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new TimelineProjectStoreV2(root);
                await store.SaveAsync(report.Project, CancellationToken.None);
                var reopened = await store.LoadAsync(report.Project.Id, CancellationToken.None);
                Check(reopened.Sequence is not null && reopened.Sequence.VideoClips.Count == 1,
                    "persisted copy lost its one-clip sequence");
                Check(!Directory.Exists(Path.Combine(root, "..", "Projects")),
                    "V2 store escaped its test root");
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }

            // Reject anything that is not schema 7.
            Throws<InvalidDataException>(
                () => TimelineMigrator.MigrateSchema7(
                    schema7.project with { Schema = 6 }, mediaPath, 1920, 1080, 60, true, 3, 123),
                "non-schema-7 project accepted");
        }
        finally
        {
            try { File.Delete(sourceJsonPath); } catch { }
            try { File.Delete(mediaPath); } catch { }
        }
    }

    private static (EditorProject project, string marker) CreateSchema7Project()
    {
        var source = new EditorSourceFingerprint(
            "C:/v/movie.mp4", Size: 1000, LastWriteUtcTicks: 42,
            Width: 1920, Height: 1080, Duration: 60);
        var regions = new List<EditRegion>
        {
            new(.1, .1, .2, .2, "blur", 18, false, 0, 60, "g4-region"),
        };
        var tts = new EditorTtsProject(
            "complete", "nghi-tts", "1", "male", "ngoc_huyen",
            "C:/cache/manifest.json", "sha", VoiceTrack: null!, CueCount: 1, ReviewCount: 0);
        var project = new EditorProject(
            7,
            "legacy0123456789abcdef012345",
            "G4 legacy",
            source,
            "movie_edited.mp4",
            regions,
            null,
            DateTimeOffset.UtcNow,
            EditorAudioSettings.Default,
            Tts: tts,
            Trim: new EditorTrimRange(0, 60));
        return (project, "schema-7-fixture-bytes");
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
