using System.Security.Cryptography;
using System.Text.Json;
using BiliSubStudio.Core.Application;
using BiliSubStudio.Core.Configuration;
using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

// Opt-in real field regression: first 26 seconds of the supplied Chinese video,
// original frames, actual Paddle GPU, public scan/pause/resume/export APIs.
internal static class OcrFragmentRuntimeContract
{
    public static async Task<int> RunAsync(string root, string video)
    {
        var paths = AppPaths.FromRoot(root);
        paths.EnsureBootstrapDirectories();
        // Do not dispose the app's process-containing Windows job before errors print.
        var app = new BiliSubApplication(paths);
        var request = new OcrScanRequest(video, new(.05, .83, .90, .13), "accurate", "gpu", "1", 1, 26);
        var checks = new List<string>();
        void Check(bool valid, string message)
        {
            if (!valid) throw new InvalidOperationException(message);
            checks.Add(message);
            Console.WriteLine("CHECK OK: " + message);
        }
        async Task<OcrScanResult> Scan(OcrScanStartMode mode, bool pause = false)
        {
            var id = app.StartOcrScan(request, mode);
            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(10);
            Task? pauseTask = null;
            var last = "";
            var lastBucket = -1;
            while (true)
            {
                var job = app.Jobs.GetSnapshot(id);
                var bucket = (int)(job.Progress / 10);
                if (last != job.Message && (job.Status != "scanning" || bucket != lastBucket))
                {
                    Console.WriteLine(job.Message);
                    last = job.Message;
                    lastBucket = bucket;
                }
                if (job.Done)
                {
                    if (pauseTask is not null) await pauseTask;
                    return job.Result is OcrScanResult result && job.Error is null
                        ? result : throw new InvalidOperationException(job.Error ?? job.Message);
                }
                if (pause && pauseTask is null && job.Result is OcrScanResult live && live.SafeFrontierSeconds >= 8.6)
                    pauseTask = app.PauseJobAsync(id, CancellationToken.None);
                if (DateTime.UtcNow > deadline)
                {
                    await app.CancelOcrScanAsync(id, request, CancellationToken.None);
                    throw new TimeoutException("real OCR field regression deadline");
                }
                await Task.Delay(40);
            }
        }
        void Validate(OcrScanResult result)
        {
            Console.WriteLine(JsonSerializer.Serialize(result.Cues));
            Check(result.CompletedLanes == 1 && !result.Paused && result.Frames >= 779, "every real frame reached OCR to the bounded end");
            var shortCue = result.Cues.Where(cue => cue.Start >= 3.5 && cue.End <= 4.5).ToArray();
            Check(shortCue.Length == 1 && shortCue[0].Text == "走", "genuine one-glyph subtitle survives as one correct cue, not 杰/徒");
            var repeated = result.Cues.Where(cue => cue.Start >= 7.7 && cue.Start < 9.6).ToArray();
            Check(repeated.Length == 1 && repeated[0].Text == "一万年", "full/short/full readings export as one 一万年 cue");
            var spaced = result.Cues.Where(cue => cue.Start >= 22.5 && cue.Start < 23.25).ToArray();
            Check(spaced.Length == 1 && spaced[0].Text == "一万年前" && spaced[0].End >= 23.25,
                "CJK whitespace variant exports as one continuous 一万年前 cue");
            Check(result.Cues.Any(cue => cue.Text == "你走吧") && result.Cues.Any(cue => cue.Text == "师父")
                && result.Cues.Any(cue => cue.Text == "整整一万年"), "neighboring distinct captions are retained");
        }
        var full = await Scan(OcrScanStartMode.Fresh);
        Validate(full);
        var paused = await Scan(OcrScanStartMode.Fresh, pause: true);
        Check(paused.Paused && app.OcrStatus.Workers == 0, "pause persists state only after real workers stop");
        var resumed = await Scan(OcrScanStartMode.Resume);
        Validate(resumed);
        Check(full.Cues.Select(cue => cue.Text).SequenceEqual(resumed.Cues.Select(cue => cue.Text)), "pause/resume preserves final caption sequence");
        Check(full.Cues.Zip(resumed.Cues).All(pair => Math.Abs(pair.First.Start - pair.Second.Start) < .05
            && Math.Abs(pair.First.End - pair.Second.End) < .05), "pause/resume timing agrees within one source frame");
        var srt = await app.ExportOcrAsync(resumed.Cues, paths.DefaultDownloads, "ocr-fragments-fixed", CancellationToken.None);
        var text = await File.ReadAllTextAsync(srt);
        Check(text.Contains("\n走\n", StringComparison.Ordinal) && !text.Contains("\n杰\n", StringComparison.Ordinal)
            && !text.Contains("\n徒\n", StringComparison.Ordinal), "public SRT export contains corrected short caption");
        using var stream = File.OpenRead(typeof(OcrScanner).Assembly.Location);
        await File.WriteAllTextAsync(Path.Combine(root, "ocr-fragment-checks.json"), JsonSerializer.Serialize(new
        {
            checks, full, paused, resumed, srt,
            core_sha256 = Convert.ToHexStringLower(SHA256.HashData(stream)),
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
