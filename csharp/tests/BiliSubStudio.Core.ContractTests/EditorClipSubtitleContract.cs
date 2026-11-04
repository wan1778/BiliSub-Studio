using BiliSubStudio.Core.Configuration;
using BiliSubStudio.Core.Editor;

namespace BiliSubStudio.Core.ContractTests;

/// <summary>
/// A composed project keeps one Vietnamese SRT per timeline clip. The primary
/// clip still uses the project-level Subtitle/Tts; every other clip binds its
/// own SRT by EditorTimelineClip.Id so 3-4 videos each read their own file.
/// </summary>
internal static class EditorClipSubtitleContract
{
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"expected {expected}; got {actual}");
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "bilisub-clip-srt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = AppPaths.FromRoot(root);
            paths.EnsureBootstrapDirectories();
            var primaryVideo = Path.Combine(root, "tap-01.mp4");
            var secondVideo = Path.Combine(root, "tap-02.mp4");
            await File.WriteAllBytesAsync(primaryVideo, [1, 2, 3]);
            await File.WriteAllBytesAsync(secondVideo, [4, 5, 6]);
            var primarySrt = Path.Combine(root, "tap-01.vi.srt");
            var secondSrt = Path.Combine(root, "tap-02.vi.srt");
            await File.WriteAllTextAsync(primarySrt, "1\n00:00:01,000 --> 00:00:02,000\nTập một\n");
            await File.WriteAllTextAsync(secondSrt, "1\n00:00:00,500 --> 00:00:01,500\nTập hai\n");

            var store = new EditorProjectStore(paths);
            var project = await store.LoadOrCreateAsync(primaryVideo, 1920, 1080, 10, CancellationToken.None);
            var primary = EditorSubtitleDocument.UseVietnameseSrt(
                await EditorSubtitleDocument.LoadAsync(primarySrt, CancellationToken.None));
            var second = EditorSubtitleDocument.UseVietnameseSrt(
                await EditorSubtitleDocument.LoadAsync(secondSrt, CancellationToken.None));
            var clips = new[]
            {
                new EditorTimelineClip(EditorTimelineComposition.PrimaryClipId, primaryVideo, 0, 10, "Tập 1"),
                new EditorTimelineClip("tap-02", secondVideo, 0, 8, "Tập 2"),
            };
            var primaryProject = new EditorSubtitleProject(
                primary.Path, primary.Size, primary.LastWriteUtcTicks, primary.Sha256,
                primary.Cues, EditorSubtitlePlacement.Default, string.Empty, string.Empty, string.Empty);
            var secondProject = new EditorSubtitleProject(
                second.Path, second.Size, second.LastWriteUtcTicks, second.Sha256,
                second.Cues, EditorSubtitlePlacement.Default, string.Empty, string.Empty, string.Empty);

            await store.SaveAsync(project with
            {
                Subtitle = primaryProject,
                Timeline = new EditorTimelineProject(clips),
                ClipSubtitles = new Dictionary<string, EditorSubtitleProject> { ["tap-02"] = secondProject },
            }, CancellationToken.None);

            var reopened = await store.LoadOrCreateAsync(primaryVideo, 1920, 1080, 10, CancellationToken.None);
            Equal("Tập một", reopened.Subtitle!.Cues[0].VietnameseText);
            Equal(1, reopened.ClipSubtitles!.Count);
            Equal("Tập hai", reopened.ClipSubtitles["tap-02"].Cues[0].VietnameseText);
            True(reopened.ClipTts is null, "a project with no clip voice must not persist ClipTts");

            // A clip removed from the strip loses its SRT binding on the next save.
            var withRemoved = new Dictionary<string, EditorSubtitleProject>(reopened.ClipSubtitles)
            {
                ["removed-clip"] = secondProject,
            };
            await store.SaveAsync(reopened with { ClipSubtitles = withRemoved }, CancellationToken.None);
            var pruned = await store.LoadOrCreateAsync(primaryVideo, 1920, 1080, 10, CancellationToken.None);
            Equal(1, pruned.ClipSubtitles!.Count);
            True(!pruned.ClipSubtitles.ContainsKey("removed-clip"), "SRT of a removed clip survived the save");

            // Voice without its own clip SRT is meaningless and must be dropped.
            var orphanVoice = new EditorTtsProject(
                "complete", LocalTtsInstaller.Engine, LocalTtsInstaller.EngineVersion,
                TtsVoiceCatalog.DefaultVoice, TtsVoiceCatalog.DefaultVoice,
                string.Empty, new string('0', 64),
                new EditorVoiceTrack(Path.Combine(root, "missing.flac"), 0, 8), 1, 0);
            await store.SaveAsync(pruned with { ClipTts = new Dictionary<string, EditorTtsProject> { ["tap-02"] = orphanVoice } },
                CancellationToken.None);
            var withoutOrphan = await store.LoadOrCreateAsync(primaryVideo, 1920, 1080, 10, CancellationToken.None);
            True(withoutOrphan.ClipTts is null, "clip voice without a valid master/manifest was not dropped");

            // A missing clip SRT only drops that clip's binding, never the project.
            File.Delete(secondSrt);
            var missing = await store.LoadOrCreateAsync(primaryVideo, 1920, 1080, 10, CancellationToken.None);
            True(missing.ClipSubtitles is null, "missing clip SRT did not drop only its own binding");
            Equal("Tập một", missing.Subtitle!.Cues[0].VietnameseText);

            // Without a timeline there is no clip id to bind to, so the map is not persisted.
            await store.SaveAsync(missing with
            {
                Timeline = null,
                ClipSubtitles = new Dictionary<string, EditorSubtitleProject> { ["tap-02"] = secondProject },
            }, CancellationToken.None);
            var noTimeline = await store.LoadOrCreateAsync(primaryVideo, 1920, 1080, 10, CancellationToken.None);
            True(noTimeline.ClipSubtitles is null, "clip SRT survived without a timeline to bind to");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
