namespace BiliSubStudio.Core.Video;

public static class SubtitleTrackPolicy
{
    public static int Priority(SubtitleTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        var language = RawLanguage(track.Language);
        var chinese = language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            || language.Contains("chi", StringComparison.OrdinalIgnoreCase);

        // Product contract: an available human/platform subtitle always beats an
        // AI-generated subtitle. Language preference is only a tie-breaker inside
        // the same source class.
        if (track.Official) return chinese ? 0 : 1;
        if (track.Ai) return chinese ? 2 : 3;
        return chinese ? 4 : 5;
    }

    public static SubtitleTrack? Preferred(IEnumerable<SubtitleTrack> tracks) => tracks
        .OrderBy(Priority)
        .ThenBy(track => track.DisplayName, StringComparer.OrdinalIgnoreCase)
        .FirstOrDefault();

    public static string RawLanguage(string language)
    {
        var value = language ?? string.Empty;
        var separator = value.IndexOf(':');
        return separator >= 0 ? value[(separator + 1)..] : value;
    }
}
