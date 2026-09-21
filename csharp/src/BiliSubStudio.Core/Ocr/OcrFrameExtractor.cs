using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using BiliSubStudio.Core.Processes;
using BiliSubStudio.Core.Tools;

namespace BiliSubStudio.Core.Ocr;

public sealed record ExtractedFrame(double RequestedPts, double DecodedPts, double Delta, byte[] Pixels, int Width, int Height, System.Drawing.Rectangle SharedCrop);

public static partial class OcrFrameExtractor
{
    private static readonly Regex ShowInfoPattern = new(@"Parsed_showinfo_.*?\bn:\s*(?<n>\d+).*?\bpts:\s*(?<pts>-?\d+).*?\bpts_time:(?<ptstime>-?(?:\d+(?:\.\d*)?|\.\d+))", RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex ShowInfoLinePattern = new(@"Parsed_showinfo_", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly TimeSpan ExtractTimeout = TimeSpan.FromSeconds(30);

    public sealed record ShowInfoFrame(int N, long Pts, double PtsTime);

    // Probe cache theo (path + size + mtime): ffprobe chạy nhiều lần cho cùng video trong
    // một scan/replay (lane start, window compute, mỗi pixel check). File đổi => key đổi, không stale.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Path, long Size, DateTime Written), VideoProbe?> ProbeCache = new();

    public static async Task<VideoProbe?> ProbeAsync(ToolManager tools, ProcessRunner processes, string videoPath, CancellationToken cancellationToken)
    {
        try
        {
            var full = Path.GetFullPath(videoPath.Trim());
            var info = new FileInfo(full);
            if (!info.Exists || info.Length <= 0) return null;
            var key = (full, info.Length, info.LastWriteTimeUtc);
            if (ProbeCache.TryGetValue(key, out var cachedProbe)) return cachedProbe;
            var probe = await ProbeUncachedAsync(tools, processes, full, cancellationToken);
            if (probe is not null) ProbeCache[key] = probe;
            return probe;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private static async Task<VideoProbe?> ProbeUncachedAsync(ToolManager tools, ProcessRunner processes, string videoPath, CancellationToken cancellationToken)
    {
        try
        {
            var ffprobe = await tools.EnsureFfprobeAsync(cancellationToken);
            var result = await processes.RunAsync(ffprobe,
                ["-hide_banner", "-loglevel", "error", "-select_streams", "v:0",
                 "-show_entries", "stream=width,height,avg_frame_rate,r_frame_rate,time_base,duration,start_time:format=start_time",
                 "-of", "default=noprint_wrappers=1", Path.GetFullPath(videoPath.Trim())],
                cancellationToken);
            if (result.ExitCode != 0) return null;
            int w = 0, h = 0;
            double fps = 0, timeBase = 0, startTime = 0, streamDuration = 0;
            bool hasStart = false;
            foreach (var line in result.StandardOutput.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = line.Split('=', 2);
                if (kv.Length != 2) continue;
                var key = kv[0].Trim();
                var value = kv[1].Trim();
                if (key == "width") int.TryParse(value, out w);
                else if (key == "height") int.TryParse(value, out h);
                else if (key == "avg_frame_rate") fps = ParseFps(value, fps);
                else if (key == "r_frame_rate" && fps <= 0) fps = ParseFps(value, 0);
                else if (key == "time_base") timeBase = ParseTimeBase(value);
                else if (key == "duration" && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var sd) && double.IsFinite(sd) && sd > 0)
                    streamDuration = sd;
                else if (key == "start_time" && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var st) && double.IsFinite(st))
                {
                    // stream start_time ưu tiên, format start_time fallback. Domain absolute = media + start.
                    if (!hasStart) { startTime = Math.Max(0, st); hasStart = true; }
                }
            }
            if (w <= 0 || h <= 0) return null;
            var frameDuration = fps > 0 ? 1.0 / fps : 0;
            var tbNum = 0; var tbDen = 0;
            // Parse lại time_base rational để giữ exact Num/Den cho provenance validation.
            foreach (var line in result.StandardOutput.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
            {
                var kv2 = line.Split('=', 2);
                if (kv2.Length != 2 || kv2[0].Trim() != "time_base") continue;
                var vp = kv2[1].Trim().Split('/');
                if (vp.Length == 2 && int.TryParse(vp[0], out var nn) && int.TryParse(vp[1], out var dd) && nn > 0 && dd > 0)
                { tbNum = nn; tbDen = dd; break; }
            }
            if (tbNum <= 0 || tbDen <= 0) return null;
            return new VideoProbe(w, h, timeBase, frameDuration, fps, startTime, tbNum, tbDen, streamDuration);
        }
        catch { return null; }
    }

    private static double ParseFps(string value, double fallback)
    {
        var parts = value.Split('/');
        if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d != 0)
        {
            var fps = n / d;
            return double.IsFinite(fps) && fps > 0 ? fps : fallback;
        }
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f > 0) return f;
        return fallback;
    }

    private static double ParseTimeBase(string value)
    {
        var parts = value.Split('/');
        if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d != 0)
        {
            var tb = n / d;
            return double.IsFinite(tb) && tb > 0 ? tb : 0;
        }
        return 0;
    }

    public sealed record ExtractRequest(double RequestedPtsTime, long? ExpectedSourcePts);

    public static async Task<ExtractedFrame?> ExtractFrameAsync(
        string ffmpegPath,
        string videoPath,
        double requestedPts,
        long? expectedSourcePts,
        System.Drawing.Rectangle sharedCrop,
        VideoProbe probe,
        ProcessRunner processes,
        OwnedProcessGroup? owner,
        CancellationToken cancellationToken)
    {
        if (sharedCrop.IsEmpty || sharedCrop.Width <= 0 || sharedCrop.Height <= 0) return null;
        if (probe.Width <= 0 || probe.Height <= 0) return null;
        if (expectedSourcePts is null) return null;
        if (probe.TimeBaseNum <= 0 || probe.TimeBaseDen <= 0) return null;
        var cropFilter = $"crop={sharedCrop.Width}:{sharedCrop.Height}:{sharedCrop.X}:{sharedCrop.Y}";
        var timeBaseExpr = $"{probe.TimeBaseNum}/{probe.TimeBaseDen}";
        const double GuardSeconds = 2.0;
        var seekPts = requestedPts - GuardSeconds;
        if (double.IsFinite(probe.StartTimeSeconds))
        {
            seekPts = Math.Max(probe.StartTimeSeconds, seekPts);
        }
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "info", "-nostdin", "-noautorotate",
            "-copyts", "-seek_timestamp", "1", "-accurate_seek",
            "-ss", seekPts.ToString("0.#########", CultureInfo.InvariantCulture),
            "-i", Path.GetFullPath(videoPath.Trim()),
            "-map", "0:v:0", "-an", "-sn", "-dn",
            "-frames:v", "1",
            "-vf", $"settb=expr={timeBaseExpr},select='eq(pts\\,{expectedSourcePts.Value})',{cropFilter},showinfo=checksum=0,format=gray",
            "-fps_mode", "passthrough",
            "-pix_fmt", "gray", "-f", "rawvideo", "pipe:1",
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ExtractTimeout);
        var token = timeout.Token;
        var start = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) start.ArgumentList.Add(a);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) return null;
        using var ownership = owner?.Track(process);
        using var registration = token.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { } });
        await using var output = new MemoryStream();
        var copyTask = process.StandardOutput.BaseStream.CopyToAsync(output, token);
        var errorTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await Task.WhenAll(copyTask, process.WaitForExitAsync(token));
            var stderr = await errorTask;
            if (process.ExitCode != 0) return null;
            var frames = ParseShowInfoFrames(stderr);
            if (frames.Count != 1) return null;
            // Exact integer PTS match: frame dùng phải đúng SourcePts đã request. Không lấy frame gần nhất.
            if (expectedSourcePts.HasValue && frames[0].Pts != expectedSourcePts.Value) return null;
            var decodedPts = frames[0].PtsTime;
            var delta = decodedPts - requestedPts;
            var expected = sharedCrop.Width * sharedCrop.Height;
            var pixels = output.ToArray();
            if (pixels.Length != expected) return null;
            return new ExtractedFrame(requestedPts, decodedPts, delta, pixels, sharedCrop.Width, sharedCrop.Height, sharedCrop);
        }
        catch
        {
            return null;
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        }
    }

    public static IReadOnlyList<ShowInfoFrame> ParseShowInfoFrames(string stderr)
    {
        var list = new List<ShowInfoFrame>();
        // Chỉ nhận line từ video filter showinfo, parse record hoàn chỉnh n/pts/pts_time.
        foreach (var line in stderr.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!ShowInfoLinePattern.IsMatch(line)) continue;
            var m = ShowInfoPattern.Match(line);
            if (!m.Success) continue;
            if (!int.TryParse(m.Groups["n"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) continue;
            if (!long.TryParse(m.Groups["pts"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pts)) continue;
            if (!double.TryParse(m.Groups["ptstime"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var ptsTime)
                || !double.IsFinite(ptsTime)) continue;
            list.Add(new ShowInfoFrame(n, pts, ptsTime));
        }
        return list;
    }

    public static double? ToleranceFor(VideoProbe probe)
    {
        if (probe.FrameDurationSeconds > 0 && probe.TimeBaseSeconds > 0)
            return Math.Max(probe.FrameDurationSeconds * 1.5, probe.TimeBaseSeconds * 2);
        if (probe.FrameDurationSeconds > 0) return probe.FrameDurationSeconds * 1.5;
        if (probe.TimeBaseSeconds > 0) return probe.TimeBaseSeconds * 2;
        return null;
    }

    public static bool EquivalentTimeBase(RationalTimeBase? a, RationalTimeBase? b)
    {
        if (a is null || b is null) return false;
        if (a.Num <= 0 || a.Den <= 0 || b.Num <= 0 || b.Den <= 0) return false;
        static long Gcd(long x, long y)
        {
            x = Math.Abs(x); y = Math.Abs(y);
            while (y != 0) { var t = x % y; x = y; y = t; }
            return x;
        }
        var ga = Gcd(a.Num, a.Den);
        var gb = Gcd(b.Num, b.Den);
        return a.Num / ga == b.Num / gb && a.Den / ga == b.Den / gb;
    }

    public static bool PtsTimeMatches(long pts, RationalTimeBase tb, double ptsTime)
    {
        if (tb.Num <= 0 || tb.Den <= 0) return false;
        var expected = pts * (double)tb.Num / tb.Den;
        var tolerance = Math.Max(Math.Abs((double)tb.Num / tb.Den), 1e-6);
        return double.IsFinite(ptsTime) && Math.Abs(ptsTime - expected) <= tolerance;
    }
}
