using System.Collections;
using System.Reflection;
using System.Text.Json;
using BiliSubStudio.Core.Configuration;
using BiliSubStudio.Core.Hardware;
using BiliSubStudio.Core.Ocr;
using BiliSubStudio.Core.Processes;

namespace BiliSubStudio.Core.ContractTests;

internal static class OcrWorkerRecoveryContract
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "bilisub-ocr-worker-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var executable = Path.Combine(AppContext.BaseDirectory, "BiliSubStudio.Core.ContractTests.exe");
            Check(File.Exists(executable), "contract-test apphost missing for fake OCR worker");
            var assembly = typeof(OcrManager).Assembly;
            var runtimeType = assembly.GetType("BiliSubStudio.Core.Ocr.OcrRuntime")
                ?? throw new InvalidOperationException("missing OcrRuntime");
            var runtime = Activator.CreateInstance(runtimeType, executable, "fake-ocr-worker", root, "gpu:0", "gpu")
                ?? throw new InvalidOperationException("cannot create fake OcrRuntime");
            var workerType = assembly.GetType("BiliSubStudio.Core.Ocr.OcrWorkerClient")
                ?? throw new InvalidOperationException("missing OcrWorkerClient");

            var diagnosticRoot = Path.Combine(root, "diagnostic");
            Directory.CreateDirectory(diagnosticRoot);
            var diagnosticRuntime = Activator.CreateInstance(runtimeType, executable, "fake-ocr-worker", diagnosticRoot, "gpu:0", "gpu")
                ?? throw new InvalidOperationException("cannot create diagnostic OcrRuntime");
            var diagnosticWorker = Activator.CreateInstance(workerType, diagnosticRuntime)
                ?? throw new InvalidOperationException("cannot create diagnostic OcrWorkerClient");
            await InvokeTask(workerType.GetMethod("StartAsync")!, diagnosticWorker, CancellationToken.None);
            try
            {
                await InvokeTask(workerType.GetMethod("RunAsync")!, diagnosticWorker,
                    Convert.ToBase64String([1]), CancellationToken.None, false, null!);
                throw new InvalidOperationException("crashed worker request unexpectedly succeeded");
            }
            catch (Exception error)
            {
                Check(error.Message.Contains("exit=37", StringComparison.Ordinal)
                    && error.Message.Contains("simulated native Paddle worker crash", StringComparison.Ordinal),
                    "worker crash lost exit code or stderr diagnostics");
            }
            finally
            {
                await ((IAsyncDisposable)diagnosticWorker).DisposeAsync();
            }

            var worker = Activator.CreateInstance(workerType, runtime)
                ?? throw new InvalidOperationException("cannot create fake OcrWorkerClient");
            await InvokeTask(workerType.GetMethod("StartAsync")!, worker, CancellationToken.None);

            var paths = AppPaths.FromRoot(Path.Combine(root, "app"));
            var installerType = assembly.GetType("BiliSubStudio.Core.Ocr.OcrInstaller")
                ?? throw new InvalidOperationException("missing OcrInstaller");
            using var http = new HttpClient();
            var installer = Activator.CreateInstance(installerType, paths, http, new ProcessRunner())
                ?? throw new InvalidOperationException("cannot create OcrInstaller");
            var manager = (OcrManager)(Activator.CreateInstance(
                typeof(OcrManager), BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, args: [paths, new HardwareService(), installer], culture: null)
                ?? throw new InvalidOperationException("cannot create OcrManager"));
            var workers = (IList)(typeof(OcrManager).GetField("_workers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager)
                ?? throw new InvalidOperationException("missing OCR worker pool"));
            workers.Add(worker);
            typeof(OcrManager).GetMethod("RebuildAvailabilityLocked", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(manager, null);
            typeof(OcrManager).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manager, "ready");
            typeof(OcrManager).GetField("_deviceMode", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manager, "gpu");
            typeof(OcrManager).GetField("_activeMode", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manager, "gpu");

            try
            {
                var result = await manager.RunAsync(Convert.ToBase64String([1, 2, 3]), CancellationToken.None);
                Check(result.Ok && result.Detected && result.Text == "恢复成功", "request was not replayed on replacement OCR worker");
                Check(manager.Status.Ready && manager.Status.Workers == 1, "replacement OCR worker did not restore pool topology");
                Check(File.Exists(Path.Combine(root, "crashed-once")), "fake OCR worker never exercised the crash path");
            }
            finally
            {
                await manager.DisposeAsync();
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    public static async Task<int> RunFakeWorkerAsync(string[] arguments)
    {
        var cacheIndex = Array.IndexOf(arguments, "--model-cache");
        if (cacheIndex < 0 || cacheIndex + 1 >= arguments.Length) return 90;
        var stateRoot = arguments[cacheIndex + 1];
        Directory.CreateDirectory(stateRoot);
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("{\"type\":\"ready\",\"device\":\"gpu:0\",\"models\":[\"PP-OCRv6_small_det\",\"PP-OCRv6_small_rec\"]}");
        Console.Out.Flush();
        var marker = Path.Combine(stateRoot, "crashed-once");
        while (await Console.In.ReadLineAsync() is { } line)
        {
            if (!File.Exists(marker))
            {
                await File.WriteAllTextAsync(marker, "1");
                await Console.Error.WriteLineAsync("simulated native Paddle worker crash");
                Console.Error.Flush();
                return 37;
            }

            using var request = JsonDocument.Parse(line);
            var id = request.RootElement.GetProperty("id").GetInt64();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                id,
                ok = true,
                detected = true,
                text = "恢复成功",
                confidence = .99,
                lines = Array.Empty<object>(),
            }));
            Console.Out.Flush();
        }
        return 0;
    }

    private static async Task InvokeTask(MethodInfo method, object target, params object[] arguments)
    {
        var operation = (Task?)method.Invoke(target, arguments)
            ?? throw new InvalidOperationException(method.Name + " did not return Task");
        await operation;
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
