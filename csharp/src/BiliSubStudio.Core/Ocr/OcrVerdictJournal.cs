using System.Security.Cryptography;
using System.Text;

namespace BiliSubStudio.Core.Ocr;

// Nhật ký verdict giám khảo VLM (spec khóa 16/09): 1 entry/ca đã chấm.
// 28/09 (user cấm file cạnh video): verdict ghi vào Data/Verdicts/<sha>.json,
// KHÔNG bao giờ cạnh video nữa. Alongside chỉ còn để ĐỌC di sản (migration),
// Save luôn appdata. Không thuộc checkpoint (key/resume không đụng).
// Câu đổi chữ/giờ thì entry cũ khớp hụt (vô hại) — verdict hết hiệu lực tự nhiên.
// Xóa file là quên hết verdict (làm lại).
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
            // File RIÊNG cho lọc thời gian: journal cũ ".ocr-verdicts.json" của
            // VLM (16/09) ghi "keep" cho chữ bối cảnh — nạp lại là dedup chặn
            // chấm lại, đúng ca cần xóa thì không bao giờ bị drop. Tên mới để
            // verdict cũ không bao giờ được nạp.
            return videoPath + ".temporal-verdicts.json";
        }
        catch
        {
            var digest = Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(videoPath))).ToLowerInvariant();
            return Path.Combine(appDataDir, "Verdicts", digest[..16] + ".temporal-verdicts.json");
        }
    }

    // Vị trí chuẩn mới: Data app, đặt tên theo digest video (không bao giờ cạnh
    // video). Mọi Save sau 28/09 dùng đường này.
    public static string AppDataPath(string videoPath, string appDataDir)
    {
        var digest = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(videoPath))).ToLowerInvariant();
        var root = string.IsNullOrWhiteSpace(appDataDir) ? "Verdicts" : appDataDir;
        return Path.Combine(root, "Verdicts", digest[..16] + ".temporal-verdicts.json");
    }

    // Đọc di sản: appdata trước (sự thật mới), cạnh video sau (file cũ còn sót
    // vẫn cho cache hit, tới lần Save sau thì nằm appdata).
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

    // Đánh dấu 1 verdict đã duyệt (người bấm Giữ/Bỏ trong hàng đợi).
    // Pure: trả danh sách mới + found. Caller tự Save (đã có hàm Save).
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
