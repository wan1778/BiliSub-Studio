using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BiliSubStudio.Core.Configuration;
using BiliSubStudio.Core.Hardware;
using BiliSubStudio.Core.Jobs;
using BiliSubStudio.Core.Processes;
using BiliSubStudio.Core.Tools;

namespace BiliSubStudio.Core.Editor;

public sealed record EditorAsrRequest(string ProjectId, string SourcePath, double Duration);
public sealed record EditorAsrResult(
    EditorSubtitleSource Source,
    string AnalysisPath,
    string AnalysisSha256,
    int SegmentCount,
    int WordCount,
    string Device,
    string ComputeType,
    double ProbeRealtimeFactor,
    int RestoredCueCount,
    string ModelName,
    string ModelRevision);

internal sealed class LocalAsrService : IDisposable
{
    private const int CheckpointSchema = 2;
    private const double ProbeSeconds = 20;
    private readonly AppPaths _paths;
    private readonly LocalAsrInstaller _installer;
    private readonly ToolManager _tools;
    private readonly ProcessRunner _processes;
    private readonly HardwareService _hardware;
    private readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public LocalAsrService(AppPaths paths, LocalAsrInstaller installer, ToolManager tools, ProcessRunner processes, HardwareService hardware)
    {
        _paths = paths;
        _installer = installer;
        _tools = tools;
        _processes = processes;
        _hardware = hardware;
    }

    public LocalAsrStatus Status => _installer.Status;

    public async Task<EditorAsrResult> TranscribeAsync(AppJob job, EditorAsrRequest request)
    {
        ValidateRequest(request);
        var source = new FileInfo(Path.GetFullPath(request.SourcePath));
        var key = CheckpointKey(source, request.Duration);
        var checkpointPath = Path.Combine(_paths.Data, "Projects", "ASR", request.ProjectId + ".json");
        var checkpoint = await LoadCheckpointAsync(checkpointPath, key, job.CancellationToken);
        var restored = checkpoint.Cues.Count;
        var runtime = await _installer.PrepareAsync(job, 18);
        var ffmpeg = await _tools.EnsureFfmpegAsync(job.CancellationToken);
        var operationRoot = Path.Combine(_paths.Temp, "Editor", "ASR", request.ProjectId, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(operationRoot);
        await using var processes = new OwnedProcessGroup();
        try
        {
            var probeAudio = Path.Combine(operationRoot, "probe.wav");
            var probeDuration = Math.Min(ProbeSeconds, request.Duration);
            var probeStart = Math.Max(0, Math.Min(Math.Max(0, request.Duration - probeDuration), request.Duration * .35));
            job.Set("asr-probe-audio", 19, "Đang lấy mẫu audio thật để benchmark Whisper timing...");
            await ExtractAudioAsync(ffmpeg, source.FullName, probeAudio, probeStart, probeDuration, processes, job.CancellationToken);
            var selection = await SelectRuntimeAsync(runtime, probeAudio, probeDuration, processes, job);
            job.Log($"Whisper timing benchmark khóa {selection.Device.ToUpperInvariant()}/{selection.ComputeType} · {selection.RealtimeFactor:0.00}× thời gian thực.");

            var resumeStart = checkpoint.Cues.Count == 0 ? 0 : Math.Max(0, checkpoint.Cues[^1].End - 1.5);
            var retained = checkpoint.Cues.Where(x => x.End <= resumeStart + .05).ToList();
            checkpoint = checkpoint with
            {
                Device = selection.Device,
                ComputeType = selection.ComputeType,
                Cues = retained,
                Complete = false,
            };
            await SaveCheckpointAsync(checkpointPath, checkpoint, job.CancellationToken);
            var restoredRetained = retained.Count;
            var audio = Path.Combine(operationRoot, "source.wav");
            job.Set("asr-extract", 27, resumeStart > 0
                ? $"Đang trích audio từ checkpoint {Time(resumeStart)} để tiếp tục timing..."
                : "Đang trích audio mono 16 kHz để lấy word timing, khoảng lặng và chất giọng...");
            await ExtractAudioAsync(ffmpeg, source.FullName, audio, resumeStart, null, processes, job.CancellationToken);

            var sync = new SemaphoreSlim(1, 1);
            try
            {
                var completed = false;
                var ready = false;
                var arguments = WorkerArguments(runtime, audio, selection, resumeStart, probe: false);
                var result = await _processes.RunStreamingAsync(runtime.Python, arguments, job.CancellationToken,
                    async (line, _) =>
                    {
                        if (!TryParseEvent(line, out var parsed)) return;
                        using (parsed)
                        {
                            var root = parsed.RootElement;
                            var kind = GetString(root, "event");
                            if (kind == "ready") { ready = true; return; }
                            if (kind == "complete") { completed = true; return; }
                            if (kind != "segment") return;
                            var cue = ParseCue(root);
                            await sync.WaitAsync(CancellationToken.None);
                            try
                            {
                                cue = NormalizeCue(cue, checkpoint.Cues);
                                if (cue is null) return;
                                checkpoint.Cues.Add(cue);
                                checkpoint = checkpoint with { Frontier = cue.End };
                                await SaveCheckpointAsync(checkpointPath, checkpoint, CancellationToken.None);
                                var percent = 34 + Math.Clamp(cue.End / Math.Max(.1, request.Duration), 0, 1) * 62;
                                var words = checkpoint.Cues.Sum(x => x.Words.Count);
                                job.Set("asr-transcribe", percent,
                                    $"Đang phân tích nhịp thoại · {checkpoint.Cues.Count} đoạn / {words} từ · {Time(cue.End)}/{Time(request.Duration)} · checkpoint đã lưu.");
                            }
                            finally { sync.Release(); }
                        }
                    }, runtime.Environment, processes);
                if (result.ExitCode != 0 || !ready || !completed)
                {
                    var detail = string.IsNullOrWhiteSpace(result.StandardError) ? "worker không trả trạng thái hoàn tất" : LastLine(result.StandardError);
                    throw new InvalidOperationException("Whisper local dừng bất thường: " + detail);
                }
            }
            finally { sync.Dispose(); }

            if (checkpoint.Cues.Count == 0) throw new InvalidDataException("Không tìm thấy đoạn thoại tiếng Trung nào để phân tích timing.");
            checkpoint = checkpoint with { Complete = true, Frontier = request.Duration };
            await SaveCheckpointAsync(checkpointPath, checkpoint, job.CancellationToken);

            // Keep an ASR-generated source SRT only as a fallback when the project has no external SRT.
            var outputDirectory = Path.Combine(_paths.Data, "Projects", "ASR");
            Directory.CreateDirectory(outputDirectory);
            var output = Path.Combine(outputDirectory, request.ProjectId + ".zh.srt");
            var cues = checkpoint.Cues.Select((cue, index) => ToSubtitleCue(cue, index + 1)).ToArray();
            await WriteAtomicAsync(output, EditorSubtitleDocument.RenderSource(cues), job.CancellationToken);
            var loaded = await EditorSubtitleDocument.LoadAsync(output, job.CancellationToken);

            var analysisDirectory = Path.Combine(_paths.Data, "Projects", "Speech");
            Directory.CreateDirectory(analysisDirectory);
            var analysisPath = Path.Combine(analysisDirectory, request.ProjectId + ".speech.json");
            var analysis = new EditorSpeechAnalysis(
                EditorSpeechAnalysisDocument.CurrentSchema,
                key,
                LocalAsrInstaller.ModelName,
                LocalAsrInstaller.ModelRevision,
                selection.Device,
                selection.ComputeType,
                selection.RealtimeFactor,
                checkpoint.Cues.Select(ToSpeechSegment).ToArray());
            var analysisSha = await EditorSpeechAnalysisDocument.SaveAsync(analysisPath, analysis, job.CancellationToken);
            var wordCount = analysis.Segments.Sum(x => x.Words.Count);
            job.Set("asr-final", 99, $"Whisper timing hoàn tất · {analysis.Segments.Count} đoạn / {wordCount} từ · đã lưu nhịp, khoảng lặng và Nam/Nữ gợi ý.");
            return new EditorAsrResult(loaded, analysisPath, analysisSha, analysis.Segments.Count, wordCount,
                selection.Device, selection.ComputeType, selection.RealtimeFactor, restoredRetained,
                LocalAsrInstaller.ModelName, LocalAsrInstaller.ModelRevision);
        }
        finally
        {
            try { await processes.StopAsync(); } catch { }
            TryDeleteDirectory(operationRoot);
        }
    }

    private async Task<AsrSelection> SelectRuntimeAsync(LocalAsrRuntime runtime, string audio, double probeSeconds, OwnedProcessGroup processes, AppJob job)
    {
        var hardware = _hardware.ResourceSnapshot();
        var snapshot = _hardware.Snapshot();
        var threads = Math.Clamp(snapshot.LogicalProcessors - 2, 1, 12);
        if (snapshot.NvidiaDetected && (!hardware.VramTelemetryAvailable || hardware.AvailableVramBytes >= 1_500L * 1024 * 1024))
        {
            var compute = hardware.VramTelemetryAvailable && hardware.AvailableVramBytes < 2_500L * 1024 * 1024 ? "int8_float16" : "float16";
            job.Set("asr-probe-gpu", 21, $"Đang benchmark Whisper GPU thật ({compute}) trên mẫu {probeSeconds:0}s...");
            try
            {
                var probe = await ProbeAsync(runtime, audio, probeSeconds, new AsrSelection("cuda", compute, threads, 0), processes, job.CancellationToken);
                if (probe.RealtimeFactor <= 1.5) return probe;
                job.Warn($"Whisper GPU benchmark chậm {probe.RealtimeFactor:0.00}× thời gian thực; đo CPU trước khi khóa cấu hình.");
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                job.Warn("Whisper GPU không vượt benchmark (VRAM/CUDA/cuDNN): " + error.Message + " · chuyển đo CPU.");
            }
        }

        job.Set("asr-probe-cpu", 24, $"Đang benchmark Whisper CPU thật với {threads} luồng...");
        var cpu = await ProbeAsync(runtime, audio, probeSeconds, new AsrSelection("cpu", "int8", threads, 0), processes, job.CancellationToken);
        if (cpu.RealtimeFactor > 4) job.Warn($"Whisper CPU dự kiến chậm ({cpu.RealtimeFactor:0.00}× thời gian thực), nhưng benchmark đã PASS an toàn.");
        return cpu;
    }

    private async Task<AsrSelection> ProbeAsync(
        LocalAsrRuntime runtime,
        string audio,
        double probeSeconds,
        AsrSelection selection,
        OwnedProcessGroup processes,
        CancellationToken cancellationToken)
    {
        var elapsed = -1d;
        var ready = false;
        var complete = false;
        var result = await _processes.RunStreamingAsync(runtime.Python, WorkerArguments(runtime, audio, selection, 0, probe: true), cancellationToken,
            (line, _) =>
            {
                if (!TryParseEvent(line, out var parsed)) return ValueTask.CompletedTask;
                using (parsed)
                {
                    var root = parsed.RootElement;
                    var kind = GetString(root, "event");
                    if (kind == "ready") ready = true;
                    if (kind == "complete")
                    {
                        complete = true;
                        elapsed = GetDouble(root, "elapsed_seconds");
                    }
                }
                return ValueTask.CompletedTask;
            }, runtime.Environment, processes);
        if (result.ExitCode != 0 || !ready || !complete || !double.IsFinite(elapsed) || elapsed <= 0)
            throw new InvalidOperationException(LastLine(result.StandardError));
        return selection with { RealtimeFactor = elapsed / Math.Max(.1, probeSeconds) };
    }

    private static string[] WorkerArguments(LocalAsrRuntime runtime, string audio, AsrSelection selection, double offset, bool probe)
    {
        var values = new List<string>
        {
            "-I", "-X", "utf8", runtime.Worker,
            "--model", runtime.ModelDirectory,
            "--audio", audio,
            "--device", selection.Device,
            "--compute", selection.ComputeType,
            "--threads", selection.Threads.ToString(CultureInfo.InvariantCulture),
            "--offset", offset.ToString("0.000", CultureInfo.InvariantCulture),
            "--beam", probe ? "1" : "5",
        };
        if (probe) values.Add("--probe");
        return values.ToArray();
    }

    private async Task ExtractAudioAsync(
        string ffmpeg,
        string source,
        string destination,
        double start,
        double? duration,
        OwnedProcessGroup processes,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
        if (start > 0) { arguments.Add("-ss"); arguments.Add(start.ToString("0.000", CultureInfo.InvariantCulture)); }
        arguments.AddRange(["-i", source]);
        if (duration is > 0) { arguments.Add("-t"); arguments.Add(duration.Value.ToString("0.000", CultureInfo.InvariantCulture)); }
        arguments.AddRange(["-vn", "-sn", "-dn", "-map", "0:a:0", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", "-y", destination]);
        var result = await _processes.RunAsync(ffmpeg, arguments, cancellationToken, owner: processes);
        if (result.ExitCode != 0 || !File.Exists(destination) || new FileInfo(destination).Length <= 44)
            throw new InvalidOperationException("Không trích được audio để phân tích timing: " + LastLine(result.StandardError));
    }

    private async Task<AsrCheckpoint> LoadCheckpointAsync(string path, string key, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length is <= 0 or > 128L * 1024 * 1024) return AsrCheckpoint.New(key);
            await using var stream = File.OpenRead(path);
            var loaded = await JsonSerializer.DeserializeAsync<AsrCheckpoint>(stream, _json, cancellationToken);
            if (loaded is null || loaded.Schema != CheckpointSchema || loaded.Key != key || loaded.ModelRevision != LocalAsrInstaller.ModelRevision
                || loaded.Cues is null || loaded.Cues.Count > EditorSubtitleDocument.MaxCues)
                return AsrCheckpoint.New(key);
            var valid = loaded.Cues.Where(ValidCue).OrderBy(x => x.Start).ToList();
            return loaded with { Cues = valid };
        }
        catch (OperationCanceledException) { throw; }
        catch { return AsrCheckpoint.New(key); }
    }

    private static bool ValidCue(AsrCue cue) =>
        double.IsFinite(cue.Start) && double.IsFinite(cue.End) && cue.Start >= 0 && cue.End > cue.Start
        && !string.IsNullOrWhiteSpace(cue.Text) && cue.Text.Length <= EditorSubtitleDocument.MaxCueCharacters
        && double.IsFinite(cue.VoiceConfidence) && cue.VoiceConfidence is >= 0 and <= 1
        && double.IsFinite(cue.MedianPitchHz)
        && cue.Words is not null && cue.Words.Count <= 1_000
        && cue.Words.All(x => !string.IsNullOrWhiteSpace(x.Text) && x.Text.Length <= 256 && double.IsFinite(x.Start) && double.IsFinite(x.End)
            && x.Start >= 0 && x.End > x.Start && double.IsFinite(x.Probability) && x.Probability is >= 0 and <= 1.0001);

    private async Task SaveCheckpointAsync(string path, AsrCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, checkpoint, _json, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { TryDelete(temporary); }
    }

    private static AsrCue? NormalizeCue(AsrCue cue, IReadOnlyList<AsrCue> existing)
    {
        var text = string.Join(' ', cue.Text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length is 0 or > EditorSubtitleDocument.MaxCueCharacters || !double.IsFinite(cue.Start) || !double.IsFinite(cue.End)) return null;
        var start = Math.Max(0, cue.Start);
        var end = Math.Max(start + .05, cue.End);
        if (existing.Count > 0)
        {
            var previous = existing[^1];
            if (end <= previous.End + .03) return null;
            start = Math.Max(start, previous.End);
            if (end <= start + .04) return null;
        }
        var words = cue.Words
            .Where(x => x.End > start && x.Start < end)
            .Select(x => x with { Start = Math.Max(start, x.Start), End = Math.Min(end, x.End) })
            .Where(x => x.End > x.Start + .005)
            .OrderBy(x => x.Start)
            .ToList();
        return cue with
        {
            Start = start,
            End = end,
            Text = text,
            Words = words,
            VoiceClass = EditorSpeechAnalysisDocument.NormalizeVoiceClass(cue.VoiceClass),
            VoiceConfidence = Math.Clamp(double.IsFinite(cue.VoiceConfidence) ? cue.VoiceConfidence : 0, 0, 1),
            MedianPitchHz = double.IsFinite(cue.MedianPitchHz) && cue.MedianPitchHz >= 0 ? cue.MedianPitchHz : 0,
        };
    }

    private static AsrCue ParseCue(JsonElement root)
    {
        var words = new List<AsrWord>();
        if (root.TryGetProperty("words", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var word in list.EnumerateArray())
            {
                var text = GetString(word, "text").Trim();
                var start = GetDouble(word, "start");
                var end = GetDouble(word, "end");
                var wordProbability = GetDouble(word, "probability");
                if (text.Length == 0 || !double.IsFinite(start) || !double.IsFinite(end) || end <= start) continue;
                words.Add(new AsrWord(start, end, text, double.IsFinite(wordProbability) ? Math.Clamp(wordProbability, 0, 1) : 0));
            }
        }
        return new AsrCue(
            GetDouble(root, "start"),
            GetDouble(root, "end"),
            GetString(root, "text"),
            root.TryGetProperty("avg_logprob", out var log) && log.TryGetDouble(out var value) ? value : 0,
            root.TryGetProperty("no_speech_prob", out var silence) && silence.TryGetDouble(out var probability) ? probability : 0,
            words,
            EditorSpeechAnalysisDocument.NormalizeVoiceClass(GetString(root, "voice_class")),
            Math.Clamp(double.IsFinite(GetDouble(root, "voice_confidence")) ? GetDouble(root, "voice_confidence") : 0, 0, 1),
            double.IsFinite(GetDouble(root, "median_pitch_hz")) ? Math.Max(0, GetDouble(root, "median_pitch_hz")) : 0);
    }

    private static EditorSpeechSegment ToSpeechSegment(AsrCue cue) => new(
        cue.Start,
        cue.End,
        cue.Text,
        cue.AverageLogProbability,
        cue.NoSpeechProbability,
        cue.Words.Select(x => new EditorWordTiming(x.Text, x.Start, x.End, x.Probability)).ToArray(),
        cue.VoiceClass,
        cue.VoiceConfidence,
        cue.MedianPitchHz);

    private static EditorSubtitleCue ToSubtitleCue(AsrCue cue, int number)
    {
        var timing = $"{SrtTime(cue.Start)} --> {SrtTime(cue.End)}";
        var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"asr\n{number}\n{timing}\n{cue.Text}")))[..20];
        return new EditorSubtitleCue(id, number.ToString(CultureInfo.InvariantCulture), timing, cue.Start, cue.End, cue.Text);
    }

    private static bool TryParseEvent(string line, out JsonDocument document)
    {
        document = null!;
        line = line.Trim();
        if (!line.StartsWith('{') || !line.EndsWith('}') || line.Length > 512_000) return false;
        try { document = JsonDocument.Parse(line); return document.RootElement.ValueKind == JsonValueKind.Object; }
        catch (JsonException) { document?.Dispose(); document = null!; return false; }
    }

    private static string GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static double GetDouble(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetDouble(out var result) ? result : double.NaN;

    private static string CheckpointKey(FileInfo source, double duration) =>
        EditorSpeechAnalysisDocument.SourceKey(source.FullName, source.Length, source.LastWriteTimeUtc.Ticks, duration, LocalAsrInstaller.ModelRevision);

    private static async Task WriteAtomicAsync(string path, string content, CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        finally { TryDelete(temporary); }
    }

    private static void ValidateRequest(EditorAsrRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ProjectId.Length is < 8 or > 64 || request.ProjectId.Any(x => !char.IsAsciiLetterOrDigit(x) && x is not ('-' or '_')))
            throw new InvalidDataException("Project ID Whisper không hợp lệ.");
        var source = new FileInfo(Path.GetFullPath(request.SourcePath));
        if (!source.Exists || source.Length <= 0) throw new FileNotFoundException("Video nguồn Whisper không tồn tại.", source.FullName);
        if (!double.IsFinite(request.Duration) || request.Duration <= 0) throw new InvalidDataException("Thời lượng video không hợp lệ.");
    }

    private static string SrtTime(double seconds)
    {
        var value = TimeSpan.FromMilliseconds(Math.Round(Math.Max(0, seconds) * 1000));
        return $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00},{value.Milliseconds:000}";
    }

    private static string Time(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"mm\:ss");
    private static string LastLine(string? text) => string.IsNullOrWhiteSpace(text) ? "không có chi tiết lỗi" : text.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1].Trim();
    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }
    private static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { } }

    public void Dispose() => _installer.Dispose();

    private sealed record AsrSelection(string Device, string ComputeType, int Threads, double RealtimeFactor);
    private sealed record AsrWord(double Start, double End, string Text, double Probability);
    private sealed record AsrCue(
        double Start,
        double End,
        string Text,
        double AverageLogProbability,
        double NoSpeechProbability,
        List<AsrWord> Words,
        string VoiceClass,
        double VoiceConfidence,
        double MedianPitchHz);
    private sealed record AsrCheckpoint(int Schema, string Key, string ModelRevision, string Device, string ComputeType, double Frontier, bool Complete, List<AsrCue> Cues)
    {
        public static AsrCheckpoint New(string key) => new(CheckpointSchema, key, LocalAsrInstaller.ModelRevision, string.Empty, string.Empty, 0, false, []);
    }
}
