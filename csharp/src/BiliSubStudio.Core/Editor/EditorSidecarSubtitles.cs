using System.Text.RegularExpressions;

namespace BiliSubStudio.Core.Editor;

/// <summary>
/// Finds the Vietnamese sidecar SRT that belongs to a video the way an editor
/// imports a clip together with its subtitle: same file name first, then a
/// language-tagged name, then the episode number (2.srt for "... (2).mp4").
/// Only the file name decides the match; the caller still parses and verifies
/// the SRT before attaching it.
/// </summary>
public static class EditorSidecarSubtitles
{
    private static readonly HashSet<string> VietnameseTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "vi", "vie", "vi-vn", "vi_vn", "vietnamese", "vietsub", "viet", "translated", "dich",
    };

    // A sidecar tagged with one of these is the source-language subtitle, not the
    // Vietnamese one the editor burns, so it must never be auto-attached.
    private static readonly HashSet<string> ForeignTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "zh", "zho", "chi", "chs", "cht", "zh-cn", "zh_cn", "zh-hans", "zh-hant", "cn",
        "en", "eng", "ja", "jpn", "jp", "ko", "kor", "kr", "th", "tha",
        "fr", "fra", "de", "deu", "es", "spa", "ru", "rus", "pt", "por", "id", "ind",
    };

    // Episode stems accepted for "... (2).mp4" -> "2.srt": the bare number plus the
    // common Vietnamese part prefixes. The prefix list is deliberately closed so a
    // random "part2.srt" cannot hijack a video that has nothing to do with it.
    private static readonly string[] EpisodePrefixes =
    [
        "", "tap ", "tap-", "tap", "ep ", "ep-", "ep", "phan ", "phan-", "phan", "p",
    ];

    private static readonly Regex TrailingEpisode = new(
        @"(?:^|[\s._\-\(\[（【])(\d{1,4})(?:\)|\]|）|】)?\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Best Vietnamese SRT beside the video, or null when it has no clear match.
    /// </summary>
    /// <param name="videoPath">Video whose folder and base name drive the match.</param>
    /// <param name="allowSingleFolderFallback">
    /// When the folder holds exactly one SRT and its name does not match, use it
    /// anyway. Only enable this for an explicit one-video import, never for a batch.
    /// </param>
    public static string? Find(string videoPath, bool allowSingleFolderFallback = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        var full = Path.GetFullPath(videoPath.Trim());
        var folder = Path.GetDirectoryName(full);
        if (string.IsNullOrWhiteSpace(folder)) return null;
        var files = SrtFiles(folder);
        if (files.Count == 0) return null;
        var stem = Path.GetFileNameWithoutExtension(full);
        string? best = null;
        var bestScore = 0;
        foreach (var candidate in files)
        {
            var score = Score(stem, Path.GetFileNameWithoutExtension(candidate));
            if (score <= 0) continue;
            if (best is not null && score < bestScore) continue;
            if (best is not null && score == bestScore
                && string.Compare(Path.GetFileName(candidate), Path.GetFileName(best),
                    StringComparison.OrdinalIgnoreCase) >= 0)
                continue;
            best = candidate;
            bestScore = score;
        }
        if (best is not null) return Path.GetFullPath(best);
        return allowSingleFolderFallback && files.Count == 1 ? Path.GetFullPath(files[0]) : null;
    }

    /// <summary>Whether one explicit SRT path belongs to the video (used by drag and drop).</summary>
    public static bool Matches(string videoPath, string subtitlePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(subtitlePath);
        var videoStem = Path.GetFileNameWithoutExtension(Path.GetFullPath(videoPath.Trim()));
        var subtitleStem = Path.GetFileNameWithoutExtension(Path.GetFullPath(subtitlePath.Trim()));
        if (subtitleStem.Equals(videoStem, StringComparison.OrdinalIgnoreCase)) return true;
        if (subtitleStem.StartsWith(videoStem + ".", StringComparison.OrdinalIgnoreCase)) return true;
        var episode = EpisodeNumber(videoStem);
        return episode is not null && EpisodeTag(subtitleStem, episode) is not null;
    }

    private static int Score(string videoStem, string subtitleStem)
    {
        if (subtitleStem.Equals(videoStem, StringComparison.OrdinalIgnoreCase)) return 1200;
        if (subtitleStem.StartsWith(videoStem + ".", StringComparison.OrdinalIgnoreCase))
        {
            var tag = subtitleStem[(videoStem.Length + 1)..];
            if (IsForeignTag(tag)) return 0;
            return IsVietnameseTag(tag) ? 1150 : 900;
        }
        var episode = EpisodeNumber(videoStem);
        if (episode is null) return 0;
        var episodeTag = EpisodeTag(subtitleStem, episode);
        if (episodeTag is null) return 0;
        if (IsForeignTag(episodeTag)) return 0;
        return IsVietnameseTag(episodeTag) ? 1030 : 1000;
    }

    /// <summary>Tag after an episode stem ("" for the bare number), or null when it does not match.</summary>
    private static string? EpisodeTag(string subtitleStem, string episode)
    {
        var numbers = new List<string> { episode };
        if (int.TryParse(episode, out var value) && value is >= 0 and <= 9999)
        {
            // "tap-02.mp4" must still pair with the user's "2.srt".
            numbers.Add(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            numbers.Add(value.ToString("00", System.Globalization.CultureInfo.InvariantCulture));
        }
        foreach (var number in numbers.Distinct(StringComparer.Ordinal))
        {
            foreach (var prefix in EpisodePrefixes)
            {
                var stem = prefix + number;
                if (subtitleStem.Equals(stem, StringComparison.OrdinalIgnoreCase)) return string.Empty;
                if (subtitleStem.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase))
                    return subtitleStem[(stem.Length + 1)..];
            }
        }
        return null;
    }

    private static string? EpisodeNumber(string videoStem)
    {
        var match = TrailingEpisode.Match(videoStem);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static bool IsVietnameseTag(string tag) =>
        VietnameseTags.Contains(tag) || VietnameseTags.Contains(FirstTag(tag));

    private static bool IsForeignTag(string tag) =>
        ForeignTags.Contains(tag) || ForeignTags.Contains(FirstTag(tag));

    private static string FirstTag(string tag)
    {
        var dot = tag.IndexOf('.');
        return dot < 0 ? tag : tag[..dot];
    }

    private static IReadOnlyList<string> SrtFiles(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*.srt", SearchOption.TopDirectoryOnly)
                .ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
