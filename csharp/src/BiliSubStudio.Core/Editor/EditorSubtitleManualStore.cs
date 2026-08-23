using System.Text.Json;
using BiliSubStudio.Core.Configuration;

namespace BiliSubStudio.Core.Editor;

public sealed record EditorManualCueState(string? SourceOverride, string? VietnameseOverride, bool Locked);

public sealed class EditorSubtitleManualStore
{
    private const int Schema = 1;
    private const long MaxBytes = 8L * 1024 * 1024;
    private readonly string _directory;
    private readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private sealed record ManualDocument(int Schema, string SourceSha256, Dictionary<string, EditorManualCueState> Cues);

    public EditorSubtitleManualStore(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _directory = Path.Combine(paths.Data, "Projects", "SubtitleManual");
    }

    public async Task<IReadOnlyDictionary<string, EditorManualCueState>> LoadAsync(string sourceSha256, CancellationToken cancellationToken)
    {
        ValidateSha(sourceSha256);
        var path = StatePath(sourceSha256);
        if (!File.Exists(path)) return new Dictionary<string, EditorManualCueState>(StringComparer.Ordinal);
        try
        {
            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > MaxBytes) throw new InvalidDataException("Manual subtitle state có kích thước không hợp lệ.");
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var document = await JsonSerializer.DeserializeAsync<ManualDocument>(stream, _json, cancellationToken)
                ?? throw new InvalidDataException("Manual subtitle state rỗng.");
            if (document.Schema != Schema || !string.Equals(document.SourceSha256, sourceSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Manual subtitle state không khớp SRT nguồn.");
            var result = new Dictionary<string, EditorManualCueState>(StringComparer.Ordinal);
            foreach (var pair in document.Cues ?? new Dictionary<string, EditorManualCueState>())
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null) continue;
                ValidateText(pair.Value.SourceOverride);
                ValidateText(pair.Value.VietnameseOverride);
                if (pair.Value.SourceOverride is null && pair.Value.VietnameseOverride is null && !pair.Value.Locked) continue;
                result[pair.Key] = pair.Value;
            }
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            Quarantine(path);
            return new Dictionary<string, EditorManualCueState>(StringComparer.Ordinal);
        }
    }

    public async Task SaveAsync(string sourceSha256, IReadOnlyDictionary<string, EditorManualCueState> states, CancellationToken cancellationToken)
    {
        ValidateSha(sourceSha256);
        ArgumentNullException.ThrowIfNull(states);
        Directory.CreateDirectory(_directory);
        var path = StatePath(sourceSha256);
        var normalized = new Dictionary<string, EditorManualCueState>(StringComparer.Ordinal);
        foreach (var pair in states)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null) continue;
            ValidateText(pair.Value.SourceOverride);
            ValidateText(pair.Value.VietnameseOverride);
            if (pair.Value.SourceOverride is null && pair.Value.VietnameseOverride is null && !pair.Value.Locked) continue;
            normalized[pair.Key] = pair.Value;
        }
        if (normalized.Count == 0)
        {
            TryDelete(path);
            return;
        }
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
                await JsonSerializer.SerializeAsync(stream, new ManualDocument(Schema, sourceSha256.ToLowerInvariant(), normalized), _json, cancellationToken);
            File.Move(temp, path, true);
        }
        finally { TryDelete(temp); }
    }

    public static EditorSubtitleSource Apply(EditorSubtitleSource source, IReadOnlyDictionary<string, EditorManualCueState> states)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(states);
        var cues = source.Cues.Select(cue =>
        {
            if (!states.TryGetValue(cue.Id, out var state)) return cue;
            var sourceText = string.IsNullOrWhiteSpace(state.SourceOverride) ? cue.SourceText : state.SourceOverride!.Trim();
            var vietnamese = state.VietnameseOverride is null ? cue.VietnameseText : state.VietnameseOverride.Trim();
            ValidateText(sourceText);
            ValidateText(vietnamese);
            return cue with { SourceText = sourceText, VietnameseText = vietnamese };
        }).ToArray();
        EditorSubtitleDocument.ValidateUnchangedTimeline(source.Cues, cues);
        return source with { Cues = cues };
    }

    private string StatePath(string sha) => Path.Combine(_directory, sha.ToLowerInvariant() + ".json");

    private static void ValidateSha(string sha)
    {
        if (sha.Length != 64 || sha.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("SHA-256 SRT không hợp lệ.", nameof(sha));
    }

    private static void ValidateText(string? value)
    {
        if (value is not null && value.Length > EditorSubtitleDocument.MaxCueCharacters)
            throw new InvalidDataException($"Manual subtitle vượt {EditorSubtitleDocument.MaxCueCharacters} ký tự mỗi cue.");
    }

    private static void Quarantine(string path)
    {
        try { File.Move(path, path + ".corrupt-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), true); } catch { }
    }
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
