using System.Globalization;
using System.Text;

namespace BiliSubStudio.Core.Editor;

// Single source of truth for TTS voice options (UI dropdown + installer +
// worker voice resolution). Tags are ASCII slugs (safe in manifests, paths,
// logs); worker folds SDK labels the same way. "vieneu_" prefix disambiguates
// the VieNeu Ngọc Huyền preset from the NGHI ngoc_huyen voice.
public static class TtsVoiceCatalog
{
    public const string NghiVoiceTag = "ngoc_huyen";
    public const string NghiVoiceContent = "Ngọc Huyền · NGHI local";
    public const string VieneuPrefix = "vieneu_";

    public static readonly IReadOnlyList<string> VieneuPresetLabels = Array.AsReadOnly(new[]
    {
        "Adam bựa", "Trúc Ly", "Thiện Minh", "Mai Anh", "Hải Đăng",
        "Thùy Dung", "Thiền Tâm Đức", "Ngọc Huyền", "Quang Sơn", "Ngọc Trân",
        "Minh Đức", "Phạm Tuyên", "Thái Sơn", "Xuân Vĩnh", "Thanh Bình",
        "Ngọc Linh", "Đoan Trang", "Thục Đoan", "Minh Triết", "Mỹ Duyên",
        "Quỳnh Anh", "Đức Trí", "Kim Thanh", "Adam", "Quốc Tuấn",
    });

    public static string FoldSlug(string label)
    {
        // Đ/đ ghim tường minh: NFD trên máy này không rã U+0110/U+0111 (trong
        // khi ảăâêôơư rã bình thường) — không đụng vào là slug giữ nguyên Đ.
        var folded = new StringBuilder();
        foreach (var c in label.Trim().Replace('Đ', 'D').Replace('đ', 'd').Normalize(NormalizationForm.FormD))
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark) continue;
            folded.Append(c is ' ' or '-' ? '_' : char.ToLowerInvariant(c));
        }
        return folded.ToString();
    }

    public static IReadOnlyList<(string Tag, string Content)> VoiceOptions { get; } =
        Array.AsReadOnly(new[] { (NghiVoiceTag, NghiVoiceContent) }
            .Concat(VieneuPresetLabels.Select(label => (VieneuPrefix + FoldSlug(label), $"{label} · VieNeu GPU"))).ToArray());

    public static bool IsVieneuVoice(string voice) =>
        voice.StartsWith(VieneuPrefix, StringComparison.Ordinal)
        && VieneuPresetLabels.Any(label => VieneuPrefix + FoldSlug(label) == voice);

    public static string DisplayLabel(string voice)
    {
        if (!IsVieneuVoice(voice)) return voice;
        var slug = voice[VieneuPrefix.Length..];
        return VieneuPresetLabels.FirstOrDefault(label => FoldSlug(label) == slug) ?? voice;
    }

    // Pace tự nhiên đo thật 01/10 (6 câu chuẩn/preset, batch GPU, chữ/giây —
    // D:\temp\Temp\opencode\preset_pace.json): chỉ dùng ƯỚC need khi nới khung
    // giờ, không phải núm tốc độ (model chỉ có 1 nhịp). Sai số ±5% chấp nhận
    // được vì worker + validator đo lại pace thật sau đó.
    private static readonly IReadOnlyDictionary<string, double> VieneuPaceByLabel =
        new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["Adam bựa"] = 3.99, ["Trúc Ly"] = 4.98, ["Thiện Minh"] = 4.42,
            ["Mai Anh"] = 3.98, ["Hải Đăng"] = 4.70, ["Thùy Dung"] = 4.86,
            ["Thiền Tâm Đức"] = 3.25, ["Ngọc Huyền"] = 4.36, ["Quang Sơn"] = 3.77,
            ["Ngọc Trân"] = 3.70, ["Minh Đức"] = 3.74, ["Phạm Tuyên"] = 4.27,
            ["Thái Sơn"] = 3.96, ["Xuân Vĩnh"] = 4.53, ["Thanh Bình"] = 4.79,
            ["Ngọc Linh"] = 4.17, ["Đoan Trang"] = 4.21, ["Thục Đoan"] = 3.91,
            ["Minh Triết"] = 4.56, ["Mỹ Duyên"] = 3.46, ["Quỳnh Anh"] = 4.14,
            ["Đức Trí"] = 3.23, ["Kim Thanh"] = 3.04, ["Adam"] = 4.33,
            ["Quốc Tuấn"] = 4.42,
        };

    public static double PaceWordsPerSecond(string voice)
    {
        if (!IsVieneuVoice(voice)) return 0;
        var slug = voice[VieneuPrefix.Length..];
        var label = VieneuPresetLabels.FirstOrDefault(candidate => FoldSlug(candidate) == slug);
        if (label is not null && VieneuPaceByLabel.TryGetValue(label, out var pace) && pace > 0)
            return pace;
        return 4.0;
    }
}
