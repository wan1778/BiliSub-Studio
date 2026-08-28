using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

internal static class OcrLaneCoverageRegression
{
    private static readonly Type Scanner = typeof(OcrResult).Assembly.GetType("BiliSubStudio.Core.Ocr.OcrScanner")!;
    private static readonly Type Store = typeof(OcrResult).Assembly.GetType("BiliSubStudio.Core.Ocr.OcrCheckpointStore")!;
    private static readonly Type Segment = typeof(OcrResult).Assembly.GetType("BiliSubStudio.Core.Ocr.OcrScanSegment")!;

    private static List<string> Arguments(string source, string mode, double start, double end, bool nvdec)
    {
        var scanMode = Store.GetMethod("ModeFor")!.Invoke(null, [mode, 1d]);
        return ((IReadOnlyList<string>)Scanner.GetMethod("BuildLaneArguments", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [source, new OcrRegion(.05, .65, .90, .29), scanMode, start, end, nvdec])!).ToList();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static Task RunAsync()
    {
        foreach (var mode in new[] { "accurate", "balanced", "fast" })
        foreach (var nvdec in new[] { false, true })
        foreach (var start in new[] { 0d, 7200d, 14400.137d, 21600d })
        {
            var end = start + 2;
            var args = Arguments("source.mp4", mode, start, end, nvdec);
            var stop = args.IndexOf("-to");
            Check(args.Contains("-copyts") && !args.Contains("-t") && stop > args.IndexOf("-i")
                && args[stop + 1] == end.ToString("0.######", CultureInfo.InvariantCulture),
                "copyts OCR output must stop at the absolute source end, not a relative output duration");
        }

        var validate = Scanner.GetMethod("ValidateLaneCoverage", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing OCR lane coverage guard");
        var segment = Activator.CreateInstance(Segment, [1, 7200d, 14400d, 7188d, 14412d])!;
        void Coverage(int frames, double until, double duration, bool accepted)
        {
            try
            {
                validate.Invoke(null, [segment, frames, until, duration]);
                Check(accepted, "OCR accepted an empty or truncated lane as complete");
            }
            catch (TargetInvocationException error) when (error.InnerException is InvalidDataException)
            {
                Check(!accepted, "OCR rejected a lane that covered its owned interval");
            }
        }
        Coverage(0, 7188, 0, false); // exit 0 + showinfo but zero JPEGs
        Coverage(360, 7200, 1d / 30, false); // old -t can produce only the overlap
        Coverage(100, 10000, 1d / 30, false); // early EOF must not fabricate CoreEnd
        Coverage(0, 14400, 0, false); // timestamps alone are not decoded-frame evidence
        Coverage(216000, 14399.98, 1d / 30, true); // natural frame/container rounding
        Coverage(18000, 14399.7, .4, true); // sampled FPS rounding
        Coverage(216000, 14412, 1d / 30, true); // all owned frames plus overlap
        Coverage(216000, 14412, 0, true); // resume after already covering the tail
        Check((int)Store.GetField("Schema", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()! > 6,
            "old falsely-completed lanes must not be resumed as trusted coverage");
        return Task.CompletedTask;
    }

    // Opt-in: uses the production argument builder, JPEG reader and PTS reader on
    // real FFmpeg output. No OCR model required; this verifies decoding/coverage,
    // not recognition quality. Never writes to the supplied source video.
    public static async Task<int> RunFfmpegAsync(string ffmpeg, string source, bool nvdec)
    {
        try
        {
            Check(File.Exists(ffmpeg) && File.Exists(source), "FFmpeg and long source video must exist");
            foreach (var mode in new[] { "accurate", "balanced", "fast" })
            foreach (var start in new[] { 0d, 7200d, 14400d, 21600d, 21600.137d, 29168d })
                await ReadWindowAsync(ffmpeg, source, mode, start, start + 1, nvdec);
            Console.WriteLine($"PASS real FFmpeg OCR lane JPEG/PTS coverage: 18 windows, nvdec={nvdec}");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL real FFmpeg OCR lane coverage: " + error);
            return 1;
        }
    }

    private static async Task ReadWindowAsync(string ffmpeg, string source, string mode, double start, double end, bool nvdec)
    {
        var info = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in Arguments(source, mode, start, end, nvdec)) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("cannot start FFmpeg");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var cancellation = timeout.Token.Register(() => { try { process.Kill(entireProcessTree: true); } catch { } });
        var ptsType = Scanner.GetNestedType("FrameTimestampReader", BindingFlags.NonPublic)!;
        var jpegType = Scanner.GetNestedType("JpegStreamReader", BindingFlags.NonPublic)!;
        var ptsReader = Activator.CreateInstance(ptsType, [process.StandardError, start])!;
        var jpegReader = Activator.CreateInstance(jpegType, [process.StandardOutput.BaseStream])!;
        var completion = (Task<string>)ptsType.GetProperty("Completion")!.GetValue(ptsReader)!;
        var frames = 0;
        long bytes = 0;
        double first = -1, last = -1, covered = start, duration = 0;
        try
        {
            while (await (Task<byte[]?>)jpegType.GetMethod("ReadAsync")!.Invoke(jpegReader, [timeout.Token])! is { } jpeg)
            {
                var read = (Task)ptsType.GetMethod("ReadAsync")!.Invoke(ptsReader, [timeout.Token])!;
                await read;
                var timing = read.GetType().GetProperty("Result")!.GetValue(read)!;
                var at = (double)timing.GetType().GetProperty("PresentationTime")!.GetValue(timing)!;
                duration = (double)timing.GetType().GetProperty("Duration")!.GetValue(timing)!;
                Check(at >= last, "non-monotonic frame PTS");
                if (frames++ == 0) first = at;
                last = at;
                covered = at + duration;
                bytes += jpeg.Length;
            }
            await process.WaitForExitAsync(timeout.Token);
            var stderr = await completion.WaitAsync(timeout.Token);
            Console.WriteLine(FormattableString.Invariant(
                $"{mode} nvdec={nvdec} [{start:F3},{end:F3}] exit={process.ExitCode} JPEGs={frames} bytes={bytes} PTS={first:F6}..{last:F6} covered={covered:F6}"));
            Check(process.ExitCode == 0, "FFmpeg failed: " + stderr);
            Check(frames > 0 && bytes > 0, "exit 0/showinfo without JPEGs cannot complete a lane");
            var tolerance = Math.Max(.1, duration);
            Check(Math.Abs(first - start) <= tolerance && covered >= end - tolerance && last < end + .001,
                "JPEG timestamps do not cover the requested source-global interval");
            Check(mode != "accurate" || frames >= 25, "accurate mode lost source frames in a 1-second 30fps window");
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
