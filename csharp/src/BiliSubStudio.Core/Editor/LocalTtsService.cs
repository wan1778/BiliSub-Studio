using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BiliSubStudio.Core.Configuration;
using BiliSubStudio.Core.Jobs;
using BiliSubStudio.Core.Processes;
using BiliSubStudio.Core.Tools;

namespace BiliSubStudio.Core.Editor;

public sealed record EditorVoiceTrack(string Path, double Start, double Duration, double Gain = 1);
public sealed record EditorTtsCueResult(
    string Id,
    string Voice,
    string VoiceClass,
    double VoiceConfidence,
    string Status,
    double RawDuration,
    double FittedDuration);
public sealed record EditorTtsCueWindow(
    string Id,
    double VoiceStart,
    double VoiceEnd,
    string TimingSource,
    string Status);
public sealed record EditorTtsRequest(
    string ProjectId,
    string SourcePath,
    double Duration,
    EditorSubtitleSource Subtitle,
    string SpeechAnalysisPath,
    string SpeechAnalysisSha256,
    string Voice = "ngoc_huyen");
public sealed record EditorTtsResult(
    string ManifestPath,
    string ManifestSha256,
    EditorVoiceTrack VoiceTrack,
    IReadOnlyList<EditorTtsCueResult> Cues,
    int ReviewCount,
    string Engine,
    string EngineVersion,
    string Voice,
    IReadOnlyList<EditorTtsCueWindow> CueWindows);

internal sealed partial class LocalTtsService : IDisposable
{
    private const int ManifestSchema = 2;
    private const int VoiceSampleRate = 22050;
    private const string TimingAlgorithm = LocalTtsInstaller.TimingAlgorithm;
    private readonly SemaphoreSlim _generationGate = new(1, 1);
    private readonly AppPaths _paths;
    private readonly LocalTtsInstaller _installer;
    private readonly ToolManager _tools;
    private readonly ProcessRunner _processes;
    private readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public LocalTtsService(AppPaths paths, LocalTtsInstaller installer, ToolManager tools, ProcessRunner processes)
    {
        _paths = paths;
        _installer = installer;
        _tools = tools;
        _processes = processes;
    }

    public LocalTtsStatus Status => _installer.Status;

    public async Task<EditorTtsResult> GenerateAsync(AppJob job, EditorTtsRequest request)
    {
        ValidateRequest(request);
        var sourceInfo = new FileInfo(Path.GetFullPath(request.SourcePath));
        var expectedSourceKey = EditorSpeechAnalysisDocument.SourceKey(sourceInfo.FullName, sourceInfo.Length,
            sourceInfo.LastWriteTimeUtc.Ticks, request.Duration, LocalAsrInstaller.ModelRevision);
        var analysis = await EditorSpeechAnalysisDocument.LoadVerifiedAsync(
            request.SpeechAnalysisPath, request.SpeechAnalysisSha256, job.CancellationToken).ConfigureAwait(false);
        if (!string.Equals(analysis.SourceKey, expectedSourceKey, StringComparison.Ordinal))
            throw new InvalidDataException("Whisper timing không còn khớp video nguồn; hãy phân tích nhịp lại trước khi tạo voice.");
        // Collapse only proven OCR flicker runs (for example A/B/A with a
        // sub-250 ms frame) before speech mapping. The subtitle document stays
        // untouched; the voice must not read the same live-caption frame twice.
        // Vá cửa sổ voice hẹp sau gộp flicker (23/09): mảnh <0.15s không giọng
        // nào đọc vừa -> worker giữ im lặng oan. Chỉ nới khung/gộp mảnh trùng
        // y hệt, không đụng chữ, không lấn câu khác.
        var voiceCues = EditorVoiceCuePlanner.RemoveUnspeakable(
            EditorVoiceCuePlanner.RepairVoiceWindows(
                EditorVoiceCuePlanner.Build(request.Subtitle.Cues)));
        if (voiceCues.Count == 0)
            throw new InvalidDataException("Không có cue chứa chữ hoặc số để tạo voice.");
        var voice = ResolveVoice(request.Voice);
        // Nới cue chật bằng khe im kề (01/10, chỉ VieNeu): trị gốc thay vì ép
        // voice. Chạy trước MapToCues để timing/manifest/validator thấy khung
        // đã nới là ground truth.
        voiceCues = EditorVoiceCuePlanner.WidenTightWindows(voiceCues, voice, request.Duration);
        if (voiceCues.Count == 0)
            throw new InvalidDataException("Không có cue chứa chữ hoặc số để tạo voice.");
        // ASR is advisory metadata only (for example voice classification). It
        // cannot remove a cue or own TTS placement: every speakable SRT cue is
        // synthesized over its complete SRT interval.
        var mappedTiming = await Task.Run(
            () => EditorSpeechAnalysisDocument.MapToCues(analysis, voiceCues, job.CancellationToken),
            job.CancellationToken).ConfigureAwait(false);
        var cueTiming = mappedTiming.ToDictionary(x => x.CueId, StringComparer.Ordinal);
        var cues = voiceCues.Select(cue => BuildWholeCue(cue, voice, cueTiming[cue.Id])).ToArray();
        return await GenerateCuesAsync(job, request.ProjectId, request.Duration, voice, cues, cueTiming);
    }

    public Task<EditorTtsResult> GenerateSampleAsync(AppJob job, string voice)
    {
        voice = ResolveVoice(voice);
        var sampleText = IsVieneuVoice(voice)
            ? $"Xin chào, đây là giọng đọc thử {TtsVoiceCatalog.DisplayLabel(voice)}. Chúc bạn một ngày thật bình an."
            : "Xin chào, đây là giọng Ngọc Huyền. Chúc bạn một ngày thật bình an.";
        var cue = new TtsCueManifest("voice-demo-cue", 0, 10, voice,
            sampleText, 0, 10, "sample");
        // A text-only sample uses real synthesis, without a fabricated source or Whisper analysis.
        return GenerateCuesAsync(job, "voice-demo-" + voice, 10, voice, [cue],
            new Dictionary<string, EditorCueSpeechTiming>());
    }

    internal static TtsCueManifest BuildWholeCue(EditorSubtitleCue cue, string voice, EditorCueSpeechTiming timing)
    {
        var text = VietnameseTtsTextNormalizer.Normalize(cue.VietnameseText);
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException($"Cue {cue.Number} chưa có câu Việt để tạo voice.");
        if (!VietnameseTtsTextNormalizer.HasSpeakableUnits(text))
            throw new InvalidDataException($"Cue {cue.Number} chỉ có dấu câu/ký hiệu nên không có lời để tạo voice.");
        if (timing is null || timing.CueId != cue.Id || timing.CueStart != cue.Start || timing.CueEnd != cue.End)
            throw new InvalidDataException($"Cue {cue.Number} không khớp dữ liệu timing của SRT hiện tại.");
        if (!HasSampleRange(cue.Start, cue.End))
            throw new InvalidDataException($"Cue {cue.Number} có timecode SRT rỗng.");
        // `srt-fallback` is the existing worker wire value for a full SRT-owned
        // window. It is now the primary production route: never shrink or shift
        // voice timing to the ASR speech envelope.
        return new TtsCueManifest(cue.Id, cue.Start, cue.End, voice, text, cue.Start, cue.End, "srt-fallback");
    }

    private static bool HasSampleRange(double start, double end) =>
        double.IsFinite(start) && double.IsFinite(end) && end > start
        && Math.Round(end * VoiceSampleRate) > Math.Round(start * VoiceSampleRate);

    private static string ResolveVoice(string? voice)
    {
        var value = LocalTtsInstaller.CanonicalVoiceId(string.IsNullOrWhiteSpace(voice) ? LocalTtsInstaller.Voice : voice);
        if (!LocalTtsInstaller.AvailableVoices.Contains(value, StringComparer.Ordinal))
            throw new InvalidDataException($"Giọng {value} chưa có model được xác minh.");
        return value;
    }

    // Engine theo giọng (VieNeu pilot chạy song song NGHI): mọi pin
    // engine/version/revision/timing suy từ voice id, không hardcode NGHI.
    private static bool IsVieneuVoice(string voice) => LocalTtsInstaller.IsVieneuVoice(voice);
    private static string EngineFor(string voice) =>
        IsVieneuVoice(voice) ? LocalTtsInstaller.VieneuEngine : LocalTtsInstaller.Engine;
    private static string EngineVersionFor(string voice) =>
        IsVieneuVoice(voice) ? LocalTtsInstaller.VieneuEngineVersion : LocalTtsInstaller.EngineVersion;
    private static string VoiceRevisionFor(string voice) =>
        IsVieneuVoice(voice) ? LocalTtsInstaller.VieneuVoiceRevision : LocalTtsInstaller.VoiceRevision;
    private static string TimingAlgorithmFor(string voice) =>
        IsVieneuVoice(voice) ? LocalTtsInstaller.VieneuTimingAlgorithm : TimingAlgorithm;

    // Cue chật/worker chết đột ngột không được giết cả job (vụ 13:17 27/09:
    // worker tắt không traceback, C# chỉ thấy exit + stderr trống): mỗi lần
    // fail giữ hiện trường (failed-runs, tối đa 3) + báo đủ chi tiết (exit,
    // ready/complete, cue đang làm dở). Luật cứng của user: KHÔNG bỏ cue nào
    // (không im lặng thay voice) -> tự chạy lại toàn bộ tối đa 1 lần từ cache
    // (cue xong rồi ăn ngay, cue chết chạy lại; chết tiếp ở cùng chỗ thì lỗi
    // thẳng có chẩn đoán). Fail ở nạp model (chưa có cue dở) cũng chạy lại 1
    // lần vì có thể do tranh tài nguyên thoáng qua.
    private const int MaxAutoRetries = 1;

    private sealed class TtsWorkerFailedException(string message, int lastIndex, int total, string? lastId)
        : InvalidOperationException(message)
    {
        public int LastIndex { get; } = lastIndex;
        public int Total { get; } = total;
        public string? LastId { get; } = lastId;
    }

    internal static string BuildWorkerFailureMessage(
        int exitCode, bool ready, bool completed, string reportedResult, string resultPath,
        int lastIndex, int total, string? lastId, string stderrTail) =>
        $"Voice worker dừng bất thường (exit {exitCode}, ready={ready}, complete={completed}, " +
        $"result báo '{reportedResult}' vs chờ '{resultPath}'). " +
        (total > 0
            ? $"Dừng ở cue {lastIndex}/{total}" + (string.IsNullOrWhiteSpace(lastId) ? string.Empty : $" (id {lastId})") + ". "
            : "Chết trước khi tổng hợp cue nào (nạp model/worker). ") +
        "stderr: " + (string.IsNullOrWhiteSpace(stderrTail) ? "(trống — chết ngoài Python, xem failed-runs)" : stderrTail);

    private async Task<EditorTtsResult> GenerateCuesAsync(AppJob job, string projectId, double duration, string voice,
        IReadOnlyList<TtsCueManifest> cues, IReadOnlyDictionary<string, EditorCueSpeechTiming> cueTiming)
    {
        await _generationGate.WaitAsync(job.CancellationToken);
        try
        {
            var runtime = await _installer.PrepareAsync(job, voice, 35);
            var ffmpeg = await _tools.EnsureFfmpegAsync(job.CancellationToken);
            var outputRoot = Path.Combine(_paths.Cache, "Editor", "TTS", projectId);
            TtsWorkerFailedException? lastFailure = null;
            for (var attempt = 0; attempt <= MaxAutoRetries; attempt++)
            {
                var runId = Guid.NewGuid().ToString("N");
                var runRoot = Path.Combine(outputRoot, "runs", runId);
                var inputPath = Path.Combine(runRoot, "input.json");
                var resultPath = Path.Combine(outputRoot, $"result-{runId}.json");
                var masterPath = Path.Combine(outputRoot, $"voice-master-{runId}.flac");
                Directory.CreateDirectory(runRoot);
                try
                {
                    return await RunWorkerOnceAsync(
                        job, runtime, ffmpeg, outputRoot, runRoot, inputPath, resultPath, masterPath,
                        projectId, duration, voice, cues, cueTiming, job.CancellationToken);
                }
                catch (TtsWorkerFailedException failure)
                {
                    job.CancellationToken.ThrowIfCancellationRequested();
                    lastFailure = failure;
                    if (attempt < MaxAutoRetries)
                    {
                        job.Warn($"Worker TTS dừng đột ngột lần {attempt + 1}; tự chạy lại toàn bộ 1 lần từ cache " +
                            $"(cue nào xong rồi ăn ngay, không bỏ cue nào). Chi tiết đã giữ trong failed-runs. {failure.Message}");
                        continue;
                    }
                    throw new InvalidOperationException(failure.Message, failure);
                }
            }
            throw new InvalidOperationException(lastFailure?.Message ?? "Worker TTS thất bại không rõ nguyên nhân.", lastFailure);
        }
        finally { _generationGate.Release(); }
    }

    private async Task<EditorTtsResult> RunWorkerOnceAsync(
        AppJob job, LocalTtsRuntime runtime, string ffmpeg, string outputRoot, string runRoot,
        string inputPath, string resultPath, string masterPath, string projectId, double duration, string voice,
        IReadOnlyList<TtsCueManifest> cues, IReadOnlyDictionary<string, EditorCueSpeechTiming> cueTiming,
        CancellationToken cancellationToken)
    {
        var accepted = false;
        string? failureMessage = null;
        string stderrTail = string.Empty;
        await using var processes = new OwnedProcessGroup();
        try
        {
                var manifest = new TtsInputManifest(ManifestSchema, EngineVersionFor(voice),
                    voice, TimingAlgorithmFor(voice), duration, cues);
                await WriteAtomicJsonAsync(inputPath, manifest, job.CancellationToken);
                job.Set("tts-generate", 37, IsVieneuVoice(voice)
                    ? $"Đang tạo {voice} (VieNeu GPU) · {cues.Count} câu · mỗi cue đọc nguyên câu..."
                    : $"Đang tạo Ngọc Huyền local · {cues.Count} câu · mỗi cue đọc nguyên câu...");
                var ready = false;
                var completed = false;
                var reportedResult = string.Empty;
                var lastAttemptIndex = 0;
                var lastAttemptTotal = 0;
                string? lastAttemptId = null;
                var lastCueIndex = 0;
                string? lastCueId = null;
                string? inFlightId = null;
                var result = await _processes.RunStreamingAsync(runtime.Python,
                    ["-I", "-X", "utf8", runtime.Worker, "--manifest", inputPath,
                     "--model", runtime.Model, "--config", runtime.Config, "--ffmpeg", ffmpeg,
                     "--output-root", outputRoot, "--run-root", runRoot,
                     "--result", resultPath, "--master", masterPath, "--voice", voice],
                    cancellationToken, (line, _) =>
                    {
                        try
                        {
                            return HandleWorkerLine(line);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception error)
                        {
                            throw new InvalidOperationException(
                                $"Voice worker event lỗi: {error.Message} (dòng: {line.Trim()[..Math.Min(160, line.Trim().Length)]})", error);
                        }

                        ValueTask HandleWorkerLine(string eventLine)
                        {
                            if (!TryParseEvent(eventLine, out var parsed)) return ValueTask.CompletedTask;
                            using (parsed)
                            {
                                var root = parsed.RootElement;
                                var kind = GetString(root, "event");
                                if (kind == "ready")
                                    ready = GetString(root, "voice_revision") == VoiceRevisionFor(voice)
                                        && GetString(root, "voice") == voice && GetInt(root, "model_loads") == 1;
                                else if (kind == "attempt")
                                {
                                    var index = GetInt(root, "index");
                                    var total = GetInt(root, "total");
                                    var attempt = GetInt(root, "attempt");
                                    var maximum = GetInt(root, "max_attempts");
                                    lastAttemptIndex = index;
                                    lastAttemptTotal = total;
                                    lastAttemptId = TryGetString(root, "id");
                                    inFlightId = lastAttemptId;
                                    if (total == cues.Count && index >= 1 && index <= total && attempt >= 1
                                        && maximum is >= 1 and <= 12 && attempt <= maximum)
                                        job.Set("tts-generate", 38 + (index - 1) / (double)total * 53,
                                            $"Model đang đọc nguyên câu {index}/{total} · lượt {attempt}/{maximum} · ưu tiên nhịp tự nhiên, chỉ tăng khi SRT quá ngắn...");
                                }
                                else if (kind == "cue")
                                {
                                    var index = GetInt(root, "index");
                                    var total = Math.Max(1, GetInt(root, "total"));
                                    lastCueIndex = index;
                                    lastCueId = TryGetString(root, "id");
                                    if (!string.IsNullOrWhiteSpace(lastCueId)
                                        && string.Equals(lastCueId, inFlightId, StringComparison.Ordinal))
                                        inFlightId = null;
                                    job.Set("tts-generate", 38 + index / (double)total * 53,
                                        $"Đang tạo voice nguyên câu · {index}/{total}...");
                                }
                                else if (kind == "fallback")
                                {
                                    var index = GetInt(root, "index");
                                    var total = Math.Max(1, GetInt(root, "total"));
                                    if (index >= 1 && index <= total)
                                        job.Set("tts-generate", 38 + (index - 1) / (double)total * 53,
                                            $"Cue {index}/{total} quá chật · đang giữ nguyên toàn timecode SRT...");
                                }
                                else if (kind == "block")
                                {
                                    var total = TryGetDouble(root, "total") is { } blockTotal && blockTotal > 0 ? blockTotal : 1d;
                                    var blockIndex = TryGetDouble(root, "index") ?? 0d;
                                    job.Set("tts-mix", 91 + blockIndex / total * 6,
                                        "Đang ghép nguyên câu voice theo timecode SRT, không cắt đuôi...");
                                }
                                else if (kind == "notice")
                                {
                                    // Worker-side watch lists (mistimed cues, boundary drift,
                                    // per-run rate summary). Informational only: unknown future
                                    // notice codes must keep flowing to the log, never fail.
                                    job.Log($"Voice chú ý: {GetString(root, "message")}");
                                }
                                else if (kind == "complete")
                                {
                                    completed = true;
                                    reportedResult = GetString(root, "result");
                                }
                            }
                            return ValueTask.CompletedTask;
                        }
                    }, runtime.Environment, processes);
                job.CancellationToken.ThrowIfCancellationRequested();
                stderrTail = LastLine(result.StandardError);
                if (result.ExitCode != 0 || !ready || !completed || !SamePath(reportedResult, resultPath))
                {
                    failureMessage = BuildWorkerFailureMessage(result.ExitCode, ready, completed, reportedResult, resultPath,
                        lastAttemptIndex > 0 ? lastAttemptIndex : lastCueIndex,
                        lastAttemptTotal > 0 ? lastAttemptTotal : cues.Count,
                        inFlightId ?? lastAttemptId ?? lastCueId, stderrTail);
                    throw new TtsWorkerFailedException(failureMessage,
                        lastAttemptIndex, lastAttemptTotal, inFlightId ?? lastAttemptId);
                }
                var parsedResult = await ReadResultAsync(resultPath, masterPath, duration, runtime.SampleRate, voice, cues, job.CancellationToken);
                var manifestSha = await HashAsync(resultPath, job.CancellationToken);
                var cueResults = parsedResult.Cues.Select(cue =>
                {
                    var timing = cueTiming.TryGetValue(cue.Id, out var value) ? value : null;
                    return new EditorTtsCueResult(cue.Id, cue.Voice, timing?.VoiceClass ?? "uncertain",
                        timing?.VoiceConfidence ?? 0, cue.Status, cue.RawDuration, cue.FittedDuration);
                }).ToArray();
                var cueWindows = parsedResult.Cues.Select(cue => new EditorTtsCueWindow(
                    cue.Id,
                    cue.ClipStartSample / (double)runtime.SampleRate,
                    (cue.ClipStartSample + cue.TargetFrames) / (double)runtime.SampleRate,
                    cue.TimingSource,
                    cue.Status)).ToArray();
                job.CancellationToken.ThrowIfCancellationRequested();
                job.Set("tts-final", 99, parsedResult.ReviewCount == 0
                    ? $"Voice Việt hoàn tất · {cueResults.Length} câu đã canh thời lượng, không cắt đuôi."
                    : $"Voice Việt đã canh theo SRT · {parsedResult.ReviewCount} câu cần nghe lại; câu chật được tăng tốc động, không cắt chữ.");
                accepted = true;
                PruneOldRunsInBackground(outputRoot);
                return new EditorTtsResult(resultPath, manifestSha,
                    new EditorVoiceTrack(masterPath, 0, duration), cueResults, parsedResult.ReviewCount,
                    parsedResult.Engine, parsedResult.EngineVersion, parsedResult.Voice, cueWindows);
            }
            finally
            {
                // Giữ hiện trường TRƯỚC khi dọn (vụ 13:17 27/09: cleanup xóa
                // sạch input/result nên chết không để lại gì để repro).
                if (!accepted)
                    QuarantineFailedRun(outputRoot, Path.GetFileName(runRoot), inputPath,
                        File.Exists(resultPath) ? resultPath : null,
                        failureMessage ?? "worker fail trước khi có chẩn đoán", stderrTail);
                // Cleanup is part of completion/cancellation, never swallowed.
                await processes.StopAsync();
                await CleanupRunAsync(runRoot);
                if (!accepted) { File.Delete(resultPath); File.Delete(masterPath); }
            }
    }

    // Hiện trường fail TTS: input manifest + result dở + chẩn đoán, giữ tối đa
    // 3 lần gần nhất trong cache project (không phải thư mục xuất). Best-effort
    // tuyệt đối: lỗi ở đây không được đè lên lỗi gốc của worker.
    private static void QuarantineFailedRun(
        string outputRoot, string runId, string inputPath, string? partialResultPath,
        string diagnostics, string stderrTail)
    {
        try
        {
            var root = Path.Combine(outputRoot, "failed-runs");
            Directory.CreateDirectory(root);
            var dir = Path.Combine(root, runId);
            Directory.CreateDirectory(dir);
            if (File.Exists(inputPath)) File.Copy(inputPath, Path.Combine(dir, "input.json"), overwrite: true);
            if (!string.IsNullOrWhiteSpace(partialResultPath) && File.Exists(partialResultPath))
                File.Copy(partialResultPath, Path.Combine(dir, "result-partial.json"), overwrite: true);
            File.WriteAllText(Path.Combine(dir, "diag.txt"), diagnostics + "\n--- stderr tail ---\n" + stderrTail);
            foreach (var old in new DirectoryInfo(root).GetDirectories()
                .OrderByDescending(candidate => candidate.CreationTimeUtc).Skip(3))
                try { old.Delete(recursive: true); }
                catch { }
        }
        catch { }
    }

    private static async Task CleanupRunAsync(string runRoot)
    {
        // Kill(entireProcessTree) reaps the Python parent first. Windows can still
        // hold a descendant's file handle briefly while that process exits.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (Directory.Exists(runRoot))
        {
            try { Directory.Delete(runRoot, recursive: true); }
            catch (IOException) when (DateTime.UtcNow < deadline) { await Task.Delay(50); }
        }
    }

    // Tự tỉa cache voice sau mỗi job thành công (user duyệt 05/10, luật
    // giữ-3-bản): giữ 3 result mới nhất + master của chúng + clip chúng
    // tham chiếu; xóa result/master cũ hơn và clip mồ côi (không result nào
    // giữ dùng tới). Chạy nền best-effort tuyệt đối: cấm fail job, cấm đụng
    // file còn mới hơn result mới nhất (job retry/fail vừa xong còn dùng).
    private static void PruneOldRunsInBackground(string outputRoot)
    {
        _ = Task.Run(() =>
        {
            try { PruneOldRuns(outputRoot); }
            catch { }
        }, CancellationToken.None);
    }

    private static void PruneOldRuns(string outputRoot)
    {
        const int keepResults = 3;
        List<FileInfo> results;
        try
        {
            results = new DirectoryInfo(outputRoot).GetFiles("result-*.json")
                .OrderByDescending(candidate => candidate.LastWriteTimeUtc).ToList();
        }
        catch { return; }
        if (results.Count == 0) return;
        var kept = results.Take(keepResults).ToList();
        var newestUtc = kept.Max(candidate => candidate.LastWriteTimeUtc);
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var keptResult in kept)
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(keptResult.FullName));
                if (!document.RootElement.TryGetProperty("cues", out var cues)) continue;
                foreach (var cue in cues.EnumerateArray())
                {
                    if (cue.TryGetProperty("clip_path", out var clipPath)
                        && clipPath.GetString() is { } path && path.Length > 0)
                    {
                        try { referenced.Add(Path.GetFullPath(path)); }
                        catch { }
                    }
                }
            }
            catch { }
        }
        foreach (var dropped in results.Skip(keepResults))
        {
            try { dropped.Delete(); }
            catch { }
            try
            {
                const string prefix = "result-";
                const string suffix = ".json";
                var name = dropped.Name;
                if (name.StartsWith(prefix, StringComparison.Ordinal)
                    && name.EndsWith(suffix, StringComparison.Ordinal))
                {
                    var master = Path.Combine(outputRoot,
                        "voice-master-" + name.Substring(prefix.Length, name.Length - prefix.Length - suffix.Length) + ".flac");
                    if (File.Exists(master)) File.Delete(master);
                }
            }
            catch { }
        }
        var clipsRoot = Path.Combine(outputRoot, "clips");
        if (!Directory.Exists(clipsRoot)) return;
        var pending = new Stack<string>();
        pending.Push(clipsRoot);
        while (pending.TryPop(out var directory))
        {
            string[] files;
            string[] children;
            try
            {
                files = Directory.GetFiles(directory);
                children = Directory.GetDirectories(directory);
            }
            catch { continue; }
            foreach (var child in children) pending.Push(child);
            foreach (var file in files)
            {
                string extension;
                DateTime lastWrite;
                try
                {
                    extension = Path.GetExtension(file).ToLowerInvariant();
                    lastWrite = File.GetLastWriteTimeUtc(file);
                    if ((extension is not ".wav" and not ".json")
                        || lastWrite >= newestUtc
                        || referenced.Contains(Path.GetFullPath(file)))
                        continue;
                    if (extension == ".json"
                        && File.Exists(Path.ChangeExtension(file, ".wav")))
                        continue;
                }
                catch { continue; }
                try { File.Delete(file); }
                catch { }
                if (extension == ".wav")
                {
                    try
                    {
                        var sidecar = Path.ChangeExtension(file, ".json");
                        if (File.Exists(sidecar)) File.Delete(sidecar);
                    }
                    catch { }
                }
            }
        }
    }

    private async Task<TtsWorkerResult> ReadResultAsync(string path, string expectedMaster, double duration,
        int expectedSampleRate, string voice, IReadOnlyList<TtsCueManifest> expectedCues, CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length is <= 0 or > 32L * 1024 * 1024)
            throw new InvalidDataException("Worker TTS không tạo result hợp lệ.");
        await using var stream = File.OpenRead(path);
        var result = await JsonSerializer.DeserializeAsync<TtsWorkerResult>(stream, _json, cancellationToken)
            ?? throw new InvalidDataException("Result TTS rỗng.");
        if (result.Schema != ManifestSchema || result.Cues is null || result.Master is null
            || result.Engine != EngineFor(voice) || result.EngineVersion != EngineVersionFor(voice)
            || result.VoiceRevision != VoiceRevisionFor(voice) || result.Voice != voice
            || result.SampleRate != expectedSampleRate || result.Cues.Count != expectedCues.Count)
            throw new InvalidDataException("Result TTS sai schema/model/voice/cue count.");
        ValidateSentenceGroupWindows(result.Cues, expectedCues, expectedSampleRate, IsVieneuVoice(voice));
        for (var index = 0; index < expectedCues.Count; index++)
        {
            var cue = result.Cues[index];
            var expected = expectedCues[index];
            if (cue.Id != expected.Id || cue.Voice != voice || cue.Status is not ("fit" or "review")
                || !double.IsFinite(cue.RawDuration) || cue.RawDuration <= 0
                || !double.IsFinite(cue.FittedDuration) || cue.FittedDuration <= 0)
                throw new InvalidDataException("Result TTS chứa cue sai hoặc bị thay thứ tự.");
            var usedSrtFallback = expected.TimingSource == "whisper" && cue.TimingSource == "srt-fallback";
            var usedSentenceGroup = cue.TimingSource == "sentence-group" && expected.TimingSource != "sample";
            if (cue.TimingSource != expected.TimingSource && !usedSrtFallback && !usedSentenceGroup)
                throw new InvalidDataException("Worker TTS tự đổi nguồn timing không hợp lệ.");
            var effectiveStart = usedSrtFallback ? expected.CueStart : expected.VoiceStart;
            var effectiveEnd = usedSrtFallback ? expected.CueEnd : expected.VoiceEnd;
            var startSample = usedSentenceGroup
                ? cue.ClipStartSample
                : checked((long)Math.Round(effectiveStart * expectedSampleRate));
            var targetFrames = usedSentenceGroup
                ? cue.TargetFrames
                : checked((long)Math.Round(effectiveEnd * expectedSampleRate)) - startSample;
            var target = targetFrames / (double)expectedSampleRate;
            var naturalSample = expected.TimingSource == "sample";
            // Overflow (user duyet): cue don qua dai doc nguyen cau tran khung
            // (toi da OverflowBudgetFrames, mirror worker OVERFLOW_MAX_FRAMES).
            // Chi chap nhan khi worker khai overflow_frames + bat review + khong
            // phai nhom/sample; moi thu khac giu nghiem nhu cu.
            var overflow = cue.OverflowFrames;
            var allowOverflow = overflow > 0 && overflow <= OverflowBudgetFrames
                && cue.Status == "review" && cue.TimingSource != "sentence-group" && cue.TimingSource != "sample";
            var wantFrames = targetFrames + (allowOverflow ? overflow : 0);
            if (targetFrames <= 0 || cue.TargetFrames != targetFrames
                || cue.Frames <= 0 || cue.Clipped is not false || cue.ClipStartSample != startSample
                || cue.ClipEndSample != startSample + cue.Frames || cue.ClipEndSample > startSample + wantFrames
                || cue.Frames != wantFrames
                || Math.Abs(cue.FittedDuration - cue.Frames / (double)expectedSampleRate) > 1e-9)
                throw new InvalidDataException("Voice chưa khớp đủ thời lượng thoại gốc hoặc bị cắt đuôi; không nhận master này.");
            var needsReview = ValidateNativeSynthesis(cue, targetFrames, naturalSample, expectedSampleRate, IsVieneuVoice(voice))
                || cue.TimingSource is "srt-fallback" or "sentence-group"
                // 28/09: rút gọn chữ đọc luôn review (worker khai spoken_text) —
                // khớp worker native_record_valid, không lệch status.
                || !string.IsNullOrEmpty(cue.SpokenText);
            if ((cue.Status == "review") != needsReview || cue.VoiceReview != needsReview)
                throw new InvalidDataException("Result TTS báo sai trạng thái fit/review.");
            var clipRoot = Path.Combine(Path.GetDirectoryName(path)!, "clips") + Path.DirectorySeparatorChar;
            if (string.IsNullOrWhiteSpace(cue.ClipPath) || !Path.GetFullPath(cue.ClipPath).StartsWith(clipRoot, StringComparison.OrdinalIgnoreCase)
                || cue.ClipSha256?.Length != 64 || cue.ClipSha256.Any(value => !Uri.IsHexDigit(value))
                || await HashAsync(cue.ClipPath, cancellationToken) != cue.ClipSha256
                || ReadClipFrames(cue.ClipPath, expectedSampleRate) != cue.Frames)
                throw new InvalidDataException("WAV voice thực tế không khớp SHA/số mẫu đã báo.");
        }
        // Kiểm tra khớp giờ voice-sub (08/10, user bắt buộc): mọi cue (kể cả
        // nhóm/overflow/sample) phải bắt đầu trong ±1s quanh giờ sub hiển thị,
        // nếu không master này lệch là cho rớt thẳng kèm danh sách để sửa
        // SRT/timing, không bao giờ để lọt voice sớm/trễ hàng giây xuống
        // video. Validator cũ chỉ khớp khung/giây tuyệt đối từng cue nên
        // lệch tương đối (sớm 1.8s/trễ 4s) vẫn lọt — nay chặn ở đây.
        // Ngưỡng 1s (không phải 0): tròn mili-giây + biên làm tròn frame nằm
        // trong ~0.05s; luật van worker (trễ 0/sớm 0) mới là enforcing chính,
        // đây là lưới phòng thủ cuối cho video sau.
        const double MaxVoiceSubOffsetSeconds = 1.0;
        var misaligned = new List<string>();
        for (var index = 0; index < expectedCues.Count; index++)
        {
            var cue = result.Cues[index];
            var offset = cue.ClipStartSample / (double)expectedSampleRate - expectedCues[index].CueStart;
            if (double.IsFinite(offset) && Math.Abs(offset) > MaxVoiceSubOffsetSeconds)
                misaligned.Add($"{expectedCues[index].Id} {offset:+0.00;-0.00}s");
            if (misaligned.Count >= 20) break;
        }
        if (misaligned.Count > 0)
            throw new InvalidDataException(
                "Voice lệch giờ sub quá 1s ở " + misaligned.Count + "+ cue (không nhận master này; kiểm tra SRT/timing rồi chạy lại): "
                + string.Join(", ", misaligned));
        if (result.ReviewCount != result.Cues.Count(cue => cue.Status == "review"))
            throw new InvalidDataException("Result TTS báo sai số câu cần kiểm tra.");
        if (!SamePath(result.Master.Path, expectedMaster) || !File.Exists(expectedMaster)
            || new FileInfo(expectedMaster).Length <= 64 || result.Master.Start != 0
            || !double.IsFinite(result.Master.Duration) || Math.Abs(result.Master.Duration - duration) > .001
            || await HashAsync(expectedMaster, cancellationToken) != result.Master.Sha256)
            throw new InvalidDataException("Track voice master TTS không hợp lệ.");
        var probe = await _tools.EnsureFfprobeAsync(cancellationToken);
        var info = await _processes.RunAsync(probe,
            ["-v", "error", "-print_format", "json", "-show_streams", "-show_format", expectedMaster], cancellationToken);
        if (info.ExitCode != 0) throw new InvalidDataException("Không giải mã được master voice: " + info.StandardError);
        using var probeJson = JsonDocument.Parse(info.StandardOutput);
        var audioStreams = probeJson.RootElement.GetProperty("streams").EnumerateArray()
            .Where(item => GetString(item, "codec_type") == "audio").ToArray();
        if (audioStreams.Length != 1 || GetString(audioStreams[0], "sample_rate") != expectedSampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture)
            || GetInt(audioStreams[0], "channels") != 1
            || !double.TryParse(GetString(probeJson.RootElement.GetProperty("format"), "duration"),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var actualDuration)
            || !double.IsFinite(actualDuration) || Math.Abs(actualDuration - duration) > .05)
            throw new InvalidDataException("Master voice sai sample rate/kênh/thời lượng.");
        return result;
    }

    private static bool SamePath(string left, string right)
    {
        try { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
    }

    private static void ValidateRequest(EditorTtsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ProjectId.Length is < 8 or > 64 || request.ProjectId.Any(x => !char.IsAsciiLetterOrDigit(x) && x is not ('-' or '_')))
            throw new InvalidDataException("Project ID TTS không hợp lệ.");
        var source = new FileInfo(Path.GetFullPath(request.SourcePath));
        if (!source.Exists || source.Length <= 0) throw new FileNotFoundException("Video nguồn TTS không tồn tại.", source.FullName);
        if (!double.IsFinite(request.Duration) || request.Duration <= 0) throw new InvalidDataException("Thời lượng video TTS không hợp lệ.");
        if (request.Subtitle.Cues.Count == 0 || request.Subtitle.Cues.Any(x => string.IsNullOrWhiteSpace(x.VietnameseText)))
            throw new InvalidDataException("Phải Vietsub đầy đủ trước khi tạo voice Việt.");
        if (request.Subtitle.Cues.Count > EditorSubtitleDocument.MaxCues
            || request.Subtitle.Cues.Select(cue => cue.Id).Distinct(StringComparer.Ordinal).Count() != request.Subtitle.Cues.Count
            || request.Subtitle.Cues.Any(cue => string.IsNullOrWhiteSpace(cue.Id) || !double.IsFinite(cue.Start)
                || !double.IsFinite(cue.End) || cue.Start < 0 || cue.End <= cue.Start || cue.End > request.Duration))
            throw new InvalidDataException("Cue voice bị trùng ID hoặc nằm ngoài thời lượng video.");
        if (request.SpeechAnalysisSha256.Length != 64 || request.SpeechAnalysisSha256.Any(x => !Uri.IsHexDigit(x)))
            throw new InvalidDataException("SHA-256 Whisper timing không hợp lệ.");
        var canonicalVoice = LocalTtsInstaller.CanonicalVoiceId(request.Voice ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(request.Voice) && !LocalTtsInstaller.AvailableVoices.Contains(canonicalVoice, StringComparer.Ordinal))
            throw new InvalidDataException($"Giọng {request.Voice} không hỗ trợ.");
    }

    private async Task WriteAtomicJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, _json, cancellationToken);
                await stream.WriteAsync("\n"u8.ToArray(), cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { TryDelete(temporary); }
    }

    private static bool TryParseEvent(string line, out JsonDocument document)
    {
        document = null!;
        line = line.Trim();
        if (!line.StartsWith('{') || !line.EndsWith('}') || line.Length > 512_000) return false;
        try { document = JsonDocument.Parse(line); return document.RootElement.ValueKind == JsonValueKind.Object; }
        catch (JsonException) { document?.Dispose(); document = null!; return false; }
    }
    private static string GetString(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
    private static int GetInt(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : 0;
    private static string? TryGetString(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static double? TryGetDouble(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var result) && double.IsFinite(result) ? result : null;
    private static string LastLine(string? text) => string.IsNullOrWhiteSpace(text) ? "không có chi tiết lỗi" : text.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1].Trim();
    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }
    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }

    public void Dispose() { _generationGate.Dispose(); _installer.Dispose(); }

    internal sealed record TtsCueManifest(string Id, double CueStart, double CueEnd, string Voice, string Text,
        double VoiceStart, double VoiceEnd, string TimingSource);
    private sealed record TtsInputManifest(int Schema, string EngineVersion, string Voice, string TimingAlgorithm, double Duration, IReadOnlyList<TtsCueManifest> Cues);
    private sealed record TtsWorkerCue(string Id, string Voice, bool VoiceReview, double RawDuration, double FittedDuration, string Status,
        string TimingSource, long TargetFrames, long Frames, long ClipStartSample, long ClipEndSample, bool? Clipped,
        string ClipPath, string ClipSha256, string FitMethod, double BaseLengthScale, double LengthScale,
        long SourceFrames, long GeneratedFrames, long TrimmedSilenceFrames, long PaddingFrames,
        int SynthesisAttempts, int SynthesisCalls, bool CacheHit,
        double TempoFactor, long TempoInputFrames, int TempoAttempts,
        long NativeReferenceFrames, long GroupNativeFrames, double ActualSpeedFactor, string SpokenText = "", long OverflowFrames = 0);
    private sealed record TtsWorkerTrack(string Path, double Start, double Duration, string Sha256);
    private sealed record TtsWorkerResult(int Schema, string Engine, string EngineVersion, string Voice, string VoiceRevision,
        IReadOnlyList<TtsWorkerCue> Cues, TtsWorkerTrack Master, int ReviewCount, int SampleRate);
}
