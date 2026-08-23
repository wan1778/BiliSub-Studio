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

public static partial class EditorSubtitleDocument
{
    public const long MaxSourceBytes = 32L * 1024 * 1024;
    public const int MaxCues = 100_000;
    public const int MaxCueCharacters = 2_000;

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
