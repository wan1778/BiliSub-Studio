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
    internal const string TimingAlgorithm = "whole-cue-piper-natural-first-v17";
    internal const string VoiceRevision = ModelRevision + ":" + ConfigSha256 + ":" + TimingAlgorithm;
    internal static readonly IReadOnlyList<string> AvailableVoices = Array.AsReadOnly(
        TtsVoiceCatalog.VoiceOptions.Select(option => option.Tag).ToArray());
    internal static string CanonicalVoiceId(string voice) => voice.Trim().ToLowerInvariant().Replace('-', '_');

    // VieNeu v3 Turbo pilot (engine thứ 2, chạy song song NGHI): GPU bắt buộc,
    // 25 preset. C# giữ label gốc có dấu; worker khớp không phân biệt hoa/thường.
    internal const string VieneuEngine = "vieneu-tts";
    internal const string VieneuEngineVersion = "vieneu-v3-turbo-1";
    internal const string VieneuModelRevision = "61b85e3d937fbbacb387714180e8182823512523";
    internal const string VieneuTimingAlgorithm = "whole-cue-vieneu-natural-first-v1";
    internal const string VieneuVoiceRevision = VieneuModelRevision + ":" + VieneuTimingAlgorithm;
    internal const string VieneuPackages = "torch==2.8.0+cu128;torchaudio==2.8.0+cu128;transformers==4.57.6;vieneu==3.8.3;soxr==1.1.0;numpy==2.2.6;vietnormalizer==0.2.3";
    internal const string VieneuCudaIndex = "https://download.pytorch.org/whl/cu128";
    // Preset labels/merchandising live in TtsVoiceCatalog (single source for
    // UI + installer + worker); installer only adds canonical matching.
    internal static bool IsVieneuVoice(string voice) => TtsVoiceCatalog.IsVieneuVoice(voice);
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
    private string VieneuRuntimeRoot => Path.Combine(Root, "runtime-vieneu");
    private string VieneuVenvRoot => Path.Combine(VieneuRuntimeRoot, "venv");
    private string VieneuPython => Path.Combine(VieneuVenvRoot, "Scripts", "python.exe");
    private string VieneuRuntimeManifest => Path.Combine(VieneuRuntimeRoot, "install-vieneu.json");
    private string VieneuWorker => Path.Combine(Root, "worker-vieneu.py");
    private string VieneuBundledWorker => Path.Combine(AppContext.BaseDirectory, "Assets", "TTS", "worker-vieneu.py");
    private string VieneuModelRoot => Path.Combine(Root, "vieneu-models");

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
            throw new InvalidDataException($"Giọng {voice} chưa có model được xác minh.");
        if (IsVieneuVoice(voice)) return await PrepareVieneuAsync(job, voice, progressCeiling);
        progressCeiling = Math.Clamp(progressCeiling, 1, 98);
        double Progress(double value) => Math.Clamp(value, 0, 100) / 100d * progressCeiling;
        await _gate.WaitAsync(job.CancellationToken);
        try
        {
            _lastError = null;
            Directory.CreateDirectory(Root);
            var worker = await EnsureWorkerAsync(job.CancellationToken);
            if (!RuntimeEnvironmentMatches())
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
            }
            // A bundled timing-policy update changes only worker.py. Refresh its
            // verified manifest without reinstalling the unchanged pinned Python
            // packages; RuntimeEnvironmentMatches already proved that runtime.
            if (!RuntimeMatches()) await WriteRuntimeManifestAsync(worker, job.CancellationToken);
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

    // VieNeu pilot: venv torch-cu128 + SDK + models HF (rev đã pin) riêng biệt,
    // không đụng runtime Piper. GPU bắt buộc (worker fail-fast nếu không CUDA).
    public async Task<LocalTtsRuntime> PrepareVieneuAsync(AppJob job, string voice, double progressCeiling = 98)
    {
        voice = CanonicalVoiceId(voice);
        if (!IsVieneuVoice(voice)) throw new InvalidDataException($"Giọng {voice} không phải preset VieNeu.");
        progressCeiling = Math.Clamp(progressCeiling, 1, 98);
        double Progress(double value) => Math.Clamp(value, 0, 100) / 100d * progressCeiling;
        await _gate.WaitAsync(job.CancellationToken);
        try
        {
            _lastError = null;
            Directory.CreateDirectory(Root);
            var worker = await EnsureWorkerVieneuAsync(job.CancellationToken);
            if (!VieneuRuntimeMatches())
            {
                job.Set("tts-python", Progress(2), "Đang kiểm tra runtime VieNeu local (torch CUDA + SDK)...");
                Directory.CreateDirectory(VieneuRuntimeRoot);
                var managed = await _pythonBootstrap.EnsurePrivatePythonAsync(VieneuVenvRoot, job.CancellationToken);
                var arguments = new List<string> { "pip", "install", "--python", managed.Python, "--no-python-downloads", "--no-config",
                    "--extra-index-url", VieneuCudaIndex };
                arguments.AddRange(VieneuPackages.Split(';'));
                var install = await _processes.RunAsync(managed.Uv, arguments, job.CancellationToken, managed.Environment);
                if (install.ExitCode != 0) throw new InvalidOperationException("Cài runtime VieNeu: " + install.StandardError.Trim());
                var verify = await _processes.RunAsync(managed.Python,
                    ["-I", "-X", "utf8", "-c", "import importlib.metadata as m,torch;from vieneu import Vieneu;import soxr,numpy;from vietnormalizer import VietnameseNormalizer;assert torch.cuda.is_available(),'no CUDA';assert m.version('torch')=='2.8.0+cu128';assert m.version('torchaudio')=='2.8.0+cu128';assert m.version('transformers')=='4.57.6';assert m.version('vieneu')=='3.8.3';assert m.version('soxr')=='1.1.0';assert m.version('numpy')=='2.2.6';assert m.version('vietnormalizer')=='0.2.3';print('vieneu-ok')"],
                    job.CancellationToken, managed.Environment);
                if (verify.ExitCode != 0) throw new InvalidOperationException("Runtime VieNeu không vượt kiểm tra: " + verify.StandardError.Trim());
            }
            if (!VieneuRuntimeMatches()) await WriteVieneuManifestAsync(worker, job.CancellationToken);
            await EnsureVieneuModelsAsync(voice, job, Progress);
            if (!VieneuStatusReady()) throw new InvalidOperationException(VieneuLastError() ?? "Voice VieNeu chưa hoàn chỉnh sau khi cài.");
            job.Set("tts-ready", Progress(99), $"Preset VieNeu {voice} đã xác minh, sẵn sàng tạo speech local (GPU).");
            return new LocalTtsRuntime(VieneuPython, worker, VieneuModelRoot, VieneuBackboneConfig(),
                new Dictionary<string, string?> { ["HF_HOME"] = VieneuModelRoot, ["HF_HUB_OFFLINE"] = "1", ["TRANSFORMERS_OFFLINE"] = "1" }, 22050);
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

    private async Task<string> EnsureWorkerVieneuAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(VieneuBundledWorker)) throw new FileNotFoundException("Thiếu worker VieNeu đã đóng gói.", VieneuBundledWorker);
        var sourceHash = await HashAsync(VieneuBundledWorker, cancellationToken);
        if (!File.Exists(VieneuWorker) || await HashAsync(VieneuWorker, cancellationToken) != sourceHash)
        {
            var temporary = VieneuWorker + ".tmp-" + Guid.NewGuid().ToString("N");
            try { File.Copy(VieneuBundledWorker, temporary); File.Move(temporary, VieneuWorker, overwrite: true); }
            finally { TryDelete(temporary); }
        }
        return VieneuWorker;
    }

    private bool VieneuRuntimeMatches()
    {
        try
        {
            if (!VieneuRuntimeEnvironmentMatches() || !File.Exists(VieneuWorker) || !File.Exists(VieneuBundledWorker)) return false;
            var manifest = JsonSerializer.Deserialize<RuntimeInstallManifest>(File.ReadAllText(VieneuRuntimeManifest), Json);
            return manifest is not null && manifest.WorkerSha256 == HashFile(VieneuWorker)
                && manifest.WorkerSha256 == HashFile(VieneuBundledWorker);
        }
        catch { return false; }
    }

    private bool VieneuRuntimeEnvironmentMatches()
    {
        try
        {
            if (!File.Exists(VieneuPython) || !File.Exists(VieneuRuntimeManifest)) return false;
            var manifest = JsonSerializer.Deserialize<RuntimeInstallManifest>(File.ReadAllText(VieneuRuntimeManifest), Json);
            return manifest is not null && manifest.Schema == 3 && manifest.Python == OcrInstaller.PythonVersion
                && manifest.Packages == VieneuPackages && manifest.EngineVersion == VieneuEngineVersion;
        }
        catch { return false; }
    }

    private async Task WriteVieneuManifestAsync(string worker, CancellationToken cancellationToken)
    {
        var manifest = new RuntimeInstallManifest(3, OcrInstaller.PythonVersion, VieneuEngineVersion, VieneuPackages, await HashAsync(worker, cancellationToken));
        var temporary = VieneuRuntimeManifest + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(manifest, Json) + "\n", new UTF8Encoding(false), cancellationToken);
            File.Move(temporary, VieneuRuntimeManifest, overwrite: true);
        }
        finally { TryDelete(temporary); }
    }

    private string VieneuBackboneConfig() => Path.Combine(VieneuModelRoot, "hub",
        "models--pnnbao-ump--VieNeu-TTS-v3-Turbo", "snapshots", VieneuModelRevision, "update", "config.json");

    private string VieneuBackboneWeights() => Path.Combine(VieneuModelRoot, "hub",
        "models--pnnbao-ump--VieNeu-TTS-v3-Turbo", "snapshots", VieneuModelRevision, "update", "model.safetensors");

    // Cache HF dùng symlink: FileInfo.Length chỉ đọc metadata của link (luôn 0).
    // Mở handle đọc mới ra size thật của blob đích.
    private static long EffectiveFileLength(string path)
    {
        try { using var stream = File.OpenRead(path); return stream.Length; }
        catch { return -1; }
    }

    // Model HF (rev đã pin B0/B1: tải được không token). SDK tự resolve cache;
    // installer chỉ verify backbone + 1 synth smoke test giọng yêu cầu (đồng
    // thời prefetch file giọng đó nếu thiếu). Thiếu -> throw rõ, không chạy mù.
    private async Task EnsureVieneuModelsAsync(string voice, AppJob job, Func<double, double> progress)
    {
        Directory.CreateDirectory(VieneuModelRoot);
        var weights = VieneuBackboneWeights();
        if (!File.Exists(weights) || EffectiveFileLength(weights) < 100_000_000)
        {
            job.Set("tts-model", progress(35), "Đang tải backbone VieNeu v3 Turbo (~250MB)...");
            var fetch = await _processes.RunAsync(VieneuPython,
                ["-I", "-X", "utf8", "-c", "from vieneu import Vieneu; v=Vieneu(); print(len(v.list_preset_voices()))"],
                job.CancellationToken,
                new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["HF_HOME"] = VieneuModelRoot });
            if (fetch.ExitCode != 0) throw new IOException("Tải model VieNeu: " + fetch.StandardError.Trim());
        }
        if (!File.Exists(weights) || EffectiveFileLength(weights) < 100_000_000
            || !File.Exists(VieneuBackboneConfig()))
            throw new InvalidDataException("Backbone VieNeu chưa đầy đủ sau khi tải.");
        // 30/09 (vụ file 0 byte): cache HF toàn symlink; FileInfo.Length KHÔNG
        // đi qua link nên luôn đọc 0 -> EffectiveFileLength mở handle đọc size thật.
        // Hỏng file nào báo đúng tên file đó.
        foreach (var small in new[] { "config.json", "tokenizer.json", "tokenizer_config.json", "special_tokens_map.json" })
        {
            var path = Path.Combine(Path.GetDirectoryName(VieneuBackboneConfig())!, small);
            if (!File.Exists(path) || EffectiveFileLength(path) <= 0)
                throw new InvalidDataException($"Model VieNeu thiếu nội dung: {small} (0 byte); xóa cache HF của file đó rồi thử lại.");
        }
        job.Set("tts-model", progress(60), $"Đang kiểm tra preset giọng {voice} trên GPU...");
        var smoke = await _processes.RunAsync(VieneuPython,
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
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["HF_HOME"] = VieneuModelRoot });
        if (smoke.ExitCode != 0) throw new InvalidDataException($"Preset VieNeu {voice} không chạy được trên GPU: " + smoke.StandardError.Trim());
    }

    private bool VieneuStatusReady()
    {
        try { return VieneuRuntimeMatches() && File.Exists(VieneuBackboneWeights()); }
        catch { return false; }
    }

    private string? VieneuLastError() => _lastError;

    private bool RuntimeMatches()
    {
        try
        {
            if (!RuntimeEnvironmentMatches() || !File.Exists(Worker) || !File.Exists(BundledWorker)) return false;
            var manifest = JsonSerializer.Deserialize<RuntimeInstallManifest>(File.ReadAllText(RuntimeManifest), Json);
            return manifest is not null && manifest.WorkerSha256 == HashFile(Worker)
                && manifest.WorkerSha256 == HashFile(BundledWorker);
        }
        catch { return false; }
    }

    private bool RuntimeEnvironmentMatches()
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
