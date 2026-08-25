using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BiliSubStudio.Core.Configuration;
using BiliSubStudio.Core.Hardware;
using BiliSubStudio.Core.IO;
using BiliSubStudio.Core.Jobs;
using BiliSubStudio.Core.Processes;

namespace BiliSubStudio.Core.Editor;

public enum EditorTranslationModelMode
{
    Quality,
    Fast,
}

public sealed record LocalTranslationStatus(
    bool RuntimeReady,
    bool ModelReady,
    string ModelName,
    long ModelBytes,
    string SkillName,
    string SkillSha256);

public sealed record EditorTranslationRequest(
    string ProjectId,
    EditorSubtitleSource Source,
    string OutputDirectory,
    string OutputFileName,
    bool ForceFresh = false,
    EditorTranslationModelMode ModelMode = EditorTranslationModelMode.Quality);

public sealed record EditorTranslationResult(
    IReadOnlyList<EditorSubtitleCue> Cues,
    string OutputPath,
    int RestoredCueCount,
    string ModelName,
    string SkillSha256);

public sealed class LocalSubtitleTranslationService : IDisposable
{
    internal const string RuntimeVersion = "b10566";
    internal const string RuntimeUrl = "https://github.com/ggml-org/llama.cpp/releases/download/b10566/llama-b10566-bin-win-vulkan-x64.zip";
    internal const long RuntimeArchiveBytes = 34_937_857;
    internal const string RuntimeArchiveSha256 = "68e15a0a0d07df55a695ec4d81465cf57400431d54ae19fadcb51dc919724042";
    internal const string ModelName = "Qwen3-8B Q4_K_M";
    internal const string ModelUrl = "https://huggingface.co/Qwen/Qwen3-8B-GGUF/resolve/7c41481f57cb95916b40956ab2f0b139b296d974/Qwen3-8B-Q4_K_M.gguf?download=true";
    internal const long ModelBytes = 5_027_783_488;
    internal const string ModelSha256 = "d98cdcbd03e17ce47681435b5150e34c1417f50b5c0019dd560e4882c5745785";
    internal const string QualityModelKey = "qwen3-8b-q4-k-m";
    internal const string FastModelName = "Qwen3-4B Q4_K_M";
    internal const string FastModelUrl = "https://huggingface.co/Qwen/Qwen3-4B-GGUF/resolve/a9a60d009fa7ff9606305047c2bf77ac25dbec49/Qwen3-4B-Q4_K_M.gguf?download=true";
    internal const long FastModelBytes = 2_497_280_256;
    internal const string FastModelSha256 = "7485fe6f11af29433bc51cab58009521f205840f5b4ae3a32fa7f92e8534fdf5";
    internal const string FastModelKey = "qwen3-4b-q4-k-m";
    internal const string ThinkingTemplateKwargs = "{\"enable_thinking\":false}";
    internal const string ReasoningMode = "off";
    internal const int RuntimeAutoGpuLayers = -1;
    private const int TranslationBatchSmall = 8;
    private const int TranslationBatchMedium = 24;
    private const int TranslationBatchLarge = 48;
    private const double TranslationLatencySpikeFactor = 1.6;
    private const double TranslationLatencyEwmaAlpha = 0.25;
    private static readonly TimeSpan TranslationLatencySpikeFloor = TimeSpan.FromSeconds(12);
    private const int AnalysisBatchSize = 420;
    private const string TranslationSchema = "{\"type\":\"object\",\"properties\":{\"translations\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\"},\"text\":{\"type\":\"string\"}},\"required\":[\"id\",\"text\"],\"additionalProperties\":false}}},\"required\":[\"translations\"],\"additionalProperties\":false}";
    private const string BibleSchema = "{\"type\":\"object\",\"properties\":{\"bible\":{\"type\":\"string\"}},\"required\":[\"bible\"],\"additionalProperties\":false}";

    private readonly AppPaths _paths;
    private readonly HttpClient _http;
    private readonly HardwareService _hardware;
    private readonly string _skillPath;
    private TranslationSkillBundle? _skill;
    private bool? _runtimeReady;
    private bool? _modelReady;
    private bool? _fastModelReady;
    private readonly SemaphoreSlim _prepareGate = new(1, 1);
    private readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };

    public LocalSubtitleTranslationService(AppPaths paths, ProcessRunner processes, HardwareService hardware, string skillPath)
    {
        _paths = paths;
        _http = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            AutomaticDecompression = DecompressionMethods.None,
        }) { Timeout = Timeout.InfiniteTimeSpan };
        _hardware = hardware;
        _skillPath = Path.GetFullPath(skillPath);
    }

    private string Root => Path.Combine(_paths.Tools, "Translation");
    private string RuntimeDirectory => Path.Combine(Root, "llama-" + RuntimeVersion + "-vulkan");
    private string RuntimeArchive => Path.Combine(Root, "llama-" + RuntimeVersion + "-vulkan.zip");
    private string RuntimeStamp => Path.Combine(RuntimeDirectory, ".verified");
    private string LlamaServer => Path.Combine(RuntimeDirectory, "llama-server.exe");
    private string ModelDirectory => Path.Combine(Root, "Models");
    private string CheckpointDirectory => Path.Combine(_paths.Data, "Projects", "Translation");
    private static readonly TranslationModelSpec QualityModel = new(EditorTranslationModelMode.Quality, QualityModelKey, ModelName, ModelUrl, ModelBytes, ModelSha256, "Qwen3-8B-Q4_K_M.gguf");
    private static readonly TranslationModelSpec FastModel = new(EditorTranslationModelMode.Fast, FastModelKey, FastModelName, FastModelUrl, FastModelBytes, FastModelSha256, "Qwen3-4B-Q4_K_M.gguf");
    private string ModelPath(TranslationModelSpec model) => Path.Combine(ModelDirectory, model.FileName);
    private string ModelStamp(TranslationModelSpec model) => ModelPath(model) + ".verified";
    private static TranslationModelSpec ModelFor(EditorTranslationModelMode mode) => mode == EditorTranslationModelMode.Fast ? FastModel : QualityModel;
    private TranslationSkillBundle Skill => _skill ??= TranslationSkillBundle.Load(_skillPath, requireBuiltInHash: true);

    public LocalTranslationStatus Status => StatusFor(EditorTranslationModelMode.Quality);

    public LocalTranslationStatus StatusFor(EditorTranslationModelMode mode)
    {
        var model = ModelFor(mode);
        var runtimeReady = _runtimeReady ??= ValidPe(LlamaServer) && RuntimeStampMatches();
        var modelReady = mode == EditorTranslationModelMode.Fast
            ? _fastModelReady ??= ModelStampMatches(model)
            : _modelReady ??= ModelStampMatches(model);
        return new LocalTranslationStatus(runtimeReady, modelReady, model.Name, model.Bytes, Skill.Info.Name, Skill.Info.Sha256);
    }

    public async Task PrepareAsync(AppJob job, EditorTranslationModelMode mode = EditorTranslationModelMode.Quality)
    {
        await _prepareGate.WaitAsync(job.CancellationToken);
        try
        {
            var model = ModelFor(mode);
            Directory.CreateDirectory(Root);
            if (!StatusFor(mode).RuntimeReady)
            {
                job.Set("ai-runtime", 1, "Đang tải runtime AI local đã xác minh (~35 MB)...");
                await DownloadVerifiedAsync(RuntimeUrl, RuntimeArchive, RuntimeArchiveBytes, RuntimeArchiveSha256, 1, 8, job);
                job.Set("ai-runtime", 9, "Đang kiểm tra và giải nén runtime AI...");
                ExtractRuntime(RuntimeArchive);
                _runtimeReady = null;
            }
            if (!StatusFor(mode).ModelReady)
            {
                Directory.CreateDirectory(ModelDirectory);
                job.Set("ai-model", 10, $"Đang tải {model.Name} (~{model.Bytes / 1024d / 1024 / 1024:0.00} GB); có thể hủy và tải tiếp sau.");
                await DownloadVerifiedAsync(model.Url, ModelPath(model), model.Bytes, model.Sha256, 10, 98, job);
                WriteModelStamp(model);
                ResetModelReady(mode);
            }
            job.Set("ai-ready", 99, $"AI local sẵn sàng · {model.Name} · skill {Skill.Info.Name}.");
        }
        finally { _prepareGate.Release(); }
    }

    public async Task<EditorTranslationResult> TranslateAsync(AppJob job, EditorTranslationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateProjectId(request.ProjectId);
        var model = ModelFor(request.ModelMode);
        EnsureResources(model);
        var selectedStatus = StatusFor(request.ModelMode);
        if (!selectedStatus.RuntimeReady || !selectedStatus.ModelReady) throw new InvalidOperationException($"Hãy bấm Chuẩn bị AI cho {model.Name} trước khi Vietsub.");
        var currentSource = await EditorSubtitleDocument.LoadAsync(request.Source.Path, job.CancellationToken);
        if (!string.Equals(currentSource.Sha256, request.Source.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException("File SRT nguồn đã thay đổi sau khi nạp; hãy chọn lại để không dịch nhầm checkpoint.");
        var source = request.Source.Cues;
        if (source.Count == 0) throw new InvalidDataException("SRT nguồn không có cue.");
        var checkpointPath = Path.Combine(CheckpointDirectory, request.ProjectId + ".json");
        if (request.ForceFresh) TryDelete(checkpointPath);
        var checkpoint = await LoadCheckpointAsync(checkpointPath, request.Source.Sha256, model, job.CancellationToken);
        var layers = RuntimeAutoGpuLayers;
        var analysisBatches = CreateBatches(source, AnalysisBatchSize, 45_000);
        var analysisPages = analysisBatches.Count;
        if (checkpoint.AnalysisPagesCompleted < 0 || checkpoint.AnalysisPagesCompleted > analysisPages || checkpoint.Bible.Length > 24_000)
            checkpoint = TranslationCheckpoint.New(request.Source.Sha256, Skill.Info.Sha256, model.Key);
        var recovered = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var cue in source)
        {
            if (!checkpoint.Translations.TryGetValue(cue.Id, out var value)) continue;
            try { ValidateTranslationText(cue, value); recovered[cue.Id] = value; }
            catch (InvalidDataException) { }
        }
        checkpoint = checkpoint with { Translations = recovered };
        var restored = checkpoint.Translations.Count;
        var bible = checkpoint.Bible;
        var adaptiveResources = _hardware.ResourceSnapshot();
        var translationBatchSize = RecommendedTranslationBatchSize(adaptiveResources);
        var latencyBaselineMsPerCue = 0d;
        var needsInference = checkpoint.AnalysisPagesCompleted < analysisPages
            || source.Any(x => !checkpoint.Translations.ContainsKey(x.Id));

        var liveEvents = new Queue<string>();
        void PushLive(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            while (liveEvents.Count >= 4) liveEvents.Dequeue();
            liveEvents.Enqueue(value);
        }
        void SetLive(string status, double progress, string headline, string? eventLine = null)
        {
            if (!string.IsNullOrWhiteSpace(eventLine)) PushLive(eventLine);
            var tail = liveEvents.Count == 0 ? string.Empty : Environment.NewLine + string.Join(Environment.NewLine, liveEvents);
            var alignedProgress = status is "translation-validating" or "translation-writing"
                ? 100d
                : status.StartsWith("translation", StringComparison.Ordinal)
                    ? TranslationOverallProgress(checkpoint.Translations.Count, source.Count)
                    : progress;
            job.Set(status, alignedProgress, headline + tail);
        }

        if (restored > 0)
        {
            var resumeLine = $"Khôi phục {restored:N0} câu đã dịch từ checkpoint.";
            job.Log(resumeLine);
            PushLive(resumeLine);
        }

        TranslationServerSession? runtime = null;
        try
        {
            if (needsInference)
            {
                SetLive("translation-loading", checkpoint.AnalysisPagesCompleted < analysisPages ? 1 : TranslationOverallProgress(restored, source.Count),
                    $"Bước 1/6 · Nạp model {model.Name} vào runtime...");
                runtime = await StartTranslationServerWithFallbackAsync(model, layers, job, job.CancellationToken);
                job.Log($"Qwen runtime giữ model trong RAM/VRAM cho toàn bộ lượt Vietsub · {model.Name}.");
                SetLive("translation-loading", checkpoint.AnalysisPagesCompleted < analysisPages ? 2 : TranslationOverallProgress(restored, source.Count),
                    $"Bước 1/6 · Nạp model xong · {model.Name}.", "Model đã sẵn sàng; bắt đầu xử lý SRT.");
            }

            if (checkpoint.AnalysisPagesCompleted < analysisPages)
            {
                for (var page = checkpoint.AnalysisPagesCompleted; page < analysisPages; page++)
                {
                    job.CancellationToken.ThrowIfCancellationRequested();
                    var pageCues = analysisBatches[page];
                    var analysisProgress = 2 + page / (double)analysisPages * 10;
                    SetLive("translation-analysis", analysisProgress,
                        $"Bước 2/6 · Phân tích SRT · phần {page + 1}/{analysisPages}." + Environment.NewLine +
                        $"Bước 3/6 · Tạo bible · phần {page + 1}/{analysisPages}...");
                    var prompt = BuildBiblePrompt(pageCues, bible, page + 1, analysisPages);
                    string nextBible;
                    try
                    {
                        nextBible = ValidateBible(await RunJsonAsync(runtime!, prompt, BibleSchema, 2048, job.CancellationToken, job));
                        if (string.IsNullOrWhiteSpace(nextBible))
                        {
                            const string warning = "Bible rỗng → giữ hồ sơ trước đó và tiếp tục.";
                            job.Warn($"Lượt đọc SRT {page + 1} không trả bible; giữ hồ sơ trước đó và tiếp tục Vietsub.");
                            SetLive("translation-bible-warning", analysisProgress,
                                $"Bước 3/6 · Tạo bible · phần {page + 1}/{analysisPages}.", warning);
                            nextBible = bible;
                        }
                    }
                    catch (InvalidDataException first)
                    {
                        var warning = $"JSON/hồ sơ sai → giữ bible cũ: {TrimLiveText(first.Message, 110)}";
                        job.Warn($"Lượt đọc SRT {page + 1} trả JSON/hồ sơ không hợp lệ; giữ hồ sơ trước đó và tiếp tục Vietsub: {first.Message}");
                        SetLive("translation-bible-warning", analysisProgress,
                            $"Bước 3/6 · Tạo bible · phần {page + 1}/{analysisPages}.", warning);
                        nextBible = bible;
                    }
                    catch (Exception first) when (first is not OperationCanceledException)
                    {
                        var warning = $"GPU/runtime lỗi → chuyển CPU: {TrimLiveText(first.Message, 100)}";
                        job.Warn($"Lượt đọc SRT {page + 1} gặp lỗi runtime; thử lại bằng CPU an toàn: {first.Message}");
                        SetLive("translation-fallback", analysisProgress,
                            $"Bước 3/6 · Tạo bible · phần {page + 1}/{analysisPages}.", warning);
                        await RestartTranslationServerAsync(runtime!, model, LowerGpuLayers(layers), job.CancellationToken);
                        try
                        {
                            nextBible = ValidateBible(await RunJsonAsync(runtime!, prompt + "\nRuntime vừa được chuyển sang CPU. Chỉ trả đúng JSON object theo schema và luôn có key bible.", BibleSchema, 2048, job.CancellationToken, job));
                            if (string.IsNullOrWhiteSpace(nextBible))
                            {
                                const string cpuWarning = "CPU fallback vẫn thiếu bible → giữ hồ sơ trước đó.";
                                job.Warn($"Lượt đọc SRT {page + 1} sau CPU fallback vẫn thiếu bible; giữ hồ sơ trước đó và tiếp tục Vietsub.");
                                SetLive("translation-bible-warning", analysisProgress,
                                    $"Bước 3/6 · Tạo bible · phần {page + 1}/{analysisPages}.", cpuWarning);
                                nextBible = bible;
                            }
                        }
                        catch (InvalidDataException retryError)
                        {
                            var retryWarning = $"CPU fallback vẫn sai JSON → giữ bible cũ: {TrimLiveText(retryError.Message, 90)}";
                            job.Warn($"Lượt đọc SRT {page + 1} sau CPU fallback vẫn trả JSON/hồ sơ không hợp lệ; giữ hồ sơ trước đó và tiếp tục Vietsub: {retryError.Message}");
                            SetLive("translation-bible-warning", analysisProgress,
                                $"Bước 3/6 · Tạo bible · phần {page + 1}/{analysisPages}.", retryWarning);
                            nextBible = bible;
                        }
                    }
                    bible = nextBible;
                    checkpoint = checkpoint with { Bible = bible, AnalysisPagesCompleted = page + 1 };
                    await SaveCheckpointAsync(checkpointPath, checkpoint, job.CancellationToken);
                    SetLive("translation-analysis", 2 + (page + 1d) / analysisPages * 10,
                        $"Bước 3/6 · Tạo bible · đã xong phần {page + 1}/{analysisPages}.");
                }
            }

            var pending = source.Where(x => !checkpoint.Translations.ContainsKey(x.Id)).ToArray();
            var pendingOffset = 0;
            var completedBatches = 0;
            var translationClock = Stopwatch.StartNew();
            if (pending.Length > 0)
            {
                job.Log($"Adaptive Vietsub chọn batch {translationBatchSize} câu theo VRAM khả dụng trước khi nạp Qwen.");
                SetLive("translation-batch", TranslationOverallProgress(checkpoint.Translations.Count, source.Count),
                    $"Bước 4/6 · Dịch batch · chuẩn bị {pending.Length:N0} câu còn lại.");
            }
            while (pendingOffset < pending.Length)
            {
                job.CancellationToken.ThrowIfCancellationRequested();
                var remainingBatches = CountAdaptiveTranslationBatches(pending, pendingOffset, translationBatchSize, 20_000);
                var batchNumber = completedBatches + 1;
                var batchTotal = completedBatches + remainingBatches;
                var completedBefore = checkpoint.Translations.Count;
                var sentencePercentBefore = completedBefore / (double)source.Count * 100;
                var overallBefore = TranslationOverallProgress(completedBefore, source.Count);
                SetLive("translation-batch", overallBefore,
                    $"Bước 4/6 · Dịch batch · Batch {batchNumber}/{batchTotal}." + Environment.NewLine +
                    $"Đã dịch {completedBefore:N0} / {source.Count:N0} câu · {sentencePercentBefore:0.#}%.");

                var batch = CreateAdaptiveTranslationBatch(pending, pendingOffset, translationBatchSize, 20_000);
                var firstIndex = IndexOf(source, batch[0].Id);
                var context = source.Skip(Math.Max(0, firstIndex - 4)).Take(batch.Length + 8).ToArray();
                var prompt = BuildTranslationPrompt(batch, context, bible);
                var batchWatch = Stopwatch.StartNew();
                IReadOnlyDictionary<string, string> translations;
                try
                {
                    var root = await RunJsonAsync(runtime!, prompt, TranslationSchema, 4096, job.CancellationToken, job);
                    translations = ValidateBatch(root, batch);
                }
                catch (InvalidDataException first)
                {
                    var warning = $"JSON sai → retry Batch {batchNumber}/{batchTotal}: {TrimLiveText(first.Message, 90)}";
                    job.Warn($"Batch bắt đầu cue {batch[0].Number} sai JSON/ID/nội dung; đang thử lại chặt hơn: {first.Message}");
                    SetLive("translation-retry", overallBefore,
                        $"Bước 4/6 · Dịch batch · Batch {batchNumber}/{batchTotal}.", warning);
                    var retry = await RunJsonAsync(runtime!, prompt + "\nLần trước sai JSON/schema/ID hoặc còn chữ Hán. Dịch lại đúng toàn bộ TARGET và chỉ trả đúng một JSON object.",
                        TranslationSchema, 4096, job.CancellationToken, job);
                    translations = ValidateBatch(retry, batch);
                }
                catch (Exception first) when (first is not OperationCanceledException)
                {
                    var warning = $"GPU/runtime lỗi → chuyển CPU · Batch {batchNumber}/{batchTotal}: {TrimLiveText(first.Message, 80)}";
                    job.Warn($"Batch bắt đầu cue {batch[0].Number} gặp lỗi runtime; thử lại bằng CPU an toàn: {first.Message}");
                    SetLive("translation-fallback", overallBefore,
                        $"Bước 4/6 · Dịch batch · Batch {batchNumber}/{batchTotal}.", warning);
                    await RestartTranslationServerAsync(runtime!, model, LowerGpuLayers(layers), job.CancellationToken);
                    var retry = await RunJsonAsync(runtime!, prompt + "\nLần trước runtime không hoàn tất. Dịch lại đúng toàn bộ TARGET và chỉ trả đúng một JSON object.",
                        TranslationSchema, 4096, job.CancellationToken, job);
                    translations = ValidateBatch(retry, batch);
                }
                batchWatch.Stop();
                if (batch.Length == translationBatchSize)
                {
                    if (translationBatchSize > TranslationBatchSmall
                        && IsTranslationLatencySpike(batchWatch.Elapsed, batch.Length, latencyBaselineMsPerCue))
                    {
                        var previousBatchSize = translationBatchSize;
                        translationBatchSize = LowerTranslationBatchSize(translationBatchSize);
                        latencyBaselineMsPerCue = 0d;
                        var warning = $"Batch chậm → giảm batch {previousBatchSize} xuống {translationBatchSize} câu.";
                        job.Warn($"Adaptive Vietsub: batch {previousBatchSize} mất {batchWatch.Elapsed.TotalSeconds:0.0}s, phản hồi tăng đột biến; giảm batch lượt sau còn {translationBatchSize} câu.");
                        PushLive(warning);
                    }
                    else
                    {
                        latencyBaselineMsPerCue = UpdateTranslationLatencyBaseline(latencyBaselineMsPerCue, batchWatch.Elapsed, batch.Length);
                    }
                }
                foreach (var pair in translations) checkpoint.Translations[pair.Key] = pair.Value;
                await SaveCheckpointAsync(checkpointPath, checkpoint, job.CancellationToken);
                pendingOffset += batch.Length;
                completedBatches++;

                var completed = checkpoint.Translations.Count;
                var sentencePercent = completed / (double)source.Count * 100;
                var overallProgress = TranslationOverallProgress(completed, source.Count);
                var speed = translationClock.Elapsed.TotalSeconds >= 1
                    ? pendingOffset / translationClock.Elapsed.TotalMinutes
                    : 0d;
                var speedText = speed > 0 ? $" · {speed:0.0} câu/phút" : string.Empty;
                var lastCue = batch[^1];
                var lastTranslation = translations.TryGetValue(lastCue.Id, out var liveTranslation) ? liveTranslation : string.Empty;
                var pairLine = $"Câu {lastCue.Number}: {TrimLiveText(lastCue.SourceText, 52)} → {TrimLiveText(lastTranslation, 72)}";
                job.Log(pairLine);

                var nextRemainingBatches = CountAdaptiveTranslationBatches(pending, pendingOffset, translationBatchSize, 20_000);
                var currentBatchTotal = completedBatches + nextRemainingBatches;
                SetLive("translation-batch", overallProgress,
                    $"Bước 4/6 · Dịch batch · Batch {completedBatches}/{currentBatchTotal}." + Environment.NewLine +
                    $"Đã dịch {completed:N0} / {source.Count:N0} câu · {sentencePercent:0.#}%{speedText}.",
                    $"Vừa xong · {pairLine}");
            }
        }
        finally
        {
            if (runtime is not null) await runtime.DisposeAsync();
        }

        SetLive("translation-validating", 97,
            $"Bước 5/6 · Kiểm tra {source.Count:N0} câu · số block, thứ tự và timecode...");
        var translated = source.Select(cue => cue with
        {
            VietnameseText = checkpoint.Translations.TryGetValue(cue.Id, out var value) ? value : string.Empty,
        }).ToArray();
        EditorSubtitleDocument.ValidateUnchangedTimeline(source, translated);
        var outputDirectory = string.IsNullOrWhiteSpace(request.OutputDirectory) ? _paths.DefaultDownloads : Path.GetFullPath(request.OutputDirectory.Trim());
        Directory.CreateDirectory(outputDirectory);
        var safeName = FileNamePolicy.Sanitize(request.OutputFileName, Path.GetFileNameWithoutExtension(request.Source.Path) + ".vi.srt");
        if (!safeName.EndsWith(".srt", StringComparison.OrdinalIgnoreCase)) safeName += ".srt";
        var output = FileNamePolicy.UniquePath(Path.Combine(outputDirectory, safeName), request.Source.Path);
        SetLive("translation-writing", 99, $"Bước 6/6 · Ghi SRT Việt · {Path.GetFileName(output)}...");
        await WriteAtomicAsync(output, EditorSubtitleDocument.RenderVietnamese(translated), job.CancellationToken);
        job.Log($"Đã ghi SRT Việt: {output}");
        return new EditorTranslationResult(translated, output, restored, model.Name, Skill.Info.Sha256);
    }

    internal static int RecommendedGpuLayers(HardwareResourceSnapshot resources)
    {
        const long gib = 1024L * 1024 * 1024;
        if (!resources.VramTelemetryAvailable) return 0;
        if (resources.AvailableVramBytes >= 13 * gib / 2) return 99;
        if (resources.AvailableVramBytes >= 9 * gib / 2) return 24;
        if (resources.AvailableVramBytes >= 3 * gib) return 12;
        return 0;
    }

    private static int LowerGpuLayers(int current) => current switch { >= 99 => 24, >= 24 => 12, _ => 0 };

    internal static int RecommendedTranslationBatchSize(HardwareResourceSnapshot resources)
    {
        const long gib = 1024L * 1024 * 1024;
        var tierVram = resources.VramTelemetryAvailable && resources.AvailableVramBytes > 0
            ? Math.Min(resources.TotalVramBytes, resources.AvailableVramBytes)
            : resources.TotalVramBytes;
        if (tierVram < 6 * gib) return TranslationBatchSmall;
        if (tierVram <= 12 * gib) return TranslationBatchMedium;
        return TranslationBatchLarge;
    }

    internal static int LowerTranslationBatchSize(int current) => current switch
    {
        > TranslationBatchMedium => TranslationBatchMedium,
        > TranslationBatchSmall => TranslationBatchSmall,
        _ => TranslationBatchSmall,
    };

    internal static double TranslationOverallProgress(int completed, int total)
    {
        if (total <= 0) return 0;
        return Math.Clamp(completed, 0, total) / (double)total * 100;
    }

    private static int CountAdaptiveTranslationBatches(IReadOnlyList<EditorSubtitleCue> cues, int startIndex, int maxItems, int maxCharacters)
    {
        if (startIndex >= cues.Count) return 0;
        var count = 0;
        var offset = Math.Max(0, startIndex);
        while (offset < cues.Count)
        {
            var batch = CreateAdaptiveTranslationBatch(cues, offset, maxItems, maxCharacters);
            if (batch.Length == 0) break;
            offset += batch.Length;
            count++;
        }
        return count;
    }

    private static string TrimLiveText(string value, int maxLength)
    {
        var flat = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (flat.Length <= maxLength) return flat;
        return flat[..Math.Max(1, maxLength - 3)] + "...";
    }

    internal static bool IsTranslationLatencySpike(TimeSpan elapsed, int cueCount, double baselineMsPerCue)
    {
        if (cueCount <= 0 || baselineMsPerCue <= 0 || elapsed < TranslationLatencySpikeFloor) return false;
        var currentMsPerCue = elapsed.TotalMilliseconds / cueCount;
        return currentMsPerCue >= baselineMsPerCue * TranslationLatencySpikeFactor;
    }

    internal static double UpdateTranslationLatencyBaseline(double baselineMsPerCue, TimeSpan elapsed, int cueCount)
    {
        if (cueCount <= 0) return baselineMsPerCue;
        var currentMsPerCue = elapsed.TotalMilliseconds / cueCount;
        if (baselineMsPerCue <= 0) return currentMsPerCue;
        return baselineMsPerCue * (1 - TranslationLatencyEwmaAlpha) + currentMsPerCue * TranslationLatencyEwmaAlpha;
    }

    internal static IReadOnlyDictionary<string, string> ValidateBatch(JsonElement root, IReadOnlyList<EditorSubtitleCue> expected)
    {
        if (!root.TryGetProperty("translations", out var array) || array.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Model không trả mảng translations.");
        var expectedIds = expected.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in array.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idValue) ? idValue.GetString()?.Trim() : null;
            var text = item.TryGetProperty("text", out var textValue) ? textValue.GetString()?.Trim() : null;
            if (id is null || !expectedIds.Contains(id) || !result.TryAdd(id, text ?? string.Empty))
                throw new InvalidDataException("Model trả cue ID thừa, lặp hoặc sai.");
            var source = expected.First(x => string.Equals(x.Id, id, StringComparison.Ordinal));
            ValidateTranslationText(source, text ?? string.Empty);
        }
        if (result.Count != expected.Count) throw new InvalidDataException("Model bỏ sót cue trong batch.");
        return result;
    }

    private static string ValidateBible(JsonElement root)
    {
        if (!root.TryGetProperty("bible", out var value)) return string.Empty;
        if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0) return string.Empty;
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Model trả bible sai kiểu dữ liệu.");
        var bible = value.GetString()?.Trim() ?? string.Empty;
        if (bible.Length > 24_000) throw new InvalidDataException("Model tạo hồ sơ thuật ngữ/nhân vật vượt giới hạn an toàn.");
        return bible;
    }

    private async Task<TranslationServerSession> StartTranslationServerWithFallbackAsync(
        TranslationModelSpec model,
        int gpuLayers,
        AppJob job,
        CancellationToken cancellationToken)
    {
        var session = new TranslationServerSession();
        try
        {
            await RestartTranslationServerAsync(session, model, gpuLayers, cancellationToken);
            return session;
        }
        catch (OperationCanceledException)
        {
            await session.DisposeAsync();
            throw;
        }
        catch (Exception first)
        {
            job.Warn("Qwen GPU/Vulkan không khởi động được persistent runtime; chuyển sang CPU an toàn: " + first.Message);
            job.Set("translation-fallback", -1, "Bước 1/6 · GPU lỗi → chuyển CPU để nạp model an toàn...");
            try
            {
                await RestartTranslationServerAsync(session, model, 0, cancellationToken);
                return session;
            }
            catch
            {
                await session.DisposeAsync();
                throw;
            }
        }
    }

    private async Task RestartTranslationServerAsync(
        TranslationServerSession session,
        TranslationModelSpec model,
        int gpuLayers,
        CancellationToken cancellationToken)
    {
        await session.StopCurrentAsync();
        var port = ReserveLoopbackPort();
        var endpoint = new Uri($"http://127.0.0.1:{port}/", UriKind.Absolute);
        var start = new ProcessStartInfo(LlamaServer)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        var args = new List<string>
        {
            "-m", ModelPath(model), "--host", "127.0.0.1", "--port", port.ToString(),
            "-ngl", gpuLayers < 0 ? "auto" : gpuLayers.ToString(), "--fit", "on", "-c", "24576", "-np", "1",
            "--jinja", "--no-warmup", "--no-context-shift", "--cache-prompt",
            "--chat-template-kwargs", ThinkingTemplateKwargs, "--reasoning", ReasoningMode, "--reasoning-format", "none",
        };
        foreach (var argument in args) start.ArgumentList.Add(argument);

        var process = new Process { StartInfo = start };
        IDisposable? ownership = null;
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Không khởi động được llama-server.exe.");
            ownership = session.Processes.Track(process);
            var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            session.Attach(process, ownership, stdout, stderr, endpoint, gpuLayers, cancellationToken);
            ownership = null;
            await WaitForTranslationServerReadyAsync(session, cancellationToken);
        }
        catch
        {
            ownership?.Dispose();
            if (!ReferenceEquals(session.Process, process))
            {
                KillProcess(process);
                process.Dispose();
            }
            await session.StopCurrentAsync();
            throw;
        }
    }

    private async Task WaitForTranslationServerReadyAsync(
        TranslationServerSession session,
        CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(90))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (session.Process is null || session.Process.HasExited)
            {
                var reason = await session.ReadErrorTailAsync();
                throw new InvalidOperationException("llama-server dừng khi đang nạp Qwen: " + reason);
            }

            try
            {
                using var response = await _http.GetAsync(new Uri(session.Endpoint, "health"), cancellationToken);
                if (response.StatusCode == HttpStatusCode.OK) return;
                if (response.StatusCode != HttpStatusCode.ServiceUnavailable)
                {
                    var detail = await response.Content.ReadAsStringAsync(cancellationToken);
                    throw new InvalidOperationException($"llama-server health HTTP {(int)response.StatusCode}: {TrimRuntimeDetail(detail)}");
                }
            }
            catch (HttpRequestException) when (session.Process is not null && !session.Process.HasExited)
            {
                // Socket can reject connections briefly before the loopback listener is ready.
            }
            await Task.Delay(100, cancellationToken);
        }
        throw new TimeoutException("llama-server không sẵn sàng sau khi Nạp model.");
    }

    private async Task<JsonElement> RunJsonAsync(
        TranslationServerSession session,
        string prompt,
        string schema,
        int maxTokens,
        CancellationToken cancellationToken,
        AppJob? progressJob = null)
    {
        async Task<JsonElement> RunAttemptAsync(string attemptPrompt, bool enforceSchema)
        {
            var templated = await ApplyTranslationTemplateAsync(session, attemptPrompt, cancellationToken);
            var payload = new Dictionary<string, object?>
            {
                ["prompt"] = templated,
                ["n_predict"] = maxTokens,
                ["temperature"] = 0.4,
                ["top_k"] = 20,
                ["top_p"] = 0.8,
                ["min_p"] = 0.0,
                ["presence_penalty"] = 1.0,
                ["cache_prompt"] = true,
            };
            if (enforceSchema)
            {
                using var schemaDocument = JsonDocument.Parse(schema);
                payload["json_schema"] = schemaDocument.RootElement.Clone();
            }

            var body = await PostTranslationJsonAsync(session, "completion", payload, cancellationToken);
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("content", out var contentValue) || contentValue.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("AI local không trả content từ persistent runtime.");
            return ExtractJson(contentValue.GetString() ?? string.Empty);
        }

        try
        {
            try
            {
                return await RunAttemptAsync(prompt, enforceSchema: true);
            }
            catch (InvalidDataException first)
            {
                progressJob?.Warn("JSON sai → retry không schema với cùng model: " + first.Message);
                progressJob?.Set("translation-retry", -1, "JSON sai → retry với cùng model; progress hiện tại được giữ nguyên...");
                // Keep the reviewed Qwen fallback: if schema-constrained generation is empty or malformed,
                // retry on the same loaded model without grammar while downstream validators still own shape/IDs/text.
                return await RunAttemptAsync(prompt + "\nCHỈ TRẢ đúng một JSON object hợp lệ, không markdown, không giải thích hay tiền tố/hậu tố.", enforceSchema: false);
            }
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Persistent Qwen runtime trả JSON giao thức không hợp lệ.", error);
        }
    }

    private async Task<string> ApplyTranslationTemplateAsync(
        TranslationServerSession session,
        string prompt,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            messages = new[] { new { role = "user", content = prompt } },
            add_generation_prompt = true,
        };
        var body = await PostTranslationJsonAsync(session, "apply-template", payload, cancellationToken);
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("prompt", out var promptValue) || promptValue.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("llama-server không trả prompt từ chat template.");
        var templated = promptValue.GetString();
        if (string.IsNullOrWhiteSpace(templated)) throw new InvalidDataException("llama-server trả chat template rỗng.");
        return templated;
    }

    private async Task<string> PostTranslationJsonAsync(
        TranslationServerSession session,
        string route,
        object payload,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(session.Endpoint, route))
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"llama-server {route} HTTP {(int)response.StatusCode}: {TrimRuntimeDetail(body)}");
        return body;
    }

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private static string TrimRuntimeDetail(string value)
    {
        var detail = value.Trim();
        if (detail.Length > 800) detail = detail[^800..];
        return string.IsNullOrWhiteSpace(detail) ? "không có chi tiết" : detail;
    }

    private static void KillProcess(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }

    private sealed class TranslationServerSession : IAsyncDisposable
    {
        private IDisposable? _ownership;
        private CancellationTokenRegistration _registration;
        private bool _hasRegistration;
        private Task<string>? _stdoutTask;
        private Task<string>? _stderrTask;

        public OwnedProcessGroup Processes { get; } = new();
        public Process? Process { get; private set; }
        public Uri Endpoint { get; private set; } = new("http://127.0.0.1/");
        public int GpuLayers { get; private set; }

        public void Attach(
            Process process,
            IDisposable ownership,
            Task<string> stdoutTask,
            Task<string> stderrTask,
            Uri endpoint,
            int gpuLayers,
            CancellationToken cancellationToken)
        {
            Process = process;
            _ownership = ownership;
            _stdoutTask = stdoutTask;
            _stderrTask = stderrTask;
            Endpoint = endpoint;
            GpuLayers = gpuLayers;
            _registration = cancellationToken.Register(() => KillProcess(process));
            _hasRegistration = true;
        }

        public async Task<string> ReadErrorTailAsync()
        {
            if (_stderrTask is null) return "không có stderr";
            if (Process is not null && !Process.HasExited) return "runtime vẫn đang chạy";
            try { return TrimRuntimeDetail(await _stderrTask.WaitAsync(TimeSpan.FromSeconds(1))); }
            catch { return "không đọc được stderr"; }
        }

        public async Task StopCurrentAsync()
        {
            if (_hasRegistration)
            {
                _registration.Dispose();
                _hasRegistration = false;
            }

            var process = Process;
            if (process is not null)
            {
                KillProcess(process);
                try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            }
            if (_stdoutTask is not null)
            {
                try { _ = await _stdoutTask.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
            }
            if (_stderrTask is not null)
            {
                try { _ = await _stderrTask.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
            }

            _ownership?.Dispose();
            _ownership = null;
            Process?.Dispose();
            Process = null;
            _stdoutTask = null;
            _stderrTask = null;
        }

        public async ValueTask DisposeAsync()
        {
            await StopCurrentAsync();
            try { await Processes.StopAsync(); } catch { }
        }
    }

    internal static JsonElement ExtractJson(string output)
    {
        for (var end = output.LastIndexOf('}'); end >= 0; end = end == 0 ? -1 : output.LastIndexOf('}', end - 1))
        {
            for (var start = output.LastIndexOf('{', end); start >= 0; start = start == 0 ? -1 : output.LastIndexOf('{', start - 1))
            {
                try
                {
                    using var document = JsonDocument.Parse(output[start..(end + 1)], new JsonDocumentOptions { MaxDepth = 16 });
                    return document.RootElement.Clone();
                }
                catch (JsonException) { }
            }
        }
        throw new InvalidDataException("AI local không trả JSON hợp lệ.");
    }

    private string BuildBiblePrompt(IReadOnlyList<EditorSubtitleCue> cues, string currentBible, int page, int totalPages)
    {
        var skill = Skill.BuildInstructions(cues.Select(x => x.SourceText), 36_000);
        var source = string.Join('\n', cues.Select(x => $"[{x.Id}] {x.SourceText}"));
        return $$"""
            Bạn đang thực hiện lượt đọc toàn bộ SRT Trung trước khi dịch (phần {{page}}/{{totalPages}}).
            {{skill}}

            HỒ SƠ ĐÃ TÍCH LŨY TỪ PHẦN TRƯỚC:
            {{(string.IsNullOrWhiteSpace(currentBible) ? "Chưa có." : currentBible)}}

            NGUYÊN VĂN PHẦN NÀY:
            {{source}}

            Chưa dịch từng câu. Hãy cập nhật một hồ sơ gọn nhưng đầy đủ bằng tiếng Việt gồm: tên Hán-Việt đã khóa,
            giới tính/vai vế/quan hệ, cách xưng hô, tông môn-địa danh, cảnh giới, công pháp/pháp bảo và điểm mơ hồ cần giữ nhất quán.
            Không bịa dữ kiện.

            BẮT BUỘC: chỉ trả một JSON object hợp lệ và luôn có key "bible"; tuyệt đối không bỏ key này.
            Ví dụ hợp lệ: {"bible":"Trương Tam: nam, đệ tử Thanh Vân Tông; xưng hô với Lý trưởng lão là sư thúc."}
            Nếu phần này không có dữ kiện mới, vẫn trả {"bible":""}. Không markdown, không giải thích, không tiền tố/hậu tố.
            Lượt này chỉ xây hồ sơ; không trả lines/translations vì phụ đề được dịch ở batch riêng sau đó.
            """;
    }

    private string BuildTranslationPrompt(IReadOnlyList<EditorSubtitleCue> target, IReadOnlyList<EditorSubtitleCue> context, string bible)
    {
        var coreSkill = Skill.BuildCoreInstructions();
        var relevantSkill = Skill.BuildReferenceInstructions(context.Select(x => x.SourceText), 34_000, coreSkill.Length);
        var contextJson = JsonSerializer.Serialize(context.Select(x => new { id = x.Id, text = x.SourceText }));
        var targetJson = JsonSerializer.Serialize(target.Select(x => new { id = x.Id, text = x.SourceText }));
        return $$"""
            Dịch phụ đề phim Trung Quốc sang tiếng Việt tự nhiên, có cảm xúc, đúng lore tiên hiệp/cổ trang.
            {{coreSkill}}

            HỒ SƠ PHIM ĐÃ KHÓA:
            {{bible}}

            {{relevantSkill}}

            NGỮ CẢNH LÂN CẬN (chỉ để hiểu, không trả các cue ngoài TARGET):
            {{contextJson}}

            TARGET PHẢI DỊCH:
            {{targetJson}}

            Luật máy bắt buộc: trả đúng một phần tử cho từng id TARGET, giữ nguyên id và thứ tự; không thêm/bớt/gộp cue;
            chỉ trả tiếng Việt hoàn chỉnh trong text; không timecode, không giải thích, không markdown, không [CẦN XÁC NHẬN].
            Trả đúng JSON theo schema.
            """;
    }

    private static void ValidateTranslationText(EditorSubtitleCue cue, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException($"Model trả rỗng cue {cue.Number}.");
        if (text.Contains("[CẦN XÁC NHẬN]", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Cue {cue.Number} còn nhãn nội bộ [CẦN XÁC NHẬN].");
        if (text.Contains("```", StringComparison.Ordinal) || text.Contains("-->", StringComparison.Ordinal))
            throw new InvalidDataException($"Cue {cue.Number} chứa giải thích/định dạng ngoài nội dung phụ đề.");
        if (text.Any(ch => ch is >= '\u3400' and <= '\u9FFF'))
            throw new InvalidDataException($"Cue {cue.Number} còn chữ Hán chưa Vietsub.");
        if (text.Length > Math.Max(400, cue.SourceText.Length * 10))
            throw new InvalidDataException($"Cue {cue.Number} dài bất thường; từ chối để tránh model lan man.");
        if (text.Any(char.IsControl) && text.Any(ch => ch is not ('\r' or '\n' or '\t')))
            throw new InvalidDataException($"Cue {cue.Number} chứa ký tự điều khiển.");
    }

    private void EnsureResources(TranslationModelSpec model)
    {
        const long gib = 1024L * 1024 * 1024;
        var resource = _hardware.ResourceSnapshot();
        if (resource.TotalMemoryBytes < 8 * gib)
            throw new InvalidOperationException($"{model.Name} cần máy có ít nhất 8 GB RAM theo gate an toàn hiện tại.");
        if (resource.AvailableMemoryBytes < 7 * gib / 2 && RecommendedGpuLayers(resource) == 0)
            throw new InvalidOperationException($"RAM trống chưa đủ để chạy {model.Name} an toàn. Hãy đóng bớt ứng dụng rồi thử lại.");
    }

    private async Task DownloadVerifiedAsync(string url, string destination, long expectedSize, string expectedSha, double startProgress, double endProgress, AppJob job)
    {
        if (ValidSizedFile(destination, expectedSize) && await HashAsync(destination, job.CancellationToken) == expectedSha) return;
        var partial = destination + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var existing = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (existing < 0 || existing > expectedSize) { TryDelete(partial); existing = 0; }
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("BiliSubStudio/4-CSharp");
        if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, job.CancellationToken);
        if (existing > 0 && response.StatusCode != HttpStatusCode.PartialContent)
        {
            TryDelete(partial);
            existing = 0;
        }
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(job.CancellationToken);
        await using (var target = new FileStream(partial, existing > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var buffer = new byte[1024 * 1024];
            var total = existing;
            var clock = Stopwatch.StartNew();
            var lastReport = TimeSpan.Zero;
            while (true)
            {
                var read = await source.ReadAsync(buffer, job.CancellationToken);
                if (read == 0) break;
                await target.WriteAsync(buffer.AsMemory(0, read), job.CancellationToken);
                total += read;
                if (total > expectedSize) throw new InvalidDataException("File AI tải về lớn hơn manifest đã khóa.");
                if (clock.Elapsed - lastReport >= TimeSpan.FromMilliseconds(400))
                {
                    lastReport = clock.Elapsed;
                    var progress = startProgress + total / (double)expectedSize * (endProgress - startProgress);
                    job.Set("ai-download", progress, $"Đang tải {Path.GetFileName(destination)} · {total / 1024d / 1024 / 1024:0.00}/{expectedSize / 1024d / 1024 / 1024:0.00} GB");
                }
            }
            await target.FlushAsync(job.CancellationToken);
            target.Flush(flushToDisk: true);
        }
        if (new FileInfo(partial).Length != expectedSize) throw new InvalidDataException("File AI tải về chưa đủ kích thước manifest.");
        job.Set("ai-verify", endProgress, $"Đang xác minh SHA-256 của {Path.GetFileName(destination)}...");
        var actual = await HashAsync(partial, job.CancellationToken);
        if (!string.Equals(actual, expectedSha, StringComparison.Ordinal))
        {
            TryDelete(partial);
            throw new InvalidDataException("SHA-256 file AI không khớp; đã xóa dữ liệu không tin cậy.");
        }
        File.Move(partial, destination, overwrite: true);
    }

    private void ExtractRuntime(string archivePath)
    {
        var temporary = RuntimeDirectory + ".extracting-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(temporary);
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            if (archive.Entries.Count is 0 or > 256) throw new InvalidDataException("Runtime AI có số entry không hợp lệ.");
            long expanded = 0;
            var root = Path.GetFullPath(temporary).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (var entry in archive.Entries)
            {
                expanded += entry.Length;
                if (expanded > 512L * 1024 * 1024) throw new InvalidDataException("Runtime AI giải nén vượt giới hạn.");
                var target = Path.GetFullPath(Path.Combine(temporary, entry.FullName));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase) || entry.FullName.Contains(':', StringComparison.Ordinal))
                    throw new InvalidDataException("Runtime AI chứa path không an toàn.");
                if (entry.FullName.EndsWith("/", StringComparison.Ordinal)) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: false);
            }
            if (!ValidPe(Path.Combine(temporary, "llama-server.exe"))) throw new InvalidDataException("Runtime AI thiếu llama-server.exe x64 hợp lệ.");
            WriteStamp(Path.Combine(temporary, ".verified"), RuntimeArchiveSha256 + "|" + DirectoryFingerprint(temporary));
            if (Directory.Exists(RuntimeDirectory)) Directory.Delete(RuntimeDirectory, recursive: true);
            Directory.Move(temporary, RuntimeDirectory);
        }
        finally { TryDeleteDirectory(temporary); TryDelete(archivePath); }
    }

    private async Task<TranslationCheckpoint> LoadCheckpointAsync(string path, string sourceSha, TranslationModelSpec model, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 64L * 1024 * 1024) return TranslationCheckpoint.New(sourceSha, Skill.Info.Sha256, model.Key);
            await using var stream = File.OpenRead(path);
            var loaded = await JsonSerializer.DeserializeAsync<TranslationCheckpoint>(stream, _json, cancellationToken);
            if (loaded is null || loaded.Schema != 1 || loaded.SourceSha256 != sourceSha || loaded.SkillSha256 != Skill.Info.Sha256)
                return TranslationCheckpoint.New(sourceSha, Skill.Info.Sha256, model.Key);
            var checkpointModelKey = loaded.ModelKey;
            if (string.IsNullOrWhiteSpace(checkpointModelKey))
            {
                if (model.Mode != EditorTranslationModelMode.Quality)
                    return TranslationCheckpoint.New(sourceSha, Skill.Info.Sha256, model.Key);
                checkpointModelKey = QualityModelKey;
            }
            if (!string.Equals(checkpointModelKey, model.Key, StringComparison.Ordinal))
                return TranslationCheckpoint.New(sourceSha, Skill.Info.Sha256, model.Key);
            return loaded with
            {
                ModelKey = checkpointModelKey,
                Translations = new Dictionary<string, string>(loaded.Translations, StringComparer.Ordinal),
            };
        }
        catch (OperationCanceledException) { throw; }
        catch { return TranslationCheckpoint.New(sourceSha, Skill.Info.Sha256, model.Key); }
    }

    private async Task SaveCheckpointAsync(string path, TranslationCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, checkpoint, _json, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { TryDelete(temporary); }
    }

    private static async Task WriteAtomicAsync(string path, string content, CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken);
            File.Move(temporary, path, overwrite: false);
        }
        finally { TryDelete(temporary); }
    }

    private static int IndexOf(IReadOnlyList<EditorSubtitleCue> cues, string id)
    {
        for (var index = 0; index < cues.Count; index++) if (cues[index].Id == id) return index;
        return -1;
    }

    private static IReadOnlyList<EditorSubtitleCue[]> CreateBatches(IReadOnlyList<EditorSubtitleCue> cues, int maxItems, int maxCharacters)
    {
        var result = new List<EditorSubtitleCue[]>();
        var current = new List<EditorSubtitleCue>(maxItems);
        var characters = 0;
        foreach (var cue in cues)
        {
            var size = cue.SourceText.Length + 64;
            if (current.Count > 0 && (current.Count >= maxItems || characters + size > maxCharacters))
            {
                result.Add(current.ToArray());
                current.Clear();
                characters = 0;
            }
            current.Add(cue);
            characters += size;
        }
        if (current.Count > 0) result.Add(current.ToArray());
        return result;
    }

    private static EditorSubtitleCue[] CreateAdaptiveTranslationBatch(
        IReadOnlyList<EditorSubtitleCue> cues,
        int startIndex,
        int maxItems,
        int maxCharacters)
    {
        if (startIndex < 0 || startIndex >= cues.Count) throw new ArgumentOutOfRangeException(nameof(startIndex));
        var current = new List<EditorSubtitleCue>(maxItems);
        var characters = 0;
        for (var index = startIndex; index < cues.Count; index++)
        {
            var cue = cues[index];
            var size = cue.SourceText.Length + 64;
            if (current.Count > 0 && (current.Count >= maxItems || characters + size > maxCharacters)) break;
            current.Add(cue);
            characters += size;
        }
        return current.ToArray();
    }

    private static void ValidateProjectId(string value)
    {
        if (value.Length is < 8 or > 64 || value.Any(x => !char.IsAsciiLetterOrDigit(x) && x is not ('-' or '_')))
            throw new InvalidDataException("Project ID dịch không hợp lệ.");
    }

    private static bool ValidSizedFile(string path, long expected) => File.Exists(path) && new FileInfo(path).Length == expected && new FileInfo(path).LinkTarget is null;
    private static void WriteStamp(string path, string value) => File.WriteAllText(path, value + Environment.NewLine, new UTF8Encoding(false));
    private bool ModelStampMatches(TranslationModelSpec model)
    {
        try
        {
            var path = ModelPath(model);
            var stamp = ModelStamp(model);
            if (!File.Exists(stamp) || !ValidSizedFile(path, model.Bytes)) return false;
            var expected = $"{model.Sha256}|{model.Bytes}|{new FileInfo(path).LastWriteTimeUtc.Ticks}";
            return string.Equals(File.ReadAllText(stamp).Trim(), expected, StringComparison.Ordinal);
        }
        catch { return false; }
    }
    private void WriteModelStamp(TranslationModelSpec model)
    {
        var path = ModelPath(model);
        WriteStamp(ModelStamp(model), $"{model.Sha256}|{model.Bytes}|{new FileInfo(path).LastWriteTimeUtc.Ticks}");
    }
    private void ResetModelReady(EditorTranslationModelMode mode)
    {
        if (mode == EditorTranslationModelMode.Fast) _fastModelReady = null;
        else _modelReady = null;
    }
    private bool RuntimeStampMatches()
    {
        try
        {
            if (!File.Exists(RuntimeStamp)) return false;
            var expected = RuntimeArchiveSha256 + "|" + DirectoryFingerprint(RuntimeDirectory);
            return string.Equals(File.ReadAllText(RuntimeStamp).Trim(), expected, StringComparison.Ordinal);
        }
        catch { return false; }
    }
    private static string DirectoryFingerprint(string directory)
    {
        var inventory = string.Join('\n', Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(x => !string.Equals(Path.GetFileName(x), ".verified", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => Path.GetRelativePath(directory, x), StringComparer.OrdinalIgnoreCase)
            .Select(x =>
            {
                var info = new FileInfo(x);
                return $"{Path.GetRelativePath(directory, x).Replace('\\', '/')}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            }));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(inventory)));
    }
    private static bool ValidPe(string path)
    {
        try { using var stream = File.OpenRead(path); return stream.ReadByte() == 'M' && stream.ReadByte() == 'Z'; }
        catch { return false; }
    }
    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }
    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }
    private static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { } }

    public void Dispose()
    {
        _prepareGate.Dispose();
        _http.Dispose();
    }

    private sealed record TranslationModelSpec(
        EditorTranslationModelMode Mode,
        string Key,
        string Name,
        string Url,
        long Bytes,
        string Sha256,
        string FileName);

    private sealed record TranslationCheckpoint(int Schema, string SourceSha256, string SkillSha256, string? ModelKey, string Bible, int AnalysisPagesCompleted, Dictionary<string, string> Translations)
    {
        public static TranslationCheckpoint New(string sourceSha, string skillSha, string modelKey) => new(1, sourceSha, skillSha, modelKey, string.Empty, 0, new Dictionary<string, string>(StringComparer.Ordinal));
    }
}
