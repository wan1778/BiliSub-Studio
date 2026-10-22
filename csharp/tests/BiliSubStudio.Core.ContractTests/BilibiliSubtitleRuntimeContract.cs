using BiliSubStudio.Core.Video;

namespace BiliSubStudio.Core.ContractTests;

// Opt-in field check for the Bilibili subtitle path (22/10): the plain
// web-interface/view endpoint answers HTTP 412 for this session, so the
// subtitle client used to report a misleading "session expired" warning and
// lose the Chinese AI caption track. This harness proves the signed
// view/detail identity path against the live API.
internal static class BilibiliSubtitleRuntimeContract
{
    public static async Task<int> RunAsync(string bvid, string cookieFile)
    {
        var client = new BilibiliSubtitleClient();
        var probe = new List<string>();
        var tracks = await client.GetTracksAsync(
            "https://www.bilibili.com/video/" + bvid + "/",
            bvid,
            cookieFile,
            CancellationToken.None,
            probe.Add);
        foreach (var line in probe) Console.WriteLine("probe: " + line);
        Console.WriteLine($"tracks: {tracks.Count}");
        foreach (var track in tracks)
            Console.WriteLine($"  lang={track.Language} name={track.DisplayName} ai={track.Ai} url={track.Url}");
        return tracks.Count == 0 ? 1 : 0;
    }
}
