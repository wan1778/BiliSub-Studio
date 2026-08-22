namespace BiliSubStudio.Core.IO;

public static class FileNamePolicy
{
    public static string Sanitize(string? value, string fallback)
    {
        var text = (value ?? string.Empty).Trim();
        foreach (var c in Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*"))
        {
            text = text.Replace(c, '_');
        }
        text = text.Trim(' ', '.');
        if (text.Length > 150)
        {
            text = text[..150];
        }
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        var stem = Path.GetFileNameWithoutExtension(text);
        if (ReservedNames.Contains(stem)) text = "_" + text;
        return text;
    }

    public static string UniquePath(string candidate, string? forbiddenInput = null)
    {
        candidate = Path.GetFullPath(candidate);
        var forbidden = string.IsNullOrWhiteSpace(forbiddenInput) ? null : Path.GetFullPath(forbiddenInput);
        if (!File.Exists(candidate) && !string.Equals(candidate, forbidden, StringComparison.OrdinalIgnoreCase))
        {
            return candidate;
        }
        var directory = Path.GetDirectoryName(candidate)!;
        var name = Path.GetFileNameWithoutExtension(candidate);
        var extension = Path.GetExtension(candidate);
        for (var index = 2; index < 10_000; index++)
        {
            var next = Path.Combine(directory, $"{name} ({index}){extension}");
            if (!File.Exists(next) && !string.Equals(next, forbidden, StringComparison.OrdinalIgnoreCase))
            {
                return next;
            }
        }
        throw new IOException("Không thể tạo tên file đầu ra duy nhất.");
    }

    private static readonly HashSet<string> ReservedNames = new(
        ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"],
        StringComparer.OrdinalIgnoreCase);
}
