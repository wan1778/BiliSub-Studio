using System.Text.Json;
using System.Text.Json.Serialization;
using BiliSubStudio.Core.Processes;
using BiliSubStudio.Core.Tools;

namespace BiliSubStudio.Core.Video;

public sealed class YtDlpResolver
{
    private readonly ToolManager _tools;
    private readonly ProcessRunner _processes;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _generation;

    public YtDlpResolver(ToolManager tools, ProcessRunner processes)
    {
        _tools = tools;
        _processes = processes;
    }

    public async Task<StreamSelection> ResolveAsync(VideoResolveRequest request, CancellationToken cancellationToken)
    {
        ValidateUrl(request.Url);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var info = await LoadInfoAsync(request.Url, request.CookieFile, cancellationToken);
            var generation = Interlocked.Increment(ref _generation);
            var qualityHeight = ParseQuality(request.Quality);
            ResolvedStream? video = null;
            ResolvedStream? audio = null;
            if (!string.Equals(request.Mode, "audio-only", StringComparison.OrdinalIgnoreCase))
            {
                var selected = ChooseVideo(info.Formats, qualityHeight, !string.Equals(request.Container, "mkv", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("Không tìm thấy video stream phù hợp.");
                video = ToStream(StreamKind.Video, selected, generation);
            }
            if (!string.Equals(request.Mode, "video-only", StringComparison.OrdinalIgnoreCase))
            {
                var selected = ChooseAudio(info.Formats)
                    ?? throw new InvalidOperationException("Không tìm thấy audio stream phù hợp.");
                audio = ToStream(StreamKind.Audio, selected, generation);
            }
            return new StreamSelection(info.Title, info.Id, video, audio);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<VideoMetadata> GetMetadataAsync(string url, string? cookieFile, CancellationToken cancellationToken)
    {
        ValidateUrl(url);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var info = await LoadInfoAsync(url, cookieFile, cancellationToken);
            var qualities = info.Formats
                .Where(IsVideo)
                .Select(x => x.Height is > 0 and <= int.MaxValue ? (int)Math.Round(x.Height.Value) : 0)
                .Where(x => x > 0)
                .Distinct()
                .OrderDescending()
                .Select(x => $"{x}p")
                .Prepend("best")
                .ToArray();

            var tracks = new List<SubtitleTrack>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddTracks(info.Subtitles, official: true, ai: false, tracks, seen);
            AddTracks(info.AutomaticCaptions, official: false, ai: true, tracks, seen);
            return new VideoMetadata(info.Title, info.Id, qualities, tracks);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<YtDlpInfo> LoadInfoAsync(string url, string? cookieFile, CancellationToken cancellationToken)
    {
        var ytDlp = await _tools.EnsureYtDlpAsync(cancellationToken);
        var args = new List<string>();
        if (!string.IsNullOrWhiteSpace(cookieFile))
        {
            args.AddRange(["--cookies", cookieFile]);
        }
        args.AddRange(["--ignore-config", "--no-playlist", "--skip-download", "--no-warnings", "-J", url.Trim()]);
        var result = await _processes.RunAsync(ytDlp, args, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"yt-dlp resolve: {Compact(result.StandardError)}");
        }
        return JsonSerializer.Deserialize<YtDlpInfo>(result.StandardOutput, JsonOptions)
            ?? throw new InvalidDataException("yt-dlp trả JSON rỗng.");
    }

    private static void AddTracks(
        IReadOnlyDictionary<string, List<YtDlpSubtitle>> source,
        bool official,
        bool ai,
        List<SubtitleTrack> output,
        HashSet<string> seen)
    {
        foreach (var pair in source.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var trackId = (official ? "official:" : ai ? "ai:" : "track:") + pair.Key;
            if (!seen.Add(trackId)) continue;
            var selected = pair.Value
                .Where(x => !string.IsNullOrWhiteSpace(x.Url))
                .OrderBy(x => SubtitleRank(x.Extension))
                .FirstOrDefault();
            if (selected is null) continue;
            output.Add(new SubtitleTrack(
                trackId,
                (string.IsNullOrWhiteSpace(selected.Name) ? pair.Key : selected.Name) + (official ? " · Chính thức" : ai ? " · AI" : string.Empty),
                official,
                ai,
                selected.Url,
                selected.Extension));
        }
    }

    private static int SubtitleRank(string ext) => ext.ToLowerInvariant() switch
    {
        "json3" => 0,
        "json" => 1,
        "srv3" => 2,
        "vtt" => 3,
        "srt" => 4,
        _ => 10,
    };

    private static YtDlpFormat? ChooseVideo(IReadOnlyList<YtDlpFormat> formats, int requestedHeight, bool preferAvc)
    {
        var candidates = formats.Where(IsVideo)
            .Where(x => requestedHeight <= 0 || x.Height.GetValueOrDefault() <= requestedHeight)
            .ToList();
        if (candidates.Count == 0 && requestedHeight > 0)
        {
            candidates = formats.Where(IsVideo).ToList();
        }
        return candidates
            .OrderByDescending(x => x.Height.GetValueOrDefault())
            .ThenBy(x => preferAvc ? CodecRank(x.VideoCodec) : 0)
            .ThenByDescending(x => x.TotalBitrate.GetValueOrDefault())
            .FirstOrDefault();
    }

    private static YtDlpFormat? ChooseAudio(IReadOnlyList<YtDlpFormat> formats) => formats
        .Where(x => !string.IsNullOrWhiteSpace(x.Url) && HasCodec(x.AudioCodec) && !HasCodec(x.VideoCodec))
        .OrderByDescending(x => x.AudioBitrate.GetValueOrDefault() > 0 ? x.AudioBitrate.GetValueOrDefault() : x.TotalBitrate.GetValueOrDefault())
        .FirstOrDefault();

    private static bool IsVideo(YtDlpFormat x) =>
        !string.IsNullOrWhiteSpace(x.Url) && HasCodec(x.VideoCodec) && !HasCodec(x.AudioCodec);

    private static bool HasCodec(string? value) => !string.IsNullOrWhiteSpace(value) && !string.Equals(value, "none", StringComparison.OrdinalIgnoreCase);

    private static int CodecRank(string codec)
    {
        var value = codec.ToLowerInvariant();
        if (value.StartsWith("avc1") || value.Contains("h264") || value.Contains("h.264")) return 0;
        if (value.StartsWith("hev1") || value.StartsWith("hvc1") || value.Contains("hevc") || value.Contains("h265")) return 1;
        if (value.StartsWith("av01") || value.Contains("av1")) return 2;
        return 3;
    }

    private static ResolvedStream ToStream(StreamKind kind, YtDlpFormat format, long generation)
    {
        var rawSize = format.FileSize is > 0 ? format.FileSize.Value : format.ApproximateFileSize.GetValueOrDefault();
        var size = double.IsFinite(rawSize) && rawSize > 0 && rawSize <= long.MaxValue
            ? (long)Math.Floor(rawSize)
            : 0L;
        var rawHeight = format.Height.GetValueOrDefault();
        var height = double.IsFinite(rawHeight) && rawHeight > 0 && rawHeight <= int.MaxValue
            ? (int)Math.Round(rawHeight)
            : 0;
        return new ResolvedStream(
            kind,
            format.FormatId,
            format.Url,
            new Dictionary<string, string>(format.HttpHeaders, StringComparer.OrdinalIgnoreCase),
            size,
            height,
            format.Extension,
            generation);
    }

    private static int ParseQuality(string quality)
    {
        var text = (quality ?? string.Empty).Trim().ToLowerInvariant();
        return text is "" or "best" ? 0 : int.TryParse(text.TrimEnd('p'), out var value) ? value : 0;
    }

    private static void ValidateUrl(string url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("URL Bilibili không hợp lệ.", nameof(url));
        }
    }

    private static string Compact(string value)
    {
        value = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return value.Length <= 500 ? value : value[..500] + "…";
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private sealed record YtDlpInfo
    {
        [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
        [JsonPropertyName("title")] public string Title { get; init; } = string.Empty;
        [JsonPropertyName("formats")] public List<YtDlpFormat> Formats { get; init; } = [];
        [JsonPropertyName("subtitles")] public Dictionary<string, List<YtDlpSubtitle>> Subtitles { get; init; } = [];
        [JsonPropertyName("automatic_captions")] public Dictionary<string, List<YtDlpSubtitle>> AutomaticCaptions { get; init; } = [];
    }

    private sealed record YtDlpFormat
    {
        [JsonPropertyName("format_id")] public string FormatId { get; init; } = string.Empty;
        [JsonPropertyName("url")] public string Url { get; init; } = string.Empty;
        [JsonPropertyName("ext")] public string Extension { get; init; } = string.Empty;
        [JsonPropertyName("vcodec")] public string VideoCodec { get; init; } = string.Empty;
        [JsonPropertyName("acodec")] public string AudioCodec { get; init; } = string.Empty;
        [JsonPropertyName("height")] public double? Height { get; init; }
        [JsonPropertyName("filesize")] public double? FileSize { get; init; }
        [JsonPropertyName("filesize_approx")] public double? ApproximateFileSize { get; init; }
        [JsonPropertyName("http_headers")] public Dictionary<string, string> HttpHeaders { get; init; } = [];
        [JsonPropertyName("tbr")] public double? TotalBitrate { get; init; }
        [JsonPropertyName("abr")] public double? AudioBitrate { get; init; }
    }

    private sealed record YtDlpSubtitle
    {
        [JsonPropertyName("ext")] public string Extension { get; init; } = string.Empty;
        [JsonPropertyName("url")] public string Url { get; init; } = string.Empty;
        [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    }
}
