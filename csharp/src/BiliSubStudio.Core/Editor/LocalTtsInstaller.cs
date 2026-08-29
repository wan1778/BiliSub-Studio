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

internal sealed record LocalTtsRuntime(string Python, string Worker, string Model, string Config, IReadOnlyDictionary<string, string?> Environment, int SampleRate);
internal sealed record TtsModelFile(string Name, long Size, string Sha256, string DriveId);

internal sealed class LocalTtsInstaller : IDisposable
{
    internal const string EngineVersion = "nghi-tts-1.0.0";
    internal const string Engine = "nghi-tts";
    internal const string Voice = "ngoc_huyen";
    internal const string ModelRepository = "nghimestudio/nghitts";
    // Drive objects are mutable; reviewed content hashes pin identity.
    internal const string ModelRevision = "2140977786d76d834736c059dacfa553d4931dac2b2c7aaaea438bb2aa9da697";
    internal const string ConfigSha256 = "971f57f8d504223fee5b40d664f503cf769baf7db21f7d2ae0554a75d07de2f8";
    internal const string TimingAlgorithm = "whole-cue-piper-rate-v8";
    internal const string VoiceRevision = ModelRevision + ":" + ConfigSha256 + ":" + TimingAlgorithm;
    internal static readonly IReadOnlyList<string> AvailableVoices = Array.AsReadOnly(new[] { Voice });
    internal static string CanonicalVoiceId(string voice) => voice.Trim().ToLowerInvariant().Replace('-', '_');
    internal static readonly IReadOnlyList<TtsModelFile> ModelFiles = Array.AsReadOnly(new[]
    {
        new TtsModelFile("ngoc_huyen.onnx", 63_516_050, ModelRevision, "12HNgJmBY3GiNCcFBRpHxYFv-qbE-jEv7"),
        new TtsModelFile("ngoc_huyen.onnx.json", 4_855, ConfigSha256, "1p-oDIiuhecInjgys4bqsaeOf794OFcHC"),
    });
    private const string Packages = "piper-tts==1.7.0;onnxruntime==1.22.1;vietnormalizer==0.2.3;numpy==2.5.2;gdown==6.1.0";
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };
    private readonly AppPaths _paths;
    private readonly OcrInstaller _pythonBootstrap;
    private readonly ProcessRunner _processes;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _lastError;

    public LocalTtsInstaller(AppPaths paths, OcrInstaller pythonBootstrap, ProcessRunner processes)
    {
        _paths = paths;
        _pythonBootstrap = pythonBootstrap;
        _processes = processes;
    }

    private string Root => Path.Combine(_paths.Tools, "TTS");
    private string RuntimeRoot => Path.Combine(Root, "runtime-piper");
    private string VenvRoot => Path.Combine(RuntimeRoot, "venv");
    private string Python => Path.Combine(VenvRoot, "Scripts", "python.exe");
    private string RuntimeManifest => Path.Combine(RuntimeRoot, "install.json");
    private string Worker => Path.Combine(Root, "worker.py");
    private string BundledWorker => Path.Combine(AppContext.BaseDirectory, "Assets", "TTS", "worker.py");
    private string ModelRoot => Path.Combine(Root, "Models", "nghi-vi-" + ModelRevision[..12]);

    public LocalTtsStatus Status
    {
        get
        {
            try
            {
                return new LocalTtsStatus(RuntimeMatches(), ModelFiles.All(FileMatchesStamp), Engine, EngineVersion,
                    Voice, ModelFiles.Sum(file => file.Size), _lastError);
            }
            catch (Exception error) { return new LocalTtsStatus(false, false, Engine, EngineVersion, Voice, 0, error.Message); }
        }
    }

    public Task<LocalTtsRuntime> PrepareAsync(AppJob job, double progressCeiling = 98) => PrepareAsync(job, Voice, progressCeiling);

    public async Task<LocalTtsRuntime> PrepareAsync(AppJob job, string voice, double progressCeiling = 98)
    {
        voice = CanonicalVoiceId(string.IsNullOrWhiteSpace(voice) ? Voice : voice);
        if (!AvailableVoices.Contains(voice, StringComparer.Ordinal))
            throw new InvalidDataException($"Giọng {voice} chưa có model NGHI được xác minh.");
        progressCeiling = Math.Clamp(progressCeiling, 1, 98);
        double Progress(double value) => Math.Clamp(value, 0, 100) / 100d * progressCeiling;
        await _gate.WaitAsync(job.CancellationToken);
        try
        {
            _lastError = null;
            Directory.CreateDirectory(Root);
            var worker = await EnsureWorkerAsync(job.CancellationToken);
            if (!RuntimeMatches())
            {
                job.Set("tts-python", Progress(2), "Đang kiểm tra runtime Piper/NGHI local...");
                Directory.CreateDirectory(RuntimeRoot);
                var managed = await _pythonBootstrap.EnsurePrivatePythonAsync(VenvRoot, job.CancellationToken);
                var arguments = new List<string> { "pip", "install", "--python", managed.Python, "--no-python-downloads", "--no-config" };
                arguments.AddRange(Packages.Split(';'));
                var install = await _processes.RunAsync(managed.Uv, arguments, job.CancellationToken, managed.Environment);
                if (install.ExitCode != 0) throw new InvalidOperationException("Cài runtime NGHI-TTS: " + install.StandardError.Trim());
                var verify = await _processes.RunAsync(managed.Python,
                    ["-I", "-X", "utf8", "-c", "import sys,importlib.metadata as m; from piper import PiperVoice; from vietnormalizer import VietnameseNormalizer; import numpy,onnxruntime,gdown; assert all(m.version(p.split('==')[0]) == p.split('==')[1] for p in sys.argv[1].split(';')); print('nghi-ok')", Packages],
                    job.CancellationToken, managed.Environment);
                if (verify.ExitCode != 0) throw new InvalidOperationException("Runtime NGHI-TTS không vượt kiểm tra: " + verify.StandardError.Trim());
                await WriteRuntimeManifestAsync(worker, job.CancellationToken);
            }
            Directory.CreateDirectory(ModelRoot);
            foreach (var file in ModelFiles)
            {
                job.Set("tts-model", Progress(35), $"Đang xác minh NGHI Ngọc Huyền · {file.Name}...");
                await DownloadVerifiedAsync(file, job);
            }
            var config = Path.Combine(ModelRoot, ModelFiles[1].Name);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(config, job.CancellationToken));
            var sampleRate = document.RootElement.GetProperty("audio").GetProperty("sample_rate").GetInt32();
            if (sampleRate != 22050) throw new InvalidDataException("Config NGHI sai sample rate đã khóa.");
            if (!Status.Ready) throw new InvalidOperationException("Voice NGHI-TTS chưa hoàn chỉnh sau khi cài.");
            job.Set("tts-ready", Progress(99), "Ngọc Huyền NGHI-TTS đã xác minh, sẵn sàng tạo speech local.");
            return new LocalTtsRuntime(Python, worker, Path.Combine(ModelRoot, ModelFiles[0].Name), config,
                new Dictionary<string, string?> { ["HF_HUB_OFFLINE"] = "1", ["TRANSFORMERS_OFFLINE"] = "1" }, sampleRate);
        }
        catch (Exception error) when (error is not OperationCanceledException) { _lastError = error.Message; throw; }
        finally { _gate.Release(); }
    }

    private async Task<string> EnsureWorkerAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(BundledWorker)) throw new FileNotFoundException("Thiếu worker NGHI-TTS đã đóng gói.", BundledWorker);
        var sourceHash = await HashAsync(BundledWorker, cancellationToken);
        if (!File.Exists(Worker) || await HashAsync(Worker, cancellationToken) != sourceHash)
        {
            var temporary = Worker + ".tmp-" + Guid.NewGuid().ToString("N");
            try { File.Copy(BundledWorker, temporary); File.Move(temporary, Worker, overwrite: true); }
            finally { TryDelete(temporary); }
        }
        return Worker;
    }

    private bool RuntimeMatches()
    {
        try
        {
            if (!File.Exists(Python) || !File.Exists(Worker) || !File.Exists(BundledWorker) || !File.Exists(RuntimeManifest)) return false;
            var manifest = JsonSerializer.Deserialize<RuntimeInstallManifest>(File.ReadAllText(RuntimeManifest), Json);
            return manifest is not null && manifest.Schema == 3 && manifest.Python == OcrInstaller.PythonVersion
                && manifest.Packages == Packages && manifest.EngineVersion == EngineVersion
                && manifest.WorkerSha256 == HashFile(Worker) && manifest.WorkerSha256 == HashFile(BundledWorker);
        }
        catch { return false; }
    }

    private async Task WriteRuntimeManifestAsync(string worker, CancellationToken cancellationToken)
    {
        var manifest = new RuntimeInstallManifest(3, OcrInstaller.PythonVersion, EngineVersion, Packages, await HashAsync(worker, cancellationToken));
        var temporary = RuntimeManifest + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(manifest, Json) + "\n", new UTF8Encoding(false), cancellationToken);
            File.Move(temporary, RuntimeManifest, overwrite: true);
        }
        finally { TryDelete(temporary); }
    }

    private async Task DownloadVerifiedAsync(TtsModelFile file, AppJob job)
    {
        var destination = Path.Combine(ModelRoot, file.Name);
        if (await MatchesAsync(destination, file, job.CancellationToken)) { WriteStamp(destination, file); return; }
        var partial = destination + ".partial";
        // gdown handles Drive confirmation pages. Only pinned size AND hash allow promotion.
        var result = await _processes.RunAsync(Python,
            ["-I", "-X", "utf8", "-c", "import gdown,sys; p=gdown.download(id=sys.argv[1],output=sys.argv[2],quiet=True,use_cookies=False,resume=True); sys.exit(0 if p else 1)", file.DriveId, partial],
            job.CancellationToken);
        if (result.ExitCode != 0) throw new IOException("Tải NGHI model: " + result.StandardError.Trim());
        if (!await MatchesAsync(partial, file, job.CancellationToken))
        {
            TryDelete(partial);
            throw new InvalidDataException($"NGHI {file.Name} sai size/SHA-256; không được dùng model thay thế.");
        }
        job.CancellationToken.ThrowIfCancellationRequested();
        File.Move(partial, destination, overwrite: true);
        WriteStamp(destination, file);
    }

    internal static async Task<bool> MatchesAsync(string path, TtsModelFile file, CancellationToken cancellationToken) =>
        File.Exists(path) && new FileInfo(path).Length == file.Size && await HashAsync(path, cancellationToken) == file.Sha256;

    private bool FileMatchesStamp(TtsModelFile file)
    {
        var path = Path.Combine(ModelRoot, file.Name);
        return File.Exists(path) && new FileInfo(path).Length == file.Size && File.Exists(path + ".verified")
            && File.ReadAllText(path + ".verified") == Stamp(path, file);
    }
    private static string Stamp(string path, TtsModelFile file) => $"{file.Sha256}|{file.Size}|{new FileInfo(path).LastWriteTimeUtc.Ticks}\n";
    private static void WriteStamp(string path, TtsModelFile file) => File.WriteAllText(path + ".verified", Stamp(path, file), new UTF8Encoding(false));
    private static string HashFile(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken) { await using var stream = File.OpenRead(path); return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken)); }
    private static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } }
    public void Dispose() => _gate.Dispose();
    private sealed record RuntimeInstallManifest(int Schema, string Python, string EngineVersion, string Packages, string WorkerSha256);
}
