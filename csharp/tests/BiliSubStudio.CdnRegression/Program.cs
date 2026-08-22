using System.Net;
using System.Net.Http.Headers;
using BiliSubStudio.Core.Video;

var cookieFile = Path.Combine(Path.GetTempPath(), $"bilisub-cdn-cookie-{Guid.NewGuid():N}.txt");
await File.WriteAllTextAsync(cookieFile,
    "# Netscape HTTP Cookie File\n.bilibili.com\tTRUE\t/\tTRUE\t2147483647\tSESSDATA\tfixture-session\n");
try
{
    var handler = new PlayurlFixtureHandler();
    using var http = new HttpClient(handler);
    var client = new BilibiliPlayurlClient(http);
    var endpoints = await client.GetEndpointMapAsync(
        "https://www.bilibili.com/video/BV1Fixture123?p=1",
        "BV1Fixture123",
        cookieFile,
        CancellationToken.None);

    if (!endpoints.TryGetValue("30064", out var video) || video.Count != 3)
        throw new InvalidOperationException("Video format 30064 did not preserve primary + two backup endpoints.");
    if (!endpoints.TryGetValue("30280", out var audio) || audio.Count != 2)
        throw new InvalidOperationException("Audio format 30280 did not preserve primary + backup endpoint.");

    var expectedVideoHosts = new[] { "primary-video.example", "backup-video-a.example", "backup-video-b.example" };
    var actualVideoHosts = video.Select(value => new Uri(value).Host).ToArray();
    if (!expectedVideoHosts.SequenceEqual(actualVideoHosts, StringComparer.OrdinalIgnoreCase))
        throw new InvalidOperationException("Video CDN endpoint order changed: " + string.Join(",", actualVideoHosts));

    if (!handler.SawPagelist || !handler.SawNav || !handler.SawPlayurl)
        throw new InvalidOperationException("Raw Bilibili discovery did not execute pagelist + nav + WBI playurl.");
    if (!handler.PlayurlHadSignature)
        throw new InvalidOperationException("WBI playurl request was not signed with wts/w_rid.");
    if (!handler.SawCookie)
        throw new InvalidOperationException("Bilibili session cookie was not propagated to endpoint discovery.");

    Console.WriteLine("PASS: raw Bilibili playurl retained primary + backup CDN endpoints for selected DASH format IDs");
    Console.WriteLine("Video hosts: " + string.Join(" -> ", actualVideoHosts));
    return 0;
}
finally
{
    try { File.Delete(cookieFile); } catch { }
}

internal sealed class PlayurlFixtureHandler : HttpMessageHandler
{
    public bool SawPagelist { get; private set; }
    public bool SawNav { get; private set; }
    public bool SawPlayurl { get; private set; }
    public bool PlayurlHadSignature { get; private set; }
    public bool SawCookie { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        SawCookie |= request.Headers.TryGetValues("Cookie", out var cookies)
            && cookies.Any(value => value.Contains("SESSDATA=fixture-session", StringComparison.Ordinal));
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        if (path == "/x/player/pagelist")
        {
            SawPagelist = true;
            return Json("{\"code\":0,\"message\":\"0\",\"data\":[{\"cid\":123456789,\"page\":1}]}");
        }
        if (path == "/x/web-interface/nav")
        {
            SawNav = true;
            return Json("{\"code\":0,\"message\":\"0\",\"data\":{\"wbi_img\":{\"img_url\":\"https://i0.hdslb.com/bfs/wbi/abcdefghijklmnopqrstuvwxyz0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZab.png\",\"sub_url\":\"https://i0.hdslb.com/bfs/wbi/ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789abcdefghijklmnopqrstuv.png\"}}}");
        }
        if (path == "/x/player/wbi/playurl")
        {
            SawPlayurl = true;
            var query = request.RequestUri?.Query ?? string.Empty;
            PlayurlHadSignature = query.Contains("wts=", StringComparison.Ordinal)
                && query.Contains("w_rid=", StringComparison.Ordinal)
                && query.Contains("bvid=BV1Fixture123", StringComparison.Ordinal)
                && query.Contains("cid=123456789", StringComparison.Ordinal)
                && query.Contains("fnval=4048", StringComparison.Ordinal);
            return Json("""
                {
                  "code":0,
                  "message":"0",
                  "data":{
                    "dash":{
                      "video":[{
                        "id":64,
                        "baseUrl":"https://primary-video.example/media/video-30064.m4s?token=primary-secret",
                        "backupUrl":[
                          "https://backup-video-a.example/media/video-30064.m4s?token=backup-a-secret",
                          "https://backup-video-b.example/media/video-30064.m4s?token=backup-b-secret"
                        ]
                      }],
                      "audio":[{
                        "id":30280,
                        "base_url":"https://primary-audio.example/media/audio-30280.m4s?token=audio-primary",
                        "backup_url":["https://backup-audio.example/media/audio-30280.m4s?token=audio-backup"]
                      }]
                    }
                  }
                }
                """);
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static Task<HttpResponseMessage> Json(string content)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(content),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return Task.FromResult(response);
    }
}
