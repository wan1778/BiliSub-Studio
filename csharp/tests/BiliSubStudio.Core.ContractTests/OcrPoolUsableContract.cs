using System.Reflection;
using BiliSubStudio.Core.Configuration;
using BiliSubStudio.Core.Hardware;
using BiliSubStudio.Core.Ocr;
using BiliSubStudio.Core.Processes;

namespace BiliSubStudio.Core.ContractTests;

// NO-REBUILD-WHEN-USABLE regression (field 10:40 + 19:00 13/9):
// lane recovery chi rebuild pool khi that su khong dung duoc (kenh chet hoac
// 0 worker song). Meo 1 slot nhung con song -> bo qua rebuild (rebuild retire
// queue -> huy hang loat viec dang bay -> rebuild storm dot attempt -> job chet).
// + pin ReuseEnabledDefault == false (canary bat ~15 hit/scan -> tat cho tune lai).
internal static class OcrPoolUsableContract
{
    public static async Task RunAsync()
    {
        Check(!OcrGateShadow.ReuseEnabledDefault, "B-reuse must stay off until re-tuned after production canary hits");

        var root = Path.Combine(Path.GetTempPath(), "bilisub-ocr-pool-usable-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var executable = Path.Combine(AppContext.BaseDirectory, "BiliSubStudio.Core.ContractTests.exe");
            Check(File.Exists(executable), "contract-test apphost missing for fake OCR worker");
            var assembly = typeof(OcrManager).Assembly;
            var runtimeType = assembly.GetType("BiliSubStudio.Core.Ocr.OcrRuntime")
                ?? throw new InvalidOperationException("missing OcrRuntime");
            var workerType = assembly.GetType("BiliSubStudio.Core.Ocr.OcrWorkerClient")
                ?? throw new InvalidOperationException("missing OcrWorkerClient");

            var paths = AppPaths.FromRoot(Path.Combine(root, "app"));
            using var http = new HttpClient();
            var installerType = assembly.GetType("BiliSubStudio.Core.Ocr.OcrInstaller")
                ?? throw new InvalidOperationException("missing OcrInstaller");
            var installer = Activator.CreateInstance(installerType, paths, http, new ProcessRunner())
                ?? throw new InvalidOperationException("cannot create OcrInstaller");
            var manager = (OcrManager)(Activator.CreateInstance(
                typeof(OcrManager), BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, args: [paths, new HardwareService(), installer], culture: null)
                ?? throw new InvalidOperationException("cannot create OcrManager"));
            try
            {
                var workers = (System.Collections.IList)(typeof(OcrManager).GetField("_workers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager)
                    ?? throw new InvalidOperationException("missing OCR worker pool"));
                var disposables = new List<IAsyncDisposable>();
                foreach (var name in new[] { "worker-a", "worker-b" })
                {
                    var workerRoot = Path.Combine(root, name);
                    Directory.CreateDirectory(workerRoot);
                    var runtime = Activator.CreateInstance(
                        runtimeType, executable, "fake-ocr-worker", workerRoot, "gpu:0", "gpu")
                        ?? throw new InvalidOperationException("cannot create fake OcrRuntime");
                    var worker = (IAsyncDisposable)(Activator.CreateInstance(workerType, runtime)
                        ?? throw new InvalidOperationException("cannot create fake OcrWorkerClient"));
                    await InvokeTask(workerType.GetMethod("StartAsync")!, worker, CancellationToken.None);
                    // Burn crash-once marker so later requests serve (khong lien quan usable).
                    workers.Add(worker);
                    disposables.Add(worker);
                }
                typeof(OcrManager).GetMethod("RebuildAvailabilityLocked", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(manager, null);
                typeof(OcrManager).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manager, "ready");

                var usable = typeof(OcrManager).GetMethod("IsPoolUsableForRetry", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("missing IsPoolUsableForRetry");
                object? Call() => usable.Invoke(manager, null);
                Check((bool)Call()!, "two live workers must be usable");
                Check(manager.Status.LiveWorkers == 2, "status must count 2 live workers");

                await disposables[1].DisposeAsync();
                Check(manager.Status.LiveWorkers == 1, "status must count 1 live worker");

                await disposables[0].DisposeAsync();
                Check(!(bool)Call()!, "zero live workers must be unusable (rebuild allowed)");
                Check(manager.Status.LiveWorkers == 0, "status must count 0 live workers");
                Console.WriteLine("PASS OCR pool usable: 2/1 live usable, 0 live rebuilds, reuse off");
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

    private static async Task InvokeTask(MethodInfo method, object target, params object[] arguments)
    {
        var operation = (Task?)method.Invoke(target, arguments)
            ?? throw new InvalidOperationException(method.Name + " did not return Task");
        await operation.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
