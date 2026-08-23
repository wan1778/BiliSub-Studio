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

public sealed record LocalTtsStatus(
    bool RuntimeReady,
    bool MaleVoiceReady,
    bool FemaleVoiceReady,
    string Engine,
    string EngineVersion,
    string MaleVoice,
    string FemaleVoice,
    long DownloadBytes,
    string? Error = null)
{
    public bool Ready => RuntimeReady && MaleVoiceReady && FemaleVoiceReady;
}

internal sealed record LocalTtsRuntime(
    string Python,
    string Worker,
    string MaleModel,
    string MaleConfig,
    string FemaleModel,
    string FemaleConfig,
    IReadOnlyDictionary<string, string?> Environment);

internal sealed record TtsModelFile(string Name, long Size, string Sha256, string Url);

internal sealed class LocalTtsInstaller : IDisposable
{
    internal const string PiperVersion = "1.4.2";
    internal const string PiperWheel = "https://files.pythonhosted.org/packages/c5/5a/fda959ca07554a8ec3e380b168e79fff16f3020f4956c356a613616c1994/piper_tts-1.4.2-cp39-abi3-win_amd64.whl#sha256=9c4a3a11f5889ea9d0df4414dce2bd9bee5ce7d9cf604c8fd5e307441d4c031f";
    internal const string VoiceRepository = "rhasspy/piper-voices";
    internal const string VoiceRevision = "3d796cc2f2c884b3517c527507e084f7bb245aea";
    internal const string BaseVoice = "vi_VN-vais1000-medium";
    internal const string MaleVoice = "vais1000-male-profile-v1";
    internal const string FemaleVoice = "vais1000-female-profile-v1";
    internal const long VoiceModelBytes = 63_201_294;
    internal const long VoiceConfigBytes = 4_860;
    internal const string VoiceModelSha256 = "ec7c89e2c85f4d1edc24b6120c18aaf1bda614f06b511567eb9c7c0de15e2dab";
    internal const string VoiceConfigSha256 = "fafb9da1354ed4b77c31af228ed41fb41cd825c14cffa105454b25e6ae751ee0";

    private readonly AppPaths _paths;
    private readonly OcrInstaller _pythonBootstrap;
    private readonly ProcessRunner _processes;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool? _runtimeReady;
    private string? _lastError;

    public LocalTtsInstaller(AppPaths paths, OcrInstaller pythonBootstrap, ProcessRunner processes)
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

    private string Root => Path.Combine(_paths.Tools, "TTS");
    private string RuntimeRoot => Path.Combine(Root, "runtime");
    private string VenvRoot => Path.Combine(RuntimeRoot, "venv");
    private string Python => Path.Combine(VenvRoot, "Scripts", "python.exe");
    private string RuntimeManifest => Path.Combine(RuntimeRoot, "install.json");
    private string Worker => Path.Combine(Root, "worker.py");
    private string ModelRoot => Path.Combine(Root, "Models", "piper-vais1000-" + VoiceRevision[..12]);
    private string VoiceModelPath => Path.Combine(ModelRoot, BaseVoice + ".onnx");
    private string VoiceConfigPath => Path.Combine(ModelRoot, BaseVoice + ".onnx.json");

    public LocalTtsStatus Status
    {
        get
        {
            try
            {
                var runtime = _runtimeReady ??= RuntimeMatches();
                var voiceReady = FileMatches(VoiceModelPath, VoiceModelBytes, VoiceModelSha256)
                    && FileMatches(VoiceConfigPath, VoiceConfigBytes, VoiceConfigSha256);
                return new LocalTtsStatus(
                    runtime,
                    voiceReady,
                    voiceReady,
                    "Piper local + VAIS-1000 acoustic profiles",
                    PiperVersion,
                    MaleVoice,
                    FemaleVoice,
                    VoiceModelBytes + VoiceConfigBytes,
                    _lastError);
            }
            catch (Exception error)
            {
                return new LocalTtsStatus(false, false, false, "Piper local + VAIS-1000 acoustic profiles", PiperVersion, MaleVoice, FemaleVoice,
                    VoiceModelBytes + VoiceConfigBytes, error.Message);
            }
        }
    }

    public async Task<LocalTtsRuntime> PrepareAsync(AppJob job, double progressCeiling = 98)
    {
        progressCeiling = Math.Clamp(progressCeiling, 1, 98);
        double Progress(double value) => Math.Clamp(value, 0, 100) / 100d * progressCeiling;
        await _gate.WaitAsync(job.CancellationToken);
        try
        {
            _lastError = null;
            Directory.CreateDirectory(Root);
            var worker = await EnsureWorkerAsync(job.CancellationToken);
            if (!Status.RuntimeReady)
            {
                job.Set("tts-python", Progress(2), "Đang dựng runtime Piper local riêng cho voice Việt...");
                if (Directory.Exists(RuntimeRoot)) Directory.Delete(RuntimeRoot, recursive: true);
                Directory.CreateDirectory(RuntimeRoot);
                var managed = await _pythonBootstrap.EnsurePrivatePythonAsync(VenvRoot, job.CancellationToken);
                var install = await _processes.RunAsync(
                    managed.Uv,
                    ["pip", "install", "--python", managed.Python, "--no-python-downloads", "--no-config", PiperWheel],
                    job.CancellationToken,
                    managed.Environment);
                if (install.ExitCode != 0)
                    throw new InvalidOperationException("Cài runtime Piper local: " + install.StandardError.Trim());
                var verify = await _processes.RunAsync(
                    managed.Python,
                    ["-I", "-c", $"import importlib.metadata,piper,numpy; assert importlib.metadata.version('piper-tts')=='{PiperVersion}'"],
                    job.CancellationToken,
                    managed.Environment);
                if (verify.ExitCode != 0)
                    throw new InvalidOperationException("Runtime Piper không vượt kiểm tra import/version: " + verify.StandardError.Trim());
                await WriteRuntimeManifestAsync(worker, job.CancellationToken);
                _runtimeReady = null;
            }

            Directory.CreateDirectory(ModelRoot);
            var files = ModelFiles();
            var total = files.Sum(x => x.Size);
            long completed = files.Where(x => FileMatches(Path.Combine(ModelRoot, x.Name), x.Size, x.Sha256)).Sum(x => x.Size);
            foreach (var file in files)
            {
                var destination = Path.Combine(ModelRoot, file.Name);
                if (FileMatches(destination, file.Size, file.Sha256)) continue;
                var start = Progress(25 + completed / (double)total * 72);
                var end = Progress(25 + (completed + file.Size) / (double)total * 72);
                job.Set("tts-model", start, $"Đang tải voice Piper VAIS-1000 đã xác minh · {completed / 1024d / 1024:0}/{total / 1024d / 1024:0} MB...");
                await DownloadVerifiedAsync(file.Url, destination, file.Size, file.Sha256, start, end, total, completed, job);
                WriteStamp(destination, file.Size, file.Sha256);
                completed += file.Size;
            }

            if (!Status.Ready) throw new InvalidOperationException(Status.Error ?? "Voice local chưa hoàn chỉnh sau khi cài.");
            job.Set("tts-ready", Progress(99), "Voice Việt local sẵn sàng · profile Nam tổng hợp + Nữ gốc từ VAIS-1000.");
            return new LocalTtsRuntime(Python, worker, VoiceModelPath, VoiceConfigPath, VoiceModelPath, VoiceConfigPath,
                new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["PYTHONUTF8"] = "1",
                    ["PYTHONIOENCODING"] = "utf-8",
                    ["HF_HUB_OFFLINE"] = "1",
                    ["TRANSFORMERS_OFFLINE"] = "1",
                });
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _lastError = error.Message;
            throw;
        }
        finally { _gate.Release(); }
    }

    private static IReadOnlyList<TtsModelFile> ModelFiles()
    {
        static string Url(string name) => $"https://huggingface.co/{VoiceRepository}/resolve/{VoiceRevision}/vi/vi_VN/vais1000/medium/{name}?download=true";
        return
        [
            new TtsModelFile(BaseVoice + ".onnx", VoiceModelBytes, VoiceModelSha256, Url(BaseVoice + ".onnx")),
            new TtsModelFile(BaseVoice + ".onnx.json", VoiceConfigBytes, VoiceConfigSha256, Url(BaseVoice + ".onnx.json")),
        ];
    }

    private async Task<string> EnsureWorkerAsync(CancellationToken cancellationToken)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Assets", "TTS", "worker.py");
        if (!File.Exists(source)) throw new FileNotFoundException("Thiếu worker TTS đã đóng gói.", source);
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
            var manifest = JsonSerializer.Deserialize<RuntimeInstallManifest>(File.ReadAllText(RuntimeManifest));
            return manifest is not null && manifest.Schema == 1 && manifest.Piper == PiperVersion && manifest.WorkerSha256 == HashFile(Worker);
        }
        catch { return false; }
    }

    private async Task WriteRuntimeManifestAsync(string worker, CancellationToken cancellationToken)
    {
        var manifest = new RuntimeInstallManifest(1, OcrInstaller.PythonVersion, PiperVersion, await HashAsync(worker, cancellationToken));
        var temporary = RuntimeManifest + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(manifest, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                WriteIndented = true,
            }) + "\n", new UTF8Encoding(false), cancellationToken);
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
        long totalBytes,
        long completedBytes,
        AppJob job)
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
        request.Headers.UserAgent.ParseAdd("BiliSubStudio/4-CSharp-TTS");
        if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, job.CancellationToken);
        if (existing > 0 && response.StatusCode != HttpStatusCode.PartialContent)
        {
            TryDelete(partial);
            existing = 0;
        }
        response.EnsureSuccessStatusCode();
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
                if (fileBytes > expectedSize) throw new InvalidDataException("File voice lớn hơn manifest đã khóa.");
                if (clock.Elapsed - lastReport >= TimeSpan.FromMilliseconds(400))
                {
                    lastReport = clock.Elapsed;
                    var progress = startProgress + fileBytes / (double)expectedSize * (endProgress - startProgress);
                    var all = completedBytes + fileBytes;
                    job.Set("tts-download", progress, $"Đang tải voice local · {all / 1024d / 1024:0}/{totalBytes / 1024d / 1024:0} MB");
                }
            }
            await target.FlushAsync(job.CancellationToken);
            target.Flush(flushToDisk: true);
        }
        if (new FileInfo(partial).Length != expectedSize) throw new InvalidDataException("File voice tải về chưa đủ kích thước manifest.");
        job.Set("tts-verify", endProgress, $"Đang xác minh SHA-256 {Path.GetFileName(destination)}...");
        var actual = await HashAsync(partial, job.CancellationToken);
        if (!string.Equals(actual, expectedSha, StringComparison.Ordinal))
        {
            TryDelete(partial);
            throw new InvalidDataException("SHA-256 voice Piper VAIS-1000 không khớp; đã xóa file không tin cậy.");
        }
        File.Move(partial, destination, overwrite: true);
    }

    private static bool FileMatches(string path, long size, string sha)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != size || info.LinkTarget is not null || !File.Exists(path + ".verified")) return false;
            var expected = $"{sha}|{size}|{info.LastWriteTimeUtc.Ticks}";
            return string.Equals(File.ReadAllText(path + ".verified").Trim(), expected, StringComparison.Ordinal);
        }
        catch { return false; }
    }

    private static void WriteStamp(string path, long size, string sha)
    {
        File.WriteAllText(path + ".verified", $"{sha}|{size}|{new FileInfo(path).LastWriteTimeUtc.Ticks}\n", new UTF8Encoding(false));
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

    private sealed record RuntimeInstallManifest(int Schema, string Python, string Piper, string WorkerSha256);
}
