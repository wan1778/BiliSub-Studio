using System.Net;
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
        AutomaticDecompression = DecompressionMethods.None,
    })
    {
        Timeout = TimeSpan.FromSeconds(45),
    };

    private readonly HttpClient _http;

    public BilibiliSubtitleClient() : this(SharedHttp) { }
    public BilibiliSubtitleClient(HttpClient http) => _http = http;

    public async Task<IReadOnlyList<SubtitleTrack>> GetTracksAsync(
        string sourceUrl,
        string infoId,
        string? cookieFile,
        CancellationToken cancellationToken)
    {
        var bvid = ExtractBvid(infoId) ?? ExtractBvid(sourceUrl)
            ?? throw new InvalidOperationException("Không xác định được BVID để lấy phụ đề Bilibili.");
        var part = ExtractPart(infoId, sourceUrl);
        var cookieHeader = await ReadCookieHeaderAsync(cookieFile, cancellationToken);
        var identity = await ResolveVideoIdentityAsync(bvid, part, cookieHeader, cancellationToken);

        var tracks = new List<SubtitleTrack>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var legacy = await SendJsonAsync(
                new Uri($"https://api.bilibili.com/x/player/v2?bvid={Uri.EscapeDataString(bvid)}&cid={identity.Cid}"),
                cookieHeader,
                bvid,
                cancellationToken);
            AddLegacyTracks(legacy.RootElement, tracks, seen);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            // Metadata remains usable; the authenticated binary endpoint below can
            // still recover an AI caption track when available.
        }

        var hasAi = tracks.Any(track => track.Ai);
        if (!hasAi && ContainsCookie(cookieHeader, "SESSDATA"))
        {
            var context = Uri.EscapeDataString("{\"video_type\":1}");
            var uri = new Uri(
                $"https://api.bilibili.com/x/v2/subtitle/web/view?oid={identity.Cid}&pid={identity.Aid}" +
                $"&context_ext={context}&type=1&cur_production_type=0&preferred_language=ai-zh&playlist_switch=0");
            try
            {
                var payload = await SendBytesAsync(uri, cookieHeader, bvid, cancellationToken);
                foreach (var raw in ParseWebSubtitleTracks(payload))
                {
                    if (!raw.Language.StartsWith("ai-", StringComparison.OrdinalIgnoreCase)) continue;
                    AddTrack(tracks, seen, raw.Language, raw.DisplayName, ai: true, raw.Url);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                // AI subtitle absence/failure is optional and must not block media.
            }
        }

        return tracks
            .OrderBy(SubtitleTrackPolicy.Priority)
            .ThenBy(track => track.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
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
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/151 Safari/537.36");
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

    [GeneratedRegex(@"_p(?<part>\d+)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex PartRegex();
}
