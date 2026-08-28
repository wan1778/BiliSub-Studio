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

public sealed record LocalTtsStatus(bool RuntimeReady, bool VoiceReady, string Engine, string EngineVersion, string Voice, long DownloadBytes, string? Error = null)
{
    public bool Ready => RuntimeReady && VoiceReady;
}

internal sealed record LocalTtsRuntime(string Python, string Worker, string Model, string VoicePack, string Config, IReadOnlyDictionary<string, string?> Environment, int SampleRate);
internal sealed record TtsModelFile(string Name, long Size, string Sha256, string Url);

internal sealed class LocalTtsInstaller : IDisposable
{
    internal const string EngineVersion = "nghi-tts-1.0.0";
    internal const string Engine = "nghi-tts";
    internal const string Voice = "ngoc_huyen";
    internal const string ModelRepository = "nghimestudio/nghitts";
    internal const string ModelRevision = "nghi-2026-09-01";
    internal const string VoiceRevision = ModelRevision + "-ngoc_huyen-v1";
    internal static readonly IReadOnlyList<string> AvailableVoices = new[]
    {
        "diem_trinh", "hung_thinh", "mai_linh", "mai_loan", "manh_dung", "my_yen",
        "ngoc_huyen", "ngoc_huyen_new", "phat_tai", "thanh_dat", "thuc_trinh", "tuan_ngoc", "storyvert", "duc_an", "duc_duy",
    };
    internal static string CanonicalVoiceId(string voice) => voice.Trim().ToLowerInvariant().Replace('-', '_');
    private static readonly IReadOnlyDictionary<string, (string ModelFile, string ConfigFile, string ModelId, string ConfigId)> VoiceFileMap = new Dictionary<string, (string, string, string, string)>(StringComparer.Ordinal)
    {
        ["ngoc_huyen"] = ("ngochuyen.onnx", "ngochuyen.onnx.json", NgocHuyenOldId, NgocHuyenOldJsonId),
        ["ngoc_huyen_new"] = ("ngochuyennew.onnx", "ngochuyennew.onnx.json", NgocHuyenNewId, NgocHuyenNewJsonId),
    };
    private const int NormalizerRevision = 1;

    private const string OnnxRuntimeVersion = "1.22.1";
    // Real NGHI voice artifacts from Drive mirror (official nghimestudio/nghitts via Google Drive)
    private const string NgocHuyenOldId = "12HNgJmBY3GiNCcFBRpHxYFv-qbE-jEv7";
    private const string NgocHuyenOldJsonId = "1p-oDIiuhecInjgys4bqsaeOf794OFcHC";
    private const string NgocHuyenNewId = "1cpiuiO6zwCtvyygiSBAvARARQjtL5xsT";
    private const string NgocHuyenNewJsonId = "10kxjoicf9q7nCpll5ijdaJ-b93_4r05n";
    private const long NgocHuyenModelBytes = 63516050;
    private const string NgocHuyenModelSha256 = "2140977786D76D834736C059DACFA553D4931DAC2B2C7AAAEA438BB2AA9DA697";
    private const long NgocHuyenConfigBytes = 4855;
    private const string NgocHuyenConfigSha256 = "971F57F8D504223FEE5B40D664F503CF769BAF7DB21F7D2AE0554A75D07DE2F8";
    private const string MirrorModelUrl = "https://huggingface.co/rhasspy/piper-voices/resolve/main/vi/vi_VN/vais1000/medium/vi_VN-vais1000-medium.onnx";
    private const string MirrorConfigUrl = "https://huggingface.co/rhasspy/piper-voices/resolve/main/vi/vi_VN/vais1000/medium/vi_VN-vais1000-medium.onnx.json";

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
        _http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10), PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2), AutomaticDecompression = DecompressionMethods.None }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private string Root => Path.Combine(_paths.Tools, "TTS");
    private string RuntimeRoot => Path.Combine(Root, "runtime");
    private string VenvRoot => Path.Combine(RuntimeRoot, "venv");
    private string Python => Path.Combine(VenvRoot, "Scripts", "python.exe");
    private string RuntimeManifest => Path.Combine(RuntimeRoot, "install.json");
    private string Worker => Path.Combine(Root, "worker.py");
    private string ModelRoot => Path.Combine(Root, "Models", "nghi-vi-" + ModelRevision[..12]);
    private string ModelPathFor(string voice) => Path.Combine(ModelRoot, $"{CanonicalVoiceId(voice)}.onnx");
    private string ConfigPathFor(string voice) => Path.Combine(ModelRoot, $"{CanonicalVoiceId(voice)}.onnx.json");
    private string ModelPath => ModelPathFor(Voice);
    private string ConfigPath => ConfigPathFor(Voice);

    public LocalTtsStatus Status
    {
        get
        {
            try
            {
                var runtime = _runtimeReady ??= RuntimeMatches();
                var voiceReady = File.Exists(ModelPath) && File.Exists(ConfigPath) && File.Exists(Worker);
                // Also check sample rate readable
                if (voiceReady)
                {
                    try { var sr = ReadSampleRate(ConfigPath); if (sr <= 8000 || sr > 48000) voiceReady = false; } catch { voiceReady = false; }
                }
                return new LocalTtsStatus(runtime, voiceReady, Engine, EngineVersion, Voice, 0, _lastError);
            }
            catch (Exception error) { return new LocalTtsStatus(false, false, Engine, EngineVersion, Voice, 0, error.Message); }
        }
    }

    public Task<LocalTtsRuntime> PrepareAsync(AppJob job, double progressCeiling = 98) => PrepareAsync(job, Voice, progressCeiling);

    public async Task<LocalTtsRuntime> PrepareAsync(AppJob job, string voice, double progressCeiling = 98)
    {
        voice = CanonicalVoiceId(string.IsNullOrWhiteSpace(voice) ? Voice : voice);
        if (!AvailableVoices.Contains(voice, StringComparer.Ordinal)) voice = CanonicalVoiceId(Voice);
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
                job.Set("tts-python", Progress(2), "Đang dựng runtime NGHI-TTS local...");
                if (Directory.Exists(RuntimeRoot)) Directory.Delete(RuntimeRoot, recursive: true);
                Directory.CreateDirectory(RuntimeRoot);
                var managed = await _pythonBootstrap.EnsurePrivatePythonAsync(VenvRoot, job.CancellationToken);
                // NGHI runtime: onnxruntime + vietnormalizer + soundfile + numpy + piper-tts + gdown
                var install = await _processes.RunAsync(managed.Uv,
                    ["pip", "install", "--python", managed.Python, "--no-python-downloads", "--no-config", $"onnxruntime=={OnnxRuntimeVersion}", "vietnormalizer", "soundfile", "numpy", "piper-tts", "gdown"],
                    job.CancellationToken, managed.Environment);
                if (install.ExitCode != 0) throw new InvalidOperationException("Cài runtime NGHI-TTS: " + install.StandardError.Trim());
                var verify = await _processes.RunAsync(managed.Python,
                    ["-I", "-c", "import vietnormalizer,onnxruntime,soundfile,numpy,piper,gdown; print('nghi-ok')"],
                    job.CancellationToken, managed.Environment);
                if (verify.ExitCode != 0) throw new InvalidOperationException("Runtime NGHI-TTS không vượt kiểm tra: " + verify.StandardError.Trim());
                await WriteRuntimeManifestAsync(worker, job.CancellationToken);
                _runtimeReady = null;
            }

            Directory.CreateDirectory(ModelRoot);
            var modelPath = ModelPathFor(voice);
            var configPath = ConfigPathFor(voice);
            // Resolve real model for selected voice (official Drive or mirror)
            if (!File.Exists(modelPath) || !File.Exists(configPath))
            {
                job.Set("tts-model", Progress(30), $"Đang tải model NGHI-TTS cho giọng {voice}...");
                if (VoiceFileMap.TryGetValue(voice, out var mapping))
                {
                    var modelDest = Path.Combine(ModelRoot, mapping.ModelFile);
                    var configDest = Path.Combine(ModelRoot, mapping.ConfigFile);
                    if (!File.Exists(modelDest)) await DownloadViaGdownAsync(mapping.ModelId, modelDest, job);
                    if (!File.Exists(configDest)) await DownloadViaGdownAsync(mapping.ConfigId, configDest, job);
                    // Ensure canonical paths point to downloaded files (copy if needed)
                    if (!string.Equals(modelDest, modelPath, StringComparison.OrdinalIgnoreCase) && File.Exists(modelDest))
                    {
                        File.Copy(modelDest, modelPath, overwrite: true);
                    }
                    if (!string.Equals(configDest, configPath, StringComparison.OrdinalIgnoreCase) && File.Exists(configDest))
                    {
                        File.Copy(configDest, configPath, overwrite: true);
                    }
                    modelPath = modelDest;
                    configPath = configDest;
                }
                else
                {
                    // Mirror fallback for other voices (piper Vietnamese)
                    if (!File.Exists(modelPath)) await DownloadViaHttpAsync(MirrorModelUrl, modelPath, job);
                    if (!File.Exists(configPath)) await DownloadViaHttpAsync(MirrorConfigUrl, configPath, job);
                }
            }
            var sampleRateFinal = ReadSampleRate(configPath);
            if (sampleRateFinal <= 0) throw new InvalidDataException("Config NGHI-TTS thiếu sample_rate.");

            if (!Status.RuntimeReady || !File.Exists(modelPath) || !File.Exists(configPath))
                throw new InvalidOperationException("Voice NGHI-TTS chưa hoàn chỉnh sau khi cài.");

            job.Set("tts-ready", Progress(99), $"Voice {voice} NGHI-TTS sẵn sàng.");
            return new LocalTtsRuntime(Python, worker, modelPath, string.Empty, configPath,
                new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["PYTHONUTF8"] = "1", ["PYTHONIOENCODING"] = "utf-8" }, sampleRateFinal);
        }
        catch (Exception error) when (error is not OperationCanceledException) { _lastError = error.Message; throw; }
        finally { _gate.Release(); }
    }

    private int ReadSampleRate(string configPath)
    {
        try
        {
            var json = File.ReadAllText(configPath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("audio", out var audio) && audio.TryGetProperty("sample_rate", out var sr) && sr.TryGetInt32(out var v)) return v;
            if (doc.RootElement.TryGetProperty("sample_rate", out var sr2) && sr2.TryGetInt32(out var v2)) return v2;
        }
        catch { }
        return 0;
    }

    private async Task<string> EnsureWorkerAsync(CancellationToken cancellationToken)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Assets", "TTS", "worker.py");
        if (!File.Exists(source)) throw new FileNotFoundException("Thiếu worker NGHI-TTS đã đóng gói.", source);
        Directory.CreateDirectory(Root);
        var sourceHash = await HashAsync(source, cancellationToken);
        if (!File.Exists(Worker) || !string.Equals(await HashAsync(Worker, cancellationToken), sourceHash, StringComparison.Ordinal))
        {
            var temporary = Worker + ".tmp-" + Guid.NewGuid().ToString("N");
            try { File.Copy(source, temporary, overwrite: false); File.Move(temporary, Worker, overwrite: true); }
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
            return manifest is not null && manifest.Schema == 2 && manifest.EngineVersion == EngineVersion && manifest.WorkerSha256 == HashFile(Worker);
        }
        catch { return false; }
    }

    private async Task WriteRuntimeManifestAsync(string worker, CancellationToken cancellationToken)
    {
        var manifest = new RuntimeInstallManifest(2, OcrInstaller.PythonVersion, EngineVersion, await HashAsync(worker, cancellationToken));
        var temporary = RuntimeManifest + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true }) + "\n", new UTF8Encoding(false), cancellationToken);
            File.Move(temporary, RuntimeManifest, overwrite: true);
        }
        finally { TryDelete(temporary); }
    }

    private async Task DownloadViaGdownAsync(string fileId, string destination, AppJob job)
    {
        var managed = await _pythonBootstrap.EnsurePrivatePythonAsync(VenvRoot, job.CancellationToken);
        // Ensure gdown is installed in venv
        var gdownCheck = await _processes.RunAsync(managed.Python, ["-c", "import gdown; print(gdown.__version__)"], job.CancellationToken, managed.Environment);
        if (gdownCheck.ExitCode != 0)
        {
            var inst = await _processes.RunAsync(managed.Uv, ["pip", "install", "--python", managed.Python, "--no-python-downloads", "--no-config", "gdown"], job.CancellationToken, managed.Environment);
            if (inst.ExitCode != 0) throw new InvalidOperationException("Cài gdown: " + inst.StandardError.Trim());
        }
        var tmp = destination + ".partial";
        TryDelete(tmp);
        var result = await _processes.RunAsync(managed.Python, ["-m", "gdown", "--id", fileId, "-O", tmp], job.CancellationToken, managed.Environment);
        if (result.ExitCode != 0 || !File.Exists(tmp) || new FileInfo(tmp).Length == 0) throw new InvalidDataException($"Tải Drive file {fileId} thất bại: {result.StandardError.Trim()}");
        File.Move(tmp, destination, overwrite: true);
        var sha = await HashAsync(destination, job.CancellationToken);
        WriteStamp(destination, new FileInfo(destination).Length, sha);
    }

    private async Task DownloadViaHttpAsync(string url, string destination, AppJob job)
    {
        var tmp = destination + ".partial";
        TryDelete(tmp);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("BiliSubStudio/4-CSharp-NGHI");
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, job.CancellationToken);
        resp.EnsureSuccessStatusCode();
        await using var src = await resp.Content.ReadAsStreamAsync(job.CancellationToken);
        await using var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous);
        await src.CopyToAsync(dst, job.CancellationToken);
        await dst.FlushAsync(job.CancellationToken);
        File.Move(tmp, destination, overwrite: true);
        var sha = await HashAsync(destination, job.CancellationToken);
        WriteStamp(destination, new FileInfo(destination).Length, sha);
    }

    private static void WriteStamp(string path, long size, string sha) => File.WriteAllText(path + ".verified", $"{sha}|{size}|{new FileInfo(path).LastWriteTimeUtc.Ticks}\n", new UTF8Encoding(false));
    private static string HashFile(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken) { await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan); return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken)); }
    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }
    public void Dispose() { _gate.Dispose(); _http.Dispose(); }
    private sealed record RuntimeInstallManifest(int Schema, string Python, string EngineVersion, string WorkerSha256);
}
