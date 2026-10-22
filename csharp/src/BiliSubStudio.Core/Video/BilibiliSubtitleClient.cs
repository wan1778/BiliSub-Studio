using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BiliSubStudio.Core.Video;

/// <summary>
/// Discovers Bilibili subtitle tracks directly from player metadata. This is a
/// metadata-only fallback for cases where yt-dlp exposes no subtitles even though
/// the signed-in Bilibili player has normal or AI captions.
/// </summary>
public sealed partial class BilibiliSubtitleClient
{
    private static readonly HttpClient SharedHttp = new(new SocketsHttpHandler
    {
        MaxConnectionsPerServer = 4,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        // Bilibili gzip trang video dù không hỏi (23/09): không giải nén là
        // đọc ra rác nhị phân, regex aid mù tịt. Bật giải nén không ảnh hưởng
        // JSON/protobuf trả về thường.
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
    })
    {
        Timeout = TimeSpan.FromSeconds(45),
    };

    private readonly HttpClient _http;

    // Vân tay buvid chống chặn 412 (23/09): API sub của Bilibili khóa rủi ro
    // khi thiếu buvid3. Lấy 1 lần/process từ finger/spi (không cần đăng nhập),
    // hỏng thì thôi (best-effort, không bao giờ chặn metadata).
    private static readonly SemaphoreSlim FingerGate = new(1, 1);
    private static string? _fingerCookies;

    public BilibiliSubtitleClient() : this(SharedHttp) { }
    public BilibiliSubtitleClient(HttpClient http) => _http = http;

    public async Task<IReadOnlyList<SubtitleTrack>> GetTracksAsync(
        string sourceUrl,
        string infoId,
        string? cookieFile,
        CancellationToken cancellationToken,
        Action<string>? probe = null)
    {
        var bvid = ExtractBvid(infoId) ?? ExtractBvid(sourceUrl)
            ?? throw new InvalidOperationException("Không xác định được BVID để lấy phụ đề Bilibili.");
        var part = ExtractPart(infoId, sourceUrl);
        var cookieHeader = await ReadCookieHeaderAsync(cookieFile, cancellationToken);
        cookieHeader = await WithFingerprintAsync(cookieHeader, bvid, cancellationToken);
        (long Aid, long Cid) identity;
        try
        {
            identity = await ResolveVideoIdentityAsync(bvid, part, cookieHeader, cancellationToken);
            probe?.Invoke("identity: view");
        }
        catch (HttpRequestException viewError)
        {
            // view hay bị chặn 412 trong khi pagelist/archive-stat thường mở:
            // đi đường vòng lấy cid/aid, giữ nguyên lỗi gốc để báo rõ.
            probe?.Invoke($"identity: view HTTP {(int?)viewError.StatusCode}, thử đường vòng");
            try
            {
                identity = await ResolveVideoIdentityFallbackAsync(bvid, part, cookieHeader, cancellationToken);
                probe?.Invoke($"identity: fallback aid={identity.Aid} cid={identity.Cid}");
            }
            catch (Exception fallbackError)
            {
                probe?.Invoke("identity: fallback lỗi: " + Compact(fallbackError.GetBaseException().Message));
                throw new HttpRequestException(
                    $"Bilibili subtitle API HTTP {(int?)viewError.StatusCode} (bước lấy mã video view).", null, viewError.StatusCode);
            }
        }
        var tracks = new List<SubtitleTrack>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // player wbi/v2 ký trước (track URL host mới còn sống), hỏng thì
        // player/v2 cũ (giữ tương thích fixture + video cũ).
        try
        {
            var playerUri = await PlayerV2UriAsync(identity, bvid, cookieHeader, cancellationToken);
            JsonDocument legacy;
            try
            {
                legacy = await SendJsonAsync(playerUri, cookieHeader, bvid, cancellationToken);
                probe?.Invoke($"legacy player: { (playerUri.AbsolutePath.Contains("/wbi/", StringComparison.Ordinal) ? "wbi" : "plain")}");
            }
            catch (HttpRequestException) when (playerUri.AbsolutePath.Contains("/wbi/", StringComparison.Ordinal))
            {
                probe?.Invoke("legacy player: wbi hỏng, lùi về plain");
                legacy = await SendJsonAsync(
                    new Uri($"https://api.bilibili.com/x/player/v2?bvid={Uri.EscapeDataString(bvid)}&cid={identity.Cid}"),
                    cookieHeader,
                    bvid,
                    cancellationToken);
            }
            using (legacy)
            {
                AddLegacyTracks(legacy.RootElement, tracks, seen);
            }
            probe?.Invoke($"legacy player/v2: {tracks.Count} track");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            // Metadata remains usable; the authenticated binary endpoint below can
            // still recover an AI caption track when available.
            probe?.Invoke("legacy player/v2 lỗi: " + Compact(error.GetBaseException().Message));
        }

        var hasAi = tracks.Any(track => track.Ai);
        var hasSession = ContainsCookie(cookieHeader, "SESSDATA");
        probe?.Invoke($"web/view: hasAi={hasAi} hasSession={hasSession}");
        if (!hasAi && hasSession)
        {
            var context = Uri.EscapeDataString("{\"video_type\":1}");
            var uri = new Uri(
                $"https://api.bilibili.com/x/v2/subtitle/web/view?oid={identity.Cid}&pid={identity.Aid}" +
                $"&context_ext={context}&type=1&cur_production_type=0&preferred_language=ai-zh&playlist_switch=0");
            try
            {
                var payload = await SendBytesAsync(uri, cookieHeader, bvid, cancellationToken);
                var before = tracks.Count;
                foreach (var raw in ParseWebSubtitleTracks(payload))
                {
                    if (!raw.Language.StartsWith("ai-", StringComparison.OrdinalIgnoreCase)) continue;
                    AddTrack(tracks, seen, raw.Language, raw.DisplayName, ai: true, raw.Url);
                }
                probe?.Invoke($"web/view: +{tracks.Count - before} AI track ({payload.Length} bytes)");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                // AI subtitle absence/failure is optional and must not block media.
                probe?.Invoke("web/view lỗi: " + Compact(error.GetBaseException().Message));
            }
        }

        return tracks
            .OrderBy(SubtitleTrackPolicy.Priority)
            .ThenBy(track => track.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<(long Aid, long Cid)> ResolveVideoIdentityFallbackAsync(
        string bvid,
        int part,
        string cookieHeader,
        CancellationToken cancellationToken)
    {
        // Đường vòng khi view bị chặn: pagelist cho cid từng phần (200 đã kiểm
        // chứng), HTML trang video cho aid (__NEXT_DATA__, archive/stat đã
        // chết 404). Cả hai thường mở không cần ký.
        using var pages = await SendJsonAsync(
            new Uri("https://api.bilibili.com/x/player/pagelist?bvid=" + Uri.EscapeDataString(bvid) + "&jsonp=jsonp"),
            cookieHeader,
            bvid,
            cancellationToken);
        if (!pages.RootElement.TryGetProperty("code", out var pageCode) || pageCode.GetInt32() != 0
            || !pages.RootElement.TryGetProperty("data", out var pageData) || pageData.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Đường vòng pagelist không trả danh sách phần video.");
        JsonElement selected = default;
        var found = false;
        foreach (var page in pageData.EnumerateArray())
        {
            if (page.ValueKind != JsonValueKind.Object) continue;
            if (page.TryGetProperty("page", out var number) && number.GetInt32() == part
                && page.TryGetProperty("cid", out var pageCid) && pageCid.TryGetInt64(out var cidValue) && cidValue > 0)
            {
                selected = page;
                found = true;
                break;
            }
        }
        if (!found)
            throw new InvalidDataException($"Đường vòng pagelist không thấy phần {part}.");
        var cid = selected.GetProperty("cid").GetInt64();

        var aid = await ResolveAidFromPageAsync(bvid, cookieHeader, cancellationToken);
        return (aid, cid);
    }

    private async Task<long> ResolveAidFromPageAsync(string bvid, string cookieHeader, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            new Uri("https://www.bilibili.com/video/" + Uri.EscapeDataString(bvid) + "/"));
        request.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
        request.Headers.TryAddWithoutValidation("User-Agent", BilibiliPlayurlClient.BrowserUserAgent);
        if (!string.IsNullOrWhiteSpace(cookieHeader)) request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Bilibili video page HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        foreach (var pair in AidBvidRegex().Matches(html).Cast<Match>())
        {
            if (string.Equals(pair.Groups["bvid"].Value, bvid, StringComparison.Ordinal)
                && long.TryParse(pair.Groups["aid"].Value, out var aid) && aid > 0)
                return aid;
        }
        var loose = AidRegex().Match(html);
        if (loose.Success && long.TryParse(loose.Groups["aid"].Value, out var looseAid) && looseAid > 0)
            return looseAid;
        throw new InvalidDataException("Đường vòng trang video không thấy AID.");
    }

    private async Task<(long Aid, long Cid)> ResolveVideoIdentityAsync(
        string bvid,
        int part,
        string cookieHeader,
        CancellationToken cancellationToken)
    {
        using var document = await SendJsonAsync(
            new Uri("https://api.bilibili.com/x/web-interface/view?bvid=" + Uri.EscapeDataString(bvid)),
            cookieHeader,
            bvid,
            cancellationToken);
        var root = document.RootElement;
        var code = root.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var codeValue)
            ? codeValue
            : int.MinValue;
        if (code != 0 || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            var message = root.TryGetProperty("message", out var messageElement) ? messageElement.GetString() : null;
            throw new InvalidOperationException($"Không lấy được thông tin video cho phụ đề · code {code}: {Compact(message)}");
        }

        if (!data.TryGetProperty("aid", out var aidElement) || !aidElement.TryGetInt64(out var aid) || aid <= 0)
            throw new InvalidDataException("Thông tin Bilibili thiếu AID để lấy phụ đề AI.");

        long cid = 0;
        if (data.TryGetProperty("pages", out var pages) && pages.ValueKind == JsonValueKind.Array)
        {
            JsonElement selected = default;
            var found = false;
            foreach (var page in pages.EnumerateArray())
            {
                if (page.ValueKind != JsonValueKind.Object) continue;
                if (page.TryGetProperty("page", out var pageElement) && pageElement.TryGetInt32(out var pageNumber) && pageNumber == part)
                {
                    selected = page;
                    found = true;
                    break;
                }
            }
            if (!found && pages.GetArrayLength() >= part)
            {
                selected = pages[part - 1];
                found = true;
            }
            if (found && selected.TryGetProperty("cid", out var cidElement)) cidElement.TryGetInt64(out cid);
        }
        if (cid <= 0 && data.TryGetProperty("cid", out var rootCid)) rootCid.TryGetInt64(out cid);
        if (cid <= 0) throw new InvalidDataException($"Không tìm thấy CID cho phần {part} để lấy phụ đề.");
        return (aid, cid);
    }

    private static void AddLegacyTracks(JsonElement root, List<SubtitleTrack> output, HashSet<string> seen)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("subtitle", out var subtitle) || subtitle.ValueKind != JsonValueKind.Object ||
            !subtitle.TryGetProperty("subtitles", out var tracks) || tracks.ValueKind != JsonValueKind.Array)
            return;

        foreach (var item in tracks.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var language = ReadString(item, "lan");
            var display = ReadString(item, "lan_doc");
            var url = ReadString(item, "subtitle_url");
            if (string.IsNullOrWhiteSpace(language) || string.IsNullOrWhiteSpace(url)) continue;
            var aiType = item.TryGetProperty("ai_type", out var aiElement) && aiElement.TryGetInt32(out var aiValue) ? aiValue : 0;
            var ai = aiType != 0
                || language.StartsWith("ai-", StringComparison.OrdinalIgnoreCase)
                || display.Contains("AI", StringComparison.OrdinalIgnoreCase);
            AddTrack(output, seen, language, display, ai, url);
        }
    }

    private static void AddTrack(
        List<SubtitleTrack> output,
        HashSet<string> seen,
        string language,
        string? display,
        bool ai,
        string url)
    {
        if (url.StartsWith("//", StringComparison.Ordinal)) url = "https:" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
        var id = (ai ? "ai:" : "official:") + language;
        if (!seen.Add(id)) return;
        var label = string.IsNullOrWhiteSpace(display) ? language : display.Trim();
        label += ai ? " · Bilibili AI" : " · Có sẵn";
        output.Add(new SubtitleTrack(id, label, Official: !ai, Ai: ai, url, "json"));
    }

    private async Task<JsonDocument> SendJsonAsync(Uri uri, string cookieHeader, string bvid, CancellationToken cancellationToken)
    {
        var bytes = await SendBytesAsync(uri, cookieHeader, bvid, cancellationToken, acceptBinary: false);
        return JsonDocument.Parse(bytes);
    }

    private Task<byte[]> SendBytesAsync(Uri uri, string cookieHeader, string bvid, CancellationToken cancellationToken) =>
        SendBytesAsync(uri, cookieHeader, bvid, cancellationToken, acceptBinary: true);

    private async Task<byte[]> SendBytesAsync(
        Uri uri,
        string cookieHeader,
        string bvid,
        CancellationToken cancellationToken,
        bool acceptBinary)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Referer", $"https://www.bilibili.com/video/{bvid}");
        request.Headers.TryAddWithoutValidation("Origin", "https://www.bilibili.com");
        request.Headers.TryAddWithoutValidation("User-Agent", BilibiliPlayurlClient.BrowserUserAgent);
        if (acceptBinary) request.Headers.TryAddWithoutValidation("Accept", "application/octet-stream");
        if (!string.IsNullOrWhiteSpace(cookieHeader)) request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Bilibili subtitle API HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private static IReadOnlyList<ProtoSubtitleTrack> ParseWebSubtitleTracks(byte[] payload)
    {
        var top = DecodeMessage(payload);
        var data = top.FirstOrDefault(field => field.Number == 1 && field.WireType == 2).Data;
        if (data is null || data.Length == 0) return [];

        var output = new List<ProtoSubtitleTrack>();
        foreach (var field in DecodeMessage(data))
        {
            if (field.Number != 3 || field.WireType != 2 || field.Data is null || field.Data.Length == 0) continue;
            var item = DecodeMessage(field.Data);
            var language = ReadProtoText(item, 3);
            var display = ReadProtoText(item, 4);
            var url = ReadProtoText(item, 5);
            if (string.IsNullOrWhiteSpace(language) || string.IsNullOrWhiteSpace(url)) continue;
            output.Add(new ProtoSubtitleTrack(language, display ?? language, url));
        }
        return output;
    }

    private static List<ProtoField> DecodeMessage(ReadOnlySpan<byte> payload)
    {
        var fields = new List<ProtoField>();
        var offset = 0;
        while (offset < payload.Length)
        {
            var key = ReadVarint(payload, ref offset);
            var number = checked((int)(key >> 3));
            var wireType = checked((int)(key & 0x07));
            if (number <= 0) throw new InvalidDataException("Protobuf phụ đề có field number không hợp lệ.");

            if (wireType == 0)
            {
                fields.Add(new ProtoField(number, wireType, ReadVarint(payload, ref offset), null));
                continue;
            }
            if (wireType is 1 or 5)
            {
                var size = wireType == 1 ? 8 : 4;
                if (offset + size > payload.Length) throw new InvalidDataException("Protobuf phụ đề bị cắt ở fixed field.");
                fields.Add(new ProtoField(number, wireType, 0, payload.Slice(offset, size).ToArray()));
                offset += size;
                continue;
            }
            if (wireType == 2)
            {
                var length = ReadVarint(payload, ref offset);
                if (length > int.MaxValue || offset + (int)length > payload.Length)
                    throw new InvalidDataException("Protobuf phụ đề bị cắt ở bytes field.");
                fields.Add(new ProtoField(number, wireType, 0, payload.Slice(offset, (int)length).ToArray()));
                offset += (int)length;
                continue;
            }
            throw new InvalidDataException($"Protobuf phụ đề dùng wire type {wireType} chưa hỗ trợ.");
        }
        return fields;
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> payload, ref int offset)
    {
        ulong value = 0;
        var shift = 0;
        while (offset < payload.Length)
        {
            var current = payload[offset++];
            value |= (ulong)(current & 0x7F) << shift;
            if ((current & 0x80) == 0) return value;
            shift += 7;
            if (shift > 63) throw new InvalidDataException("Protobuf phụ đề có varint quá dài.");
        }
        throw new InvalidDataException("Protobuf phụ đề có varint chưa kết thúc.");
    }

    private static string? ReadProtoText(IEnumerable<ProtoField> fields, int number)
    {
        var data = fields.FirstOrDefault(field => field.Number == number && field.WireType == 2).Data;
        return data is null ? null : Encoding.UTF8.GetString(data);
    }

    private static string ReadString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static async Task<string> ReadCookieHeaderAsync(string? cookieFile, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cookieFile) || !File.Exists(cookieFile)) return string.Empty;
        var output = new List<string>();
        foreach (var raw in await File.ReadAllLinesAsync(cookieFile, cancellationToken))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var fields = line.Split('\t');
            if (fields.Length < 7) continue;
            var name = fields[^2].Trim();
            var value = fields[^1].Trim();
            if (name.Length > 0) output.Add(name + "=" + value);
        }
        return string.Join("; ", output);
    }

    private static bool ContainsCookie(string cookieHeader, string name) => cookieHeader
        .Split(';', StringSplitOptions.RemoveEmptyEntries)
        .Any(part => part.TrimStart().StartsWith(name + "=", StringComparison.OrdinalIgnoreCase));

    private async Task<string> WithFingerprintAsync(string cookieHeader, string bvid, CancellationToken cancellationToken)
    {
        var finger = await FingerCookiesAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(finger)) return cookieHeader;
        foreach (var part in finger.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = part.Trim();
            var split = trimmed.IndexOf('=');
            if (split <= 0) continue;
            var name = trimmed[..split].Trim();
            if (name.Length == 0 || ContainsCookie(cookieHeader, name)) continue;
            cookieHeader = string.IsNullOrWhiteSpace(cookieHeader) ? trimmed : cookieHeader + "; " + trimmed;
        }
        return cookieHeader;
    }

    private async Task<string> FingerCookiesAsync(CancellationToken cancellationToken)
    {
        if (_fingerCookies is not null) return _fingerCookies;
        await FingerGate.WaitAsync(cancellationToken);
        try
        {
            if (_fingerCookies is not null) return _fingerCookies;
            var cookies = string.Empty;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    new Uri("https://api.bilibili.com/x/frontend/finger/spi"));
                request.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
                request.Headers.TryAddWithoutValidation("User-Agent", BilibiliPlayurlClient.BrowserUserAgent);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                    var root = document.RootElement;
                    if (root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number
                        && code.GetInt32() == 0 && root.TryGetProperty("data", out var data)
                        && data.ValueKind == JsonValueKind.Object)
                    {
                        var parts = new List<string>();
                        if (data.TryGetProperty("b_3", out var b3) && b3.ValueKind == JsonValueKind.String
                            && !string.IsNullOrWhiteSpace(b3.GetString())) parts.Add("buvid3=" + b3.GetString());
                        if (data.TryGetProperty("b_4", out var b4) && b4.ValueKind == JsonValueKind.String
                            && !string.IsNullOrWhiteSpace(b4.GetString())) parts.Add("buvid4=" + b4.GetString());
                        cookies = string.Join("; ", parts);
                    }
                }
            }
            catch
            {
            }
            _fingerCookies = cookies;
            return cookies;
        }
        finally
        {
            FingerGate.Release();
        }
    }

    // WBI ký cho player (theo yt-dlp 2026): nav -> wbi_img img/sub key ->
    // mixin 64 số -> w_rid=md5(query đã sắp xếp + wts + mixin). Endpoint không
    // ký trả track URL host cũ đã chết; bản ký trả host mới. Không ký được thì
    // null để caller dùng đường cũ.
    private static readonly int[] WbiMixinTable =
    [
        46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35, 27, 43, 5, 49,
        33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13, 37, 48, 7, 16, 24, 55, 40,
        61, 26, 17, 0, 1, 60, 51, 30, 4, 22, 25, 54, 21, 56, 59, 6, 63, 57, 62, 11,
        36, 20, 34, 44, 52,
    ];
    private static readonly SemaphoreSlim WbiGate = new(1, 1);
    private static string? _wbiMixin;

    private async Task<string?> WbiMixinAsync(string cookieHeader, string bvid, CancellationToken cancellationToken)
    {
        if (_wbiMixin is not null) return _wbiMixin;
        await WbiGate.WaitAsync(cancellationToken);
        try
        {
            if (_wbiMixin is not null) return _wbiMixin;
            try
            {
                using var nav = await SendJsonAsync(
                    new Uri("https://api.bilibili.com/x/web-interface/nav"),
                    cookieHeader, bvid, cancellationToken);
                if (nav.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                    && data.TryGetProperty("wbi_img", out var img) && img.ValueKind == JsonValueKind.Object
                    && img.TryGetProperty("img_url", out var imgUrl) && img.TryGetProperty("sub_url", out var subUrl))
                {
                    var lookup = WbiFileName(imgUrl.GetString()) + WbiFileName(subUrl.GetString());
                    if (lookup.Length >= 64)
                        _wbiMixin = string.Concat(WbiMixinTable.Select(index => lookup[index]))[..32];
                }
            }
            catch
            {
            }
            return _wbiMixin;
        }
        finally
        {
            WbiGate.Release();
        }
    }

    private static string WbiFileName(string? url)
    {
        if (string.IsNullOrEmpty(url)) return string.Empty;
        var name = url[(url.LastIndexOf('/') + 1)..];
        var dot = name.IndexOf('.');
        return dot < 0 ? name : name[..dot];
    }

    private static string SignWbiQuery(IEnumerable<KeyValuePair<string, string>> parameters, string mixin)
    {
        var wts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var query = string.Join("&", parameters
            .Append(new KeyValuePair<string, string>("wts", wts))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Key + "=" + Uri.EscapeDataString(WbiStrip(pair.Value))));
        return query + "&w_rid=" + Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(query + mixin)));
    }

    private static string WbiStrip(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var output = new StringBuilder(value.Length);
        foreach (var character in value)
            if (character is not ('!' or '\'' or '(' or ')' or '*')) output.Append(character);
        return output.ToString();
    }

    private async Task<Uri> PlayerV2UriAsync((long Aid, long Cid) identity, string bvid, string cookieHeader, CancellationToken cancellationToken)
    {
        try
        {
            var mixin = await WbiMixinAsync(cookieHeader, bvid, cancellationToken);
            if (!string.IsNullOrEmpty(mixin))
                return new Uri("https://api.bilibili.com/x/player/wbi/v2?" + SignWbiQuery(
                    [new KeyValuePair<string, string>("aid", identity.Aid.ToString()),
                     new KeyValuePair<string, string>("cid", identity.Cid.ToString())], mixin));
        }
        catch
        {
        }
        return new Uri($"https://api.bilibili.com/x/player/v2?bvid={Uri.EscapeDataString(bvid)}&cid={identity.Cid}");
    }

    private static string? ExtractBvid(string? value)
    {
        var match = BvidRegex().Match(value ?? string.Empty);
        return match.Success ? match.Value : null;
    }

    private static int ExtractPart(string infoId, string sourceUrl)
    {
        var idMatch = PartRegex().Match(infoId ?? string.Empty);
        if (idMatch.Success && int.TryParse(idMatch.Groups["part"].Value, out var idPart) && idPart > 0) return idPart;
        if (Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
        {
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var fields = pair.Split('=', 2);
                if (fields.Length == 2 && string.Equals(fields[0], "p", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(Uri.UnescapeDataString(fields[1]), out var part) && part > 0) return part;
            }
        }
        return 1;
    }

    private static string Compact(string? value)
    {
        var text = string.Join(" ", (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= 200 ? text : text[..200] + "…";
    }

    private sealed record ProtoSubtitleTrack(string Language, string DisplayName, string Url);
    private readonly record struct ProtoField(int Number, int WireType, ulong Value, byte[]? Data);

    [GeneratedRegex(@"BV[0-9A-Za-z]+", RegexOptions.CultureInvariant)]
    private static partial Regex BvidRegex();

    [GeneratedRegex(@"""aid"":(?<aid>\d+),""bvid"":""(?<bvid>BV[0-9A-Za-z]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex AidBvidRegex();

    [GeneratedRegex(@"""aid"":(?<aid>\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex AidRegex();

    [GeneratedRegex(@"_p(?<part>\d+)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex PartRegex();
}
