using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BiliSubStudio.Core.Configuration;
using BiliSubStudio.Core.Jobs;
using BiliSubStudio.Core.Ocr;
using BiliSubStudio.Core.Processes;

namespace BiliSubStudio.Core.Editor;

public sealed record LocalAsrStatus(
    bool RuntimeReady,
    bool ModelReady,
    string ModelName,
    long DownloadBytes,
    string ModelRevision,
    string? Error = null)
{
    public bool Ready => RuntimeReady && ModelReady;
}

internal sealed record LocalAsrRuntime(string Python, string Worker, string ModelDirectory, IReadOnlyDictionary<string, string?> Environment);
internal sealed record AsrModelFile(string Name, long Size, string Sha256);

internal sealed partial class LocalAsrInstaller : IDisposable
{
    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    internal const string FasterWhisperVersion = "1.2.1";
    internal const string CTranslate2Version = "4.8.1";
    internal const string FasterWhisperWheel = "https://files.pythonhosted.org/packages/05/99/49ee85903dee060d9f08297b4a342e5e0bcfca2f027a07b4ee0a38ab13f9/faster_whisper-1.2.1-py3-none-any.whl#sha256=79a66ad50688c0b794dd501dc340a736992a6342f7f95e5811be60b5224a26a7";
    internal const string CTranslate2Wheel = "https://files.pythonhosted.org/packages/c0/82/0a5f7f2b03b4e10aacb3146715724e1b96bb993cc7d199be28c9825aa120/ctranslate2-4.8.1-cp312-cp312-win_amd64.whl#sha256=49f96e861b57301f0b76a082109bde2cac8204a6b4fedc870883008271e82251";
    internal const string ModelName = "faster-whisper small (đa ngôn ngữ)";
    internal const string ModelRepository = "Systran/faster-whisper-small";
    internal const string ModelRevision = "536b0662742c02347bc0e980a01041f333bce120";
    internal static readonly AsrModelFile[] ModelFiles =
    [
        new("config.json", 2_370, "b55496ac7940a7ae47d2c01eab40edfd8701feec1229d9cce3b40014383fb828"),
        new("model.bin", 483_546_902, "3e305921506d8872816023e4c273e75d2419fb89b24da97b4fe7bce14170d671"),
        new("tokenizer.json", 2_203_239, "fb7b63191e9bb045082c79fd742a3106a12c99513ab30df4a0d47fa6cb6fd0ab"),
        new("vocabulary.txt", 459_861, "34ce3fe1c5041027b3f8d42912270993f986dbc4bb34cf27f951e34a1e453913"),
    ];

    private readonly AppPaths _paths;
    private readonly OcrInstaller _pythonBootstrap;
    private readonly ProcessRunner _processes;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool? _runtimeReady;
    private bool? _modelReady;
    private string? _lastError;

    public LocalAsrInstaller(AppPaths paths, OcrInstaller pythonBootstrap, ProcessRunner processes)
    {
        _paths = paths;
        _pythonBootstrap = pythonBootstrap;
        _processes = processes;
        _http = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            AutomaticDecompression = DecompressionMethods.None,
        }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private string Root => Path.Combine(_paths.Tools, "ASR");
    private string RuntimeRoot => Path.Combine(Root, "runtime");
    private string VenvRoot => Path.Combine(RuntimeRoot, "venv");
    private string Python => Path.Combine(VenvRoot, "Scripts", "python.exe");
    private string RuntimeManifest => Path.Combine(RuntimeRoot, "install.json");
    private string Worker => Path.Combine(Root, "worker.py");
    private string ModelDirectory => Path.Combine(Root, "Models", "faster-whisper-small-" + ModelRevision[..12]);

    public LocalAsrStatus Status
    {
        get
        {
            try
            {
                var runtime = _runtimeReady ??= RuntimeMatches();
                var model = _modelReady ??= ModelFiles.All(FileMatchesStamp);
                return new LocalAsrStatus(runtime, model, ModelName, ModelFiles.Sum(x => x.Size), ModelRevision, _lastError);
            }
            catch (Exception error)
            {
                return new LocalAsrStatus(false, false, ModelName, ModelFiles.Sum(x => x.Size), ModelRevision, error.Message);
            }
        }
    }

    public async Task<LocalAsrRuntime> PrepareAsync(AppJob job, double progressCeiling = 98)
    {
        progressCeiling = Math.Clamp(progressCeiling, 1, 98);
        double Progress(double value) => Math.Clamp(value, 0, 100) / 100d * progressCeiling;
        await _gate.WaitAsync(job.CancellationToken);
        try
        {
            _lastError = null;
            Directory.CreateDirectory(Root);
            var worker = await EnsureWorkerAsync(job.CancellationToken);
            _runtimeReady = null; // A bundled worker update invalidates a previously cached status.
            if (!Status.RuntimeReady)
            {
                job.Set("asr-python", Progress(1), "Đang dựng Python ASR riêng bằng bootstrap Windows an toàn...");
                if (Directory.Exists(RuntimeRoot)) Directory.Delete(RuntimeRoot, recursive: true);
                Directory.CreateDirectory(RuntimeRoot);
                var managed = await _pythonBootstrap.EnsurePrivatePythonAsync(VenvRoot, job.CancellationToken);
                var install = await _processes.RunAsync(
                    managed.Uv,
                    ["pip", "install", "--python", managed.Python, "--no-python-downloads", "--no-config",
                        FasterWhisperWheel, CTranslate2Wheel],
                    job.CancellationToken,
                    managed.Environment);
                if (install.ExitCode != 0)
                    throw new InvalidOperationException("Cài runtime ASR: " + install.StandardError.Trim());
                var verify = await _processes.RunAsync(
                    managed.Python,
                    ["-I", "-c", $"import importlib.metadata,ctranslate2,faster_whisper; assert importlib.metadata.version('faster-whisper')=='{FasterWhisperVersion}'; assert ctranslate2.__version__=='{CTranslate2Version}'"],
                    job.CancellationToken,
                    managed.Environment);
                if (verify.ExitCode != 0)
                    throw new InvalidOperationException("Runtime ASR không vượt qua kiểm tra import/version: " + verify.StandardError.Trim());
                await WriteRuntimeManifestAsync(worker, job.CancellationToken);
                _runtimeReady = null;
            }

            Directory.CreateDirectory(ModelDirectory);
            var totalBytes = ModelFiles.Sum(x => x.Size);
            long completed = ModelFiles.Where(FileMatchesStamp).Sum(x => x.Size);
            foreach (var file in ModelFiles)
            {
                if (FileMatchesStamp(file)) continue;
                var start = Progress(30 + completed / (double)totalBytes * 67);
                var end = Progress(30 + (completed + file.Size) / (double)totalBytes * 67);
                job.Set("asr-model", start, $"Đang tải model nhận giọng Trung · {completed / 1024d / 1024:0}/{totalBytes / 1024d / 1024:0} MB...");
                var url = $"https://huggingface.co/{ModelRepository}/resolve/{ModelRevision}/{file.Name}?download=true";
                var destination = Path.Combine(ModelDirectory, file.Name);
                await DownloadVerifiedAsync(url, destination, file.Size, file.Sha256, start, end, totalBytes, completed, job);
                WriteStamp(file);
                completed += file.Size;
            }
            _modelReady = null;
            if (!Status.Ready) throw new InvalidOperationException(Status.Error ?? "ASR local chưa hoàn chỉnh sau khi cài.");
            job.Set("asr-ready", Progress(98), $"ASR local sẵn sàng · {ModelName} · model đã khóa revision/SHA-256.");
            var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["PYTHONUTF8"] = "1",
                ["PYTHONIOENCODING"] = "utf-8",
                ["HF_HUB_OFFLINE"] = "1",
                ["TRANSFORMERS_OFFLINE"] = "1",
            };
            return new LocalAsrRuntime(Python, worker, ModelDirectory, environment);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _lastError = error.Message;
            throw;
        }
        finally { _gate.Release(); }
    }

    private async Task<string> EnsureWorkerAsync(CancellationToken cancellationToken)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Assets", "ASR", "worker.py");
        if (!File.Exists(source)) throw new FileNotFoundException("Thiếu worker ASR đã đóng gói.", source);
        Directory.CreateDirectory(Root);
        var sourceHash = await HashAsync(source, cancellationToken);
        if (!File.Exists(Worker) || !string.Equals(await HashAsync(Worker, cancellationToken), sourceHash, StringComparison.Ordinal))
        {
            var temporary = Worker + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.Copy(source, temporary, overwrite: false);
                File.Move(temporary, Worker, overwrite: true);
            }
            finally { TryDelete(temporary); }
        }
        return Worker;
    }

    private bool RuntimeMatches()
    {
        try
        {
            if (!File.Exists(Python) || !File.Exists(Worker) || !File.Exists(RuntimeManifest)) return false;
            var manifest = JsonSerializer.Deserialize<RuntimeInstallManifest>(File.ReadAllText(RuntimeManifest), ManifestJson);
            return manifest is not null && manifest.Schema == 1
                && manifest.FasterWhisper == FasterWhisperVersion
                && manifest.CTranslate2 == CTranslate2Version
                && manifest.WorkerSha256 == HashFile(Worker);
        }
        catch { return false; }
    }

    private async Task WriteRuntimeManifestAsync(string worker, CancellationToken cancellationToken)
    {
        var manifest = new RuntimeInstallManifest(1, OcrInstaller.PythonVersion, FasterWhisperVersion, CTranslate2Version,
            await HashAsync(worker, cancellationToken));
        var temporary = RuntimeManifest + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(manifest, ManifestJson) + "\n",
                new UTF8Encoding(false), cancellationToken);
            File.Move(temporary, RuntimeManifest, overwrite: true);
        }
        finally { TryDelete(temporary); }
    }

    private async Task DownloadVerifiedAsync(
        string url,
        string destination,
        long expectedSize,
        string expectedSha,
        double startProgress,
        double endProgress,
        long totalModelBytes,
        long completedModelBytes,
        AppJob job,
        string payloadLabel = "model ASR")
    {
        var partial = destination + ".partial";
        var existing = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (existing < 0 || existing > expectedSize) { TryDelete(partial); existing = 0; }
        if (existing == expectedSize)
        {
            var completeSha = await HashAsync(partial, job.CancellationToken);
            if (string.Equals(completeSha, expectedSha, StringComparison.Ordinal))
            {
                File.Move(partial, destination, overwrite: true);
                return;
            }
            TryDelete(partial);
            existing = 0;
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("BiliSubStudio/4-CSharp-ASR");
        if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, job.CancellationToken);
        // A CDN error must not discard a recoverable CUDA/model partial.
        response.EnsureSuccessStatusCode();
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            var range = response.Content.Headers.ContentRange;
            if (range is null || range.Unit != "bytes" || range.From != existing || range.Length != expectedSize
                || range.To is null || range.To.Value < existing || range.To.Value >= expectedSize)
                throw new InvalidDataException($"Range tải {payloadLabel} không khớp; giữ file tải dở để thử lại.");
        }
        else if (response.StatusCode == HttpStatusCode.OK) existing = 0; // Server ignored Range: replace only after success.
        else throw new InvalidDataException($"Phản hồi tải {payloadLabel} không có dữ liệu hợp lệ.");
        await using var source = await response.Content.ReadAsStreamAsync(job.CancellationToken);
        await using (var target = new FileStream(partial, existing > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var buffer = new byte[1024 * 1024];
            var fileBytes = existing;
            var clock = Stopwatch.StartNew();
            var lastReport = TimeSpan.Zero;
            while (true)
            {
                var read = await source.ReadAsync(buffer, job.CancellationToken);
                if (read == 0) break;
                await target.WriteAsync(buffer.AsMemory(0, read), job.CancellationToken);
                fileBytes += read;
                if (fileBytes > expectedSize) throw new InvalidDataException($"File {payloadLabel} lớn hơn manifest đã khóa.");
                if (clock.Elapsed - lastReport >= TimeSpan.FromMilliseconds(400))
                {
                    lastReport = clock.Elapsed;
                    var progress = startProgress + fileBytes / (double)expectedSize * (endProgress - startProgress);
                    var all = completedModelBytes + fileBytes;
                    job.Set("asr-download", progress, $"Đang tải {payloadLabel} · {all / 1024d / 1024:0}/{totalModelBytes / 1024d / 1024:0} MB");
                }
            }
            await target.FlushAsync(job.CancellationToken);
            target.Flush(flushToDisk: true);
        }
        if (new FileInfo(partial).Length != expectedSize) throw new InvalidDataException($"File {payloadLabel} tải về chưa đủ kích thước manifest.");
        job.Set("asr-verify", endProgress, $"Đang xác minh SHA-256 {Path.GetFileName(destination)}...");
        var actual = await HashAsync(partial, job.CancellationToken);
        if (!string.Equals(actual, expectedSha, StringComparison.Ordinal))
        {
            TryDelete(partial);
            throw new InvalidDataException($"SHA-256 {payloadLabel} không khớp; đã xóa file không tin cậy.");
        }
        File.Move(partial, destination, overwrite: true);
    }

    private bool FileMatchesStamp(AsrModelFile file)
    {
        try
        {
            var path = Path.Combine(ModelDirectory, file.Name);
            var stamp = path + ".verified";
            if (!File.Exists(path) || new FileInfo(path).Length != file.Size || !File.Exists(stamp) || new FileInfo(path).LinkTarget is not null) return false;
            var expected = $"{file.Sha256}|{file.Size}|{new FileInfo(path).LastWriteTimeUtc.Ticks}";
            return string.Equals(File.ReadAllText(stamp).Trim(), expected, StringComparison.Ordinal);
        }
        catch { return false; }
    }

    private void WriteStamp(AsrModelFile file)
    {
        var path = Path.Combine(ModelDirectory, file.Name);
        File.WriteAllText(path + ".verified", $"{file.Sha256}|{file.Size}|{new FileInfo(path).LastWriteTimeUtc.Ticks}\n", new UTF8Encoding(false));
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }

    public void Dispose()
    {
        _gate.Dispose();
        _http.Dispose();
    }

    private sealed record RuntimeInstallManifest(int Schema, string Python, string FasterWhisper, string CTranslate2, string WorkerSha256);
}
