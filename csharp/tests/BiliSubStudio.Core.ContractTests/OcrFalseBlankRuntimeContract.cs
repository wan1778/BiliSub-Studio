using System.Globalization;
using System.Text.Json;
using BiliSubStudio.Core.Application;
using BiliSubStudio.Core.Configuration;
using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

// Opt-in field regression for 桃花剑神: 暗中查清人族奸细的身份 is visibly present
// 9387.8-9389.9, but the old 224-luma gate dropped 16-21%-white at luma 200
// frames as blank and split the caption into 0.2s fragments. A targeted scan
// proves the exact balanced pipeline without touching the production checkpoint.
internal static class OcrFalseBlankRuntimeContract
{
    public static async Task<int> RunAsync(string root, string video, string startText, string durationText)
    {
        var start = double.Parse(startText, CultureInfo.InvariantCulture);
        var duration = double.Parse(durationText, CultureInfo.InvariantCulture);
        var paths = AppPaths.FromRoot(root);
        paths.EnsureBootstrapDirectories();
        var app = new BiliSubApplication(paths);
        var request = new OcrScanRequest(video, new(.05, .84, .91, .12), "balanced", "gpu", "1", 1, duration, start);
        var id = app.StartOcrScan(request, OcrScanStartMode.Fresh);
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(15);
        OcrScanResult? result = null;
        while (true)
        {
            var job = app.Jobs.GetSnapshot(id);
            if (job.Done)
            {
                result = job.Result as OcrScanResult;
                if (result is null || job.Error is not null)
                    throw new InvalidOperationException(job.Error ?? job.Message);
                break;
            }
            if (DateTime.UtcNow > deadline)
            {
                await app.CancelOcrScanAsync(id, request, CancellationToken.None);
                throw new TimeoutException("false-blank OCR range deadline");
            }
            await Task.Delay(100);
        }
        var report = Path.Combine(root, "ocr-false-blank-report.json");
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
        {
            start,
            duration,
            frames = result.Frames,
            ocr_images = result.OcrImages,
            cues = result.Cues,
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"frames={result.Frames} ocr_images={result.OcrImages} cues={result.Cues.Count} report={report}");
        foreach (var cue in result.Cues.Where(cue => cue.End > start && cue.Start < start + duration))
            Console.WriteLine(FormattableString.Invariant($"{cue.Start:0.000}-{cue.End:0.000} {cue.Text}"));
        if (start <= 9388.4 && start + duration >= 9390.1)
        {
            var coversGap = result.Cues.Any(cue => cue.Start <= 9388.4 && cue.End >= 9390.1);
            if (!coversGap)
                throw new InvalidOperationException("false blank: readable 9388.8-9389.9 caption was split into short cues");
            Console.WriteLine("CHECK OK: 9388.4-9390.1 stays one continuous caption");
        }
        return 0;
    }
}
