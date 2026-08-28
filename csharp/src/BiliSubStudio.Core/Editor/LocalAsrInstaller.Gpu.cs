using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using BiliSubStudio.Core.Hardware;
using BiliSubStudio.Core.Jobs;

namespace BiliSubStudio.Core.Editor;

internal sealed partial class LocalAsrInstaller
{
    // NVIDIA Windows wheels from PyPI's versioned JSON metadata. Match the
    // CUDA 12.8 / cuDNN 9.10.2 build used by the pinned CTranslate2 4.8.1 wheel.
    // Extract private runtime DLLs only; never install a driver or CUDA Toolkit.
    internal static readonly AsrGpuPackage[] GpuPackages =
    [
        new("cuda-runtime", "12.8.90", "nvidia/cuda_runtime/bin/", ["cudart64_12.dll"],
            "https://files.pythonhosted.org/packages/30/a5/a515b7600ad361ea14bfa13fb4d6687abf500adc270f19e89849c0590492/nvidia_cuda_runtime_cu12-12.8.90-py3-none-win_amd64.whl",
            944_318, "c0c6027f01505bfed6c3b21ec546f69c687689aad5f1a377554bc6ca4aa993a8"),
        new("cublas", "12.8.4.1", "nvidia/cublas/bin/", ["cublas64_12.dll", "cublasLt64_12.dll", "nvblas64_12.dll"],
            "https://files.pythonhosted.org/packages/70/61/7d7b3c70186fb651d0fbd35b01dbfc8e755f69fd58f817f3d0f642df20c3/nvidia_cublas_cu12-12.8.4.1-py3-none-win_amd64.whl",
            567_544_208, "47e9b82132fa8d2b4944e708049229601448aaad7e6f296f630f2d1a32de35af"),
        new("cuda-nvrtc", "12.8.93", "nvidia/cuda_nvrtc/bin/", ["nvrtc64_120_0.dll", "nvrtc64_120_0.alt.dll", "nvrtc-builtins64_128.dll"],
            "https://files.pythonhosted.org/packages/45/51/52a3d84baa2136cc8df15500ad731d74d3a1114d4c123e043cb608d4a32b/nvidia_cuda_nvrtc_cu12-12.8.93-py3-none-win_amd64.whl",
            73_586_838, "7a4b6b2904850fe78e0bd179c4b655c404d4bb799ef03ddc60804247099ae909"),
        new("cudnn", "9.10.2.21", "nvidia/cudnn/bin/",
            ["cudnn64_9.dll", "cudnn_ops64_9.dll", "cudnn_cnn64_9.dll", "cudnn_graph64_9.dll", "cudnn_adv64_9.dll",
                "cudnn_heuristic64_9.dll", "cudnn_engines_precompiled64_9.dll", "cudnn_engines_runtime_compiled64_9.dll"],
            "https://files.pythonhosted.org/packages/3d/90/0bd6e586701b3a890fd38aa71c387dab4883d619d6e5ad912ccbd05bfd67/nvidia_cudnn_cu12-9.10.2.21-py3-none-win_amd64.whl",
            692_992_268, "c6288de7d63e6cf62988f0923f96dc339cea362decb1bf5b3141883392a7d65e"),
    ];

    internal static string? GpuUnavailableReason(HardwareSnapshot hardware)
    {
        if (!hardware.NvidiaDetected)
            return "Không thấy GPU NVIDIA/driver CUDA hoạt động; Whisper dùng CPU/int8.";
        var match = Regex.Match(hardware.CudaDriver ?? "", @"CUDA\s+(\d+)\.(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var major)
            || !int.TryParse(match.Groups[2].Value, out var minor) || major < 12 || (major == 12 && minor < 8))
            return "Bộ GPU Whisper cần driver hỗ trợ CUDA 12.8 trở lên. Hãy cập nhật driver NVIDIA; app không tự cài driver. Tạm dùng CPU/int8.";
        return null;
    }

    public async Task<IReadOnlyList<string>> PrepareGpuAsync(AppJob job)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("Bộ GPU ASR chỉ hỗ trợ Windows x64.");
        await _gate.WaitAsync(job.CancellationToken);
        try
        {
            var gpuRoot = Path.Combine(Root, "gpu");
            var downloads = Path.Combine(gpuRoot, "downloads");
            Directory.CreateDirectory(downloads);
            var directories = new List<string>();
            var total = GpuPackages.Sum(package => package.Size);
            long completed = 0;
            job.Set("asr-gpu-prepare", 20, "Đang chuẩn bị CUDA riêng cho Whisper; lần đầu có thể tải khoảng 1,3 GB. Không cần cài CUDA Toolkit.");
            foreach (var package in GpuPackages)
            {
                job.CancellationToken.ThrowIfCancellationRequested();
                var packageRoot = Path.Combine(gpuRoot, package.Id + "-" + package.Sha256[..12]);
                var directory = await ReadGpuDirectoryAsync(packageRoot, package, job.CancellationToken);
                if (directory is null)
                {
                    var archive = Path.Combine(downloads, Path.GetFileName(new Uri(package.Url).AbsolutePath));
                    if (!File.Exists(archive) || new FileInfo(archive).Length != package.Size
                        || !string.Equals(await HashAsync(archive, job.CancellationToken), package.Sha256, StringComparison.Ordinal))
                    {
                        await DownloadVerifiedAsync(package.Url, archive, package.Size, package.Sha256,
                            20 + completed / (double)total, 20 + (completed + package.Size) / (double)total,
                            total, completed, job, "thư viện GPU ASR");
                    }
                    job.Set("asr-gpu-unpack", 20 + (completed + package.Size) / (double)total,
                        $"Đang giải nén và xác minh {package.Id} {package.Version}...");
                    directory = await ExtractGpuPackageAsync(archive, packageRoot, package, job.CancellationToken);
                }
                directories.Add(directory);
                completed += package.Size;
            }
            job.Log("Thư viện GPU ASR đã xác minh SHA-256 · CUDA 12.8 / cuDNN 9.10.2. Chưa xác nhận GPU chạy được cho tới khi benchmark speech hoàn tất.");
            return directories;
        }
        finally { _gate.Release(); }
    }

    private static bool SafeGpuFileName(string name) => !string.IsNullOrWhiteSpace(name)
        && name == Path.GetFileName(name) && name.IndexOfAny(['/', '\\', ':']) < 0
        && (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || name == "LICENSE.txt");

    private static async Task<string?> ReadGpuDirectoryAsync(string root, AsrGpuPackage package, CancellationToken cancellationToken)
    {
        try
        {
            var manifestPath = Path.Combine(root, "install.json");
            if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length > 128 * 1024) return null;
            var manifest = JsonSerializer.Deserialize<AsrGpuManifest>(await File.ReadAllTextAsync(manifestPath, cancellationToken), ManifestJson);
            if (manifest is null || manifest.Schema != 1 || manifest.PackageSha256 != package.Sha256
                || !Guid.TryParseExact(manifest.Directory, "N", out _) || manifest.Files is not { Count: > 0 }) return null;
            var directory = Path.Combine(root, manifest.Directory);
            if (!Directory.Exists(directory) || new DirectoryInfo(directory).LinkTarget is not null
                || manifest.Files.Any(file => !SafeGpuFileName(file.Name))
                || manifest.Files.Select(file => file.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Count
                || Directory.EnumerateFiles(directory).Count() != manifest.Files.Count
                || !package.RequiredDlls.All(name => manifest.Files.Any(file => file.Name == name))
                || !manifest.Files.Any(file => file.Name == "LICENSE.txt")
                || !manifest.Files.Any(file => file.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))) return null;
            foreach (var file in manifest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.Combine(directory, file.Name);
                if (!File.Exists(path) || new FileInfo(path).LinkTarget is not null || new FileInfo(path).Length != file.Size
                    || !string.Equals(await HashAsync(path, cancellationToken), file.Sha256, StringComparison.Ordinal)) return null;
            }
            return directory;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private static async Task<string> ExtractGpuPackageAsync(string archive, string root, AsrGpuPackage package, CancellationToken cancellationToken)
    {
        // Unique generations avoid overwriting DLLs held by another ASR worker.
        // Only publish the pointer after every file is extracted and hashed.
        var generation = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(root, generation);
        Directory.CreateDirectory(directory);
        var temporaryManifest = Path.Combine(root, "install.json.tmp-" + generation);
        var published = false;
        try
        {
            using var zip = ZipFile.OpenRead(archive);
            var files = new List<AsrGpuFile>();
            foreach (var entry in zip.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dll = entry.FullName.StartsWith(package.BinPrefix, StringComparison.Ordinal)
                    && entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
                var license = entry.Name.Equals("License.txt", StringComparison.OrdinalIgnoreCase)
                    || entry.Name.Equals("LICENSE", StringComparison.OrdinalIgnoreCase);
                if (!dll && !license) continue;
                var name = dll ? entry.FullName[package.BinPrefix.Length..] : "LICENSE.txt";
                if (!SafeGpuFileName(name) || files.Any(file => file.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    || entry.Length <= 0 || entry.Length > 2L * 1024 * 1024 * 1024)
                    throw new InvalidDataException("Nội dung gói GPU ASR không hợp lệ: " + entry.FullName);
                var target = Path.Combine(directory, name);
                await using (var source = entry.Open())
                await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous))
                    await source.CopyToAsync(output, cancellationToken);
                if (new FileInfo(target).Length != entry.Length) throw new InvalidDataException("Giải nén GPU ASR chưa đủ dữ liệu.");
                files.Add(new AsrGpuFile(name, entry.Length, await HashAsync(target, cancellationToken)));
            }
            if (!files.Any(file => file.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                || !files.Any(file => file.Name == "LICENSE.txt")
                || !package.RequiredDlls.All(name => files.Any(file => file.Name == name)))
                throw new InvalidDataException("Gói GPU ASR thiếu DLL hoặc giấy phép NVIDIA.");
            var manifest = new AsrGpuManifest(1, package.Sha256, generation, files);
            await File.WriteAllTextAsync(temporaryManifest, JsonSerializer.Serialize(manifest, ManifestJson), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryManifest, Path.Combine(root, "install.json"), overwrite: true);
            published = true;
            return directory;
        }
        finally
        {
            TryDelete(temporaryManifest);
            // This is only the new, unpublished generation created by this call.
            if (!published) { try { Directory.Delete(directory, recursive: true); } catch { } }
        }
    }

    internal sealed record AsrGpuPackage(string Id, string Version, string BinPrefix, string[] RequiredDlls, string Url, long Size, string Sha256);
    private sealed record AsrGpuFile(string Name, long Size, string Sha256);
    private sealed record AsrGpuManifest(int Schema, string PackageSha256, string Directory, List<AsrGpuFile> Files);
}
