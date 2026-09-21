using System.Reflection;
using System.Threading.Channels;
using BiliSubStudio.Core.Configuration;
using BiliSubStudio.Core.Hardware;
using BiliSubStudio.Core.Ocr;
using BiliSubStudio.Core.Processes;

namespace BiliSubStudio.Core.ContractTests;

// LANE-FAILURE POISON regression (field 2026-09-13 06:43):
// mot lane chet terminal (worker nga 2 lan lien tiep) KHONG duoc dong kenh
// chung: truoc day finally TryComplete kenh -> moi lane khac chet theo
// (ChannelClosedException day chuyen -> job chet, du pool van song).
// Pin: lane chet terminal xong, kenh pool van mo + lane anh em van chay duoc.
internal static class OcrWorkerPoolPoisonContract
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "bilisub-ocr-pool-poison-" + Guid.NewGuid().ToString("N"));
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

            var workerRoots = new[] { Path.Combine(root, "worker-a"), Path.Combine(root, "worker-b") };
            foreach (var workerRoot in workerRoots) Directory.CreateDirectory(workerRoot);
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
                foreach (var workerRoot in workerRoots)
                {
                    var runtime = Activator.CreateInstance(
                        runtimeType, executable, "fake-ocr-worker", workerRoot, "gpu:0", "gpu")
                        ?? throw new InvalidOperationException("cannot create fake OcrRuntime");
                    var worker = Activator.CreateInstance(workerType, runtime)
                        ?? throw new InvalidOperationException("cannot create fake OcrWorkerClient");
                    await InvokeTask(workerType.GetMethod("StartAsync")!, worker, CancellationToken.None);
                    workers.Add(worker);
                }
                typeof(OcrManager).GetMethod("RebuildAvailabilityLocked", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(manager, null);
                typeof(OcrManager).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manager, "ready");
                typeof(OcrManager).GetField("_deviceMode", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manager, "gpu");
                typeof(OcrManager).GetField("_activeMode", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manager, "gpu");
                typeof(OcrManager).GetField("_committedWorkerTarget", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manager, 2);

                // Burn-in: moi root crash-once 1 lan (fake server rule), thay the xong phuc vu.
                foreach (var _ in workerRoots)
                {
                    var warm = await manager.RunAsync(Convert.ToBase64String([9, 9, 9]), CancellationToken.None)
                        .WaitAsync(TimeSpan.FromSeconds(30));
                    Check(warm.Ok, "burn-in request failed");
                }

                // Lane chet terminal: __crash__ lam chet worker, thay the xong van chet
                // -> OcrWorkerUnavailableException (double-failure, khong phai lan dau).
                var terminal = false;
                try
                {
                    await manager.RunAsync(Convert.ToBase64String([7, 7, 7]), CancellationToken.None, false, "__crash__")
                        .WaitAsync(TimeSpan.FromSeconds(60));
                }
                catch (Exception error)
                {
                    terminal = error.ToString().Contains("sau một lần", StringComparison.Ordinal);
                }
                Check(terminal, "crash-crash request did not take the terminal double-failure path");

                // CRUX 1: kenh chung KHONG bi dong boi lane chet.
                // (ChannelReader<T> khong covariant nen doc Completion qua reflection.)
                var available = typeof(OcrManager).GetField("_available", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager)
                    ?? throw new InvalidOperationException("pool channel missing after terminal lane death");
                var reader = available.GetType().GetProperty("Reader")!.GetValue(available)!;
                var completion = (Task)reader.GetType().GetProperty("Completion")!.GetValue(reader)!;
                Check(!completion.IsCompleted, "terminal lane death poisoned the shared pool channel");

                // CRUX 2: lane anh em van chay duoc tren pool con lai (tu hoi phuc neu gap xac).
                var sibling = await manager.RunAsync(Convert.ToBase64String([1, 2, 3]), CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(30));
                Check(sibling.Ok, "sibling lane died with the terminal lane (channel poison cascade)");
                var third = await manager.RunAsync(Convert.ToBase64String([4, 5, 6]), CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(30));
                Check(third.Ok, "pool did not self-heal past the dead replacement worker");
                Console.WriteLine("PASS OCR pool poison: terminal lane death leaves channel open and siblings running");
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
