using System.Net;
using System.Net.Http.Headers;
using System.Text;
using BiliSubStudio.Core.Video;

var cookieFile = Path.Combine(Path.GetTempPath(), $"bilisub-subtitle-cookie-{Guid.NewGuid():N}.txt");
await File.WriteAllTextAsync(cookieFile,
    "# Netscape HTTP Cookie File\n.bilibili.com\tTRUE\t/\tTRUE\t2147483647\tSESSDATA\tfixture-session\n");
try
{
    await VerifyNormalBeatsAiAsync(cookieFile);
    await VerifyProtobufAiFallbackAsync(cookieFile);
    Console.WriteLine("PASS: Bilibili subtitle source priority and authenticated AI Protobuf fallback");
    return 0;
}
finally
{
    try { File.Delete(cookieFile); } catch { }
}

static async Task VerifyNormalBeatsAiAsync(string cookieFile)
{
    var handler = new SubtitleFixtureHandler(protobufFallback: false);
    using var http = new HttpClient(handler);
    var client = new BilibiliSubtitleClient(http);
    var tracks = await client.GetTracksAsync(
        "https://www.bilibili.com/video/BV1SubtitleFixture?p=1",
        "BV1SubtitleFixture",
        cookieFile,
        CancellationToken.None);

    if (tracks.Count != 2)
        throw new InvalidOperationException($"Expected two legacy subtitle tracks, got {tracks.Count}.");
    var preferred = SubtitleTrackPolicy.Preferred(tracks)
        ?? throw new InvalidOperationException("Subtitle priority returned no preferred track.");
    if (!preferred.Official || preferred.Ai || !preferred.Language.StartsWith("official:", StringComparison.Ordinal))
        throw new InvalidOperationException("A Bilibili AI Chinese track outranked an available normal subtitle.");
    if (!handler.SawSessionCookie)
        throw new InvalidOperationException("Bilibili session cookie was not propagated to subtitle discovery.");
    if (handler.SawProtobuf)
        throw new InvalidOperationException("Protobuf fallback should not run when legacy metadata already has AI captions.");

    Console.WriteLine("PASS available subtitle > Bilibili AI subtitle");
}

static async Task VerifyProtobufAiFallbackAsync(string cookieFile)
{
    var handler = new SubtitleFixtureHandler(protobufFallback: true);
    using var http = new HttpClient(handler);
    var client = new BilibiliSubtitleClient(http);
    var tracks = await client.GetTracksAsync(
        "https://www.bilibili.com/video/BV1SubtitleFixture?p=1",
        "BV1SubtitleFixture",
        cookieFile,
        CancellationToken.None);

    if (tracks.Count != 1 || !tracks[0].Ai || tracks[0].Official)
        throw new InvalidOperationException("Authenticated Protobuf fallback did not return exactly one AI subtitle track.");
    if (!string.Equals(tracks[0].Language, "ai:ai-zh", StringComparison.Ordinal))
        throw new InvalidOperationException("AI subtitle synthetic language id changed: " + tracks[0].Language);
    if (!tracks[0].DisplayName.Contains("Bilibili AI", StringComparison.Ordinal))
        throw new InvalidOperationException("AI subtitle display label is not explicit.");
    if (!handler.SawProtobuf || !handler.SawSessionCookie)
        throw new InvalidOperationException("AI Protobuf fallback did not execute with the current Bilibili session.");

    Console.WriteLine("PASS normal metadata empty → Bilibili AI Protobuf subtitle");
}

internal sealed class SubtitleFixtureHandler(bool protobufFallback) : HttpMessageHandler
{
    public bool SawProtobuf { get; private set; }
    public bool SawSessionCookie { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        SawSessionCookie |= request.Headers.TryGetValues("Cookie", out var cookies)
            && cookies.Any(value => value.Contains("SESSDATA=fixture-session", StringComparison.Ordinal));
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;

        if (path == "/x/web-interface/view")
        {
            return Json("""
                {
                  "code":0,
                  "message":"0",
                  "data":{
                    "bvid":"BV1SubtitleFixture",
                    "aid":99887766,
                    "cid":11223344,
                    "pages":[{"page":1,"cid":11223344}]
                  }
                }
                """);
        }

        if (path == "/x/player/v2")
        {
            if (protobufFallback)
            {
                return Json("{\"code\":0,\"message\":\"0\",\"data\":{\"subtitle\":{\"subtitles\":[]}}}");
            }
            return Json("""
                {
                  "code":0,
                  "message":"0",
                  "data":{
                    "subtitle":{
                      "subtitles":[
                        {
                          "lan":"en-US",
                          "lan_doc":"English",
                          "subtitle_url":"//subtitle.example/normal.json",
                          "ai_type":0
                        },
                        {
                          "lan":"ai-zh",
                          "lan_doc":"中文 AI",
                          "subtitle_url":"//subtitle.example/ai.json",
                          "ai_type":1
                        }
                      ]
                    }
                  }
                }
                """);
        }

        if (path == "/x/v2/subtitle/web/view")
        {
            SawProtobuf = true;
            var query = request.RequestUri?.Query ?? string.Empty;
            if (!query.Contains("oid=11223344", StringComparison.Ordinal)
                || !query.Contains("pid=99887766", StringComparison.Ordinal)
                || !query.Contains("preferred_language=ai-zh", StringComparison.Ordinal))
                throw new InvalidOperationException("Bilibili AI subtitle Protobuf request lost required identifiers.");
            return Bytes(BuildProtobufSubtitle("ai-zh", "中文 AI", "//subtitle.example/protobuf-ai.json"));
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static Task<HttpResponseMessage> Json(string content)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json"),
        };
        return Task.FromResult(response);
    }

    private static Task<HttpResponseMessage> Bytes(byte[] content)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(content),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return Task.FromResult(response);
    }

    private static byte[] BuildProtobufSubtitle(string language, string display, string url)
    {
        var track = Concat(
            StringField(3, language),
            StringField(4, display),
            StringField(5, url));
        var data = BytesField(3, track);
        return BytesField(1, data);
    }

    private static byte[] StringField(int number, string value) => BytesField(number, Encoding.UTF8.GetBytes(value));

    private static byte[] BytesField(int number, byte[] value) => Concat(
        Varint((ulong)((number << 3) | 2)),
        Varint((ulong)value.Length),
        value);

    private static byte[] Varint(ulong value)
    {
        var output = new List<byte>();
        do
        {
            var current = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0) current |= 0x80;
            output.Add(current);
        } while (value != 0);
        return output.ToArray();
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var length = parts.Sum(part => part.Length);
        var output = new byte[length];
        var offset = 0;
        foreach (var part in parts)
        {
            Buffer.BlockCopy(part, 0, output, offset, part.Length);
            offset += part.Length;
        }
        return output;
    }
}
