using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using BiliSubStudio.Core.Video;

namespace BiliSubStudio.Core.ContractTests;

internal static class YtDlpNumericMetadataContract
{
    [ModuleInitializer]
    internal static void Validate()
    {
        var resolverType = typeof(YtDlpResolver);
        var infoType = resolverType.GetNestedType("YtDlpInfo", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing yt-dlp info model");
        var options = resolverType.GetField("JsonOptions", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) as JsonSerializerOptions
            ?? throw new InvalidOperationException("missing yt-dlp JSON options");

        var json = """
        {
          "id": "fixture",
          "title": "fixture",
          "formats": [
            {
              "format_id": "v1",
              "url": "https://example.invalid/video",
              "ext": "mp4",
              "vcodec": "avc1.640028",
              "acodec": "none",
              "height": null,
              "filesize": 123.5,
              "filesize_approx": "456",
              "http_headers": {},
              "tbr": null,
              "abr": "128.5"
            },
            {
              "format_id": "v2",
              "url": "https://example.invalid/video2",
              "ext": "mp4",
              "vcodec": "avc1.640028",
              "acodec": "none",
              "height": "1080",
              "filesize": null,
              "filesize_approx": 789.0,
              "http_headers": {},
              "tbr": "2500.25",
              "abr": null
            }
          ]
        }
        """;

        var info = JsonSerializer.Deserialize(json, infoType, options)
            ?? throw new InvalidOperationException("yt-dlp numeric fixture deserialized to null");
        var formats = infoType.GetProperty("Formats")?.GetValue(info) as IEnumerable
            ?? throw new InvalidOperationException("yt-dlp numeric fixture lost formats");
        var items = formats.Cast<object>().ToArray();
        if (items.Length != 2) throw new InvalidOperationException("yt-dlp numeric fixture format count mismatch");

        var formatType = items[0].GetType();
        var firstFileSize = Convert.ToDouble(formatType.GetProperty("FileSize")?.GetValue(items[0]), System.Globalization.CultureInfo.InvariantCulture);
        var secondHeight = Convert.ToDouble(formatType.GetProperty("Height")?.GetValue(items[1]), System.Globalization.CultureInfo.InvariantCulture);
        var secondApprox = Convert.ToDouble(formatType.GetProperty("ApproximateFileSize")?.GetValue(items[1]), System.Globalization.CultureInfo.InvariantCulture);
        if (Math.Abs(firstFileSize - 123.5) > 0.001 || Math.Abs(secondHeight - 1080) > 0.001 || Math.Abs(secondApprox - 789) > 0.001)
            throw new InvalidOperationException("yt-dlp numeric fixture values were not preserved");

        var toStream = resolverType.GetMethod("ToStream", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing yt-dlp stream conversion");
        var endpoints = new[]
        {
            "https://primary.invalid/video?token=one",
            "https://backup.invalid/video?token=two",
        };
        var stream = toStream.Invoke(null, [StreamKind.Video, items[0], 1L, endpoints, 1]) as ResolvedStream
            ?? throw new InvalidOperationException("yt-dlp stream conversion returned null");
        if (stream.Size != 123L)
            throw new InvalidOperationException($"yt-dlp floating filesize must normalize safely; got {stream.Size}");
        if (stream.EndpointUrls is null || stream.EndpointUrls.Count != 2 || stream.EndpointIndex != 1 ||
            !string.Equals(stream.Url, endpoints[1], StringComparison.Ordinal))
            throw new InvalidOperationException("yt-dlp stream conversion lost the selected CDN endpoint/index");
    }
}
