using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BiliSubStudio.Core.Video;

/// <summary>
/// Retrieves the raw Bilibili web playurl payload for a normal BV video so the
/// downloader can preserve Bilibili's primary + backup CDN endpoints. yt-dlp
/// remains the format/quality selector; this client only enriches the already
/// selected format with transport alternatives that yt-dlp's normalized format
/// currently discards.
/// </summary>
public sealed partial class BilibiliPlayurlClient
{
    private static readonly int[] MixinKeyEncTable =
    [
        46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35,
        27, 43, 5, 49, 33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13,
        37, 48, 7, 16, 24, 55, 40, 61, 26, 17, 0, 1, 60, 51, 30, 4,
        22, 25, 54, 21, 56, 59, 6, 63, 57, 62, 11, 36, 20, 34, 44, 52,
    ];

    private readonly HttpClient _http;

    public BilibiliPlayurlClient(HttpClient http) => _http = http;

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetEndpointMapAsync(
        string sourceUrl,
        string infoId,
        string? cookieFile,
        CancellationToken cancellationToken)
    {
        var bvid = ExtractBvid(infoId) ?? ExtractBvid(sourceUrl)
            ?? throw new InvalidOperationException("Không xác định được BVID để lấy CDN backup.");
        var part = ExtractPart(infoId, sourceUrl);
        var cookieHeader = await ReadCookieHeaderAsync(cookieFile, cancellationToken);
        var cid = await ResolveCidAsync(bvid, part, cookieHeader, cancellationToken);
        var mixinKey = await ResolveMixinKeyAsync(bvid, cookieHeader, cancellationToken);

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["bvid"] = bvid,
            ["cid"] = cid.ToString(CultureInfo.InvariantCulture),
            ["fnval"] = "4048",
            ["fourk"] = "1",
            ["dm_img_list"] = "[]",
            ["dm_img_str"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).TrimEnd('='),
            ["dm_cover_img_str"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)).TrimEnd('='),
            ["dm_img_inter"] = "{\"ds\":[],\"wh\":[6000,6600,0],\"of\":[50,0,0]}",
        };
        if (!ContainsCookie(cookieHeader, "SESSDATA")) parameters["try_look"] = "1";

        parameters["wts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var canonical = BuildQuery(parameters);
        parameters["w_rid"] = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(canonical + mixinKey)));
        var playurl = new Uri("https://api.bilibili.com/x/player/wbi/playurl?" + BuildQuery(parameters));
        using var document = await SendJsonAsync(playurl, cookieHeader, cancellationToken);
        var root = document.RootElement;
        var code = root.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var codeValue)
            ? codeValue
            : int.MinValue;
        if (code != 0)
        {
            var message = root.TryGetProperty("message", out var messageElement) ? messageElement.GetString() : null;
            throw new InvalidOperationException($"Bilibili playurl trả code {code}: {Compact(message)}");
        }
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("dash", out var dash) || dash.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Bilibili playurl không trả DASH endpoint.");
        }

        var mutable = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        CollectArray(dash, "video", mutable);
        CollectArray(dash, "audio", mutable);
        if (dash.TryGetProperty("dolby", out var dolby) && dolby.ValueKind == JsonValueKind.Object)
            CollectArray(dolby, "audio", mutable);
        if (dash.TryGetProperty("flac", out var flac) && flac.ValueKind == JsonValueKind.Object &&
            flac.TryGetProperty("audio", out var flacAudio) && flacAudio.ValueKind == JsonValueKind.Object)
            CollectItem(flacAudio, mutable);

        return mutable.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    private async Task<long> ResolveCidAsync(string bvid, int part, string cookieHeader, CancellationToken cancellationToken)
    {
        var uri = new Uri("https://api.bilibili.com/x/player/pagelist?bvid=" + Uri.EscapeDataString(bvid) + "&jsonp=jsonp");
        using var document = await SendJsonAsync(uri, cookieHeader, cancellationToken);
        var root = document.RootElement;
        if (!root.TryGetProperty("code", out var codeElement) || !codeElement.TryGetInt32(out var code) || code != 0 ||
            !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Không lấy được CID của video Bilibili.");

        JsonElement selected = default;
        var found = false;
        foreach (var entry in data.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (entry.TryGetProperty("page", out var pageElement) && pageElement.TryGetInt32(out var page) && page == part)
            {
                selected = entry;
                found = true;
                break;
            }
        }
        if (!found && data.GetArrayLength() >= part)
        {
            selected = data[part - 1];
            found = true;
        }
        if (!found || !selected.TryGetProperty("cid", out var cidElement) || !cidElement.TryGetInt64(out var cid) || cid <= 0)
            throw new InvalidOperationException($"Không tìm thấy CID cho phần {part}.");
        return cid;
    }

    private async Task<string> ResolveMixinKeyAsync(string bvid, string cookieHeader, CancellationToken cancellationToken)
    {
        using var document = await SendJsonAsync(new Uri("https://api.bilibili.com/x/web-interface/nav"), cookieHeader, cancellationToken);
        var root = document.RootElement;
        var code = root.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var codeValue)
            ? codeValue
            : int.MinValue;
        var message = root.TryGetProperty("message", out var messageElement) ? messageElement.GetString() : null;

        // Bilibili deliberately returns code -101 for anonymous nav requests while
        // still exposing data.wbi_img. Those public WBI keys are valid and must be
        // accepted; rejecting every non-zero nav code disables backup-CDN discovery
        // for users who are not logged in.
        if (code is not (0 or -101))
            throw new InvalidOperationException($"Không lấy được WBI key từ Bilibili · nav code {code}: {Compact(message)}");
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("wbi_img", out var wbi) || wbi.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"Không lấy được WBI key từ Bilibili · nav code {code} không có wbi_img.");

        var lookup = FileStem(wbi, "img_url") + FileStem(wbi, "sub_url");
        if (lookup.Length <= MixinKeyEncTable.Max())
            throw new InvalidDataException($"WBI key Bilibili không hợp lệ · nav code {code}.");
        var builder = new StringBuilder(32);
        foreach (var index in MixinKeyEncTable)
        {
            if (builder.Length == 32) break;
            builder.Append(lookup[index]);
        }
        return builder.ToString();
    }

    private async Task<JsonDocument> SendJsonAsync(Uri uri, string cookieHeader, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
        request.Headers.TryAddWithoutValidation("Origin", "https://www.bilibili.com");
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/151 Safari/537.36");
        if (!string.IsNullOrWhiteSpace(cookieHeader)) request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Bilibili API HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

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

    private static void CollectArray(JsonElement parent, string property, Dictionary<string, List<string>> output)
    {
        if (!parent.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array) return;
        foreach (var item in array.EnumerateArray())
            if (item.ValueKind == JsonValueKind.Object) CollectItem(item, output);
    }

    private static void CollectItem(JsonElement item, Dictionary<string, List<string>> output)
    {
        var primary = ReadString(item, "baseUrl", "base_url", "url");
        if (!IsHttpUrl(primary)) return;
        var formatId = ExtractFormatId(primary);
        if (formatId is null && item.TryGetProperty("id", out var id))
            formatId = id.ValueKind == JsonValueKind.String ? id.GetString() : id.GetRawText();
        if (string.IsNullOrWhiteSpace(formatId)) return;

        if (!output.TryGetValue(formatId, out var candidates))
        {
            candidates = [];
            output[formatId] = candidates;
        }
        AddCandidate(candidates, primary);
        foreach (var property in new[] { "backupUrl", "backup_url" })
        {
            if (!item.TryGetProperty(property, out var backups) || backups.ValueKind != JsonValueKind.Array) continue;
            foreach (var backup in backups.EnumerateArray())
                if (backup.ValueKind == JsonValueKind.String) AddCandidate(candidates, backup.GetString());
        }
    }

    private static void AddCandidate(List<string> candidates, string? value)
    {
        if (!IsHttpUrl(value) || candidates.Contains(value!, StringComparer.Ordinal)) return;
        candidates.Add(value!);
    }

    private static string? ReadString(JsonElement item, params string[] names)
    {
        foreach (var name in names)
            if (item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                return value.GetString();
        return null;
    }

    private static string FileStem(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String ||
            !Uri.TryCreate(element.GetString(), UriKind.Absolute, out var uri)) return string.Empty;
        return Path.GetFileNameWithoutExtension(uri.AbsolutePath);
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

    private static string? ExtractFormatId(string? url)
    {
        var match = FormatIdRegex().Match(url ?? string.Empty);
        return match.Success ? match.Groups["id"].Value : null;
    }

    private static string BuildQuery(IReadOnlyDictionary<string, string> parameters) => string.Join("&",
        parameters.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
            Encode(pair.Key) + "=" + Encode(RemoveWbiForbidden(pair.Value))));

    private static string Encode(string value) => Uri.EscapeDataString(value).Replace("%20", "+", StringComparison.Ordinal);

    private static string RemoveWbiForbidden(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
            if (character is not ('!' or '\'' or '(' or ')' or '*')) builder.Append(character);
        return builder.ToString();
    }

    private static bool ContainsCookie(string cookieHeader, string name) => cookieHeader
        .Split(';', StringSplitOptions.RemoveEmptyEntries)
        .Any(part => part.TrimStart().StartsWith(name + "=", StringComparison.OrdinalIgnoreCase));

    private static bool IsHttpUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    private static string Compact(string? value)
    {
        var text = string.Join(" ", (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= 200 ? text : text[..200] + "…";
    }

    [GeneratedRegex(@"BV[0-9A-Za-z]+", RegexOptions.CultureInvariant)]
    private static partial Regex BvidRegex();

    [GeneratedRegex(@"_p(?<part>\d+)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex PartRegex();

    [GeneratedRegex(@"-(?<id>\d+)\.m4s(?:\?|$)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex FormatIdRegex();
}
