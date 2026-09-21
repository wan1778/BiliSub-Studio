using System.Reflection;
using BiliSubStudio.Core.Configuration;
using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

internal static class OcrCrashRecoveryContract
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public static async Task RunAsync()
    {
        var assembly = typeof(OcrResult).Assembly;
        var storeType = assembly.GetType("BiliSubStudio.Core.Ocr.OcrCheckpointStore")!;
        var root = Path.Combine(Path.GetTempPath(), "BiliSubStudio-OcrCrashRecovery-" + Guid.NewGuid().ToString("N"));
        var paths = AppPaths.FromRoot(root);
        var video = Path.Combine(root, "source.mp4");
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(video, new byte[4096]);
        var request = new OcrScanRequest(video, new OcrRegion(0.05, 0.65, 0.90, 0.29), "accurate", "gpu", "auto", 1.0, 100.0);
        var store = Activator.CreateInstance(storeType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, [paths], null)!;
        var checkpointDir = Path.Combine(root, "Data", "OCRCheckpoints");

        var cpA = await InvokeAsync(storeType, store, "NewAsync", [request, 2, CancellationToken.None]);
        var cpB = await InvokeAsync(storeType, store, "NewAsync", [request, 4, CancellationToken.None]);
        await InvokeAsync(storeType, store, "SaveAsync", [request, cpA!, CancellationToken.None]);
        var main = AssertSingle(Directory.GetFiles(checkpointDir, "*.json"));
        Check(!File.Exists(main + ".bak"), "first checkpoint save must not fabricate a backup");

        await InvokeAsync(storeType, store, "SaveAsync", [request, cpB!, CancellationToken.None]);
        Check(File.Exists(main + ".bak"), "second save did not rotate the previous checkpoint aside");
        Check(SelectedParallelism(await InvokeAsync(storeType, store, "LoadAsync", [request, CancellationToken.None])) == 4,
            "load did not prefer the newest checkpoint");

        // Sudden close/crash with the main file missing: resume from backup.
        File.Delete(main);
        Check(SelectedParallelism(await InvokeAsync(storeType, store, "LoadAsync", [request, CancellationToken.None])) == 2,
            "missing main checkpoint did not fall back to the backup");

        // Torn write (garbage main file): resume from backup, not fresh scan.
        await InvokeAsync(storeType, store, "SaveAsync", [request, cpB!, CancellationToken.None]);
        await File.WriteAllTextAsync(main, "{corrupt");
        Check(SelectedParallelism(await InvokeAsync(storeType, store, "LoadAsync", [request, CancellationToken.None])) == 2,
            "corrupt main checkpoint did not fall back to the backup");

        await InvokeAsync(storeType, store, "RemoveAsync", [request, CancellationToken.None]);
        Check(!File.Exists(main) && !File.Exists(main + ".bak"), "cancel/remove left checkpoint residue behind");
        Check(await InvokeAsync(storeType, store, "LoadAsync", [request, CancellationToken.None]) is null,
            "removed checkpoint still loads");

        // Orphan matcher: only our own private runtimes qualify, never foreign installs.
        var reaper = assembly.GetType("BiliSubStudio.Core.Ocr.OcrOrphanReaper")!;
        var isOwned = reaper.GetMethod("IsOwnedOcrExecutable", BindingFlags.Static | BindingFlags.NonPublic)!;
        var tools = Path.Combine(root, "Tools");
        var boot = Path.Combine(root, "OCRBootstrap");
        bool Owned(string? exe) => (bool)isOwned.Invoke(null, [exe, tools, boot])!;
        Check(Owned(Path.Combine(tools, "OCR", "runtime", "gpu", "venv", "Scripts", "python.exe")), "own python worker not recognized");
        Check(Owned(Path.Combine(tools, "ffmpeg.exe")), "own ffmpeg lane not recognized");
        Check(Owned(Path.Combine(boot, "python", "cpython-3.12", "python.exe")), "bootstrap python worker not recognized");
        Check(!Owned(@"C:\Python312\python.exe"), "foreign python install misclassified as owned");
        Check(!Owned(Path.Combine(tools, "notepad.exe")), "non-OCR executable misclassified as owned");
        Check(!Owned(null), "null path misclassified as owned");

        // Live reap must never throw; with an isolated temp root it must not
        // touch anything and must never target the current process.
        var reap = reaper.GetMethod("Reap", BindingFlags.Static | BindingFlags.NonPublic)!;
        var reaped = (int)reap.Invoke(null, [paths, null])!;
        Check(reaped >= 0, "orphan reap failed");
        Directory.Delete(root, recursive: true);
    }

    private static async Task<object?> InvokeAsync(Type storeType, object store, string name, object?[] args)
    {
        var method = storeType.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        var task = (Task)method.Invoke(store, args)!;
        await task.WaitAsync(Timeout);
        return task.GetType().GetProperty("Result")!.GetValue(task);
    }

    private static int SelectedParallelism(object? checkpoint)
    {
        if (checkpoint is null) throw new InvalidOperationException("checkpoint did not load");
        return (int)checkpoint.GetType().GetProperty("SelectedParallelism")!.GetValue(checkpoint)!;
    }

    private static string AssertSingle(string[] files)
    {
        if (files.Length != 1) throw new InvalidOperationException($"expected exactly one checkpoint file, found {files.Length}");
        return files[0];
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
