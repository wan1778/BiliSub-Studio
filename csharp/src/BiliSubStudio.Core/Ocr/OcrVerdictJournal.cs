using System.Security.Cryptography;
using System.Text;

namespace BiliSubStudio.Core.Ocr;

// Journal of VLM judge verdicts (pinned spec 16/09): 1 entry per judged cue.
// 28/09 (user forbids files beside the video): verdicts are written to Data/Verdicts/<sha>.json,
// never beside the video again. Alongside remains only to READ legacy files (migration),
// Save always goes to appdata. Not part of the checkpoint (key/resume are untouched).
// If a cue's text/time changes, the old entry misses the match (harmless) — the verdict goes stale naturally.
// Deleting the file forgets all verdicts (judge again).
public sealed record OcrCueVerdict(
    double CueStart,
    string CueText,
    string Decision,
    string Reason,
    string Model,
    string DateUtc,
    bool Reviewed = false,
    string? FixedText = null);

public static class OcrVerdictJournal
{
    public static string JournalPath(string videoPath, string appDataDir)
    {
        try
        {
            // A SEPARATE file for the time filter: the old VLM ".ocr-verdicts.json" (16/09)
            // wrote "keep" for context text — reloading it would make dedup block
            // judging again, so cues that should be deleted would never be dropped. The new name ensures
            // old verdicts are never loaded.
            return videoPath + ".temporal-verdicts.json";
        }
        catch
        {
            var digest = Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(videoPath))).ToLowerInvariant();
            return Path.Combine(appDataDir, "Verdicts", digest[..16] + ".temporal-verdicts.json");
        }
    }

    // New canonical location: app Data, named by the video digest (never beside the
    // video). Every Save after 28/09 uses this path.
    public static string AppDataPath(string videoPath, string appDataDir)
    {
        var digest = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(videoPath))).ToLowerInvariant();
        var root = string.IsNullOrWhiteSpace(appDataDir) ? "Verdicts" : appDataDir;
        return Path.Combine(root, "Verdicts", digest[..16] + ".temporal-verdicts.json");
    }

    // Read legacy: appdata first (the newer truth), then beside the video (old files still
    // provide cache hits until the next Save puts them in appdata).
    public static IReadOnlyList<OcrCueVerdict> LoadAny(string appDataPath, string alongsidePath)
    {
        var entries = Load(appDataPath);
        return entries.Count > 0 ? entries : Load(alongsidePath);
    }

    public static IReadOnlyList<OcrCueVerdict> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return [];
            var entries = System.Text.Json.JsonSerializer.Deserialize<List<OcrCueVerdict>>(
                File.ReadAllText(path));
            if (entries is null) return [];
            return entries
                .Where(entry => entry is not null
                    && entry.CueText is not null
                    && double.IsFinite(entry.CueStart) && entry.CueStart >= 0
                    && (entry.Decision is "keep" or "fix" or "drop" or "unsure"))
                .ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return [];
        }
    }

    public static void Save(string path, IReadOnlyList<OcrCueVerdict> entries)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary,
                System.Text.Json.JsonSerializer.Serialize(entries,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); } catch { }
        }
    }

    public static bool HasVerdict(
        IReadOnlyList<OcrCueVerdict> entries, double start, string? text)
    {
        foreach (var entry in entries)
        {
            if (entry is not null
                && Math.Abs(entry.CueStart - start) <= 0.001
                && string.Equals(entry.CueText, text, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    // Mark one verdict as reviewed (the user clicked Keep/Drop in the queue).
    // Pure: returns a new list + found. The caller Saves itself (a Save function already exists).
    public static (IReadOnlyList<OcrCueVerdict> Entries, bool Found) TryMarkReviewed(
        IReadOnlyList<OcrCueVerdict> entries, double start, string? text)
    {
        var found = false;
        var output = new List<OcrCueVerdict>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry is null) continue;
            if (!found
                && Math.Abs(entry.CueStart - start) <= 0.001
                && string.Equals(entry.CueText, text, StringComparison.Ordinal))
            {
                found = true;
                output.Add(entry.Reviewed ? entry : entry with { Reviewed = true });
            }
            else
            {
                output.Add(entry);
            }
        }
        return (output, found);
    }
}
