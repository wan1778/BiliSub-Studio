using BiliSubStudio.Core.Configuration;
using BiliSubStudio.Core.Editor;

namespace BiliSubStudio.Core.ContractTests;

internal static class EditorSubtitleManualContract
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "bilisub-manual-sub-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = AppPaths.FromRoot(root);
            paths.EnsureBootstrapDirectories();
            var store = new EditorSubtitleManualStore(paths);
            var sha = new string('a', 64);
            var source = new EditorSubtitleSource(
                Path.Combine(root, "source.srt"), 123, 456, sha,
                [
                    new EditorSubtitleCue("c1", "1", "00:00:01,000 --> 00:00:02,000", 1, 2, "师尊", "Sư tôn"),
                    new EditorSubtitleCue("c2", "2", "00:00:03,000 --> 00:00:04,000", 3, 4, "弟子", "Đệ tử"),
                ]);
            var state = new Dictionary<string, EditorManualCueState>(StringComparer.Ordinal)
            {
                ["c1"] = new("师尊大人", "Sư tôn đại nhân", true),
            };
            await store.SaveAsync(sha, state, CancellationToken.None);
            var loaded = await store.LoadAsync(sha, CancellationToken.None);
            if (!loaded.TryGetValue("c1", out var cue) || !cue.Locked || cue.SourceOverride != "师尊大人" || cue.VietnameseOverride != "Sư tôn đại nhân")
                throw new InvalidOperationException("manual cue state did not round-trip");
            var applied = EditorSubtitleManualStore.Apply(source, loaded);
            if (applied.Cues[0].SourceText != "师尊大人" || applied.Cues[0].VietnameseText != "Sư tôn đại nhân")
                throw new InvalidOperationException("manual cue overrides were not applied");
            if (applied.Cues[0].Timing != source.Cues[0].Timing || applied.Cues[1] != source.Cues[1])
                throw new InvalidOperationException("manual cue state changed untouched timeline/cues");
            await store.SaveAsync(sha, new Dictionary<string, EditorManualCueState>(), CancellationToken.None);
            if ((await store.LoadAsync(sha, CancellationToken.None)).Count != 0)
                throw new InvalidOperationException("empty manual cue state was not removed");
            var fresh = new EditorTranslationRequest("p", source, root, "out.srt", ForceFresh: true);
            if (!fresh.ForceFresh) throw new InvalidOperationException("force-fresh translation request contract missing");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
