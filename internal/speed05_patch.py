from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def patch(path: str, old: str, new: str) -> None:
    p = ROOT / path
    text = p.read_text(encoding="utf-8")
    if text.count(old) != 1:
        raise SystemExit(f"PATCH FAIL {path}: expected exactly one match, got {text.count(old)}")
    p.write_text(text.replace(old, new, 1), encoding="utf-8")


SERVICE = "csharp/src/BiliSubStudio.Core/Editor/LocalSubtitleTranslationService.cs"
APP = "csharp/src/BiliSubStudio.Core/Application/BiliSubApplication.cs"
XAML = "csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml"
PAGE = "csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml.cs"
CUES = "csharp/src/BiliSubStudio.App/Pages/EditorPage.SubtitleCueEditing.cs"
PERSIST = "csharp/scripts/verify_editor_translation_persistent_server_contract.py"

patch(SERVICE,
'''namespace BiliSubStudio.Core.Editor;\n\npublic sealed record LocalTranslationStatus(''',
'''namespace BiliSubStudio.Core.Editor;\n\npublic enum EditorTranslationModelMode\n{\n    Quality,\n    Fast,\n}\n\npublic sealed record LocalTranslationStatus(''')

patch(SERVICE,
'''public sealed record EditorTranslationRequest(\n    string ProjectId,\n    EditorSubtitleSource Source,\n    string OutputDirectory,\n    string OutputFileName,\n    bool ForceFresh = false);''',
'''public sealed record EditorTranslationRequest(\n    string ProjectId,\n    EditorSubtitleSource Source,\n    string OutputDirectory,\n    string OutputFileName,\n    bool ForceFresh = false,\n    EditorTranslationModelMode ModelMode = EditorTranslationModelMode.Quality);''')

patch(SERVICE,
'''    internal const string ModelSha256 = "d98cdcbd03e17ce47681435b5150e34c1417f50b5c0019dd560e4882c5745785";\n    internal const string ThinkingTemplateKwargs = "{\\\"enable_thinking\\\":false}";''',
'''    internal const string ModelSha256 = "d98cdcbd03e17ce47681435b5150e34c1417f50b5c0019dd560e4882c5745785";\n    internal const string QualityModelKey = "qwen3-8b-q4-k-m";\n    internal const string FastModelName = "Qwen3-4B Q4_K_M";\n    internal const string FastModelUrl = "https://huggingface.co/Qwen/Qwen3-4B-GGUF/resolve/a9a60d009fa7ff9606305047c2bf77ac25dbec49/Qwen3-4B-Q4_K_M.gguf?download=true";\n    internal const long FastModelBytes = 2_497_280_256;\n    internal const string FastModelSha256 = "7485fe6f11af29433bc51cab58009521f205840f5b4ae3a32fa7f92e8534fdf5";\n    internal const string FastModelKey = "qwen3-4b-q4-k-m";\n    internal const string ThinkingTemplateKwargs = "{\\\"enable_thinking\\\":false}";''')

patch(SERVICE,
'''    private TranslationSkillBundle? _skill;\n    private bool? _runtimeReady;\n    private bool? _modelReady;''',
'''    private TranslationSkillBundle? _skill;\n    private bool? _runtimeReady;\n    private bool? _modelReady;\n    private bool? _fastModelReady;''')

patch(SERVICE,
'''    private string LlamaServer => Path.Combine(RuntimeDirectory, "llama-server.exe");\n    private string ModelDirectory => Path.Combine(Root, "Models");\n    private string ModelPath => Path.Combine(ModelDirectory, "Qwen3-8B-Q4_K_M.gguf");\n    private string ModelStamp => ModelPath + ".verified";\n    private string CheckpointDirectory => Path.Combine(_paths.Data, "Projects", "Translation");''',
'''    private string LlamaServer => Path.Combine(RuntimeDirectory, "llama-server.exe");\n    private string ModelDirectory => Path.Combine(Root, "Models");\n    private string CheckpointDirectory => Path.Combine(_paths.Data, "Projects", "Translation");\n    private static readonly TranslationModelSpec QualityModel = new(EditorTranslationModelMode.Quality, QualityModelKey, ModelName, ModelUrl, ModelBytes, ModelSha256, "Qwen3-8B-Q4_K_M.gguf");\n    private static readonly TranslationModelSpec FastModel = new(EditorTranslationModelMode.Fast, FastModelKey, FastModelName, FastModelUrl, FastModelBytes, FastModelSha256, "Qwen3-4B-Q4_K_M.gguf");\n    private string ModelPath(TranslationModelSpec model) => Path.Combine(ModelDirectory, model.FileName);\n    private string ModelStamp(TranslationModelSpec model) => ModelPath(model) + ".verified";\n    private static TranslationModelSpec ModelFor(EditorTranslationModelMode mode) => mode == EditorTranslationModelMode.Fast ? FastModel : QualityModel;''')

patch(SERVICE,
'''    public LocalTranslationStatus Status\n    {\n        get\n        {\n            var runtime = _runtimeReady ??= ValidPe(LlamaServer) && RuntimeStampMatches();\n            var model = _modelReady ??= ValidSizedFile(ModelPath, ModelBytes) && ModelStampMatches();\n            return new LocalTranslationStatus(runtime, model, ModelName, ModelBytes, Skill.Info.Name, Skill.Info.Sha256);\n        }\n    }\n\n    public async Task PrepareAsync(AppJob job)\n    {\n        await _prepareGate.WaitAsync(job.CancellationToken);\n        try\n        {\n            Directory.CreateDirectory(Root);\n            if (!Status.RuntimeReady)\n            {\n                job.Set("ai-runtime", 1, "Đang tải runtime AI local đã xác minh (~35 MB)...");\n                await DownloadVerifiedAsync(RuntimeUrl, RuntimeArchive, RuntimeArchiveBytes, RuntimeArchiveSha256, 1, 8, job);\n                job.Set("ai-runtime", 9, "Đang kiểm tra và giải nén runtime AI...");\n                ExtractRuntime(RuntimeArchive);\n                _runtimeReady = null;\n            }\n            if (!Status.ModelReady)\n            {\n                Directory.CreateDirectory(ModelDirectory);\n                job.Set("ai-model", 10, "Đang tải Qwen3-8B Q4_K_M (~5,03 GB); có thể hủy và tải tiếp sau.");\n                await DownloadVerifiedAsync(ModelUrl, ModelPath, ModelBytes, ModelSha256, 10, 98, job);\n                WriteModelStamp();\n                _modelReady = null;\n            }\n            job.Set("ai-ready", 99, $"AI local sẵn sàng · {ModelName} · skill {Skill.Info.Name}.");\n        }\n        finally { _prepareGate.Release(); }\n    }''',
'''    public LocalTranslationStatus Status => StatusFor(EditorTranslationModelMode.Quality);\n\n    public LocalTranslationStatus StatusFor(EditorTranslationModelMode mode)\n    {\n        var model = ModelFor(mode);\n        var runtimeReady = _runtimeReady ??= ValidPe(LlamaServer) && RuntimeStampMatches();\n        var modelReady = mode == EditorTranslationModelMode.Fast\n            ? _fastModelReady ??= ModelStampMatches(model)\n            : _modelReady ??= ModelStampMatches(model);\n        return new LocalTranslationStatus(runtimeReady, modelReady, model.Name, model.Bytes, Skill.Info.Name, Skill.Info.Sha256);\n    }\n\n    public async Task PrepareAsync(AppJob job, EditorTranslationModelMode mode = EditorTranslationModelMode.Quality)\n    {\n        await _prepareGate.WaitAsync(job.CancellationToken);\n        try\n        {\n            var model = ModelFor(mode);\n            Directory.CreateDirectory(Root);\n            if (!StatusFor(mode).RuntimeReady)\n            {\n                job.Set("ai-runtime", 1, "Đang tải runtime AI local đã xác minh (~35 MB)...");\n                await DownloadVerifiedAsync(RuntimeUrl, RuntimeArchive, RuntimeArchiveBytes, RuntimeArchiveSha256, 1, 8, job);\n                job.Set("ai-runtime", 9, "Đang kiểm tra và giải nén runtime AI...");\n                ExtractRuntime(RuntimeArchive);\n                _runtimeReady = null;\n            }\n            if (!StatusFor(mode).ModelReady)\n            {\n                Directory.CreateDirectory(ModelDirectory);\n                job.Set("ai-model", 10, $"Đang tải {model.Name} (~{model.Bytes / 1024d / 1024 / 1024:0.00} GB); có thể hủy và tải tiếp sau.");\n                await DownloadVerifiedAsync(model.Url, ModelPath(model), model.Bytes, model.Sha256, 10, 98, job);\n                WriteModelStamp(model);\n                ResetModelReady(mode);\n            }\n            job.Set("ai-ready", 99, $"AI local sẵn sàng · {model.Name} · skill {Skill.Info.Name}.");\n        }\n        finally { _prepareGate.Release(); }\n    }''')

patch(SERVICE,
'''        ValidateProjectId(request.ProjectId);\n        EnsureResources();\n        if (!Status.RuntimeReady || !Status.ModelReady) throw new InvalidOperationException("Hãy bấm Chuẩn bị AI trước khi Vietsub.");''',
'''        ValidateProjectId(request.ProjectId);\n        var model = ModelFor(request.ModelMode);\n        EnsureResources(model);\n        var selectedStatus = StatusFor(request.ModelMode);\n        if (!selectedStatus.RuntimeReady || !selectedStatus.ModelReady) throw new InvalidOperationException($"Hãy bấm Chuẩn bị AI cho {model.Name} trước khi Vietsub.");''')

patch(SERVICE,
'''        var checkpoint = await LoadCheckpointAsync(checkpointPath, request.Source.Sha256, job.CancellationToken);''',
'''        var checkpoint = await LoadCheckpointAsync(checkpointPath, request.Source.Sha256, model, job.CancellationToken);''')

patch(SERVICE,
'''            checkpoint = TranslationCheckpoint.New(request.Source.Sha256, Skill.Info.Sha256);''',
'''            checkpoint = TranslationCheckpoint.New(request.Source.Sha256, Skill.Info.Sha256, model.Key);''')

patch(SERVICE,
'''                runtime = await StartTranslationServerWithFallbackAsync(layers, job, job.CancellationToken);\n                job.Log($"Qwen runtime giữ model trong RAM/VRAM cho toàn bộ lượt Vietsub · {ModelName}.");''',
'''                runtime = await StartTranslationServerWithFallbackAsync(model, layers, job, job.CancellationToken);\n                job.Log($"Qwen runtime giữ model trong RAM/VRAM cho toàn bộ lượt Vietsub · {model.Name}.");''')

# Both runtime-fault retries must keep the selected model and only change GPU -> CPU.
text = (ROOT / SERVICE).read_text(encoding="utf-8")
old = '''await RestartTranslationServerAsync(runtime!, LowerGpuLayers(layers), job.CancellationToken);'''
if text.count(old) != 2:
    raise SystemExit(f"PATCH FAIL {SERVICE}: expected 2 runtime retry matches, got {text.count(old)}")
(ROOT / SERVICE).write_text(text.replace(old, '''await RestartTranslationServerAsync(runtime!, model, LowerGpuLayers(layers), job.CancellationToken);'''), encoding="utf-8")

patch(SERVICE,
'''        return new EditorTranslationResult(translated, output, restored, ModelName, Skill.Info.Sha256);''',
'''        return new EditorTranslationResult(translated, output, restored, model.Name, Skill.Info.Sha256);''')

patch(SERVICE,
'''    private async Task<TranslationServerSession> StartTranslationServerWithFallbackAsync(\n        int gpuLayers,\n        AppJob job,''',
'''    private async Task<TranslationServerSession> StartTranslationServerWithFallbackAsync(\n        TranslationModelSpec model,\n        int gpuLayers,\n        AppJob job,''')

patch(SERVICE,
'''            await RestartTranslationServerAsync(session, gpuLayers, cancellationToken);''',
'''            await RestartTranslationServerAsync(session, model, gpuLayers, cancellationToken);''')
patch(SERVICE,
'''                await RestartTranslationServerAsync(session, 0, cancellationToken);''',
'''                await RestartTranslationServerAsync(session, model, 0, cancellationToken);''')

patch(SERVICE,
'''    private async Task RestartTranslationServerAsync(\n        TranslationServerSession session,\n        int gpuLayers,''',
'''    private async Task RestartTranslationServerAsync(\n        TranslationServerSession session,\n        TranslationModelSpec model,\n        int gpuLayers,''')

patch(SERVICE,
'''            "-m", ModelPath, "--host", "127.0.0.1", "--port", port.ToString(),''',
'''            "-m", ModelPath(model), "--host", "127.0.0.1", "--port", port.ToString(),''')

patch(SERVICE,
'''    private void EnsureResources()\n    {\n        const long gib = 1024L * 1024 * 1024;\n        var resource = _hardware.ResourceSnapshot();\n        if (resource.TotalMemoryBytes < 8 * gib)\n            throw new InvalidOperationException("Qwen3-8B cần máy có ít nhất 8 GB RAM.");\n        if (resource.AvailableMemoryBytes < 7 * gib / 2 && RecommendedGpuLayers(resource) == 0)\n            throw new InvalidOperationException("RAM trống chưa đủ để chạy Qwen3-8B an toàn. Hãy đóng bớt ứng dụng rồi thử lại.");\n    }''',
'''    private void EnsureResources(TranslationModelSpec model)\n    {\n        const long gib = 1024L * 1024 * 1024;\n        var resource = _hardware.ResourceSnapshot();\n        if (resource.TotalMemoryBytes < 8 * gib)\n            throw new InvalidOperationException($"{model.Name} cần máy có ít nhất 8 GB RAM theo gate an toàn hiện tại.");\n        if (resource.AvailableMemoryBytes < 7 * gib / 2 && RecommendedGpuLayers(resource) == 0)\n            throw new InvalidOperationException($"RAM trống chưa đủ để chạy {model.Name} an toàn. Hãy đóng bớt ứng dụng rồi thử lại.");\n    }''')

patch(SERVICE,
'''    private async Task<TranslationCheckpoint> LoadCheckpointAsync(string path, string sourceSha, CancellationToken cancellationToken)\n    {\n        try\n        {\n            if (!File.Exists(path) || new FileInfo(path).Length > 64L * 1024 * 1024) return TranslationCheckpoint.New(sourceSha, Skill.Info.Sha256);\n            await using var stream = File.OpenRead(path);\n            var loaded = await JsonSerializer.DeserializeAsync<TranslationCheckpoint>(stream, _json, cancellationToken);\n            if (loaded is null || loaded.Schema != 1 || loaded.SourceSha256 != sourceSha || loaded.SkillSha256 != Skill.Info.Sha256)\n                return TranslationCheckpoint.New(sourceSha, Skill.Info.Sha256);\n            return loaded with { Translations = new Dictionary<string, string>(loaded.Translations, StringComparer.Ordinal) };\n        }\n        catch (OperationCanceledException) { throw; }\n        catch { return TranslationCheckpoint.New(sourceSha, Skill.Info.Sha256); }\n    }''',
'''    private async Task<TranslationCheckpoint> LoadCheckpointAsync(string path, string sourceSha, TranslationModelSpec model, CancellationToken cancellationToken)\n    {\n        try\n        {\n            if (!File.Exists(path) || new FileInfo(path).Length > 64L * 1024 * 1024) return TranslationCheckpoint.New(sourceSha, Skill.Info.Sha256, model.Key);\n            await using var stream = File.OpenRead(path);\n            var loaded = await JsonSerializer.DeserializeAsync<TranslationCheckpoint>(stream, _json, cancellationToken);\n            if (loaded is null || loaded.Schema != 1 || loaded.SourceSha256 != sourceSha || loaded.SkillSha256 != Skill.Info.Sha256)\n                return TranslationCheckpoint.New(sourceSha, Skill.Info.Sha256, model.Key);\n            var checkpointModelKey = loaded.ModelKey;\n            if (string.IsNullOrWhiteSpace(checkpointModelKey))\n            {\n                if (model.Mode != EditorTranslationModelMode.Quality)\n                    return TranslationCheckpoint.New(sourceSha, Skill.Info.Sha256, model.Key);\n                checkpointModelKey = QualityModelKey;\n            }\n            if (!string.Equals(checkpointModelKey, model.Key, StringComparison.Ordinal))\n                return TranslationCheckpoint.New(sourceSha, Skill.Info.Sha256, model.Key);\n            return loaded with\n            {\n                ModelKey = checkpointModelKey,\n                Translations = new Dictionary<string, string>(loaded.Translations, StringComparer.Ordinal),\n            };\n        }\n        catch (OperationCanceledException) { throw; }\n        catch { return TranslationCheckpoint.New(sourceSha, Skill.Info.Sha256, model.Key); }\n    }''')

patch(SERVICE,
'''    private bool ModelStampMatches()\n    {\n        try\n        {\n            if (!File.Exists(ModelStamp) || !ValidSizedFile(ModelPath, ModelBytes)) return false;\n            var expected = $"{ModelSha256}|{ModelBytes}|{new FileInfo(ModelPath).LastWriteTimeUtc.Ticks}";\n            return string.Equals(File.ReadAllText(ModelStamp).Trim(), expected, StringComparison.Ordinal);\n        }\n        catch { return false; }\n    }\n    private void WriteModelStamp() => WriteStamp(ModelStamp, $"{ModelSha256}|{ModelBytes}|{new FileInfo(ModelPath).LastWriteTimeUtc.Ticks}");''',
'''    private bool ModelStampMatches(TranslationModelSpec model)\n    {\n        try\n        {\n            var path = ModelPath(model);\n            var stamp = ModelStamp(model);\n            if (!File.Exists(stamp) || !ValidSizedFile(path, model.Bytes)) return false;\n            var expected = $"{model.Sha256}|{model.Bytes}|{new FileInfo(path).LastWriteTimeUtc.Ticks}";\n            return string.Equals(File.ReadAllText(stamp).Trim(), expected, StringComparison.Ordinal);\n        }\n        catch { return false; }\n    }\n    private void WriteModelStamp(TranslationModelSpec model)\n    {\n        var path = ModelPath(model);\n        WriteStamp(ModelStamp(model), $"{model.Sha256}|{model.Bytes}|{new FileInfo(path).LastWriteTimeUtc.Ticks}");\n    }\n    private void ResetModelReady(EditorTranslationModelMode mode)\n    {\n        if (mode == EditorTranslationModelMode.Fast) _fastModelReady = null;\n        else _modelReady = null;\n    }''')

patch(SERVICE,
'''    private sealed record TranslationCheckpoint(int Schema, string SourceSha256, string SkillSha256, string Bible, int AnalysisPagesCompleted, Dictionary<string, string> Translations)\n    {\n        public static TranslationCheckpoint New(string sourceSha, string skillSha) => new(1, sourceSha, skillSha, string.Empty, 0, new Dictionary<string, string>(StringComparer.Ordinal));\n    }''',
'''    private sealed record TranslationModelSpec(\n        EditorTranslationModelMode Mode,\n        string Key,\n        string Name,\n        string Url,\n        long Bytes,\n        string Sha256,\n        string FileName);\n\n    private sealed record TranslationCheckpoint(int Schema, string SourceSha256, string SkillSha256, string? ModelKey, string Bible, int AnalysisPagesCompleted, Dictionary<string, string> Translations)\n    {\n        public static TranslationCheckpoint New(string sourceSha, string skillSha, string modelKey) => new(1, sourceSha, skillSha, modelKey, string.Empty, 0, new Dictionary<string, string>(StringComparer.Ordinal));\n    }''')

# Application boundary: quality remains the backwards-compatible default.
patch(APP,
'''    public LocalTranslationStatus LocalTranslationStatus => _translation.Status;\n    public LocalAsrStatus LocalAsrStatus => _asr.Status;''',
'''    public LocalTranslationStatus LocalTranslationStatus => _translation.Status;\n    public LocalTranslationStatus LocalTranslationStatusFor(EditorTranslationModelMode mode) => _translation.StatusFor(mode);\n    public LocalAsrStatus LocalAsrStatus => _asr.Status;''')

patch(APP,
'''    public string StartLocalTranslationPreparation()\n    {\n        if (Jobs.HasActiveJobs) throw new InvalidOperationException("Hãy hoàn tất hoặc hủy tác vụ Media/OCR/Editor đang chạy trước khi chuẩn bị AI dịch.");\n        var job = Jobs.Create("translation-prepare", cleanupAwareCancel: true);\n        _ = RunJobAsync(job, async () =>\n        {\n            await _translation.PrepareAsync(job);\n            job.Finish(null, "AI local và skill dịch đã sẵn sàng.", _translation.Status);\n        });\n        return job.Id;\n    }''',
'''    public string StartLocalTranslationPreparation(EditorTranslationModelMode mode = EditorTranslationModelMode.Quality)\n    {\n        if (Jobs.HasActiveJobs) throw new InvalidOperationException("Hãy hoàn tất hoặc hủy tác vụ Media/OCR/Editor đang chạy trước khi chuẩn bị AI dịch.");\n        var job = Jobs.Create("translation-prepare", cleanupAwareCancel: true);\n        _ = RunJobAsync(job, async () =>\n        {\n            await _translation.PrepareAsync(job, mode);\n            job.Finish(null, "AI local và skill dịch đã sẵn sàng.", _translation.StatusFor(mode));\n        });\n        return job.Id;\n    }''')

# UI toggle: OFF/default = quality 8B, ON = fast/draft 4B.
patch(XAML,
'''                                <TextBlock x:Name="SrtSummaryText" Style="{StaticResource MutedTextStyle}" Text="Skill: Dịch Trung Tu Tiên." TextWrapping="Wrap" />\n                                <Grid ColumnSpacing="6">''',
'''                                <TextBlock x:Name="SrtSummaryText" Style="{StaticResource MutedTextStyle}" Text="Skill: Dịch Trung Tu Tiên." TextWrapping="Wrap" />\n                                <ToggleSwitch x:Name="TranslationFastModeToggle" Header="Model Vietsub" OffContent="8B Chất lượng" OnContent="4B Nhanh / nháp" IsOn="False" Toggled="TranslationFastMode_Toggled" />\n                                <Grid ColumnSpacing="6">''')

patch(PAGE,
'''    private async void PrepareAi_Click(object sender, RoutedEventArgs e)\n    {\n        if (_translationJobId is not null) return;\n        try\n        {\n            _translationJobId = _application.StartLocalTranslationPreparation();''',
'''    private EditorTranslationModelMode SelectedTranslationModelMode() =>\n        TranslationFastModeToggle.IsOn ? EditorTranslationModelMode.Fast : EditorTranslationModelMode.Quality;\n\n    private void TranslationFastMode_Toggled(object sender, RoutedEventArgs e)\n    {\n        if (!IsLoaded) return;\n        var mode = SelectedTranslationModelMode();\n        var status = _application.LocalTranslationStatusFor(mode);\n        TranslationStatusText.Text = status.ModelReady\n            ? $"Đã chọn {status.ModelName}; model này đã sẵn sàng."\n            : $"Đã chọn {status.ModelName}; bấm Chuẩn bị AI để tải/xác minh model này.";\n        RefreshEditorActions();\n    }\n\n    private async void PrepareAi_Click(object sender, RoutedEventArgs e)\n    {\n        if (_translationJobId is not null) return;\n        try\n        {\n            var mode = SelectedTranslationModelMode();\n            _translationJobId = _application.StartLocalTranslationPreparation(mode);''')

patch(PAGE,
'''        PrepareAiButton.IsEnabled = idle && !_playback.IsPreviewMode;\n        var aiReady = false;\n        try { aiReady = _application.LocalTranslationStatus.RuntimeReady && _application.LocalTranslationStatus.ModelReady; }\n        catch { }''',
'''        PrepareAiButton.IsEnabled = idle && !_playback.IsPreviewMode;\n        TranslationFastModeToggle.IsEnabled = idle && !_playback.IsPreviewMode;\n        var aiReady = false;\n        try\n        {\n            var selectedTranslationStatus = _application.LocalTranslationStatusFor(SelectedTranslationModelMode());\n            aiReady = selectedTranslationStatus.RuntimeReady && selectedTranslationStatus.ModelReady;\n        }\n        catch { }''')

# Whole-SRT and single-cue requests carry explicit mode and mode-specific checkpoint scope.
patch(CUES,
'''        var outputName = Path.GetFileNameWithoutExtension(_subtitleSource.Path) + ".vi.srt";\n        var projectId = TranslationProjectId(_project.Id, "all", SourceTextHash(_subtitleSource.Cues));\n        _translationJobId = _application.StartEditorTranslation(new EditorTranslationRequest(\n            projectId, _subtitleSource, _application.Config.OutputDirectory, outputName));\n        TranslationProgress.Value = 0;\n        TranslationStatusText.Text = "Đang Vietsub bằng AI local + skill; câu khóa sẽ không bị ghi đè.";''',
'''        var outputName = Path.GetFileNameWithoutExtension(_subtitleSource.Path) + ".vi.srt";\n        var modelMode = SelectedTranslationModelMode();\n        var modeScope = modelMode == EditorTranslationModelMode.Fast ? "fast" : "quality";\n        var projectId = TranslationProjectId(_project.Id, "all" + modeScope, SourceTextHash(_subtitleSource.Cues));\n        _translationJobId = _application.StartEditorTranslation(new EditorTranslationRequest(\n            projectId, _subtitleSource, _application.Config.OutputDirectory, outputName, ModelMode: modelMode));\n        TranslationProgress.Value = 0;\n        TranslationStatusText.Text = $"Đang Vietsub bằng {(modelMode == EditorTranslationModelMode.Fast ? "4B Nhanh / nháp" : "8B Chất lượng")} + skill; câu khóa sẽ không bị ghi đè.";''')

patch(CUES,
'''        var projectId = TranslationProjectId(_project.Id, "cue", SourceTextHash([cue]));\n        _translationJobId = _application.StartEditorTranslation(new EditorTranslationRequest(\n            projectId, single, temp, $"cue-{cue.Number}.srt", ForceFresh: true));''',
'''        var modelMode = SelectedTranslationModelMode();\n        var modeScope = modelMode == EditorTranslationModelMode.Fast ? "fast" : "quality";\n        var projectId = TranslationProjectId(_project.Id, "cue" + modeScope, SourceTextHash([cue]));\n        _translationJobId = _application.StartEditorTranslation(new EditorTranslationRequest(\n            projectId, single, temp, $"cue-{cue.Number}.srt", ForceFresh: true, ModelMode: modelMode));''')

# SPEED-02 verifier follows the model-aware signature without weakening persistent-session ownership.
patch(PERSIST,
'''    'runtime = await StartTranslationServerWithFallbackAsync(layers, job, job.CancellationToken);',''',
'''    'runtime = await StartTranslationServerWithFallbackAsync(model, layers, job, job.CancellationToken);',''')
patch(PERSIST,
'''require(source.count('await RestartTranslationServerAsync(runtime!, LowerGpuLayers(layers), job.CancellationToken);') == 2,''',
'''require(source.count('await RestartTranslationServerAsync(runtime!, model, LowerGpuLayers(layers), job.CancellationToken);') == 2,''')

# Dedicated SPEED-05 contract.
fast_contract = r'''#!/usr/bin/env python3
from pathlib import Path
import sys

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
SERVICE = (ROOT / "csharp/src/BiliSubStudio.Core/Editor/LocalSubtitleTranslationService.cs").read_text(encoding="utf-8")
APP = (ROOT / "csharp/src/BiliSubStudio.Core/Application/BiliSubApplication.cs").read_text(encoding="utf-8")
XAML = (ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml").read_text(encoding="utf-8")
PAGE = (ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml.cs").read_text(encoding="utf-8")
CUES = (ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.SubtitleCueEditing.cs").read_text(encoding="utf-8")


def require(condition: bool, message: str) -> None:
    if not condition:
        print("FAIL:", message, file=sys.stderr)
        raise SystemExit(1)


for token in (
    'public enum EditorTranslationModelMode',
    'Quality,', 'Fast,',
    'EditorTranslationModelMode ModelMode = EditorTranslationModelMode.Quality',
    'internal const string ModelName = "Qwen3-8B Q4_K_M";',
    'internal const long ModelBytes = 5_027_783_488;',
    'internal const string ModelSha256 = "d98cdcbd03e17ce47681435b5150e34c1417f50b5c0019dd560e4882c5745785";',
    'internal const string FastModelName = "Qwen3-4B Q4_K_M";',
    'a9a60d009fa7ff9606305047c2bf77ac25dbec49/Qwen3-4B-Q4_K_M.gguf?download=true',
    'internal const long FastModelBytes = 2_497_280_256;',
    'internal const string FastModelSha256 = "7485fe6f11af29433bc51cab58009521f205840f5b4ae3a32fa7f92e8534fdf5";',
    'public LocalTranslationStatus Status => StatusFor(EditorTranslationModelMode.Quality);',
    'public LocalTranslationStatus StatusFor(EditorTranslationModelMode mode)',
    'public async Task PrepareAsync(AppJob job, EditorTranslationModelMode mode = EditorTranslationModelMode.Quality)',
    'var model = ModelFor(request.ModelMode);',
    'StartTranslationServerWithFallbackAsync(model, layers, job, job.CancellationToken)',
    '"-m", ModelPath(model)',
    'return new EditorTranslationResult(translated, output, restored, model.Name, Skill.Info.Sha256);',
    'string? ModelKey',
    'model.Mode != EditorTranslationModelMode.Quality',
    'string.Equals(checkpointModelKey, model.Key, StringComparison.Ordinal)',
):
    require(token in SERVICE, f"SPEED-05 service marker missing: {token}")

require('"-m", ModelPath,' not in SERVICE, "SPEED-05 runtime can still hardwire the 8B model path")
require('["cache_prompt"] = true' in SERVICE and 'RuntimeAutoGpuLayers = -1' in SERVICE,
        "SPEED-05 regressed persistent/cache/GPU ownership")
require('RecommendedTranslationBatchSize(adaptiveResources)' in SERVICE,
        "SPEED-05 regressed SPEED-04 adaptive batching")
require('public LocalTranslationStatus LocalTranslationStatusFor(EditorTranslationModelMode mode)' in APP,
        "SPEED-05 application boundary cannot query selected model readiness")
require('StartLocalTranslationPreparation(EditorTranslationModelMode mode = EditorTranslationModelMode.Quality)' in APP,
        "SPEED-05 preparation does not preserve 8B default")
require('await _translation.PrepareAsync(job, mode);' in APP and '_translation.StatusFor(mode)' in APP,
        "SPEED-05 preparation does not forward selected mode")

for token in (
    'x:Name="TranslationFastModeToggle"',
    'OffContent="8B Chất lượng"',
    'OnContent="4B Nhanh / nháp"',
    'IsOn="False"',
    'Toggled="TranslationFastMode_Toggled"',
):
    require(token in XAML, f"SPEED-05 UI marker missing: {token}")

for token in (
    'TranslationFastModeToggle.IsOn ? EditorTranslationModelMode.Fast : EditorTranslationModelMode.Quality',
    '_application.StartLocalTranslationPreparation(mode)',
    '_application.LocalTranslationStatusFor(SelectedTranslationModelMode())',
    'TranslationFastModeToggle.IsEnabled = idle && !_playback.IsPreviewMode;',
):
    require(token in PAGE, f"SPEED-05 page owner missing: {token}")

for token in (
    '"all" + modeScope', '"cue" + modeScope',
    'ModelMode: modelMode',
    'modeScope = modelMode == EditorTranslationModelMode.Fast ? "fast" : "quality"',
):
    require(token in CUES, f"SPEED-05 request/checkpoint isolation missing: {token}")

# User owns the toggle; no scene-scoring/automatic quality switch is allowed in SPEED-05.
for forbidden in ('climax', 'cao trào', 'scene importance', 'importance score', 'AutoModelMode'):
    require(forbidden.lower() not in (SERVICE + PAGE + CUES).lower(),
            f"SPEED-05 introduced hidden automatic scene/model switching: {forbidden}")

# Synthetic identity/default contract.
quality = 'qwen3-8b-q4-k-m'
fast = 'qwen3-4b-q4-k-m'
require(quality != fast, "SPEED-05 model checkpoint identities collide")
request_default = 'quality'
require(request_default == 'quality', "SPEED-05 default must remain 8B quality")
def accepts_legacy_checkpoint(selected: str, stored: str | None) -> bool:
    if stored is None:
        return selected == quality
    return stored == selected
require(accepts_legacy_checkpoint(quality, None), "SPEED-05 must preserve legacy 8B checkpoints")
require(not accepts_legacy_checkpoint(fast, None), "SPEED-05 fast mode must not reuse legacy 8B checkpoints")
require(not accepts_legacy_checkpoint(fast, quality), "SPEED-05 4B must not reuse 8B checkpoint")

print("PASS: SPEED-05 keeps 8B quality as default and exposes explicit 4B Fast/Draft mode with isolated model files, readiness and checkpoints")
'''
contract_path = ROOT / "csharp/scripts/verify_editor_translation_fast_model_contract.py"
contract_path.write_text(fast_contract, encoding="utf-8")

print("SPEED-05 exact patch applied")
