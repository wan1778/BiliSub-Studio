using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using BiliSubStudio.Core.Editor;
using BiliSubStudio.Core.Hardware;

namespace BiliSubStudio.Core.ContractTests;

internal static class EditorAsrGpuContract
{
    // Offline packaging/policy regression only. Never executes a DLL, downloads
    // NVIDIA packages or claims GPU inference. Runtime validation is a separate gate.
    public static async Task RunAsync()
    {
        var type = typeof(LocalAsrStatus).Assembly.GetType("BiliSubStudio.Core.Editor.LocalAsrInstaller")!;
        var flags = BindingFlags.Static | BindingFlags.NonPublic;
        var policy = type.GetMethod("GpuUnavailableReason", flags)!;
        string? Reason(bool nvidia, string driver) => (string?)policy.Invoke(null,
            [new HardwareSnapshot("cpu", 8, 16L << 30, nvidia, "gpu", driver, 4L << 30)]);
        Check(Reason(false, "CUDA 13.0") is not null, "non-NVIDIA machine must skip GPU downloads");
        foreach (var driver in new[] { "", "unknown", "CUDA 11.8", "CUDA 12.7" })
            Check(Reason(true, driver) is not null, "unsupported/unknown driver accepted: " + driver);
        foreach (var driver in new[] { "CUDA 12.8", "CUDA 12.10", "CUDA 13.0" })
            Check(Reason(true, driver) is null, "numeric compatible driver rejected: " + driver);

        var packages = (Array)type.GetField("GpuPackages", flags)!.GetValue(null)!;
        Check(packages.Length == 4, "private GPU dependency set incomplete");
        var packageType = packages.GetValue(0)!.GetType();
        foreach (var package in packages)
        {
            var url = new Uri((string)packageType.GetProperty("Url")!.GetValue(package)!);
            var sha = (string)packageType.GetProperty("Sha256")!.GetValue(package)!;
            Check(url.Scheme == "https" && url.Host == "files.pythonhosted.org"
                && url.AbsolutePath.EndsWith("win_amd64.whl", StringComparison.Ordinal)
                && sha.Length == 64 && sha.All(Uri.IsHexDigit), "GPU artifact is not pinned to a Windows wheel/SHA");
        }

        var assembly = type.Assembly;
        var service = assembly.GetType("BiliSubStudio.Core.Editor.LocalAsrService")!;
        var selectionType = service.GetNestedType("AsrSelection", BindingFlags.NonPublic)!;
        var runtimeType = assembly.GetType("BiliSubStudio.Core.Editor.LocalAsrRuntime")!;
        var runtime = Activator.CreateInstance(runtimeType, "python", "worker.py", "model", new Dictionary<string, string?>())!;
        string[] cudaDirectories = [Path.Combine(Path.GetTempPath(), "ASR private CUDA"), Path.Combine(Path.GetTempPath(), "ASR cuDNN")];
        var arguments = service.GetMethod("WorkerArguments", flags)!;
        foreach (var probe in new[] { true, false })
        {
            var selection = Activator.CreateInstance(selectionType, "cuda", "float16", 4, 0d, cudaDirectories)!;
            var values = (string[])arguments.Invoke(null, [runtime, "audio.wav", selection, 0d, probe])!;
            Check(values.Count(value => value == "--cuda-bin") == cudaDirectories.Length
                && cudaDirectories.All(values.Contains), "probe/transcription lost private CUDA paths or split spaces");
        }
        var cpu = Activator.CreateInstance(selectionType, "cpu", "int8", 4, 0d, cudaDirectories)!;
        Check(!((string[])arguments.Invoke(null, [runtime, "audio.wav", cpu, 0d, false])!).Contains("--cuda-bin"),
            "CPU fallback inherited a private CUDA requirement");

        var root = Path.Combine(Path.GetTempPath(), "bilisub-asr-gpu-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var archive = Path.Combine(root, "offline-fixture.zip");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            {
                // Existing executable bytes exercise hash/corruption handling, never loading.
                zip.CreateEntryFromFile(Environment.ProcessPath!, "nvidia/fixture/bin/example.dll");
                using var license = new StreamWriter(zip.CreateEntry("fixture.dist-info/License.txt").Open());
                license.Write("Offline packaging fixture only.");
            }
            using var stream = File.OpenRead(archive);
            var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
            var fixture = Activator.CreateInstance(packageType,
                "fixture", "1", "nvidia/fixture/bin/", new[] { "example.dll" }, "https://example.invalid/fixture", stream.Length, hash)!;
            var installRoot = Path.Combine(root, "package");
            var extract = type.GetMethod("ExtractGpuPackageAsync", flags)!;
            var read = type.GetMethod("ReadGpuDirectoryAsync", flags)!;
            async Task<string?> Read() => await (Task<string?>)read.Invoke(null, [installRoot, fixture, CancellationToken.None])!;
            Check(await Read() is null, "missing GPU install treated as ready");
            var directory = await (Task<string>)extract.Invoke(null, [archive, installRoot, fixture, CancellationToken.None])!;
            Check(await Read() == directory, "fresh GPU manifest cannot be reopened");
            var dll = Path.Combine(directory, "example.dll");
            var original = await File.ReadAllBytesAsync(dll);
            var corrupt = original.ToArray();
            corrupt[^1] ^= 1;
            await File.WriteAllBytesAsync(dll, corrupt);
            Check(await Read() is null, "same-size corrupt GPU DLL accepted");
            await File.WriteAllBytesAsync(dll, original);
            Check(await Read() == directory, "restored DLL not reusable");
            File.Delete(Path.Combine(directory, "LICENSE.txt"));
            Check(await Read() is null, "incomplete GPU install accepted");
            var manifestPath = Path.Combine(installRoot, "install.json");
            var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!;
            manifest["directory"] = "../outside";
            await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
            Check(await Read() is null, "GPU manifest traversal accepted");
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            try
            {
                await (Task<string>)extract.Invoke(null, [archive, installRoot, fixture, canceled.Token])!;
                throw new InvalidOperationException("GPU extraction ignored cancellation");
            }
            catch (OperationCanceledException) { }
            Check(Directory.EnumerateDirectories(installRoot).Count() == 1, "canceled GPU generation leaked");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
