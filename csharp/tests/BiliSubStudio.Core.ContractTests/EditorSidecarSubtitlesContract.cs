using BiliSubStudio.Core.Editor;

namespace BiliSubStudio.Core.ContractTests;

/// <summary>
/// Importing a video must bring its Vietnamese SRT along, like an editor reads a
/// sidecar subtitle. The match is name based only: exact name, Vietnamese tag,
/// then the episode number, and a foreign-language sidecar is never picked.
/// </summary>
internal static class EditorSidecarSubtitlesContract
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

    public static Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "bilisub-sidecar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string Touch(string name)
            {
                var path = Path.Combine(root, name);
                File.WriteAllText(path, "1\n00:00:00,000 --> 00:00:01,000\nXin chào\n");
                return path;
            }

            var movie = Touch("Phim.mp4");
            var untagged = Touch("Phim.srt");
            var vietnameseTagged = Touch("Phim.vi.srt");
            var foreign = Touch("Phim.en.srt");
            Equal(untagged, EditorSidecarSubtitles.Find(movie));
            True(EditorSidecarSubtitles.Matches(movie, untagged), "exact sidecar must match");
            True(EditorSidecarSubtitles.Matches(movie, foreign), "foreign sidecar must still match by name");
            File.Delete(untagged);
            Equal(vietnameseTagged, EditorSidecarSubtitles.Find(movie));
            File.Delete(vietnameseTagged);
            True(EditorSidecarSubtitles.Find(movie) is null,
                "a foreign-only sidecar must not auto-attach");

            // A foreign-language sidecar alone must never be auto-attached.
            var chineseOnly = Touch("Phim2.mp4");
            Touch("Phim2.zh.srt");
            True(EditorSidecarSubtitles.Find(chineseOnly) is null,
                "a zh source subtitle was attached as Vietnamese");

            // "2.srt" belongs to "... (2).mp4" and to "tap-02.mp4".
            var part2 = Touch("聚宝仙鼎 (2).mp4");
            var part3 = Touch("聚宝仙鼎 (3).mp4");
            var srt2 = Touch("2.srt");
            var srt3 = Touch("3.vi.srt");
            Equal(srt2, EditorSidecarSubtitles.Find(part2));
            Equal(srt3, EditorSidecarSubtitles.Find(part3));
            var tap = Touch("tap-02.mp4");
            Equal(srt2, EditorSidecarSubtitles.Find(tap));
            True(!EditorSidecarSubtitles.Matches(part2, srt3), "(2) must not read 3.srt");

            // An unrelated number must not steal a numbered SRT.
            var unrelated = Touch("Phim (9).mp4");
            True(EditorSidecarSubtitles.Find(unrelated) is null, "unmatched episode received an SRT");

            // A differently named single SRT is only used when the caller opts in,
            // and only in a folder that holds exactly that one SRT.
            var onlyFolder = Directory.CreateDirectory(Path.Combine(root, "only")).FullName;
            var lonely = Path.Combine(onlyFolder, "BatKhaThi.mp4");
            File.WriteAllText(lonely, string.Empty);
            var onlySrt = Path.Combine(onlyFolder, "phu-de.srt");
            File.WriteAllText(onlySrt, "1\n00:00:00,000 --> 00:00:01,000\nXin chào\n");
            True(EditorSidecarSubtitles.Find(lonely) is null, "single fallback must be opt-in");
            Equal(onlySrt, EditorSidecarSubtitles.Find(lonely, allowSingleFolderFallback: true));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
        return Task.CompletedTask;
    }
}
