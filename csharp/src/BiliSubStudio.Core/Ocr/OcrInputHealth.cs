using System.Diagnostics;
using System.Globalization;
using BiliSubStudio.Core.Processes;
using BiliSubStudio.Core.Tools;

namespace BiliSubStudio.Core.Ocr;

// E.0 — Input health pre-flight (OCR Gates v1.3).
//
// Chay khi mo video, TRUOC khi xoa checkpoint Fresh: input hong thi fail to,
// khong xoa checkpoint, khong chay tiep lang le. Quy tac:
// - FAIL (nem, dung scan): khong doc duoc video / khong kich thuoc / khong frame.
// - WARN (log, van chay): khong audio (VAD nghi), VFR, timebase tho hon 1 frame,
//   lech duration, PTS khong don dieu (tracker tu chiu, da co replay-guard).
// Parser thuan (ParseProbeOutput/CheckPtsMonotonic/BuildReport) de contract test
// truc tiep, khong can FFmpeg that.
public sealed record OcrInputHealthReport(
    bool HasVideo,
    bool HasAudio,
    int Width,
    int Height,
    double AvgFps,
    double RFrameRate,
    int TimeBaseNum,
    int TimeBaseDen,
    double FormatDuration,
    double StreamDuration,
    int PtsChecked,
    bool PtsMonotonic,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Failures)
{
    public bool Passed => Failures.Count == 0;
}

public static class OcrInputHealth
{
    private const int PtsProbeFrames = 60;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(60);

    public static async Task<OcrInputHealthReport> CheckAsync(
        ToolManager tools,
        ProcessRunner processes,
        string source,
        CancellationToken cancellationToken)
    {
        var full = Path.GetFullPath(source.Trim());
        try
        {
            var ffprobe = await tools.EnsureFfprobeAsync(cancellationToken);
            var streams = await processes.RunAsync(ffprobe,
                ["-hide_banner", "-loglevel", "error",
                 "-show_entries", "stream=index,codec_type,width,height,avg_frame_rate,r_frame_rate,time_base,duration",
                 "-of", "default=noprint_wrappers=1", full],
                cancellationToken);
            if (streams.ExitCode != 0)
                return Fail("FFprobe loi, khong doc duoc thong tin video.");
            var format = await processes.RunAsync(ffprobe,
                ["-hide_banner", "-loglevel", "error",
                 "-show_entries", "format=duration",
                 "-of", "default=noprint_wrappers=1", full],
                cancellationToken);
            var parsed = ParseProbeOutput(streams.StandardOutput,
                format.ExitCode == 0 ? format.StandardOutput : string.Empty);
            if (!parsed.HasVideo)
                return Fail("Khong tim thay luong hinh trong file.");

            var ffmpeg = await tools.EnsureFfmpegAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProbeTimeout);
            var show = await processes.RunAsync(ffmpeg,
                ["-hide_banner", "-loglevel", "info", "-nostdin",
                 "-i", full,
                 "-map", "0:v:0", "-an", "-sn", "-dn",
                 "-frames:v", PtsProbeFrames.ToString(CultureInfo.InvariantCulture),
                 "-vf", "showinfo=checksum=0",
                 "-f", "null", "-"],
                timeout.Token);
            var frames = OcrFrameExtractor.ParseShowInfoFrames(show.StandardError);
            var ptsTimes = frames.Select(frame => frame.PtsTime).ToList();
            var monotonic = CheckPtsMonotonic(ptsTimes);
            return BuildReport(parsed, ptsTimes.Count, monotonic);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            return Fail("Kiem tra dau vao loi: " + error.Message);
        }
    }

    private static OcrInputHealthReport Fail(string message) => new(
        false, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, true, [], [message]);

    public sealed record ParsedProbe(
        bool HasVideo, bool HasAudio, int Width, int Height,
        double AvgFps, double RFrameRate, int TbNum, int TbDen,
        double FormatDuration, double StreamDuration);

    public static ParsedProbe ParseProbeOutput(string streamsOutput, string formatOutput = "")
    {
        var hasVideo = false; var hasAudio = false;
        var width = 0; var height = 0;
        var avgFps = 0d; var rFps = 0d;
        var tbNum = 0; var tbDen = 0;
        var formatDuration = 0d; var streamDuration = 0d;
        string? section = null;
        foreach (var raw in streamsOutput.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.StartsWith("codec_type=", StringComparison.Ordinal))
            {
                var codec = line["codec_type=".Length..].Trim();
                section = codec;
                if (codec == "video") hasVideo = true;
                if (codec == "audio") hasAudio = true;
                continue;
            }
            var kv = line.Split('=', 2);
            if (kv.Length != 2) continue;
            var value = kv[1].Trim();
            if (section == "video")
            {
                if (kv[0] == "width") int.TryParse(value, out width);
                else if (kv[0] == "height") int.TryParse(value, out height);
                else if (kv[0] == "avg_frame_rate") avgFps = ParseRatio(value, avgFps);
                else if (kv[0] == "r_frame_rate") rFps = ParseRatio(value, rFps);
                else if (kv[0] == "time_base")
                {
                    var parts = value.Split('/');
                    if (parts.Length == 2 && int.TryParse(parts[0], out var n) && int.TryParse(parts[1], out var d) && n > 0 && d > 0)
                    { tbNum = n; tbDen = d; }
                }
                else if (kv[0] == "duration" && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var sd) && double.IsFinite(sd) && sd > 0)
                    streamDuration = sd;
            }
            else if (section is null && kv[0] == "duration"
                && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fd) && double.IsFinite(fd) && fd > 0)
            {
                if (formatDuration <= 0) formatDuration = fd;
            }
        }
        foreach (var raw in formatOutput.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = raw.Trim().Split('=', 2);
            if (kv.Length == 2 && kv[0].Trim() == "duration"
                && double.TryParse(kv[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var fd)
                && double.IsFinite(fd) && fd > 0)
            {
                formatDuration = fd;
                break;
            }
        }
        return new ParsedProbe(hasVideo, hasAudio, width, height, avgFps, rFps, tbNum, tbDen, formatDuration, streamDuration);
    }

    private static double ParseRatio(string value, double fallback)
    {
        var parts = value.Split('/');
        if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d != 0)
        {
            var ratio = n / d;
            return double.IsFinite(ratio) && ratio > 0 ? ratio : fallback;
        }
        return fallback;
    }

    // Kep duoi lane theo stream thuc: container thua padding khong frame thi dung
    // doi lane quet toi do (ValidateLaneCoverage se fail vinh vien). Stream khong
    // ro (<=0) hoac duoi yeu cau da trong stream thi giu nguyen. Deadband 0.05s.
    public static double ClampStreamEnd(double requestedEnd, double streamEnd)
    {
        if (streamEnd <= 0 || requestedEnd <= streamEnd + 0.05) return requestedEnd;
        return streamEnd;
    }

    // PTS don dieu: bang nhau duoc (trung timestamp), di lui thi hong.
    public static bool CheckPtsMonotonic(IReadOnlyList<double> ptsTimes)
    {
        for (var i = 1; i < ptsTimes.Count; i++)
        {
            if (!double.IsFinite(ptsTimes[i]) || ptsTimes[i] < ptsTimes[i - 1] - 1e-9)
                return false;
        }
        return true;
    }

    public static OcrInputHealthReport BuildReport(ParsedProbe parsed, int ptsChecked, bool ptsMonotonic)
    {
        var warnings = new List<string>();
        var failures = new List<string>();
        if (parsed.Width <= 0 || parsed.Height <= 0)
            failures.Add("Khong doc duoc kich thuoc khung hinh.");
        if (ptsChecked <= 0)
            failures.Add($"Khong giai ma duoc frame nao trong {PtsProbeFrames} frame dau (khong co PTS de kiem).");
        if (!parsed.HasAudio)
            warnings.Add("Video khong co tieng: VAD nghi, mat (B/C) quyet theo luat dong thuan.");
        if (parsed.AvgFps > 0 && parsed.RFrameRate > 0
            && Math.Abs(parsed.AvgFps - parsed.RFrameRate) / Math.Max(parsed.AvgFps, parsed.RFrameRate) > 0.05)
            warnings.Add($"Nghi VFR: avg {parsed.AvgFps:0.00}fps khac r {parsed.RFrameRate:0.00}fps (timing theo PTS, khong theo fps).");
        if (parsed.TbNum > 0 && parsed.TbDen > 0 && parsed.AvgFps > 0
            && (double)parsed.TbNum / parsed.TbDen > 1.0 / parsed.AvgFps)
            warnings.Add($"Timebase {parsed.TbNum}/{parsed.TbDen} tho hon 1 frame ({1.0 / parsed.AvgFps:0.000}s): PTS va cham, kiem tra provenance neu cue lech.");
        if (parsed.FormatDuration > 0 && parsed.StreamDuration > 0
            && Math.Abs(parsed.FormatDuration - parsed.StreamDuration) > 2.0)
            warnings.Add($"Duration container ({parsed.FormatDuration:0}s) lech stream ({parsed.StreamDuration:0}s): coverage lane kiem rieng, khong tin duration mu.");
        if (!ptsMonotonic)
            warnings.Add("PTS 60 frame dau khong don dieu: tracker tu chiu (replay-guard), chu y cue nhay gio.");
        return new OcrInputHealthReport(
            parsed.HasVideo, parsed.HasAudio, parsed.Width, parsed.Height,
            parsed.AvgFps, parsed.RFrameRate, parsed.TbNum, parsed.TbDen,
            parsed.FormatDuration, parsed.StreamDuration,
            ptsChecked, ptsMonotonic, warnings, failures);
    }
}
