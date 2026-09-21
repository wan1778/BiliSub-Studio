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
                    Convert.ToBase64String([1]), CancellationToken.None, false, null!, Type.Missing);
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

            var workerRoots = new[] { Path.Combine(root, "worker-a"), Path.Combine(root, "worker-b") };
            var workersUnderTest = new List<object>();
            foreach (var workerRoot in workerRoots)
            {
                Directory.CreateDirectory(workerRoot);
                var runtime = Activator.CreateInstance(
                    runtimeType, executable, "fake-ocr-worker", workerRoot, "gpu:0", "gpu")
                    ?? throw new InvalidOperationException("cannot create fake OcrRuntime");
                var worker = Activator.CreateInstance(workerType, runtime)
                    ?? throw new InvalidOperationException("cannot create fake OcrWorkerClient");
                await InvokeTask(workerType.GetMethod("StartAsync")!, worker, CancellationToken.None);
                workersUnderTest.Add(worker);
            }

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
            foreach (var worker in workersUnderTest) workers.Add(worker);
            typeof(OcrManager).GetMethod("RebuildAvailabilityLocked", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(manager, null);
            typeof(OcrManager).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manager, "ready");
            typeof(OcrManager).GetField("_deviceMode", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manager, "gpu");
            typeof(OcrManager).GetField("_activeMode", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manager, "gpu");
            typeof(OcrManager).GetField("_committedWorkerTarget", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manager, 2);

            try
            {
                var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(index =>
                    manager.RunAsync(Convert.ToBase64String([(byte)index, 2, 3]), CancellationToken.None)))
                    .WaitAsync(TimeSpan.FromSeconds(30));
                Check(results.Length == 8 && results.All(result => result.Ok && result.Detected && result.Text == "恢复成功"),
                    "concurrent requests were not replayed on replacement OCR workers");
                Check(manager.Status.Ready && manager.Status.Workers == 2,
                    "concurrent replacement did not restore the committed two-worker topology");
                Check(workerRoots.All(workerRoot => File.Exists(Path.Combine(workerRoot, "crashed-once"))),
                    "both fake OCR workers did not exercise the simultaneous crash path");

                // Exercise the real client/pipe with every byte value, base64 +/=
                // and JSON-sensitive text. No Paddle inference or video is involved.
                var bytes = Enumerable.Range(0, 32768).Select(index => (byte)index).ToArray();
                var encoded = Convert.ToBase64String(bytes);
                const string active = "\u8d70\U00020000\"\\\r\n\t\0<&>+";
                var single = await manager.RunAsync(encoded, CancellationToken.None, true, active);
                Check(single.Ok && single.Text == encoded && single.Lines.Single().Text == active,
                    "single-image pipe changed image bytes or active text");
                foreach (var count in new[] { 1, 4 })
                {
                    var batch = await manager.RunBatchAsync(Enumerable.Repeat(encoded, count).ToArray(),
                        CancellationToken.None, true, active);
                    Check(batch.Count == count && batch.All(result => result.Ok && result.Text == encoded
                        && result.Lines.Single().Text == active), "batch pipe changed images or recovery fields");
                }
                // Worker đang chạy chỉ nhận tối đa 4 (parity 15/9): batch 5-8 fail ngay
                // ở client, không ra pipe.
                foreach (var count in new[] { 5, 8 })
                {
                    try
                    {
                        await manager.RunBatchAsync(Enumerable.Repeat(encoded, count).ToArray(),
                            CancellationToken.None, true, active);
                        throw new InvalidOperationException($"batch {count} vượt giới hạn worker vẫn lọt");
                    }
                    catch (ArgumentException) { }
                }
                foreach (var count in new[] { 2, 65 })
                {
                    var scores = await manager.ProbeVisualChangesAsync(
                        Enumerable.Repeat(encoded, count).ToArray(), CancellationToken.None);
                    Check(scores.Count == count - 1 && scores.All(score => score == .02),
                        "probe pipe changed score count or values");
                }
                Console.WriteLine("PASS OCR compact transport: single, batches 1/4, reject 5/8, probes 2/65, exact bytes and escaped text");

                var held = Enumerable.Range(0, 2).Select(_ => manager.RunAsync(
                    Convert.ToBase64String([1, 2, 3]), CancellationToken.None, activeShortText: "__hold__")).ToArray();
                foreach (var workerRoot in workerRoots)
                    await WaitForFileAsync(Path.Combine(workerRoot, "request-held"));
                var oldPids = workerRoots.Select(workerRoot =>
                    int.Parse(File.ReadAllText(Path.Combine(workerRoot, "request-held")))).ToArray();
                Check(await manager.ConfigureWorkerPoolAsync(1, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)) == 1,
                    "active resize did not commit one worker");
                foreach (var request in held) await CanceledOrFailedAsync(request);
                Check(manager.Status.Ready && manager.Status.Workers == 1,
                    "active resize republished a worker killed by queue retirement");
                Check(oldPids.All(HasExited), "active resize leaked a retired process");
                var afterResize = await manager.RunAsync(Convert.ToBase64String([1, 2, 3]), CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(10));
                Check(afterResize.Ok, "resized worker could not handle a new request");
                Console.WriteLine("PASS OCR active resize: two busy workers -> one live worker, old requests settled and processes reaped");

                var retainedRoot = workerRoots[0];
                await File.WriteAllTextAsync(Path.Combine(retainedRoot, "stall-replacement"), "1");
                var stalled = manager.RunAsync(Convert.ToBase64String([1, 2, 3]), CancellationToken.None,
                    activeShortText: "__crash__");
                Task? stopping = null;
                try
                {
                    await WaitForFileAsync(Path.Combine(retainedRoot, "replacement-started"));
                    var replacementPid = int.Parse(await File.ReadAllTextAsync(Path.Combine(retainedRoot, "replacement-started")));
                    stopping = manager.StopAsync(); // No caller/request cancellation and no short Stop token.
                    await stopping.WaitAsync(TimeSpan.FromSeconds(10));
                    await CanceledOrFailedAsync(stalled);
                    Check(manager.Status.Workers == 0 && manager.Status.State == "stopped",
                        "stop did not finish cleanup of stalled recovery");
                    Check(HasExited(replacementPid), "stop left a pre-Ready replacement process alive");
                    Console.WriteLine("PASS OCR stop cancels stalled replacement startup before acquiring the recovery gate");
                }
                finally
                {
                    // A failing regression must also release its fixture, rather than
                    // leave a three-minute startup holding the manager gate.
                    await File.WriteAllTextAsync(Path.Combine(retainedRoot, "release-replacement"), "1");
                    if (stopping is not null) await stopping.WaitAsync(TimeSpan.FromSeconds(15));
                }
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
        Console.InputEncoding = System.Text.Encoding.UTF8;
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (File.Exists(Path.Combine(stateRoot, "stall-replacement")) && File.Exists(Path.Combine(stateRoot, "replacement-pending")))
        {
            await File.WriteAllTextAsync(Path.Combine(stateRoot, "replacement-started"), Environment.ProcessId.ToString());
            while (!File.Exists(Path.Combine(stateRoot, "release-replacement"))) await Task.Delay(10);
        }
        Console.WriteLine("{\"type\":\"ready\",\"device\":\"gpu:0\",\"models\":[\"PP-OCRv6_medium_det\",\"PP-OCRv6_medium_rec\"]}");
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
            var activeText = request.RootElement.TryGetProperty("active_short_text", out var activeNode) ? activeNode.GetString() : null;
            if (activeText == "__hold__")
            {
                await File.WriteAllTextAsync(Path.Combine(stateRoot, "request-held"), Environment.ProcessId.ToString());
                await Task.Delay(Timeout.Infinite);
            }
            if (activeText == "__crash__")
            {
                await File.WriteAllTextAsync(Path.Combine(stateRoot, "replacement-pending"), "1");
                return 37;
            }
            if (line.Contains('+'))
            {
                var fields = request.RootElement;
                var probe = fields.TryGetProperty("probe_images_base64", out var probeImages);
                var batch = fields.TryGetProperty("images_base64", out var batchImages);
                var images = probe ? probeImages.EnumerateArray().ToArray()
                    : batch ? batchImages.EnumerateArray().ToArray() : [fields.GetProperty("image_base64")];
                var expected = Enumerable.Range(0, 32768).Select(index => (byte)index).ToArray();
                Check(images.All(image => Convert.FromBase64String(image.GetString()!).SequenceEqual(expected)),
                    "compact transport changed decoded bytes");
                Check(!line.Contains("\\u002B", StringComparison.OrdinalIgnoreCase), "base64 plus still escaped");
                if (probe)
                {
                    var baseline = JsonSerializer.Serialize(fields);
                    var before = System.Text.Encoding.UTF8.GetByteCount(baseline);
                    var after = System.Text.Encoding.UTF8.GetByteCount(line);
                    Check(before - after == images.Length * images[0].GetString()!.Count(value => value == '+') * 5,
                        "transport reduction must equal exactly five bytes per base64 plus");
                    Console.WriteLine($"probe transport bytes: {before} -> {after}");
                    Console.WriteLine(JsonSerializer.Serialize(new { id, ok = true,
                        change_scores = Enumerable.Repeat(.02, images.Length - 1).ToArray() }));
                }
                else
                {
                    Check(fields.GetProperty("recover_short_blank").GetBoolean(), "recovery flag changed");
                    var result = new { ok = true, detected = true, text = images[0].GetString(), confidence = .99,
                        lines = new[] { new { text = fields.GetProperty("active_short_text").GetString(),
                            confidence = .99, box = Array.Empty<int>() } } };
                    Console.WriteLine(batch
                        ? JsonSerializer.Serialize(new { id, ok = true, results = Enumerable.Repeat(result, images.Length).ToArray() })
                        : JsonSerializer.Serialize(new { id, result.ok, result.detected, result.text, result.confidence, result.lines }));
                }
                Console.Out.Flush();
                continue;
            }
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

    private static async Task WaitForFileAsync(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!File.Exists(path) || new FileInfo(path).Length == 0) await Task.Delay(10, timeout.Token);
    }

    private static async Task CanceledOrFailedAsync(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception error) when (error is OperationCanceledException or IOException or System.Threading.Channels.ChannelClosedException) { return; }
        throw new InvalidOperationException("retired request unexpectedly succeeded");
    }

    private static bool HasExited(int pid)
    {
        try { using var process = System.Diagnostics.Process.GetProcessById(pid); return process.HasExited; }
        catch (ArgumentException) { return true; }
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
