using System.Security.Cryptography;

namespace BiliSubStudio.Core.Editor;

public static partial class EditorSubtitleDocument
{
    public static bool SourceFingerprintMatchesCurrent(EditorSubtitleSource? source) =>
        source is not null && SourceFingerprintMatchesCurrent(
            source.Path, source.Size, source.LastWriteUtcTicks, source.Sha256);

    public static bool SourceFingerprintMatchesCurrent(
        string path,
        long expectedSize,
        long expectedLastWriteUtcTicks,
        string expectedSha256)
    {
        if (string.IsNullOrWhiteSpace(path) || expectedSize <= 0 || expectedLastWriteUtcTicks <= 0
            || expectedSha256.Length != 64 || expectedSha256.Any(ch => !Uri.IsHexDigit(ch)))
            return false;
        try
        {
            var absolute = Path.GetFullPath(path.Trim());
            var before = new FileInfo(absolute);
            if (!before.Exists || !string.Equals(before.Extension, ".srt", StringComparison.OrdinalIgnoreCase)
                || before.Length != expectedSize || before.LastWriteTimeUtc.Ticks != expectedLastWriteUtcTicks)
                return false;

            using var stream = new FileStream(absolute, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.SequentialScan);
            var sha256 = Convert.ToHexStringLower(SHA256.HashData(stream));

            var after = new FileInfo(absolute);
            return after.Exists
                && after.Length == expectedSize
                && after.LastWriteTimeUtc.Ticks == expectedLastWriteUtcTicks
                && string.Equals(sha256, expectedSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
