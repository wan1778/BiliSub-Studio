namespace BiliSubStudio.Core.Editor;

public static class EditorSourceSelection
{
    public static string NormalizeCandidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Video path is empty.", nameof(path));
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new FileNotFoundException("Video đã bị di chuyển, xóa hoặc không còn truy cập được.", full);
        return full;
    }

    public static bool IsSameSource(string? currentPath, string candidatePath)
    {
        if (string.IsNullOrWhiteSpace(currentPath) || string.IsNullOrWhiteSpace(candidatePath)) return false;
        return string.Equals(
            Path.GetFullPath(currentPath),
            Path.GetFullPath(candidatePath),
            StringComparison.OrdinalIgnoreCase);
    }
}
