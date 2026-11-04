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

internal sealed class LocalTtsInstaller : IDisposable
{
    // VieNeu v3 Turbo is the only local speech engine. The pinned model
    // revision and timing policy are shared with internal/vieneu/worker.py;
    // the C# revision is written into the input manifest and verified again
    // on every worker event/result.
    internal const string Engine = "vieneu-tts";
    internal const string EngineVersion = "vieneu-v3-turbo-1";
    internal const string Voice = TtsVoiceCatalog.DefaultVoice;
    internal const string ModelRevision = "61b85e3d937fbbacb387714180e8182823512523";
    internal const string TimingAlgorithm = "whole-cue-vieneu-natural-first-v1";
    internal const string VoiceRevision = ModelRevision + ":" + TimingAlgorithm;
    internal const string Packages = "torch==2.8.0+cu128;torchaudio==2.8.0+cu128;transformers==4.57.6;vieneu==3.8.3;soxr==1.1.0;numpy==2.2.6;vietnormalizer==0.2.3";
    internal const string CudaIndex = "https://download.pytorch.org/whl/cu128";
    // Preset labels/merchandising live in TtsVoiceCatalog (single source for
    // UI + installer + worker); the installer only adds canonical matching.
    internal static readonly IReadOnlyList<string> AvailableVoices = Array.AsReadOnly(
        TtsVoiceCatalog.VoiceOptions.Select(option => option.Tag).ToArray());
    internal static string CanonicalVoiceId(string voice) => voice.Trim().ToLowerInvariant().Replace('-', '_');
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
    private string RuntimeRoot => Path.Combine(Root, "runtime-vieneu");
    private string VenvRoot => Path.Combine(RuntimeRoot, "venv");
    private string Python => Path.Combine(VenvRoot, "Scripts", "python.exe");
    private string RuntimeManifest => Path.Combine(RuntimeRoot, "install-vieneu.json");
    private string Worker => Path.Combine(Root, "worker-vieneu.py");
    private string BundledWorker => Path.Combine(AppContext.BaseDirectory, "Assets", "TTS", "worker-vieneu.py");
    private string ModelRoot => Path.Combine(Root, "vieneu-models");

    public LocalTtsStatus Status
    {
        get
        {
            try
            {
                var runtime = VieneuRuntimeMatches();
                var voice = VieneuBackboneReady();
                return new LocalTtsStatus(runtime, voice, Engine, EngineVersion, Voice, 0, _lastError);
            }
            catch (Exception error) { return new LocalTtsStatus(false, false, Engine, EngineVersion, Voice, 0, error.Message); }
        }
    }

    // VieNeu venv: torch-cu128 + SDK + pinned HF backbone/voice presets. GPU is
    // mandatory (the worker fails fast when CUDA is unavailable).
    public async Task<LocalTtsRuntime> PrepareAsync(AppJob job, string voice, double progressCeiling = 98)
    {
        voice = CanonicalVoiceId(string.IsNullOrWhiteSpace(voice) ? Voice : voice);
        if (!AvailableVoices.Contains(voice, StringComparer.Ordinal))
            throw new InvalidDataException($"Giọng {voice} chưa có preset VieNeu được xác minh.");
        progressCeiling = Math.Clamp(progressCeiling, 1, 98);
        double Progress(double value) => Math.Clamp(value, 0, 100) / 100d * progressCeiling;
        await _gate.WaitAsync(job.CancellationToken);
        try
        {
            _lastError = null;
            Directory.CreateDirectory(Root);
            var worker = await EnsureWorkerAsync(job.CancellationToken);
            if (!VieneuRuntimeMatches())
            {
                job.Set("tts-python", Progress(2), "Đang kiểm tra runtime VieNeu local (torch CUDA + SDK)...");
                Directory.CreateDirectory(RuntimeRoot);
                var managed = await _pythonBootstrap.EnsurePrivatePythonAsync(VenvRoot, job.CancellationToken);
                var arguments = new List<string> { "pip", "install", "--python", managed.Python, "--no-python-downloads", "--no-config",
                    "--extra-index-url", CudaIndex };
                arguments.AddRange(Packages.Split(';'));
                var install = await _processes.RunAsync(managed.Uv, arguments, job.CancellationToken, managed.Environment);
                if (install.ExitCode != 0) throw new InvalidOperationException("Cài runtime VieNeu: " + install.StandardError.Trim());
                var verify = await _processes.RunAsync(managed.Python,
                    ["-I", "-X", "utf8", "-c", "import importlib.metadata as m,torch;from vieneu import Vieneu;import soxr,numpy;from vietnormalizer import VietnameseNormalizer;assert torch.cuda.is_available(),'no CUDA';assert m.version('torch')=='2.8.0+cu128';assert m.version('torchaudio')=='2.8.0+cu128';assert m.version('transformers')=='4.57.6';assert m.version('vieneu')=='3.8.3';assert m.version('soxr')=='1.1.0';assert m.version('numpy')=='2.2.6';assert m.version('vietnormalizer')=='0.2.3';print('vieneu-ok')"],
                    job.CancellationToken, managed.Environment);
                if (verify.ExitCode != 0) throw new InvalidOperationException("Runtime VieNeu không vượt kiểm tra: " + verify.StandardError.Trim());
            }
            if (!VieneuRuntimeMatches()) await WriteRuntimeManifestAsync(worker, job.CancellationToken);
            await EnsureVieneuModelsAsync(voice, job, Progress);
            if (!VieneuRuntimeMatches() || !VieneuBackboneReady())
                throw new InvalidOperationException(_lastError ?? "Voice VieNeu chưa hoàn chỉnh sau khi cài.");
            job.Set("tts-ready", Progress(99), $"Preset VieNeu {voice} đã xác minh, sẵn sàng tạo speech local (GPU).");
            return new LocalTtsRuntime(Python, worker, ModelRoot, VieneuBackboneConfig(),
                new Dictionary<string, string?> { ["HF_HOME"] = ModelRoot, ["HF_HUB_OFFLINE"] = "1", ["TRANSFORMERS_OFFLINE"] = "1" }, 22050);
        }
        catch (Exception error) when (error is not OperationCanceledException) { _lastError = error.Message; throw; }
        finally { _gate.Release(); }
    }

    private async Task<string> EnsureWorkerAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(BundledWorker)) throw new FileNotFoundException("Thiếu worker VieNeu đã đóng gói.", BundledWorker);
        var sourceHash = await HashAsync(BundledWorker, cancellationToken);
        if (!File.Exists(Worker) || await HashAsync(Worker, cancellationToken) != sourceHash)
        {
            var temporary = Worker + ".tmp-" + Guid.NewGuid().ToString("N");
            try { File.Copy(BundledWorker, temporary); File.Move(temporary, Worker, overwrite: true); }
            finally { TryDelete(temporary); }
        }
        return Worker;
    }

    private bool VieneuRuntimeMatches()
    {
        try
        {
            if (!VieneuRuntimeEnvironmentMatches() || !File.Exists(Worker) || !File.Exists(BundledWorker)) return false;
            var manifest = JsonSerializer.Deserialize<RuntimeInstallManifest>(File.ReadAllText(RuntimeManifest), Json);
            return manifest is not null && manifest.WorkerSha256 == HashFile(Worker)
                && manifest.WorkerSha256 == HashFile(BundledWorker);
        }
        catch { return false; }
    }

    private bool VieneuRuntimeEnvironmentMatches()
    {
        try
        {
            if (!File.Exists(Python) || !File.Exists(RuntimeManifest)) return false;
            var manifest = JsonSerializer.Deserialize<RuntimeInstallManifest>(File.ReadAllText(RuntimeManifest), Json);
            return manifest is not null && manifest.Schema == 3 && manifest.Python == OcrInstaller.PythonVersion
                && manifest.Packages == Packages && manifest.EngineVersion == EngineVersion;
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

    private string VieneuBackboneConfig() => Path.Combine(ModelRoot, "hub",
        "models--pnnbao-ump--VieNeu-TTS-v3-Turbo", "snapshots", ModelRevision, "update", "config.json");

    private string VieneuBackboneWeights() => Path.Combine(ModelRoot, "hub",
        "models--pnnbao-ump--VieNeu-TTS-v3-Turbo", "snapshots", ModelRevision, "update", "model.safetensors");

    private bool VieneuBackboneReady()
    {
        try
        {
            return File.Exists(VieneuBackboneWeights())
                && EffectiveFileLength(VieneuBackboneWeights()) >= 100_000_000
                && File.Exists(VieneuBackboneConfig());
        }
        catch { return false; }
    }

    // HF cache uses symlinks: FileInfo.Length only reads the link metadata
    // (always 0). Open a read handle to get the real target blob size.
    private static long EffectiveFileLength(string path)
    {
        try { using var stream = File.OpenRead(path); return stream.Length; }
        catch { return -1; }
    }

    // HF model (pinned revision; downloadable without a token). The SDK
    // resolves its own cache; the installer verifies the backbone files and
    // runs one synth smoke test for the requested preset, so a missing or
    // zero-byte weight fails loudly instead of running blind.
    private async Task EnsureVieneuModelsAsync(string voice, AppJob job, Func<double, double> progress)
    {
        Directory.CreateDirectory(ModelRoot);
        var weights = VieneuBackboneWeights();
        if (!File.Exists(weights) || EffectiveFileLength(weights) < 100_000_000)
        {
            job.Set("tts-model", progress(35), "Đang tải backbone VieNeu v3 Turbo (~250MB)...");
            var fetch = await _processes.RunAsync(Python,
                ["-I", "-X", "utf8", "-c", "from vieneu import Vieneu; v=Vieneu(); print(len(v.list_preset_voices()))"],
                job.CancellationToken,
                new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["HF_HOME"] = ModelRoot });
            if (fetch.ExitCode != 0) throw new IOException("Tải model VieNeu: " + fetch.StandardError.Trim());
        }
        if (!File.Exists(weights) || EffectiveFileLength(weights) < 100_000_000
            || !File.Exists(VieneuBackboneConfig()))
            throw new InvalidDataException("Backbone VieNeu chưa đầy đủ sau khi tải.");
        // 30/09 (zero-byte file incident): the HF cache is all symlinks;
        // FileInfo.Length does not follow the link and always reads 0.
        foreach (var small in new[] { "config.json", "tokenizer.json", "tokenizer_config.json", "special_tokens_map.json" })
        {
            var path = Path.Combine(Path.GetDirectoryName(VieneuBackboneConfig())!, small);
            if (!File.Exists(path) || EffectiveFileLength(path) <= 0)
                throw new InvalidDataException($"Model VieNeu thiếu nội dung: {small} (0 byte); xóa cache HF của file đó rồi thử lại.");
        }
        job.Set("tts-model", progress(60), $"Đang kiểm tra preset giọng {voice} trên GPU...");
        var smoke = await _processes.RunAsync(Python,
            ["-I", "-X", "utf8", "-c",
             "import sys,unicodedata\n" +
             "from vieneu import Vieneu\n" +
             "v=Vieneu()\n" +
             "def fold(s):\n" +
             " o=[]\n" +
             " for ch in (s or '').strip().replace('Đ','D').replace('đ','d'):\n" +
             "  d=unicodedata.normalize('NFD',ch)\n" +
             "  for p in d:\n" +
             "   o.append('' if unicodedata.category(p)=='Mn' else ('_' if p in (' ','-') else p.lower()))\n" +
             " return ''.join(o)\n" +
             "want=sys.argv[1].strip()\n" +
             "slug=want[7:] if want.lower().startswith('vieneu_') else want\n" +
             "voices=list(v.list_preset_voices())\n" +
             "label=next((l for _,l in voices if fold(l)==fold(slug)),None)\n" +
             "assert label,'unknown preset: '+want\n" +
             "a=v.infer('Xin chào.',voice=label,apply_watermark=False)\n" +
             "assert len(a)>1000\n" +
             "print('voice-ok')", voice],
            job.CancellationToken,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["HF_HOME"] = ModelRoot });
        if (smoke.ExitCode != 0) throw new InvalidDataException($"Preset VieNeu {voice} không chạy được trên GPU: " + smoke.StandardError.Trim());
    }

    private static string HashFile(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken) { await using var stream = File.OpenRead(path); return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken)); }
    private static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } }
    public void Dispose() => _gate.Dispose();
    private sealed record RuntimeInstallManifest(int Schema, string Python, string EngineVersion, string Packages, string WorkerSha256);
}
