using System.Globalization;

namespace BiliSubStudio.Core.Video;

/// <summary>
/// One source URL, any part of its collection: the <c>p</c> query parameter is
/// the part selector every Bilibili path in this app already understands
/// (ExtractPart reads it), so switching parts is a URL rewrite instead of a new
/// request model.
/// </summary>
public static class BilibiliPartUrl
{
    /// <summary>Returns the URL that points at <paramref name="part"/>.</summary>
    public static string WithPart(string url, int part)
    {
        if (string.IsNullOrWhiteSpace(url)) return url ?? string.Empty;
        if (part <= 0) part = 1;
        var stripped = StripPart(url);
        return stripped + (stripped.Contains('?') ? "&" : "?") + "p=" + part.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Removes an existing <c>p</c> parameter, keeping every other one.</summary>
    public static string StripPart(string url)    {
        if (string.IsNullOrWhiteSpace(url)) return url ?? string.Empty;
        var queryIndex = url.IndexOf('?');
        if (queryIndex < 0) return url;
        var head = url[..queryIndex];
        var kept = url[(queryIndex + 1)..]
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(piece => !piece.StartsWith("p=", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return kept.Length == 0 ? head : head + "?" + string.Join('&', kept);
    }

    /// <summary>The part a URL explicitly asks for, or 0 when it does not say.</summary>
    public static int ReadPart(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return 0;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return 0;
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = pair.Split('=', 2);
            if (fields.Length == 2 && string.Equals(fields[0], "p", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(Uri.UnescapeDataString(fields[1]), out var part) && part > 0)
                return part;
        }
        return 0;
    }
}
