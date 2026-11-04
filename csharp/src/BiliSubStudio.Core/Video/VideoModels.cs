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
    long Generation,
    IReadOnlyList<string>? EndpointUrls = null,
    int EndpointIndex = 0,
    long EstimatedSize = 0,
    DateTimeOffset SignedAtUtc = default);

public sealed record StreamSelection(
    string Title,
    string Id,
    ResolvedStream? Video,
    ResolvedStream? Audio,
    string EndpointDiscoveryWarning = "");

public sealed record SubtitleTrack(string Language, string DisplayName, bool Official, bool Ai, string Url, string Extension);

/// <summary>
/// One entry of a multi-part collection (视频选集): the page number, the part
/// title Bilibili shows in its list, and its real duration in seconds.
/// </summary>
public sealed record VideoPart(int Page, string Title, double DurationSeconds);

public sealed record VideoMetadata(
    string Title,
    string Id,
    IReadOnlyList<string> Qualities,
    IReadOnlyList<SubtitleTrack> Subtitles,
    string ThumbnailUrl = "",
    string SubtitleDiscoveryWarning = "",
    IReadOnlyList<VideoPart>? Parts = null);

public sealed record VideoResolveRequest(
    string Url,
    string Quality,
    string Mode,
    string Container,
    string? CookieFile = null,
    int EndpointOffset = 0);

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
    bool BundleVideo = true,
    // Batch download of a collection: each part keeps its own file name so the
    // parts never collide on the same title.
    string? OutputName = null);

public sealed record VideoDownloadResult(string OutputPath, long Size, bool UsedRange, int PeakConnections);

public sealed record RangeDownloadStatus(
    bool RangeSupported,
    int ActiveConnections,
    int ConfiguredConnections,
    long BytesCompleted,
    long TotalBytes,
    double BytesPerSecond);
