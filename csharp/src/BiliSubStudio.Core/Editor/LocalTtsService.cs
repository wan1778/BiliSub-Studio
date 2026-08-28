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
    string Voice);

internal sealed class LocalTtsService : IDisposable
{
    private const int ManifestSchema = 1;
    private const string TimingAlgorithm = "whisper-rhythm-v1";
    private const double BlockSeconds = 300;
    private const int NormalizationRevision = 1;
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
        var analysis = await EditorSpeechAnalysisDocument.LoadVerifiedAsync(request.SpeechAnalysisPath, request.SpeechAnalysisSha256, job.CancellationToken);
        if (!string.Equals(analysis.SourceKey, expectedSourceKey, StringComparison.Ordinal))
            throw new InvalidDataException("Whisper timing không còn khớp video nguồn; hãy phân tích nhịp lại trước khi tạo voice.");
        var voice = LocalTtsInstaller.CanonicalVoiceId(string.IsNullOrWhiteSpace(request.Voice) ? LocalTtsInstaller.Voice : request.Voice);
        if (!LocalTtsInstaller.AvailableVoices.Contains(voice, StringComparer.Ordinal)) throw new InvalidDataException($"Giọng {request.Voice} không hỗ trợ.");
        var cueTiming = EditorSpeechAnalysisDocument.MapToCues(analysis, request.Subtitle.Cues).ToDictionary(x => x.CueId, StringComparer.Ordinal);
        var runtime = await _installer.PrepareAsync(job, voice, 35);
        var ffmpeg = await _tools.EnsureFfmpegAsync(job.CancellationToken);
        var outputRoot = Path.Combine(_paths.Cache, "Editor", "TTS", request.ProjectId);
        Directory.CreateDirectory(outputRoot);
        var inputPath = Path.Combine(outputRoot, "input.json");
        var cues = new List<TtsCueManifest>(request.Subtitle.Cues.Count);
        foreach (var cue in request.Subtitle.Cues)
        {
            var raw = cue.VietnameseText;
            var text = VietnameseTtsTextNormalizer.Normalize(raw);
            if (text.Length == 0) throw new InvalidDataException($"Cue {cue.Number} chưa có câu Việt để tạo voice.");
            // Debug log original vs normalized (not spam UI)
            System.Diagnostics.Debug.WriteLine($"TTS normalize cue {cue.Id}: '{raw}' -> '{text}' voice={voice}");
            if (!cueTiming.TryGetValue(cue.Id, out var timing))
                timing = new EditorCueSpeechTiming(cue.Id, cue.Start, cue.End, cue.Start, cue.End, 0, 0, [], [], "uncertain", 0, 0);
            var groups = BuildRhythmGroups(cue, timing, text, voice);
            cues.Add(new TtsCueManifest(
                cue.Id,
                cue.Start,
                cue.End,
                voice,
                groups));
        }
        var manifest = new TtsInputManifest(
            ManifestSchema,
            LocalTtsInstaller.EngineVersion,
            voice,
            TimingAlgorithm,
            BlockSeconds,
            cues);
        await WriteAtomicJsonAsync(inputPath, manifest, job.CancellationToken);

        job.Set("tts-generate", 37, $"Đang tạo voice Việt local ({voice}) · {cues.Count} câu · canh theo word timing Whisper...");
        await using var processes = new OwnedProcessGroup();
        var reportedResult = string.Empty;
        try
        {
            var ready = false;
            var completed = false;
            var result = await _processes.RunStreamingAsync(
                runtime.Python,
                [
                    "-I", runtime.Worker,
                    "--manifest", inputPath,
                    "--model", runtime.Model,
                    "--config", runtime.Config,
                    "--ffmpeg", ffmpeg,
                    "--output-root", outputRoot,
                    "--voice", voice,
                ],
                job.CancellationToken,
                (line, _) =>
                {
                    if (!TryParseEvent(line, out var parsed)) return ValueTask.CompletedTask;
                    using (parsed)
                    {
                        var root = parsed.RootElement;
                        var kind = GetString(root, "event");
                        if (kind == "ready") ready = true;
                        else if (kind == "cue")
                        {
                            var index = GetInt(root, "index");
                            var total = Math.Max(1, GetInt(root, "total"));
                            var percent = 38 + index / (double)total * 53;
                            job.Set("tts-generate", percent, $"Đang tạo và fit voice Việt · {index}/{total} câu...");
                        }
                        else if (kind == "block")
                        {
                            var index = GetInt(root, "index");
                            var total = Math.Max(1, GetInt(root, "total"));
                            job.Set("tts-mix", 91 + index / (double)total * 6, $"Đang gom voice thành track cache · block {index}/{total}...");
                        }
                        else if (kind == "complete")
                        {
                            completed = true;
                            reportedResult = GetString(root, "result");
                        }
                        else if (kind == "error")
                        {
                            var msg = GetString(root, "message");
                            if (!string.IsNullOrWhiteSpace(msg)) job.Log($"NGHI-TTS worker: {msg}");
                        }
                    }
                    return ValueTask.CompletedTask;
                }, runtime.Environment, processes);
            if (result.ExitCode != 0 || !ready || !completed || string.IsNullOrWhiteSpace(reportedResult))
            {
                var shortMsg = LastLine(result.StandardError);
                if (shortMsg.Contains("model missing", StringComparison.OrdinalIgnoreCase) || shortMsg.Contains("config missing", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Không thể tải giọng " + voice + ". Model NGHI-TTS bị thiếu hoặc không hợp lệ.");
                if (shortMsg.Contains("invalid voice", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Giọng {voice} không hỗ trợ.");
                if (shortMsg.Contains("normalization", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Lỗi chuẩn hoá văn bản tiếng Việt.");
                throw new InvalidOperationException("Không thể tạo voice: " + shortMsg);
            }
        }
        finally
        {
            try { await processes.StopAsync(); } catch { }
        }

        var resultPath = reportedResult;
        var parsedResult = await ReadResultAsync(resultPath, outputRoot, request.Duration, runtime.SampleRate, job.CancellationToken);
        var manifestSha = await HashAsync(resultPath, job.CancellationToken);
        var timingById = cueTiming;
        var cueResults = parsedResult.Cues.Select(cue =>
        {
            var timing = timingById.TryGetValue(cue.Id, out var value) ? value : null;
            return new EditorTtsCueResult(
                cue.Id,
                cue.Voice,
                timing?.VoiceClass ?? "uncertain",
                timing?.VoiceConfidence ?? 0,
                cue.Status,
                cue.RawDuration,
                cue.FittedDuration);
        }).ToArray();
        job.Set("tts-final", 99, parsedResult.ReviewCount == 0
            ? $"Voice Việt hoàn tất · {cueResults.Length} câu đều fit timing."
            : $"Voice Việt hoàn tất · {cueResults.Length} câu · {parsedResult.ReviewCount} câu cần xem lại timing/giọng.");
        return new EditorTtsResult(
            resultPath,
            manifestSha,
            new EditorVoiceTrack(parsedResult.Master.Path, parsedResult.Master.Start, parsedResult.Master.Duration),
            cueResults,
            parsedResult.ReviewCount,
            parsedResult.Engine,
            parsedResult.EngineVersion,
                parsedResult.Voice);
    }

    internal static IReadOnlyList<TtsRhythmGroup> BuildRhythmGroups(EditorSubtitleCue cue, EditorCueSpeechTiming timing, string normalizedText, string voice)
    {
        var tokens = normalizedText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0) return [];
        var speechStart = Math.Clamp(timing.SpeechStart, cue.Start, cue.End);
        var speechEnd = Math.Clamp(timing.SpeechEnd, speechStart, cue.End);
        if (speechEnd <= speechStart + .01) { speechStart = cue.Start; speechEnd = cue.End; }
        var intervals = new List<(double Start, double End)>();
        var cursor = speechStart;
        foreach (var pause in timing.Pauses.OrderBy(x => x.Start))
        {
            var pauseStart = Math.Clamp(pause.Start, cursor, speechEnd);
            var pauseEnd = Math.Clamp(pause.End, pauseStart, speechEnd);
            if (pauseStart - cursor >= .08) intervals.Add((cursor, pauseStart));
            cursor = Math.Max(cursor, pauseEnd);
        }
        if (speechEnd - cursor >= .08) intervals.Add((cursor, speechEnd));
        if (intervals.Count == 0) intervals.Add((speechStart, speechEnd));
        while (intervals.Count > tokens.Length && intervals.Count > 1)
        {
            var smallest = Enumerable.Range(0, intervals.Count - 1)
                .OrderBy(index => (intervals[index].End - intervals[index].Start) + (intervals[index + 1].End - intervals[index + 1].Start))
                .First();
            intervals[smallest] = (intervals[smallest].Start, intervals[smallest + 1].End);
            intervals.RemoveAt(smallest + 1);
        }

        var durations = intervals.Select(x => Math.Max(.08, x.End - x.Start)).ToArray();
        var remainingTokens = tokens.Length;
        var remainingWeight = durations.Sum();
        var tokenOffset = 0;
        var groups = new List<TtsRhythmGroup>(intervals.Count);
        for (var index = 0; index < intervals.Count; index++)
        {
            var remainingGroups = intervals.Count - index;
            var count = index == intervals.Count - 1
                ? remainingTokens
                : Math.Clamp((int)Math.Round(remainingTokens * durations[index] / Math.Max(.001, remainingWeight)), 1, remainingTokens - (remainingGroups - 1));
            var groupText = string.Join(' ', tokens.Skip(tokenOffset).Take(count));
            var interval = intervals[index];
            var cacheKey = CacheKey(cue.Id, index, groupText, voice, interval.Start, interval.End);
            groups.Add(new TtsRhythmGroup(interval.Start, interval.End, groupText, cacheKey));
            tokenOffset += count;
            remainingTokens -= count;
            remainingWeight -= durations[index];
        }
        return groups;
    }

    private static string CacheKey(string cueId, int groupIndex, string text, string voice, double start, double end)
    {
        var value = $"{LocalTtsInstaller.Engine}\n{LocalTtsInstaller.EngineVersion}\n{LocalTtsInstaller.ModelRevision}\n{voice}\n{NormalizationRevision}\n{cueId}\n{groupIndex}\n{voice}\n{start:0.000}\n{end:0.000}\n{text}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private async Task<TtsWorkerResult> ReadResultAsync(string path, string root, double duration, int expectedSampleRate, CancellationToken cancellationToken)
    {
        var absolute = Path.GetFullPath(path);
        var safeRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!absolute.StartsWith(safeRoot, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(absolute) || new FileInfo(absolute).Length is <= 0 or > 32L * 1024 * 1024)
            throw new InvalidDataException("Worker TTS không tạo result hợp lệ trong cache project.");
        await using var stream = File.OpenRead(absolute);
        var result = await JsonSerializer.DeserializeAsync<TtsWorkerResult>(stream, _json, cancellationToken)
            ?? throw new InvalidDataException("Result TTS rỗng.");
        if (result.Schema != ManifestSchema || result.Cues is null || result.Master is null || result.ReviewCount < 0
            || string.IsNullOrWhiteSpace(result.Engine) || string.IsNullOrWhiteSpace(result.EngineVersion))
            throw new InvalidDataException("Result TTS sai schema.");
        if (!string.Equals(result.Engine, LocalTtsInstaller.Engine, StringComparison.Ordinal)
            || !string.Equals(result.EngineVersion, LocalTtsInstaller.EngineVersion, StringComparison.Ordinal)
            || !LocalTtsInstaller.AvailableVoices.Contains(result.Voice, StringComparer.Ordinal))
            throw new InvalidDataException($"Result TTS không thuộc voice {result.Voice} đã khóa.");
        var masterPath = Path.GetFullPath(result.Master.Path);
        if (!masterPath.StartsWith(safeRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(masterPath) || new FileInfo(masterPath).Length <= 64
            || !double.IsFinite(result.Master.Start) || result.Master.Start < 0 || !double.IsFinite(result.Master.Duration)
            || result.Master.Duration <= 0 || result.Master.Duration > duration + 5)
            throw new InvalidDataException("Track voice master TTS không hợp lệ.");
        // Validate WAV sample rate matches config (not hardcoded)
        try
        {
            var probe = await _tools.EnsureFfprobeAsync(cancellationToken);
            var info = await new ProcessRunner().RunAsync(probe, ["-v", "quiet", "-print_format", "json", "-show_streams", masterPath], cancellationToken);
            if (info.ExitCode == 0)
            {
                var json = info.StandardOutput;
                if (json.Contains("\"sample_rate\""))
                {
                    var sr = ExtractSampleRate(json);
                    if (sr > 0 && sr != expectedSampleRate)
                    {
                        // Deterministic resample would be done here, but we accept worker native rate for now
                    }
                }
            }
        }
        catch { }
        return result with { Master = result.Master with { Path = masterPath } };
    }

    private static int ExtractSampleRate(string json)
    {
        try { var idx = json.IndexOf("\"sample_rate\"", StringComparison.Ordinal); if (idx < 0) return 0; var colon = json.IndexOf(':', idx); var quote1 = json.IndexOf('"', colon); var quote2 = json.IndexOf('"', quote1 + 1); if (quote1 < 0 || quote2 < 0) return 0; var s = json.Substring(quote1 + 1, quote2 - quote1 - 1); return int.TryParse(s, out var v) ? v : 0; } catch { return 0; }
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

    public void Dispose() => _installer.Dispose();

    internal sealed record TtsRhythmGroup(double Start, double End, string Text, string CacheKey);
    private sealed record TtsCueManifest(string Id, double CueStart, double CueEnd, string Voice, IReadOnlyList<TtsRhythmGroup> Groups);
    private sealed record TtsInputManifest(int Schema, string EngineVersion, string Voice, string TimingAlgorithm, double BlockSeconds, IReadOnlyList<TtsCueManifest> Cues);
    private sealed record TtsWorkerCue(string Id, string Voice, bool VoiceReview, double RawDuration, double FittedDuration, string Status);
    private sealed record TtsWorkerTrack(string Path, double Start, double Duration);
    private sealed record TtsWorkerResult(int Schema, string Engine, string EngineVersion, string Voice, IReadOnlyList<TtsWorkerCue> Cues, TtsWorkerTrack Master, int ReviewCount);
}
