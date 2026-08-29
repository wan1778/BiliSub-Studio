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
        // Fit the entire Vietnamese cue to its mapped source-speech envelope.
        // Whisper pauses never split the text sent to Piper.
        var mappedTiming = await Task.Run(
            () => EditorSpeechAnalysisDocument.MapToCues(analysis, request.Subtitle.Cues, job.CancellationToken),
            job.CancellationToken).ConfigureAwait(false);
        var cueTiming = mappedTiming.ToDictionary(x => x.CueId, StringComparer.Ordinal);
        var voice = ResolveVoice(request.Voice);
        var cues = request.Subtitle.Cues.Select(cue => BuildWholeCue(cue, voice, cueTiming[cue.Id])).ToArray();
        return await GenerateCuesAsync(job, request.ProjectId, request.Duration, voice, cues, cueTiming);
    }

    public Task<EditorTtsResult> GenerateSampleAsync(AppJob job, string voice)
    {
        voice = ResolveVoice(voice);
        var cue = new TtsCueManifest("voice-demo-cue", 0, 10, voice,
            "Xin chào, đây là giọng Ngọc Huyền. Chúc bạn một ngày thật bình an.", 0, 10, "sample");
        // A text-only sample uses real synthesis, without a fabricated source or Whisper analysis.
        return GenerateCuesAsync(job, "voice-demo-" + voice, 10, voice, [cue],
            new Dictionary<string, EditorCueSpeechTiming>());
    }

    internal static TtsCueManifest BuildWholeCue(EditorSubtitleCue cue, string voice, EditorCueSpeechTiming timing)
    {
        var text = VietnameseTtsTextNormalizer.Normalize(cue.VietnameseText);
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException($"Cue {cue.Number} chưa có câu Việt để tạo voice.");
        if (timing is null || timing.CueId != cue.Id || timing.CueStart != cue.Start || timing.CueEnd != cue.End)
            throw new InvalidDataException($"Cue {cue.Number} không khớp dữ liệu timing của SRT hiện tại.");
        // Prefer the real Whisper word envelope. If Whisper found no word in a
        // valid external-SRT cue, preserve the user's full SRT interval under an
        // explicit fallback identity instead of aborting every later cue.
        var fallback = timing.Words.Count == 0;
        var voiceStart = fallback ? cue.Start : Math.Max(cue.Start, timing.Words.Min(word => word.Start));
        var voiceEnd = fallback ? cue.End : Math.Min(cue.End, timing.Words.Max(word => word.End));
        if (!double.IsFinite(voiceStart) || !double.IsFinite(voiceEnd) || voiceEnd <= voiceStart
            || Math.Round(voiceEnd * 22050) <= Math.Round(voiceStart * 22050))
            throw new InvalidDataException($"Cue {cue.Number} có timecode SRT hoặc khoảng thoại Whisper rỗng.");
        return new TtsCueManifest(cue.Id, cue.Start, cue.End, voice, text, voiceStart, voiceEnd,
            fallback ? "srt-fallback" : "whisper");
    }

    private static string ResolveVoice(string? voice)
    {
        var value = LocalTtsInstaller.CanonicalVoiceId(string.IsNullOrWhiteSpace(voice) ? LocalTtsInstaller.Voice : voice);
        if (!LocalTtsInstaller.AvailableVoices.Contains(value, StringComparer.Ordinal))
            throw new InvalidDataException($"Giọng {value} chưa có model NGHI được xác minh.");
        return value;
    }

    private async Task<EditorTtsResult> GenerateCuesAsync(AppJob job, string projectId, double duration, string voice,
        IReadOnlyList<TtsCueManifest> cues, IReadOnlyDictionary<string, EditorCueSpeechTiming> cueTiming)
    {
        await _generationGate.WaitAsync(job.CancellationToken);
        try
        {
            var runtime = await _installer.PrepareAsync(job, voice, 35);
            var ffmpeg = await _tools.EnsureFfmpegAsync(job.CancellationToken);
            var outputRoot = Path.Combine(_paths.Cache, "Editor", "TTS", projectId);
            var runId = Guid.NewGuid().ToString("N");
            var runRoot = Path.Combine(outputRoot, "runs", runId);
            var inputPath = Path.Combine(runRoot, "input.json");
            var resultPath = Path.Combine(outputRoot, $"result-{runId}.json");
            var masterPath = Path.Combine(outputRoot, $"voice-master-{runId}.flac");
            Directory.CreateDirectory(runRoot);
            var accepted = false;
            await using var processes = new OwnedProcessGroup();
            try
            {
                var manifest = new TtsInputManifest(ManifestSchema, LocalTtsInstaller.EngineVersion,
                    voice, TimingAlgorithm, duration, cues);
                await WriteAtomicJsonAsync(inputPath, manifest, job.CancellationToken);
                job.Set("tts-generate", 37, $"Đang tạo Ngọc Huyền local · {cues.Count} câu · mỗi cue đọc nguyên câu...");
                var ready = false;
                var completed = false;
                var reportedResult = string.Empty;
                var result = await _processes.RunStreamingAsync(runtime.Python,
                    ["-I", "-X", "utf8", runtime.Worker, "--manifest", inputPath,
                     "--model", runtime.Model, "--config", runtime.Config, "--ffmpeg", ffmpeg,
                     "--output-root", outputRoot, "--run-root", runRoot,
                     "--result", resultPath, "--master", masterPath, "--voice", voice],
                    job.CancellationToken, (line, _) =>
                    {
                        if (!TryParseEvent(line, out var parsed)) return ValueTask.CompletedTask;
                        using (parsed)
                        {
                            var root = parsed.RootElement;
                            var kind = GetString(root, "event");
                            if (kind == "ready")
                                ready = GetString(root, "voice_revision") == LocalTtsInstaller.VoiceRevision
                                    && GetString(root, "voice") == voice && GetInt(root, "model_loads") == 1;
                            else if (kind == "attempt")
                            {
                                var index = GetInt(root, "index");
                                var total = GetInt(root, "total");
                                var attempt = GetInt(root, "attempt");
                                var maximum = GetInt(root, "max_attempts");
                                if (total == cues.Count && index >= 1 && index <= total && attempt >= 1
                                    && maximum is >= 1 and <= 10 && attempt <= maximum)
                                    job.Set("tts-generate", 38 + (index - 1) / (double)total * 53,
                                        $"Model đang đọc nguyên câu {index}/{total} · lượt {attempt}/{maximum} để canh thời lượng · không kéo tốc độ file...");
                            }
                            else if (kind == "cue")
                            {
                                var index = GetInt(root, "index");
                                var total = Math.Max(1, GetInt(root, "total"));
                                job.Set("tts-generate", 38 + index / (double)total * 53,
                                    $"Đang tạo voice nguyên câu · {index}/{total}...");
                            }
                            else if (kind == "block")
                            {
                                var total = Math.Max(1d, root.GetProperty("total").GetDouble());
                                job.Set("tts-mix", 91 + root.GetProperty("index").GetDouble() / total * 6,
                                    "Đang ghép nguyên câu voice theo thời lượng thoại gốc, không cắt đuôi...");
                            }
                            else if (kind == "complete")
                            {
                                completed = true;
                                reportedResult = GetString(root, "result");
                            }
                        }
                        return ValueTask.CompletedTask;
                    }, runtime.Environment, processes);
                job.CancellationToken.ThrowIfCancellationRequested();
                if (result.ExitCode != 0 || !ready || !completed || !SamePath(reportedResult, resultPath))
                    throw new InvalidOperationException("Không thể tạo voice NGHI: " + LastLine(result.StandardError));
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
                    : $"Voice Việt đã canh thời lượng · {parsedResult.ReviewCount} câu dùng nhịp model khác nhiều, cần nghe lại; không kéo tốc độ file.");
                accepted = true;
                return new EditorTtsResult(resultPath, manifestSha,
                    new EditorVoiceTrack(masterPath, 0, duration), cueResults, parsedResult.ReviewCount,
                    parsedResult.Engine, parsedResult.EngineVersion, parsedResult.Voice, cueWindows);
            }
            finally
            {
                // Cleanup is part of completion/cancellation, never swallowed.
                await processes.StopAsync();
                await CleanupRunAsync(runRoot);
                if (!accepted) { File.Delete(resultPath); File.Delete(masterPath); }
            }
        }
        finally { _generationGate.Release(); }
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

    private async Task<TtsWorkerResult> ReadResultAsync(string path, string expectedMaster, double duration,
        int expectedSampleRate, string voice, IReadOnlyList<TtsCueManifest> expectedCues, CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length is <= 0 or > 32L * 1024 * 1024)
            throw new InvalidDataException("Worker TTS không tạo result hợp lệ.");
        await using var stream = File.OpenRead(path);
        var result = await JsonSerializer.DeserializeAsync<TtsWorkerResult>(stream, _json, cancellationToken)
            ?? throw new InvalidDataException("Result TTS rỗng.");
        if (result.Schema != ManifestSchema || result.Cues is null || result.Master is null
            || result.Engine != LocalTtsInstaller.Engine || result.EngineVersion != LocalTtsInstaller.EngineVersion
            || result.VoiceRevision != LocalTtsInstaller.VoiceRevision || result.Voice != voice
            || result.SampleRate != expectedSampleRate || result.Cues.Count != expectedCues.Count)
            throw new InvalidDataException("Result TTS sai schema/model/voice/cue count.");
        ValidateSentenceGroupWindows(result.Cues, expectedCues, expectedSampleRate);
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
            var naturalSample = expected.TimingSource == "sample" && cue.RawDuration <= target;
            if (targetFrames <= 0 || cue.TargetFrames != targetFrames
                || cue.Frames <= 0 || cue.Clipped is not false || cue.ClipStartSample != startSample
                || cue.ClipEndSample != startSample + cue.Frames || cue.ClipEndSample > startSample + targetFrames
                || (!naturalSample && cue.Frames != targetFrames)
                || (naturalSample && Math.Abs(cue.FittedDuration - cue.RawDuration) > 1d / expectedSampleRate)
                || Math.Abs(cue.FittedDuration - cue.Frames / (double)expectedSampleRate) > 1e-9)
                throw new InvalidDataException("Voice chưa khớp đủ thời lượng thoại gốc hoặc bị cắt đuôi; không nhận master này.");
            var needsReview = ValidateNativeSynthesis(cue, targetFrames, naturalSample, expectedSampleRate)
                || cue.TimingSource is "srt-fallback" or "sentence-group";
            if ((cue.Status == "review") != needsReview || cue.VoiceReview != needsReview)
                throw new InvalidDataException("Result TTS báo sai trạng thái fit/review.");
            var clipRoot = Path.Combine(Path.GetDirectoryName(path)!, "clips") + Path.DirectorySeparatorChar;
            if (string.IsNullOrWhiteSpace(cue.ClipPath) || !Path.GetFullPath(cue.ClipPath).StartsWith(clipRoot, StringComparison.OrdinalIgnoreCase)
                || cue.ClipSha256?.Length != 64 || cue.ClipSha256.Any(value => !Uri.IsHexDigit(value))
                || await HashAsync(cue.ClipPath, cancellationToken) != cue.ClipSha256
                || ReadClipFrames(cue.ClipPath, expectedSampleRate) != cue.Frames)
                throw new InvalidDataException("WAV voice thực tế không khớp SHA/số mẫu đã báo.");
        }
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
        int SynthesisAttempts, int SynthesisCalls, bool CacheHit);
    private sealed record TtsWorkerTrack(string Path, double Start, double Duration, string Sha256);
    private sealed record TtsWorkerResult(int Schema, string Engine, string EngineVersion, string Voice, string VoiceRevision,
        IReadOnlyList<TtsWorkerCue> Cues, TtsWorkerTrack Master, int ReviewCount, int SampleRate);
}
