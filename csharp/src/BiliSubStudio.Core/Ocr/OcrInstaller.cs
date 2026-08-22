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
            var python = Path.Combine(runtimeRoot, "venv", "Scripts", "python.exe");
            var manifestPath = Path.Combine(runtimeRoot, "install.json");
            var expected = new InstallManifest(2, UvVersion, PythonVersion, PaddleVersion, PaddleOcrVersion,
                DetectionModel, RecognitionModel, await Sha256Async(worker, cancellationToken), kind, spec.Package, spec.Index);
            if (File.Exists(python) && await ManifestMatchesAsync(manifestPath, expected, cancellationToken))
            {
                return new OcrRuntime(python, worker, Path.Combine(_paths.Ocr, "models"), spec.Device, kind);
            }

            var uv = await EnsureUvAsync(cancellationToken);
            Directory.CreateDirectory(runtimeRoot);
            var environment = ManagedEnvironment();
            foreach (var arguments in new[]
            {
                new[] { "python", "install", PythonVersion, "--install-dir", Path.Combine(_paths.Ocr, "python"), "--managed-python", "--no-registry", "--no-bin", "--no-config" },
                new[] { "venv", Path.Combine(runtimeRoot, "venv"), "--python", PythonVersion, "--managed-python", "--no-config" },
                new[] { "pip", "install", "--python", python, $"{spec.Package}=={PaddleVersion}", "--index-url", spec.Index, "--no-config" },
                new[] { "pip", "install", "--python", python, $"paddleocr=={PaddleOcrVersion}", "--no-config" },
            })
            {
                var result = await _processes.RunAsync(uv, arguments, cancellationToken, environment);
                if (result.ExitCode != 0)
                {
                    var detail = result.StandardError.Trim();
                    if (detail.Contains("os error 448", StringComparison.OrdinalIgnoreCase) ||
                        detail.Contains("untrusted mount point", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("Windows chặn đường dẫn reparse point khi cài OCR. BiliSub Studio đã chuyển UV/Python về thư mục local và tắt Python link; hãy bấm Chuẩn bị OCR lại. Chi tiết: " + detail);
                    }
                    throw new InvalidOperationException($"Cài OCR ({arguments[0]}): {detail}");
                }
            }
            await WriteManifestAsync(manifestPath, expected, cancellationToken);
            if (!File.Exists(python)) throw new FileNotFoundException("Cài OCR không tạo private Python.", python);
            return new OcrRuntime(python, worker, Path.Combine(_paths.Ocr, "models"), spec.Device, kind);
        }
        finally { _gate.Release(); }
    }

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
        var bootstrap = Path.Combine(_paths.Ocr, "bootstrap");
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
        ["UV_DATA_DIR"] = Path.Combine(_paths.Ocr, "uv-data"),
        ["UV_PYTHON_INSTALL_DIR"] = Path.Combine(_paths.Ocr, "python"),
        ["UV_PYTHON_INSTALL_BIN"] = "0",
        ["UV_PYTHON_INSTALL_REGISTRY"] = "0",
        ["UV_CACHE_DIR"] = Path.Combine(_paths.Ocr, "cache", "uv"),
        ["UV_LINK_MODE"] = "copy",
        ["UV_MANAGED_PYTHON"] = "1",
        ["UV_NO_PROGRESS"] = "1",
        ["PYTHONUTF8"] = "1",
        ["PYTHONIOENCODING"] = "utf-8",
    };

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
            return JsonSerializer.Deserialize<InstallManifest>(await File.ReadAllTextAsync(path, cancellationToken)) == expected;
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
