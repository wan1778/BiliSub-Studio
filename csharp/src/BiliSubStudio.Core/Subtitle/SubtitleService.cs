using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BiliSubStudio.Core.IO;
using BiliSubStudio.Core.Jobs;
using BiliSubStudio.Core.Video;

namespace BiliSubStudio.Core.Subtitle;

public sealed record SubtitleRequest(string Url, string Format, string Track, string OutputDirectory, string? CookieFile = null, string? CookieRaw = null);
public sealed record SubtitleCue(double Start, double End, string Text);
public sealed record SubtitleResult(string OutputPath, int CueCount);

public sealed class SubtitleService
{
    private readonly YtDlpResolver _resolver;
    private readonly HttpClient _http;

    public SubtitleService(YtDlpResolver resolver, HttpClient http)
    {
        _resolver = resolver;
        _http = http;
    }

    public async Task<SubtitleResult> RunAsync(AppJob job, SubtitleRequest request)
    {
        var token = job.CancellationToken;
        job.Set("resolving", 5, "Đang lấy danh sách phụ đề...");
        var metadata = await _resolver.GetMetadataAsync(request.Url, request.CookieFile, token);
        var track = metadata.Subtitles.FirstOrDefault(x => string.Equals(x.Language, request.Track, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Không tìm thấy track {request.Track}.");
        job.Log($"Track: {track.DisplayName} ({track.Language})");
        job.Set("downloading", 30, "Đang tải phụ đề...");
        var raw = await FetchAsync(track.Url, request.CookieRaw, token);

        var format = request.Format.Trim().ToLowerInvariant();
        if (format is not ("srt" or "txt" or "json")) format = "srt";
        var outputDirectory = string.IsNullOrWhiteSpace(request.OutputDirectory) ? "." : Path.GetFullPath(request.OutputDirectory.Trim());
        Directory.CreateDirectory(outputDirectory);
        var baseName = FileNamePolicy.Sanitize(metadata.Title, FileNamePolicy.Sanitize(metadata.Id, "BiliSub_Subtitle"));
        var path = FileNamePolicy.UniquePath(Path.Combine(outputDirectory, $"{baseName} [{FileNamePolicy.Sanitize(track.Language, "sub")}].{format}"));

        var cues = ParseCues(raw, track.Extension);
        var cueCount = cues.Count;
        var output = Encoding.UTF8.GetBytes(format switch
        {
            "srt" => RenderSrt(cues),
            "txt" => RenderText(cues),
            "json" => JsonSerializer.Serialize(cues, new JsonSerializerOptions { WriteIndented = true }) + "\n",
            _ => throw new InvalidOperationException("Định dạng phụ đề không hợp lệ."),
        });
        await WriteAtomicAsync(path, output, token);
        job.Log($"Đã lưu: {path}");
        job.Set("done", 100, path);
        return new SubtitleResult(path, cueCount);
    }

    public static IReadOnlyList<SubtitleCue> ParseCues(ReadOnlySpan<byte> raw, string? extension = null)
    {
        var sourceText = Encoding.UTF8.GetString(raw).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (!sourceText.StartsWith('{') && !sourceText.StartsWith('['))
        {
            return ParseTimedText(sourceText, extension);
        }
        using var document = JsonDocument.Parse(sourceText);
        var root = document.RootElement;
        var output = new List<SubtitleCue>();
        if (root.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in body.EnumerateArray())
            {
                var cueText = CleanText(item.TryGetProperty("content", out var content) ? content.GetString() : null);
                if (cueText.Length == 0) continue;
                output.Add(new SubtitleCue(
                    item.TryGetProperty("from", out var from) ? from.GetDouble() : 0,
                    item.TryGetProperty("to", out var to) ? to.GetDouble() : 0,
                    cueText));
            }
        }
        else if (root.TryGetProperty("events", out var events) && events.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in events.EnumerateArray())
            {
                var textBuilder = new StringBuilder();
                if (item.TryGetProperty("segs", out var segments))
                {
                    foreach (var segment in segments.EnumerateArray())
                    {
                        if (segment.TryGetProperty("utf8", out var value)) textBuilder.Append(value.GetString());
                    }
                }
                var cleaned = CleanText(textBuilder.ToString());
                if (cleaned.Length == 0) continue;
                var start = item.TryGetProperty("tStartMs", out var startMs) ? startMs.GetInt64() / 1000d : 0;
                var duration = item.TryGetProperty("dDurationMs", out var durationMs) ? durationMs.GetInt64() / 1000d : 0;
                output.Add(new SubtitleCue(start, start + duration, cleaned));
            }
        }
        else
        {
            throw new InvalidDataException("Định dạng phụ đề không nhận diện được.");
        }
        return Normalize(output);
    }

    private static IReadOnlyList<SubtitleCue> ParseTimedText(string source, string? extension)
    {
        var lines = source.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        var output = new List<SubtitleCue>();
        for (var index = 0; index < lines.Length; index++)
        {
            var timing = lines[index].Trim();
            if (!timing.Contains("-->", StringComparison.Ordinal)) continue;
            var sides = timing.Split("-->", 2, StringSplitOptions.TrimEntries);
            var left = sides[0].Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            var right = sides[1].Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            if (!TryParseTimestamp(left, out var start) || !TryParseTimestamp(right, out var end)) continue;
            var body = new StringBuilder();
            while (++index < lines.Length && lines[index].Trim().Length > 0)
            {
                if (body.Length > 0) body.Append('\n');
                body.Append(lines[index]);
            }
            var cleaned = CleanText(Regex.Replace(body.ToString(), "<[^>]*>", string.Empty));
            if (cleaned.Length > 0) output.Add(new SubtitleCue(start, end, cleaned));
        }
        if (output.Count == 0) throw new InvalidDataException($"Không parse được phụ đề {extension ?? "timed-text"}.");
        return Normalize(output);
    }

    private static bool TryParseTimestamp(string value, out double seconds)
    {
        seconds = 0;
        var parts = value.Trim().Replace(',', '.').Split(':');
        if (parts.Length is < 2 or > 3) return false;
        if (!double.TryParse(parts[^1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var tail)) return false;
        if (!int.TryParse(parts[^2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var minutes)) return false;
        var hours = 0;
        if (parts.Length == 3 && !int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out hours)) return false;
        if (minutes is < 0 or >= 60 || tail is < 0 or >= 60 || hours < 0) return false;
        seconds = hours * 3600d + minutes * 60d + tail;
        return true;
    }

    public static string RenderSrt(IReadOnlyList<SubtitleCue> cues)
    {
        var output = new StringBuilder();
        for (var index = 0; index < cues.Count; index++)
        {
            var cue = cues[index];
            output.Append(index + 1).Append('\n')
                .Append(FormatSrtTime(cue.Start)).Append(" --> ").Append(FormatSrtTime(cue.End)).Append('\n')
                .Append(cue.Text).Append("\n\n");
        }
        return output.ToString();
    }

    private async Task<byte[]> FetchAsync(string url, string? cookie, CancellationToken token)
    {
        if (url.StartsWith("//", StringComparison.Ordinal)) url = "https:" + url;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Referrer = new Uri("https://www.bilibili.com/");
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) BiliSubStudio/4");
        if (!string.IsNullOrWhiteSpace(cookie)) request.Headers.TryAddWithoutValidation("Cookie", cookie);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 32 * 1024 * 1024) throw new InvalidDataException("Phụ đề vượt giới hạn 32 MiB.");
        await using var source = await response.Content.ReadAsStreamAsync(token);
        await using var target = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (target.Length <= 32 * 1024 * 1024)
        {
            var read = await source.ReadAsync(buffer, token);
            if (read == 0) return target.ToArray();
            await target.WriteAsync(buffer.AsMemory(0, read), token);
        }
        throw new InvalidDataException("Phụ đề vượt giới hạn 32 MiB.");
    }

    private static IReadOnlyList<SubtitleCue> Normalize(IEnumerable<SubtitleCue> source)
    {
        var output = new List<SubtitleCue>();
        foreach (var item in source.OrderBy(x => x.Start))
        {
            var cue = item.End <= item.Start ? item with { End = item.Start + 1.5 } : item;
            if (output.Count > 0 && output[^1].Text == cue.Text && cue.Start <= output[^1].End + 0.15)
            {
                output[^1] = output[^1] with { End = Math.Max(output[^1].End, cue.End) };
            }
            else output.Add(cue);
        }
        return output;
    }

    private static string RenderText(IEnumerable<SubtitleCue> cues) => string.Join('\n', cues.Select(x => x.Text).Where(x => x.Length > 0).Distinct()) + "\n";

    private static string CleanText(string? text) => string.Join('\n', WebUtility.HtmlDecode(text ?? string.Empty)
        .Replace("\r", string.Empty, StringComparison.Ordinal)
        .Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0));

    private static string FormatSrtTime(double seconds)
    {
        var value = TimeSpan.FromMilliseconds(Math.Round(Math.Max(0, seconds) * 1000));
        return $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00},{value.Milliseconds:000}";
    }

    private static async Task WriteAtomicAsync(string path, byte[] content, CancellationToken token)
    {
        var temporary = path + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await file.WriteAsync(content, token);
                await file.FlushAsync(token);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path);
        }
        finally
        {
            try { File.Delete(temporary); } catch { }
        }
    }
}
