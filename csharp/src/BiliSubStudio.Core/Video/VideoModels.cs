namespace BiliSubStudio.Core.Video;

public enum StreamKind { Video, Audio }

public sealed record ResolvedStream(
    StreamKind Kind,
    string FormatId,
    string Url,
    IReadOnlyDictionary<string, string> Headers,
    long Size,
    int Height,
    string Extension,
    long Generation);

public sealed record StreamSelection(string Title, string Id, ResolvedStream? Video, ResolvedStream? Audio);

public sealed record SubtitleTrack(string Language, string DisplayName, bool Official, bool Ai, string Url, string Extension);

public sealed record VideoMetadata(
    string Title,
    string Id,
    IReadOnlyList<string> Qualities,
    IReadOnlyList<SubtitleTrack> Subtitles,
    string ThumbnailUrl = "");

public sealed record VideoResolveRequest(
    string Url,
    string Quality,
    string Mode,
    string Container,
    string? CookieFile = null);

public sealed record VideoDownloadRequest(
    string Url,
    string Quality,
    string Container,
    string Mode,
    string Speed,
    string OutputDirectory,
    string? CookieFile = null,
    string BundleSubtitleFormat = "",
    string BundleSubtitleTrack = "",
    bool BundleSubtitleIfAvailable = false,
    bool BundleThumbnail = false,
    bool MediaBundle = false,
    bool BundleVideo = true);

public sealed record VideoDownloadResult(string OutputPath, long Size, bool UsedRange, int PeakConnections);

public sealed record RangeDownloadStatus(
    bool RangeSupported,
    int ActiveConnections,
    int ConfiguredConnections,
    long BytesCompleted,
    long TotalBytes,
    double BytesPerSecond);
