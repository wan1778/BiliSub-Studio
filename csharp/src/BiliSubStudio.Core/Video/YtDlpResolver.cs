using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using BiliSubStudio.Core.Processes;
using BiliSubStudio.Core.Tools;

namespace BiliSubStudio.Core.Video;

public sealed class YtDlpResolver
{
    private readonly ToolManager _tools;
    private readonly ProcessRunner _processes;
    private readonly BilibiliPlayurlClient _playurl;
    private readonly BilibiliSubtitleClient _subtitles;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _generation;

    public YtDlpResolver(
        ToolManager tools,
        ProcessRunner processes,
        BilibiliPlayurlClient? playurl = null,
        BilibiliSubtitleClient? subtitles = null)
    {
        _tools = tools;
        _processes = processes;
        _playurl = playurl ?? new BilibiliPlayurlClient();
        _subtitles = subtitles ?? new BilibiliSubtitleClient();
    }

    public async Task<StreamSelection> ResolveAsync(VideoResolveRequest request, CancellationToken cancellationToken)
    {
        ValidateUrl(request.Url);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var loaded = await LoadInfoWithFallbackAsync(request.Url, request.CookieFile, cancellationToken);
            var info = loaded.Info;
            IReadOnlyDictionary<string, IReadOnlyList<string>> endpointMap =
                new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            var warnings = new List<string>();
            if (loaded.Warning.Length > 0) warnings.Add(loaded.Warning);
            try
            {
                endpointMap = loaded.Native
                    ? NativeEndpointMap(info)
                    : await _playurl.GetEndpointMapAsync(request.Url, info.Id, request.CookieFile, cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                warnings.Add("Không lấy được CDN backup từ playurl: " + Compact(error.GetBaseException().Message));
            }

            var generation = Interlocked.Increment(ref _generation);
            var qualityHeight = ParseQuality(request.Quality);
            ResolvedStream? video = null;
            ResolvedStream? audio = null;
            if (!string.Equals(request.Mode, "audio-only", StringComparison.OrdinalIgnoreCase))
            {
                var selected = ChooseVideo(info.Formats, qualityHeight, !string.Equals(request.Container, "mkv", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("Không tìm thấy video stream phù hợp.");
                endpointMap.TryGetValue(selected.FormatId, out var rawEndpoints);
                var endpoints = MergeEndpointUrls(selected.Url, rawEndpoints);
                var endpointIndex = endpoints.Count == 0 ? 0 : Math.Abs(request.EndpointOffset) % endpoints.Count;
                video = ToStream(StreamKind.Video, selected, generation, endpoints, endpointIndex);
                if (endpoints.Count <= 1)
                    warnings.Add($"Video format {selected.FormatId} chỉ có một CDN endpoint khả dụng.");
            }
            if (!string.Equals(request.Mode, "video-only", StringComparison.OrdinalIgnoreCase))
            {
                var selected = ChooseAudio(info.Formats)
                    ?? throw new InvalidOperationException("Không tìm thấy audio stream phù hợp.");
                endpointMap.TryGetValue(selected.FormatId, out var rawEndpoints);
                var endpoints = MergeEndpointUrls(selected.Url, rawEndpoints);
                var endpointIndex = endpoints.Count == 0 ? 0 : Math.Abs(request.EndpointOffset) % endpoints.Count;
                audio = ToStream(StreamKind.Audio, selected, generation, endpoints, endpointIndex);
                if (endpoints.Count <= 1)
                    warnings.Add($"Audio format {selected.FormatId} chỉ có một CDN endpoint khả dụng.");
            }
            return new StreamSelection(info.Title, info.Id, video, audio, string.Join(" ", warnings.Distinct(StringComparer.Ordinal)));
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
            var loaded = await LoadInfoWithFallbackAsync(url, cookieFile, cancellationToken);
            var info = loaded.Info;
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
            var subtitleWarning = string.Empty;

            // Prefer Bilibili's player metadata because yt-dlp can expose zero tracks
            // for videos whose signed-in player still offers normal or AI captions.
            var probeLines = new List<string>();
            try
            {
                var nativeTracks = await _subtitles.GetTracksAsync(url, info.Id, cookieFile, cancellationToken, probeLines.Add);
                foreach (var track in nativeTracks)
                {
                    if (seen.Add(track.Language)) tracks.Add(track);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                // 412 means Bilibili is blocking (not logged in / session expired / IP banned): say so
                // explicitly so the user logs in again instead of guessing.
                subtitleWarning = error.GetBaseException() is HttpRequestException httpError
                    && httpError.StatusCode == (HttpStatusCode)412
                    ? "Bilibili chặn lấy phụ đề (mã 412): thường do chưa đăng nhập hoặc hết phiên — hãy đăng nhập lại tài khoản Bilibili trong app rồi dán link lại."
                    : "Không đọc được phụ đề trực tiếp từ Bilibili: " + Compact(error.GetBaseException().Message);
                if (probeLines.Count > 0)
                    subtitleWarning += " · Chi tiết: " + string.Join(" · ", probeLines);
            }

            // yt-dlp remains a compatibility source. Native Bilibili tracks win on a
            // duplicate synthetic track id; otherwise these entries fill any gaps.
            AddTracks(info.Subtitles, official: true, ai: false, tracks, seen);
            AddTracks(info.AutomaticCaptions, official: false, ai: true, tracks, seen);

            // Show ALL subtitle versions so the user can choose (official first,
            // AI after). The default remains the official one because Preferred() ranks
            // official at the top; AI is only an extra choice and does not replace the default.
            var selectedTracks = tracks
                .OrderBy(SubtitleTrackPolicy.Priority)
                .ThenBy(track => track.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var notes = new List<string>();
            if (loaded.Warning.Length > 0) notes.Add(loaded.Warning);
            if (!string.IsNullOrWhiteSpace(subtitleWarning)) notes.Add(subtitleWarning);

            // The collection's parts (视频选集) so the media tab can offer
            // "download this whole set" instead of one video at a time. A
            // failure here is cosmetic: the single-part download still works.
            var parts = await _playurl.GetPartsAsync(url, info.Id, cookieFile, cancellationToken);

            return new VideoMetadata(info.Title, info.Id, qualities, selectedTracks, info.Thumbnail, string.Join(" ", notes), parts);
        }
        finally
        {
            _gate.Release();
        }
    }

    private const int MaxResolveAttempts = 3;
    private static readonly TimeSpan[] ResolveRetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];

    private async Task<YtDlpInfo> LoadInfoAsync(string url, string? cookieFile, CancellationToken cancellationToken)
    {
        var ytDlp = await _tools.EnsureYtDlpAsync(cancellationToken);
        var args = new List<string>();
        if (!string.IsNullOrWhiteSpace(cookieFile))
        {
            args.AddRange(["--cookies", cookieFile]);
        }
        args.AddRange(["--ignore-config", "--no-playlist", "--skip-download", "--no-warnings", "--socket-timeout", "20", "-J", url.Trim()]);
        // Field 2026-09-24: transient Bilibili resets/timeouts (10054, read
        // timeout) were killing whole jobs even though a retry would succeed. Only retry transient
        // network errors, at most 3 attempts with 2s/5s backoff; definitive errors (412/403/404,
        // deleted/private videos) fail immediately to report the real problem.
        Exception? last = null;
        for (var attempt = 1; attempt <= MaxResolveAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await _processes.RunAsync(ytDlp, args, cancellationToken);
            if (result.ExitCode == 0)
            {
                return JsonSerializer.Deserialize<YtDlpInfo>(result.StandardOutput, JsonOptions)
                    ?? throw new InvalidDataException("yt-dlp trả JSON rỗng.");
            }
            var message = Compact(result.StandardError);
            last = new InvalidOperationException($"yt-dlp resolve: {message}");
            if (attempt >= MaxResolveAttempts || !IsTransientResolveError(message)) break;
            await Task.Delay(ResolveRetryDelays[attempt - 1], cancellationToken);
        }
        throw last!;
    }

    // yt-dlp is the primary source; when the BiliBili extractor breaks on some videos
    // (Unable to extract initial state — yt-dlp issues #15924/#14793 not yet fixed in
    // stable), fall back to the Bilibili API directly using the session cookie. The same
    // fallback is used for both media downloads and the metadata page.
    private async Task<(YtDlpInfo Info, bool Native, string Warning)> LoadInfoWithFallbackAsync(
        string url,
        string? cookieFile,
        CancellationToken cancellationToken)
    {
        if (BilibiliPlayurlClient.ExtractBvid(url) is not null)
        {
            Exception? nativeError = null;
            try
            {
                var native = await LoadNativeInfoAsync(url, cookieFile, cancellationToken);
                return (native, true, string.Empty);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { nativeError = error; }
            try
            {
                var info = await LoadInfoAsync(url, cookieFile, cancellationToken);
                return (info, false,
                    "API Bilibili trực tiếp không đọc được video này (" + Compact(nativeError!.GetBaseException().Message) + "); đã dùng yt-dlp.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ytError)
            {
                throw new InvalidOperationException(DescribeResolveFailure(ytError, nativeError!), ytError);
            }
        }
        try
        {
            return (await LoadInfoAsync(url, cookieFile, cancellationToken), false, string.Empty);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            throw new InvalidOperationException(
                "Không đọc được video từ yt-dlp: " + Compact(error.GetBaseException().Message), error);
        }
    }

    internal static string DescribeResolveFailure(Exception ytError, Exception nativeError)
    {
        var native = nativeError.GetBaseException();
        var nativeMessage = Compact(native.Message);
        var ytMessage = Compact(ytError.GetBaseException().Message);
        if (native is HttpRequestException { StatusCode: HttpStatusCode.PreconditionFailed }
            || nativeMessage.Contains("HTTP 412", StringComparison.OrdinalIgnoreCase))
        {
            return "Bilibili đang chặn API (412): phiên đăng nhập thiếu cookie thiết bị hoặc đã hết hạn. Hãy vào Cài đặt → Đăng nhập → đăng nhập lại bằng QR rồi thử lại.";
        }
        if (nativeMessage.Contains("code -101", StringComparison.OrdinalIgnoreCase)
            || nativeMessage.Contains("không hợp lệ hoặc đã hết hạn", StringComparison.OrdinalIgnoreCase)
            || nativeMessage.Contains("isLogin", StringComparison.OrdinalIgnoreCase))
        {
            return "Phiên đăng nhập Bilibili đã hết hạn hoặc không hợp lệ. Hãy vào Cài đặt → Đăng nhập → đăng nhập lại bằng QR rồi thử lại.";
        }
        if (nativeMessage.Contains("code -404", StringComparison.OrdinalIgnoreCase)
            || nativeMessage.Contains("code -10403", StringComparison.OrdinalIgnoreCase)
            || nativeMessage.Contains("啥都木有", StringComparison.Ordinal)
            || nativeMessage.Contains("稿件不可见", StringComparison.Ordinal))
        {
            return "Video không tồn tại, đã bị xóa hoặc đang ở chế độ riêng tư. Hãy kiểm tra lại liên kết.";
        }
        if (nativeMessage.Contains("code -352", StringComparison.OrdinalIgnoreCase))
        {
            return "Bilibili đang chặn truy cập tự động (mã -352). Hãy đăng nhập lại ở Cài đặt → Đăng nhập, hoặc đổi mạng rồi thử lại.";
        }
        if (ytMessage.Contains("deleted or geo-restricted", StringComparison.OrdinalIgnoreCase))
        {
            return "Video không tồn tại, đã bị xóa hoặc bị giới hạn khu vực. Nếu vẫn mở được trong Chrome, hãy đăng nhập lại ở Cài đặt → Đăng nhập rồi tải lại.";
        }
        if (ytMessage.Contains("Unable to extract initial state", StringComparison.OrdinalIgnoreCase))
        {
            return "Không đọc được video: Bilibili chặn truy cập tự động và trang video không còn dữ liệu cho yt-dlp. Hãy đăng nhập lại ở Cài đặt → Đăng nhập bằng QR rồi thử lại. Chi tiết API: " + nativeMessage;
        }
        return $"{ytMessage} · API Bilibili: {nativeMessage}";
    }

    private async Task<YtDlpInfo> LoadNativeInfoAsync(
        string url,
        string? cookieFile,
        CancellationToken cancellationToken)
    {
        var bvid = BilibiliPlayurlClient.ExtractBvid(url)
            ?? throw new InvalidOperationException("URL không chứa BVID để lấy metadata trực tiếp từ Bilibili.");
        var native = await _playurl.GetNativeInfoAsync(url, bvid, cookieFile, cancellationToken);
        var formats = new List<YtDlpFormat>(native.Streams.Count);
        foreach (var stream in native.Streams)
        {
            var video = string.Equals(stream.Kind, "video", StringComparison.Ordinal);
            var bandwidth = stream.Bandwidth.GetValueOrDefault();
            formats.Add(new YtDlpFormat
            {
                FormatId = stream.FormatId,
                Url = stream.Url,
                Extension = stream.Extension,
                VideoCodec = video ? NormalizeCodec(stream.Codecs) : "none",
                AudioCodec = video ? "none" : NormalizeCodec(stream.Codecs),
                Height = stream.Height,
                Fps = stream.FrameRate,
                TotalBitrate = bandwidth > 0 ? bandwidth / 1000d : (double?)null,
                AudioBitrate = !video && bandwidth > 0 ? bandwidth / 1000d : (double?)null,
                ApproximateFileSize = bandwidth > 0 && native.DurationSeconds > 0
                    ? bandwidth * native.DurationSeconds / 8d
                    : (double?)null,
                HttpHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Referer"] = "https://www.bilibili.com/",
                    ["User-Agent"] = BilibiliPlayurlClient.BrowserUserAgent,
                },
                BackupUrls = stream.BackupUrls,
            });
        }
        if (formats.Count == 0)
            throw new InvalidDataException("API Bilibili không trả stream DASH nào cho video này.");
        return new YtDlpInfo
        {
            Id = native.Bvid,
            Title = native.Title,
            Thumbnail = native.ThumbnailUrl,
            Formats = formats,
        };
    }

    private static string NormalizeCodec(string codec) =>
        string.IsNullOrWhiteSpace(codec) ? "unknown" : codec;

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> NativeEndpointMap(YtDlpInfo info)
    {
        var output = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var format in info.Formats)
        {
            if (format.BackupUrls.Count == 0) continue;
            if (!output.TryGetValue(format.FormatId, out var candidates))
            {
                candidates = [];
                output[format.FormatId] = candidates;
            }
            foreach (var backup in format.BackupUrls)
                if (!candidates.Contains(backup, StringComparer.OrdinalIgnoreCase)) candidates.Add(backup);
        }
        return output.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    // Pure classifier so the retry loop stays trivial: permanent failures
    // (auth/WAF blocks, removed content) must surface immediately with their
    // original message; only transport-level blips earn another attempt.
    private static bool IsTransientResolveError(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        if (message.Contains("HTTP Error 412", StringComparison.OrdinalIgnoreCase)
            || message.Contains("HTTP Error 403", StringComparison.OrdinalIgnoreCase)
            || message.Contains("HTTP Error 404", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Private video", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Video unavailable", StringComparison.OrdinalIgnoreCase)
            || message.Contains("has been removed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Login required", StringComparison.OrdinalIgnoreCase)
            || message.Contains("sign in to", StringComparison.OrdinalIgnoreCase))
            return false;
        return message.Contains("10054", StringComparison.OrdinalIgnoreCase)
            || message.Contains("connection aborted", StringComparison.OrdinalIgnoreCase)
            || message.Contains("connection reset", StringComparison.OrdinalIgnoreCase)
            || message.Contains("read timed out", StringComparison.OrdinalIgnoreCase)
            || message.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || message.Contains("TransportError", StringComparison.Ordinal)
            || message.Contains("Temporary failure", StringComparison.OrdinalIgnoreCase)
            || message.Contains("urlopen error", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Connection broken", StringComparison.OrdinalIgnoreCase);
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
            var rawLanguage = ai && !pair.Key.StartsWith("ai-", StringComparison.OrdinalIgnoreCase)
                ? "ai-" + pair.Key
                : pair.Key;
            var trackId = (official ? "official:" : ai ? "ai:" : "track:") + rawLanguage;
            if (!seen.Add(trackId)) continue;
            var selected = pair.Value
                .Where(x => !string.IsNullOrWhiteSpace(x.Url))
                .OrderBy(x => SubtitleRank(x.Extension))
                .FirstOrDefault();
            if (selected is null) continue;
            output.Add(new SubtitleTrack(
                trackId,
                (string.IsNullOrWhiteSpace(selected.Name) ? pair.Key : selected.Name) + (official ? " · Có sẵn" : ai ? " · Bilibili AI" : string.Empty),
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
            .ThenByDescending(x => x.Fps.GetValueOrDefault())
            .ThenByDescending(x => x.Quality.GetValueOrDefault())
            .ThenByDescending(x => x.TotalBitrate.GetValueOrDefault())
            .ThenBy(x => preferAvc ? CodecRank(x.VideoCodec) : 0)
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

    private static ResolvedStream ToStream(
        StreamKind kind,
        YtDlpFormat format,
        long generation,
        IReadOnlyList<string> endpointUrls,
        int endpointIndex)
    {
        // ApproximateFileSize is bitrate-derived and can differ materially for VBR media.
        // Only an authoritative filesize may participate in truncation checks.
        var rawSize = format.FileSize.GetValueOrDefault();
        var size = double.IsFinite(rawSize) && rawSize > 0 && rawSize <= long.MaxValue
            ? (long)Math.Floor(rawSize)
            : 0L;
        var rawEstimate = format.ApproximateFileSize.GetValueOrDefault();
        var estimatedSize = double.IsFinite(rawEstimate) && rawEstimate > 0 && rawEstimate <= long.MaxValue
            ? (long)Math.Floor(rawEstimate)
            : 0L;
        var rawHeight = format.Height.GetValueOrDefault();
        var height = double.IsFinite(rawHeight) && rawHeight > 0 && rawHeight <= int.MaxValue
            ? (int)Math.Round(rawHeight)
            : 0;
        var endpoints = endpointUrls.Count == 0 ? new[] { format.Url } : endpointUrls.ToArray();
        var activeIndex = Math.Clamp(endpointIndex, 0, endpoints.Length - 1);
        return new ResolvedStream(
            kind,
            format.FormatId,
            endpoints[activeIndex],
            new Dictionary<string, string>(format.HttpHeaders, StringComparer.OrdinalIgnoreCase),
            size,
            height,
            format.Extension,
            generation,
            endpoints,
            activeIndex,
            estimatedSize,
            DateTimeOffset.UtcNow);
    }

    private static IReadOnlyList<string> MergeEndpointUrls(string primaryUrl, IReadOnlyList<string>? rawEndpoints)
    {
        var output = new List<string>();
        var fingerprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in new[] { primaryUrl }.Concat(rawEndpoints ?? Array.Empty<string>()))
        {
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) continue;
            var fingerprint = uri.Scheme + "://" + uri.IdnHost + (uri.IsDefaultPort ? string.Empty : ":" + uri.Port) + uri.AbsolutePath;
            if (!fingerprints.Add(fingerprint)) continue;
            output.Add(candidate);
        }
        return output;
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
        [JsonPropertyName("thumbnail")] public string Thumbnail { get; init; } = string.Empty;
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
        [JsonPropertyName("fps")] public double? Fps { get; init; }
        [JsonPropertyName("quality")] public double? Quality { get; init; }
        [JsonPropertyName("filesize")] public double? FileSize { get; init; }
        [JsonPropertyName("filesize_approx")] public double? ApproximateFileSize { get; init; }
        [JsonPropertyName("http_headers")] public Dictionary<string, string> HttpHeaders { get; init; } = [];
        public IReadOnlyList<string> BackupUrls { get; init; } = [];
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
