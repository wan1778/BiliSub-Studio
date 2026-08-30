using System.Reflection;
using BiliSubStudio.Core.Configuration;
using BiliSubStudio.Core.Hardware;
using BiliSubStudio.Core.Ocr;
using BiliSubStudio.Core.Processes;

namespace BiliSubStudio.Core.ContractTests;

internal static class OcrGpuRuntimeContract
{
    public static async Task<int> PrepareAsync(string appRoot, string? imagePath = null)
    {
        var paths = AppPaths.FromRoot(appRoot);
        var hardware = new HardwareService().Snapshot();
        if (!hardware.NvidiaDetected)
            throw new InvalidOperationException("GPU runtime contract requires an NVIDIA GPU.");
        var assembly = typeof(OcrManager).Assembly;
        var installerType = assembly.GetType("BiliSubStudio.Core.Ocr.OcrInstaller")
            ?? throw new InvalidOperationException("missing OcrInstaller");
        using var http = new HttpClient();
        var installer = Activator.CreateInstance(
            installerType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, args: [paths, http, new ProcessRunner()], culture: null)
            ?? throw new InvalidOperationException("cannot create OcrInstaller");
        var ensure = installerType.GetMethod("EnsureAsync", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException("missing OcrInstaller.EnsureAsync");
        var operation = (Task?)ensure.Invoke(installer, ["gpu", hardware, CancellationToken.None])
            ?? throw new InvalidOperationException("GPU runtime preparation did not return Task");
        await operation;
        var runtime = operation.GetType().GetProperty("Result")?.GetValue(operation)
            ?? throw new InvalidOperationException("GPU runtime preparation returned no runtime");
        var workerType = assembly.GetType("BiliSubStudio.Core.Ocr.OcrWorkerClient")
            ?? throw new InvalidOperationException("missing OcrWorkerClient");
        var worker = Activator.CreateInstance(workerType, runtime)
            ?? throw new InvalidOperationException("cannot create OcrWorkerClient");
        try
        {
            var start = (Task?)workerType.GetMethod("StartAsync")?.Invoke(worker, [CancellationToken.None])
                ?? throw new InvalidOperationException("OCR worker startup did not return Task");
            await start;
            var alive = (bool)(workerType.GetProperty("IsAlive")?.GetValue(worker) ?? false);
            if (!alive) throw new InvalidOperationException("GPU OCR worker exited after Ready.");
            if (!string.IsNullOrWhiteSpace(imagePath))
            {
                var image = await File.ReadAllBytesAsync(imagePath);
                var run = workerType.GetMethod("RunAsync")?.Invoke(
                    worker,
                    [Convert.ToBase64String(image), CancellationToken.None, false, null]) as Task
                    ?? throw new InvalidOperationException("OCR worker inference did not return Task");
                await run;
                var result = run.GetType().GetProperty("Result")?.GetValue(run) as OcrResult
                    ?? throw new InvalidOperationException("OCR worker inference returned no result");
                if (!result.Ok)
                    throw new InvalidOperationException("GPU OCR inference failed: " + result.Error);
                if (!(bool)(workerType.GetProperty("IsAlive")?.GetValue(worker) ?? false))
                    throw new InvalidOperationException("GPU OCR worker exited after real inference.");
                Console.WriteLine($"PASS GPU OCR inference: detected={result.Detected} text={result.Text}");
            }
            var runtimeType = runtime.GetType();
            Console.WriteLine("PASS GPU OCR runtime: "
                + runtimeType.GetProperty("Python")?.GetValue(runtime)
                + " · " + hardware.CudaDriver);
            return 0;
        }
        finally
        {
            await ((IAsyncDisposable)worker).DisposeAsync();
        }
    }
}
