using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace BiliSubStudio.Core.Editor;

public sealed record EditorSubtitleCue(
    string Id,
    string Number,
    string Timing,
    double Start,
    double End,
    string SourceText,
    string VietnameseText = "");

public sealed record EditorSubtitleSource(
    string Path,
    long Size,
    long LastWriteUtcTicks,
    string Sha256,
    IReadOnlyList<EditorSubtitleCue> Cues);

public enum EditorCueEdge
{
    Start,
    End,
}

public static partial class EditorSubtitleDocument
{
    public const long MaxSourceBytes = 32L * 1024 * 1024;
    public const int MaxCues = 100_000;
    public const int MaxCueCharacters = 2_000;
    public const string ImportedTranslationPolicyKey = "imported-srt-v1";
    public const string DirectVietnamesePolicyKey = "vietnamese-srt-v1";

    // Explicit Vietnamese import: no language guessing, translation, retiming or file rewrite.
    public static EditorSubtitleSource UseVietnameseSrt(EditorSubtitleSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Cues.Count == 0 || source.Cues.Any(cue => string.IsNullOrWhiteSpace(cue.SourceText)))
            throw new InvalidDataException("SRT Việt không có lời đọc hợp lệ.");
        return source with
        {
            Cues = source.Cues.Select(cue => cue with { VietnameseText = cue.SourceText.Trim() }).ToArray(),
        };
    }

    public static async Task<EditorSubtitleSource> LoadAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var absolute = Path.GetFullPath(path.Trim());
        var info = new FileInfo(absolute);
        if (!info.Exists || info.Length <= 0) throw new FileNotFoundException("File SRT không tồn tại hoặc rỗng.", absolute);
        if (!string.Equals(info.Extension, ".srt", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Editor chỉ nhận file .srt ở bước Vietsub.");
        if (info.Length > MaxSourceBytes) throw new InvalidDataException("File SRT vượt giới hạn an toàn 32 MiB.");
        var bytes = await File.ReadAllBytesAsync(absolute, cancellationToken);
        var text = Decode(bytes);
        var cues = Parse(text);
        return new EditorSubtitleSource(
            absolute,
            info.Length,
            info.LastWriteTimeUtc.Ticks,
            Convert.ToHexStringLower(SHA256.HashData(bytes)),
            cues);
    }

    public static IReadOnlyList<EditorSubtitleCue> Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var normalized = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim('\n', '\uFEFF');
        if (normalized.Length == 0) throw new InvalidDataException("File SRT không có cue.");
        var matches = CueRegex().Matches(normalized);
        if (matches.Count == 0) throw new InvalidDataException("Không đọc được block SRT chuẩn: số thứ tự, timecode và nội dung.");
        if (matches.Count > MaxCues) throw new InvalidDataException("File SRT vượt giới hạn 100.000 cue.");
        var cues = new List<EditorSubtitleCue>(matches.Count);
        var previousEnd = 0;
        for (var cueIndex = 0; cueIndex < matches.Count; cueIndex++)
        {
            var match = matches[cueIndex];
            if (match.Index != previousEnd && !string.IsNullOrWhiteSpace(normalized[previousEnd..match.Index]))
                throw new InvalidDataException($"SRT có dữ liệu không hợp lệ trước block {cueIndex + 1}.");
            var number = match.Groups["number"].Value.Trim();
            var timing = match.Groups["timing"].Value.Trim();
            var text = match.Groups["text"].Value.TrimEnd('\n').Trim();
            if (text.Length == 0) throw new InvalidDataException($"Block SRT {number} không có nội dung.");
            if (text.Length > MaxCueCharacters) throw new InvalidDataException($"Block SRT {number} vượt giới hạn an toàn {MaxCueCharacters} ký tự.");
            var times = timing.Split("-->", 2, StringSplitOptions.TrimEntries);
            if (times.Length != 2 || !TryTimestamp(times[0], out var start) || !TryTimestamp(times[1].Split(' ', 2)[0], out var end) || end <= start)
                throw new InvalidDataException($"Timecode block SRT {number} không hợp lệ.");
            var stable = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{cues.Count}\n{number}\n{timing}\n{text}")))[..20];
            cues.Add(new EditorSubtitleCue(stable, number, timing, start, end, text));
            previousEnd = match.Index + match.Length;
        }
        if (!string.IsNullOrWhiteSpace(normalized[previousEnd..])) throw new InvalidDataException("SRT có dữ liệu thừa sau block cuối.");
        return cues;
    }

    public static string RenderVietnamese(IReadOnlyList<EditorSubtitleCue> cues)
    {
        ArgumentNullException.ThrowIfNull(cues);
        if (cues.Count == 0) throw new InvalidDataException("Chưa có cue SRT để xuất.");
        var output = new StringBuilder(cues.Count * 96);
        foreach (var cue in cues)
        {
            var translated = cue.VietnameseText.Trim();
            if (translated.Length == 0) throw new InvalidDataException($"Cue {cue.Number} chưa được dịch.");
            output.AppendLine(cue.Number);
            output.AppendLine(cue.Timing);
            output.AppendLine(translated);
            output.AppendLine();
        }
        return output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
    }

    public static string RenderSource(IReadOnlyList<EditorSubtitleCue> cues)
    {
        ArgumentNullException.ThrowIfNull(cues);
        if (cues.Count == 0) throw new InvalidDataException("Chưa có cue nguồn để xuất SRT.");
        var output = new StringBuilder(cues.Count * 96);
        foreach (var cue in cues)
        {
            var source = cue.SourceText.Trim();
            if (source.Length == 0) throw new InvalidDataException($"Cue {cue.Number} không có lời thoại nguồn.");
            output.AppendLine(cue.Number);
            output.AppendLine(cue.Timing);
            output.AppendLine(source);
            output.AppendLine();
        }
        return output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
    }

    public static string FormatTimestamp(double seconds)
    {
        if (!double.IsFinite(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
        var totalMilliseconds = (long)Math.Round(Math.Max(0, seconds) * 1000, MidpointRounding.AwayFromZero);
        var hours = totalMilliseconds / 3_600_000;
        var minutes = totalMilliseconds / 60_000 % 60;
        var wholeSeconds = totalMilliseconds / 1000 % 60;
        var milliseconds = totalMilliseconds % 1000;
        return string.Create(CultureInfo.InvariantCulture, $"{hours:00}:{minutes:00}:{wholeSeconds:00},{milliseconds:000}");
    }

    public static string FormatTiming(double start, double end) =>
        $"{FormatTimestamp(start)} --> {FormatTimestamp(end)}";

    public static EditorSubtitleCue RetimeCue(
        IReadOnlyList<EditorSubtitleCue> cues,
        int index,
        EditorCueEdge edge,
        double value,
        double minDuration = .2)
    {
        ArgumentNullException.ThrowIfNull(cues);
        if (index < 0 || index >= cues.Count) throw new ArgumentOutOfRangeException(nameof(index));
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        if (!double.IsFinite(minDuration) || minDuration <= 0) throw new ArgumentOutOfRangeException(nameof(minDuration));
        var cue = cues[index];
        if (edge == EditorCueEdge.Start)
        {
            var previousEnd = index > 0 ? cues[index - 1].End : 0;
            var maxStart = cue.End - minDuration;
            var minStart = Math.Min(Math.Max(previousEnd, 0), maxStart);
            var start = Math.Clamp(value, minStart, maxStart);
            return cue with { Start = start, End = cue.End, Timing = FormatTiming(start, cue.End) };
        }
        var nextStart = index < cues.Count - 1 ? cues[index + 1].Start : double.PositiveInfinity;
        var minEnd = cue.Start + minDuration;
        var maxEnd = Math.Max(nextStart, minEnd);
        var end = Math.Clamp(value, minEnd, maxEnd);
        return cue with { Start = cue.Start, End = end, Timing = FormatTiming(cue.Start, end) };
    }

    public static void ValidateUnchangedTimeline(IReadOnlyList<EditorSubtitleCue> source, IReadOnlyList<EditorSubtitleCue> translated)
    {
        if (source.Count != translated.Count) throw new InvalidDataException("Model làm thay đổi số block SRT.");
        for (var index = 0; index < source.Count; index++)
        {
            var left = source[index];
            var right = translated[index];
            if (!string.Equals(left.Id, right.Id, StringComparison.Ordinal) ||
                !string.Equals(left.Number, right.Number, StringComparison.Ordinal) ||
                !string.Equals(left.Timing, right.Timing, StringComparison.Ordinal))
                throw new InvalidDataException($"Model làm thay đổi thứ tự hoặc timecode tại block {left.Number}.");
        }
    }

    public static EditorSubtitleSource AttachVietnameseSrt(
        EditorSubtitleSource source,
        EditorSubtitleSource vietnamese)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(vietnamese);
        if (string.Equals(Path.GetFullPath(source.Path), Path.GetFullPath(vietnamese.Path), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Hãy chọn SRT Việt đã dịch khác với SRT tiếng Trung nguồn.");
        if (source.Cues.Count != vietnamese.Cues.Count)
            throw new InvalidDataException("SRT Việt không khớp số cue với SRT tiếng Trung nguồn.");

        var merged = new EditorSubtitleCue[source.Cues.Count];
        for (var index = 0; index < source.Cues.Count; index++)
        {
            var original = source.Cues[index];
            var translated = vietnamese.Cues[index];
            if (!string.Equals(original.Number, translated.Number, StringComparison.Ordinal)
                || !string.Equals(original.Timing, translated.Timing, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"SRT Việt không khớp số thứ tự hoặc timecode tại cue {original.Number}.");
            }
            if (string.IsNullOrWhiteSpace(translated.SourceText))
                throw new InvalidDataException($"SRT Việt thiếu lời dịch tại cue {original.Number}.");
            merged[index] = original with { VietnameseText = translated.SourceText.Trim() };
        }
        return source with { Cues = merged };
    }

    private static string Decode(byte[] bytes)
    {
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(bytes);
        }
    }

    private static bool TryTimestamp(string value, out double seconds)
    {
        var match = TimestampRegex().Match(value.Trim());
        if (!match.Success || !int.TryParse(match.Groups["h"].Value, out var hours) ||
            !int.TryParse(match.Groups["m"].Value, out var minutes) || !int.TryParse(match.Groups["s"].Value, out var whole) ||
            !int.TryParse(match.Groups["ms"].Value.PadRight(3, '0'), out var milliseconds) || minutes > 59 || whole > 59)
        {
            seconds = 0;
            return false;
        }
        seconds = hours * 3600d + minutes * 60d + whole + milliseconds / 1000d;
        return double.IsFinite(seconds);
    }

    [GeneratedRegex(@"(?ms)(?:\A|\n{2,})(?<number>\d+)[ \t]*\n(?<timing>[^\n]*?-->[^\n]*?)\n(?<text>.*?)(?=\n{2,}\d+[ \t]*\n[^\n]*?-->|\z)", RegexOptions.CultureInvariant)]
    private static partial Regex CueRegex();

    [GeneratedRegex(@"^(?<h>\d{1,3}):(?<m>\d{2}):(?<s>\d{2})[,.](?<ms>\d{1,3})$", RegexOptions.CultureInvariant)]
    private static partial Regex TimestampRegex();
}
