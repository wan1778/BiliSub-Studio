using System.Security.Cryptography;
using System.Text.Json;

namespace BiliSubStudio.Core.Editor;

public sealed record EditorWordTiming(string Text, double Start, double End, double Probability);
public sealed record EditorPauseTiming(double Start, double End)
{
    public double Duration => Math.Max(0, End - Start);
}

public sealed record EditorSpeechSegment(
    double Start,
    double End,
    string Text,
    double AverageLogProbability,
    double NoSpeechProbability,
    IReadOnlyList<EditorWordTiming> Words,
    string VoiceClass,
    double VoiceConfidence,
    double MedianPitchHz);

public sealed record EditorSpeechAnalysis(
    int Schema,
    string SourceKey,
    string ModelName,
    string ModelRevision,
    string Device,
    string ComputeType,
    double ProbeRealtimeFactor,
    IReadOnlyList<EditorSpeechSegment> Segments);

public sealed record EditorCueSpeechTiming(
    string CueId,
    double CueStart,
    double CueEnd,
    double SpeechStart,
    double SpeechEnd,
    double LeadingSilence,
    double TrailingSilence,
    IReadOnlyList<EditorWordTiming> Words,
    IReadOnlyList<EditorPauseTiming> Pauses,
    string VoiceClass,
    double VoiceConfidence,
    double MedianPitchHz);

public static class EditorSpeechAnalysisDocument
{
    public const int CurrentSchema = 1;
    public const double PauseThresholdSeconds = .18;
    private const long MaxAnalysisBytes = 128L * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static async Task<string> SaveAsync(string path, EditorSpeechAnalysis analysis, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        Validate(analysis);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, analysis, Json, cancellationToken);
                await stream.WriteAsync("\n"u8.ToArray(), cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { TryDelete(temporary); }
        return await Sha256Async(path, cancellationToken);
    }

    public static async Task<EditorSpeechAnalysis> LoadVerifiedAsync(string path, string sha256, CancellationToken cancellationToken)
    {
        var absolute = Path.GetFullPath(path.Trim());
        var info = new FileInfo(absolute);
        if (!info.Exists || info.Length is <= 0 or > MaxAnalysisBytes)
            throw new FileNotFoundException("Thiếu dữ liệu phân tích nhịp thoại đã lưu.", absolute);
        var actual = await Sha256Async(absolute, cancellationToken);
        if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Dữ liệu phân tích nhịp thoại không khớp SHA-256 đã lưu.");
        await using var stream = new FileStream(absolute, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var loaded = await JsonSerializer.DeserializeAsync<EditorSpeechAnalysis>(stream, Json, cancellationToken)
            ?? throw new InvalidDataException("Dữ liệu phân tích nhịp thoại rỗng.");
        Validate(loaded);
        return loaded;
    }

    public static IReadOnlyList<EditorCueSpeechTiming> MapToCues(
        EditorSpeechAnalysis analysis,
        IReadOnlyList<EditorSubtitleCue> cues,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(cues);
        Validate(analysis);
        // Index once. The previous implementation flattened and scanned every
        // Whisper word and segment again for every SRT cue (O(cues * words)),
        // which blocked the WinUI dispatcher for large projects.
        var wordsByMidpoint = analysis.Segments
            .SelectMany(segment => segment.Words)
            .Select(word => new IndexedWord(Midpoint(word.Start, word.End), word))
            .OrderBy(item => item.Midpoint)
            .ThenBy(item => item.Word.Start)
            .ThenBy(item => item.Word.End)
            .ToArray();
        var segmentsByStart = analysis.Segments
            .OrderBy(segment => segment.Start)
            .ThenBy(segment => segment.End)
            .ToArray();
        var prefixMaxSegmentEnd = new double[segmentsByStart.Length];
        var maxSegmentEnd = double.NegativeInfinity;
        for (var index = 0; index < segmentsByStart.Length; index++)
        {
            maxSegmentEnd = Math.Max(maxSegmentEnd, segmentsByStart[index].End);
            prefixMaxSegmentEnd[index] = maxSegmentEnd;
        }

        var result = new List<EditorCueSpeechTiming>(cues.Count);
        for (var cueIndex = 0; cueIndex < cues.Count; cueIndex++)
        {
            if ((cueIndex & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var cue = cues[cueIndex];
            var wordStart = LowerBoundWord(wordsByMidpoint, cue.Start - .08);
            var wordEnd = UpperBoundWord(wordsByMidpoint, cue.End + .08);
            var words = wordsByMidpoint.AsSpan(wordStart, wordEnd - wordStart).ToArray()
                .Select(item => item.Word)
                .OrderBy(word => word.Start)
                .ThenBy(word => word.End)
                .ToArray();
            var speechStart = words.Length > 0 ? Math.Clamp(words[0].Start, cue.Start, cue.End) : cue.Start;
            var speechEnd = words.Length > 0 ? Math.Clamp(words[^1].End, speechStart, cue.End) : cue.End;
            if (speechEnd <= speechStart + .01) { speechStart = cue.Start; speechEnd = cue.End; }
            var pauses = new List<EditorPauseTiming>();
            for (var index = 1; index < words.Length; index++)
            {
                var start = Math.Max(speechStart, words[index - 1].End);
                var end = Math.Min(speechEnd, words[index].Start);
                if (end - start >= PauseThresholdSeconds) pauses.Add(new EditorPauseTiming(start, end));
            }

            var overlapping = new List<(EditorSpeechSegment Segment, double Weight)>();
            var segmentEnd = LowerBoundSegmentStart(segmentsByStart, cue.End - .02);
            var segmentStart = LowerBoundPrefixEnd(prefixMaxSegmentEnd, cue.Start + .02);
            for (var index = segmentStart; index < segmentEnd; index++)
            {
                var segment = segmentsByStart[index];
                var weight = Overlap(segment.Start, segment.End, cue.Start, cue.End);
                if (weight > .02) overlapping.Add((segment, weight));
            }
            var voice = ResolveVoice(overlapping);
            result.Add(new EditorCueSpeechTiming(
                cue.Id,
                cue.Start,
                cue.End,
                speechStart,
                speechEnd,
                Math.Max(0, speechStart - cue.Start),
                Math.Max(0, cue.End - speechEnd),
                words,
                pauses,
                voice.Label,
                voice.Confidence,
                voice.Pitch));
        }
        return result;
    }

    private static int LowerBoundWord(IndexedWord[] words, double value)
    {
        var lower = 0;
        var upper = words.Length;
        while (lower < upper)
        {
            var middle = lower + (upper - lower) / 2;
            if (words[middle].Midpoint < value) lower = middle + 1;
            else upper = middle;
        }
        return lower;
    }

    private static int UpperBoundWord(IndexedWord[] words, double value)
    {
        var lower = 0;
        var upper = words.Length;
        while (lower < upper)
        {
            var middle = lower + (upper - lower) / 2;
            if (words[middle].Midpoint <= value) lower = middle + 1;
            else upper = middle;
        }
        return lower;
    }

    private static int LowerBoundSegmentStart(EditorSpeechSegment[] segments, double value)
    {
        var lower = 0;
        var upper = segments.Length;
        while (lower < upper)
        {
            var middle = lower + (upper - lower) / 2;
            if (segments[middle].Start < value) lower = middle + 1;
            else upper = middle;
        }
        return lower;
    }

    private static int LowerBoundPrefixEnd(double[] prefixEnd, double value)
    {
        var lower = 0;
        var upper = prefixEnd.Length;
        while (lower < upper)
        {
            var middle = lower + (upper - lower) / 2;
            if (prefixEnd[middle] <= value) lower = middle + 1;
            else upper = middle;
        }
        return lower;
    }

    public static string SourceKey(string sourcePath, long size, long lastWriteUtcTicks, double duration, string modelRevision)
    {
        var text = $"{Path.GetFullPath(sourcePath).ToUpperInvariant()}\n{size}\n{lastWriteUtcTicks}\n{duration:0.000}\n{modelRevision}";
        return Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
    }

    private static (string Label, double Confidence, double Pitch) ResolveVoice(IEnumerable<(EditorSpeechSegment Segment, double Weight)> segments)
    {
        double male = 0, female = 0, uncertain = 0, pitchWeight = 0, pitchSum = 0, confidenceSum = 0, total = 0;
        foreach (var (segment, weight) in segments)
        {
            if (!double.IsFinite(weight) || weight <= 0) continue;
            total += weight;
            confidenceSum += Math.Clamp(segment.VoiceConfidence, 0, 1) * weight;
            if (segment.MedianPitchHz > 0 && double.IsFinite(segment.MedianPitchHz))
            {
                pitchSum += segment.MedianPitchHz * weight;
                pitchWeight += weight;
            }
            switch (NormalizeVoiceClass(segment.VoiceClass))
            {
                case "male_like": male += weight; break;
                case "female_like": female += weight; break;
                default: uncertain += weight; break;
            }
        }
        if (total <= 0) return ("uncertain", 0, 0);
        var top = Math.Max(male, female);
        var runner = Math.Min(male, female) + uncertain * .35;
        var label = top <= 0 || top < runner * 1.25 ? "uncertain" : male > female ? "male_like" : "female_like";
        var confidence = Math.Clamp(confidenceSum / total, 0, 1);
        if (label == "uncertain") confidence = Math.Min(confidence, .59);
        var pitch = pitchWeight > 0 ? pitchSum / pitchWeight : 0;
        return (label, confidence, pitch);
    }

    public static string NormalizeVoiceClass(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "male" or "male_like" => "male_like",
        "female" or "female_like" => "female_like",
        _ => "uncertain",
    };

    private static void Validate(EditorSpeechAnalysis analysis)
    {
        if (analysis.Schema != CurrentSchema || analysis.SourceKey.Length != 64 || analysis.SourceKey.Any(x => !Uri.IsHexDigit(x))
            || string.IsNullOrWhiteSpace(analysis.ModelName) || analysis.ModelRevision.Length != 40 || analysis.ModelRevision.Any(x => !Uri.IsHexDigit(x))
            || analysis.Device is not ("cpu" or "cuda" or "hybrid") || string.IsNullOrWhiteSpace(analysis.ComputeType)
            || !double.IsFinite(analysis.ProbeRealtimeFactor) || analysis.ProbeRealtimeFactor <= 0
            || analysis.Segments is null || analysis.Segments.Count > EditorSubtitleDocument.MaxCues)
            throw new InvalidDataException("Dữ liệu phân tích nhịp thoại không hợp lệ.");
        foreach (var segment in analysis.Segments)
        {
            if (!double.IsFinite(segment.Start) || !double.IsFinite(segment.End) || segment.Start < 0 || segment.End <= segment.Start
                || segment.Text.Length > EditorSubtitleDocument.MaxCueCharacters || !double.IsFinite(segment.VoiceConfidence)
                || segment.VoiceConfidence is < 0 or > 1 || !double.IsFinite(segment.MedianPitchHz)
                || segment.Words is null || segment.Words.Count > 1_000)
                throw new InvalidDataException("Dữ liệu segment nhịp thoại không hợp lệ.");
            _ = NormalizeVoiceClass(segment.VoiceClass);
            foreach (var word in segment.Words)
            {
                if (string.IsNullOrWhiteSpace(word.Text) || word.Text.Length > 256 || !double.IsFinite(word.Start) || !double.IsFinite(word.End)
                    || word.Start < 0 || word.End <= word.Start || !double.IsFinite(word.Probability) || word.Probability is < 0 or > 1.0001)
                    throw new InvalidDataException("Dữ liệu word timing không hợp lệ.");
            }
        }
    }

    private static double Midpoint(double start, double end) => start + (end - start) * .5;
    private static double Overlap(double a0, double a1, double b0, double b1) => Math.Max(0, Math.Min(a1, b1) - Math.Max(a0, b0));
    private sealed record IndexedWord(double Midpoint, EditorWordTiming Word);
    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }
    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }
}
