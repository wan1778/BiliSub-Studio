using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BiliSubStudio.Core.Configuration;
using BiliSubStudio.Core.Hardware;
using BiliSubStudio.Core.Processes;

namespace BiliSubStudio.Core.Ocr;

internal sealed record OcrRuntime(string Python, string Worker, string Models, string Device, string Kind);

internal sealed class OcrInstaller
{
    public const string UvVersion = "0.12.0";
    public const string PythonVersion = "3.12";
    public const string PaddleVersion = "3.2.0";
    public const string PaddleOcrVersion = "3.7.0";
    public const string DetectionModel = "PP-OCRv6_small_det";
    public const string RecognitionModel = "PP-OCRv6_small_rec";
    private const string UvArchive = "uv-x86_64-pc-windows-msvc.zip";
    private const string UvUrl = "https://github.com/astral-sh/uv/releases/download/0.12.0/uv-x86_64-pc-windows-msvc.zip";
    private const string CpuIndex = "https://www.paddlepaddle.org.cn/packages/stable/cpu/";
    private const string Gpu118Index = "https://www.paddlepaddle.org.cn/packages/stable/cu118/";
    private const string Gpu126Index = "https://www.paddlepaddle.org.cn/packages/stable/cu126/";
    private readonly AppPaths _paths;
    private readonly HttpClient _http;
    private readonly ProcessRunner _processes;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public OcrInstaller(AppPaths paths, HttpClient http, ProcessRunner processes)
    {
        _paths = paths;
        _http = http;
        _processes = processes;
    }

    private string BootstrapRoot
    {
        get
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local))
                throw new InvalidOperationException("Windows không cung cấp LocalAppData để chuẩn bị OCR an toàn.");
            return Path.Combine(local, "BiliSub Studio", "OCRBootstrap");
        }
    }

    private string PythonInstallRoot => Path.Combine(BootstrapRoot, "python");

    public async Task<OcrRuntime> EnsureAsync(string kind, HardwareSnapshot hardware, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var spec = RuntimeSpec(kind, hardware);
            Directory.CreateDirectory(_paths.Ocr);
            Directory.CreateDirectory(Path.Combine(_paths.Ocr, "models"));
            var worker = await EnsureWorkerAsync(cancellationToken);
            var runtimeRoot = Path.Combine(_paths.Ocr, "runtime", kind);
            var venvRoot = Path.Combine(runtimeRoot, "venv");
            var python = Path.Combine(venvRoot, "Scripts", "python.exe");
            var manifestPath = Path.Combine(runtimeRoot, "install.json");
            var expected = new InstallManifest(3, UvVersion, PythonVersion, PaddleVersion, PaddleOcrVersion,
                DetectionModel, RecognitionModel, await Sha256Async(worker, cancellationToken), kind, spec.Package, spec.Index);
            if (File.Exists(python) && await ManifestMatchesAsync(manifestPath, expected, cancellationToken))
            {
                await WriteManifestAsync(manifestPath, expected, cancellationToken);
                return new OcrRuntime(python, worker, Path.Combine(_paths.Ocr, "models"), spec.Device, kind);
            }

            // Never layer a new runtime over a partially populated venv. Models live outside
            // runtimeRoot and survive a repair. The managed base interpreter is kept under
            // LocalAppData so OneDrive / portable install roots cannot poison uv junction work.
            if (Directory.Exists(runtimeRoot)) Directory.Delete(runtimeRoot, recursive: true);
            Directory.CreateDirectory(runtimeRoot);

            var uv = await EnsureUvAsync(cancellationToken);
            var managedEnvironment = ManagedEnvironment();
            var basePython = await EnsureBasePythonAsync(uv, managedEnvironment, cancellationToken);
            await CreatePrivateVenvAsync(uv, basePython, venvRoot, cancellationToken);

            var explicitEnvironment = ExplicitPythonEnvironment();
            await RunUvAsync(uv,
                ["pip", "install", "--python", python, "--no-python-downloads", $"{spec.Package}=={PaddleVersion}", "--index-url", spec.Index, "--no-config"],
                explicitEnvironment, "PaddlePaddle", cancellationToken);
            await RunUvAsync(uv,
                ["pip", "install", "--python", python, "--no-python-downloads", $"paddleocr=={PaddleOcrVersion}", "--no-config"],
                explicitEnvironment, "PaddleOCR", cancellationToken);

            await WriteManifestAsync(manifestPath, expected, cancellationToken);
            if (!File.Exists(python)) throw new FileNotFoundException("Cài OCR không tạo private Python.", python);
            return new OcrRuntime(python, worker, Path.Combine(_paths.Ocr, "models"), spec.Device, kind);
        }
        finally { _gate.Release(); }
    }

    public void RemoveBootstrap()
    {
        if (Directory.Exists(BootstrapRoot)) Directory.Delete(BootstrapRoot, recursive: true);
    }

    private async Task<string> EnsureBasePythonAsync(
        string uv,
        IReadOnlyDictionary<string, string?> environment,
        CancellationToken cancellationToken)
    {
        // A 4.0.4 attempt may already have downloaded a complete patch-version interpreter
        // before uv failed while creating the 3.12 junction. Reuse it if it is actually runnable.
        var existing = await FindUsableBasePythonAsync(cancellationToken);
        if (existing is not null) return existing;

        Directory.CreateDirectory(PythonInstallRoot);
        var arguments = new[]
        {
            "python", "install", PythonVersion,
            "--install-dir", PythonInstallRoot,
            "--managed-python", "--no-registry", "--no-bin", "--no-config",
        };
        var result = await _processes.RunAsync(uv, arguments, cancellationToken, environment);
        var detail = result.StandardError.Trim();
        if (result.ExitCode != 0 && !IsMinorVersionLinkFailure(detail))
            throw new InvalidOperationException("Cài Python OCR: " + detail);

        // uv finalizes the real patch directory before it creates the minor-version junction.
        // On Windows error 448 the junction can fail while the real interpreter is healthy.
        // Use that exact interpreter directly; no later OCR step depends on the junction.
        var installed = await FindUsableBasePythonAsync(cancellationToken);
        if (installed is not null) return installed;

        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                "Windows chặn minor-version junction của uv và không tìm thấy Python hoàn chỉnh để phục hồi. " +
                "Bootstrap đã được chuyển sang LocalAppData; hãy thử lại. Chi tiết: " + detail);
        throw new FileNotFoundException("uv báo cài Python thành công nhưng không tìm thấy python.exe hợp lệ trong bootstrap.");
    }

    private async Task<string?> FindUsableBasePythonAsync(CancellationToken cancellationToken)
    {
        foreach (var root in new[] { PythonInstallRoot, Path.Combine(_paths.Ocr, "python") }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root)) continue;
            foreach (var directory in Directory.EnumerateDirectories(root)
                         .OrderByDescending(ManagedPythonPatch))
            {
                if (ManagedPythonPatch(directory) < 0) continue;
                foreach (var candidate in new[]
                         {
                             Path.Combine(directory, "python.exe"),
                             Path.Combine(directory, "install", "python.exe"),
                         })
                {
                    if (await IsUsableBasePythonAsync(candidate, cancellationToken)) return candidate;
                }
            }
        }
        return null;
    }

    private async Task<bool> IsUsableBasePythonAsync(string python, CancellationToken cancellationToken)
    {
        if (!File.Exists(python)) return false;
        try
        {
            var result = await _processes.RunAsync(
                python,
                ["-I", "-c", "import sys,ssl,venv; raise SystemExit(0 if sys.version_info[:2] == (3,12) else 9)"],
                cancellationToken,
                ExplicitPythonEnvironment());
            return result.ExitCode == 0;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            return false;
        }
    }

    private static int ManagedPythonPatch(string directory)
    {
        var match = Regex.Match(
            Path.GetFileName(directory),
            @"^cpython-3\.12\.(\d+)-windows-x86_64-none$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var patch) ? patch : -1;
    }

    private async Task CreatePrivateVenvAsync(string uv, string basePython, string venvRoot, CancellationToken cancellationToken)
    {
        if (Directory.Exists(venvRoot)) Directory.Delete(venvRoot, recursive: true);
        var environment = ExplicitPythonEnvironment();

        // Windows stdlib venv uses copies by default, so this path does not require the
        // junction/symlink that fails under OneDrive Files On-Demand.
        var result = await _processes.RunAsync(basePython, ["-I", "-m", "venv", venvRoot], cancellationToken, environment);
        var python = Path.Combine(venvRoot, "Scripts", "python.exe");
        if (result.ExitCode == 0 && File.Exists(python)) return;

        // Fallback for unusual standalone-Python venv layouts. Give uv the exact executable
        // path and forbid Python downloads; it must not resolve or install a 3.12 junction.
        if (Directory.Exists(venvRoot)) Directory.Delete(venvRoot, recursive: true);
        var fallback = await _processes.RunAsync(
            uv,
            ["venv", venvRoot, "--python", basePython, "--no-python-downloads", "--no-config"],
            cancellationToken,
            environment);
        if (fallback.ExitCode != 0 || !File.Exists(python))
        {
            var detail = string.Join(" | ", new[] { result.StandardError.Trim(), fallback.StandardError.Trim() }
                .Where(x => !string.IsNullOrWhiteSpace(x)));
            throw new InvalidOperationException("Không tạo được private Python venv cho OCR: " + detail);
        }
    }

    private async Task RunUvAsync(
        string uv,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> environment,
        string phase,
        CancellationToken cancellationToken)
    {
        var result = await _processes.RunAsync(uv, arguments, cancellationToken, environment);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Cài OCR ({phase}): {result.StandardError.Trim()}");
    }

    private static bool IsMinorVersionLinkFailure(string detail) =>
        detail.Contains("Failed to create Python minor version link directory", StringComparison.OrdinalIgnoreCase)
        || detail.Contains("untrusted mount point", StringComparison.OrdinalIgnoreCase)
        || detail.Contains("os error 448", StringComparison.OrdinalIgnoreCase);

    private async Task<string> EnsureWorkerAsync(CancellationToken cancellationToken)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Assets", "worker.py");
        if (!File.Exists(source)) throw new FileNotFoundException("Thiếu Assets/worker.py của OCR.", source);
        var destination = Path.Combine(_paths.Ocr, "worker.py");
        var sourceHash = await Sha256Async(source, cancellationToken);
        if (!File.Exists(destination) || !string.Equals(await Sha256Async(destination, cancellationToken), sourceHash, StringComparison.OrdinalIgnoreCase))
        {
            var temporary = destination + ".tmp";
            try
            {
                File.Copy(source, temporary, overwrite: true);
                File.Move(temporary, destination, overwrite: true);
            }
            finally { TryDelete(temporary); }
        }
        return destination;
    }

    private async Task<string> EnsureUvAsync(CancellationToken cancellationToken)
    {
        var bootstrap = Path.Combine(BootstrapRoot, "uv");
        Directory.CreateDirectory(bootstrap);
        var uv = Path.Combine(bootstrap, "uv.exe");
        if (File.Exists(uv) && new FileInfo(uv).Length > 0) return uv;
        var archive = Path.Combine(bootstrap, UvArchive);
        var checksumPath = archive + ".sha256";
        try
        {
            await DownloadAsync(UvUrl, archive, cancellationToken);
            await DownloadAsync(UvUrl + ".sha256", checksumPath, cancellationToken);
            var expected = (await File.ReadAllTextAsync(checksumPath, cancellationToken)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
            var actual = await Sha256Async(archive, cancellationToken);
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Checksum uv không khớp.");
            using var zip = ZipFile.OpenRead(archive);
            var entry = zip.Entries.FirstOrDefault(x => string.Equals(Path.GetFileName(x.FullName), "uv.exe", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("Gói uv thiếu uv.exe.");
            var temporary = uv + ".tmp";
            await using (var source = entry.Open())
            await using (var target = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await source.CopyToAsync(target, cancellationToken);
                await target.FlushAsync(cancellationToken);
            }
            File.Move(temporary, uv, overwrite: true);
            return uv;
        }
        finally
        {
            TryDelete(archive);
            TryDelete(checksumPath);
            TryDelete(uv + ".tmp");
        }
    }

    private async Task DownloadAsync(string url, string destination, CancellationToken cancellationToken)
    {
        var temporary = destination + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await source.CopyToAsync(target, cancellationToken);
            await target.FlushAsync(cancellationToken);
            target.Close();
            File.Move(temporary, destination, overwrite: true);
        }
        finally { TryDelete(temporary); }
    }

    private Dictionary<string, string?> ManagedEnvironment() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["UV_DATA_DIR"] = Path.Combine(BootstrapRoot, "uv-data"),
        ["UV_PYTHON_INSTALL_DIR"] = PythonInstallRoot,
        ["UV_PYTHON_INSTALL_BIN"] = "0",
        ["UV_PYTHON_INSTALL_REGISTRY"] = "0",
        ["UV_CACHE_DIR"] = Path.Combine(BootstrapRoot, "cache", "uv"),
        ["UV_MANAGED_PYTHON"] = "1",
        ["UV_NO_PROGRESS"] = "1",
        ["PYTHONUTF8"] = "1",
        ["PYTHONIOENCODING"] = "utf-8",
    };

    private Dictionary<string, string?> ExplicitPythonEnvironment()
    {
        var environment = ManagedEnvironment();
        environment.Remove("UV_MANAGED_PYTHON");
        return environment;
    }

    private static (string Package, string Index, string Device) RuntimeSpec(string kind, HardwareSnapshot hardware)
    {
        if (kind == "cpu") return ("paddlepaddle", CpuIndex, "cpu");
        if (kind != "gpu" || !hardware.NvidiaDetected) throw new InvalidOperationException("Không có NVIDIA GPU tương thích cho PaddleOCR.");
        var match = Regex.Match(hardware.CudaDriver, @"\d+(?:\.\d+)?", RegexOptions.CultureInvariant);
        if (!match.Success || !Version.TryParse(match.Value, out var cuda)) throw new InvalidOperationException("Không đọc được CUDA driver cho PaddlePaddle GPU.");
        var index = cuda >= new Version(12, 6)
            ? Gpu126Index : cuda >= new Version(11, 8)
                ? Gpu118Index : throw new InvalidOperationException("PaddlePaddle GPU cần CUDA driver 11.8+.");
        return ("paddlepaddle-gpu", index, "gpu:0");
    }

    private static async Task<bool> ManifestMatchesAsync(string path, InstallManifest expected, CancellationToken cancellationToken)
    {
        try
        {
            var actual = JsonSerializer.Deserialize<InstallManifest>(await File.ReadAllTextAsync(path, cancellationToken));
            if (actual is null) return false;
            return actual with { WorkerSha256 = expected.WorkerSha256 } == expected;
        }
        catch (Exception error) when (error is IOException or JsonException) { return false; }
    }

    private static async Task WriteManifestAsync(string path, InstallManifest manifest, CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }) + "\n", cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        finally { TryDelete(temporary); }
    }

    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken));
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }

    private sealed record InstallManifest(
        [property: JsonPropertyName("schema")] int Schema,
        [property: JsonPropertyName("uv")] string Uv,
        [property: JsonPropertyName("python")] string Python,
        [property: JsonPropertyName("paddle")] string Paddle,
        [property: JsonPropertyName("paddleocr")] string PaddleOcr,
        [property: JsonPropertyName("det_model")] string DetectionModel,
        [property: JsonPropertyName("rec_model")] string RecognitionModel,
        [property: JsonPropertyName("worker_sha256")] string WorkerSha256,
        [property: JsonPropertyName("runtime")] string Runtime,
        [property: JsonPropertyName("paddle_package")] string PaddlePackage,
        [property: JsonPropertyName("paddle_index")] string PaddleIndex);
}
