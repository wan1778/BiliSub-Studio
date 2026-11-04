using System.Globalization;
using System.Text;

namespace BiliSubStudio.Core.Editor;

// Single source of truth for TTS voice options (UI dropdown + installer +
// worker voice resolution). Tags are ASCII slugs (safe in manifests, paths,
// logs); worker folds SDK labels the same way.
public static class TtsVoiceCatalog
{
    public const string VieneuPrefix = "vieneu_";
    public const string DefaultVoice = "vieneu_ngoc_huyen";

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
        // Pin Đ/đ explicitly: NFD on this machine does not decompose
        // U+0110/U+0111 (while the other Vietnamese vowels decompose normally),
        // so leaving them untouched keeps Đ in the slug.
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
        Array.AsReadOnly(VieneuPresetLabels
            .Select(label => (VieneuPrefix + FoldSlug(label), $"{label} · VieNeu GPU")).ToArray());

    private static string Canonical(string voice) => voice.Trim().ToLowerInvariant().Replace('-', '_');

    public static bool IsVieneuVoice(string voice)
    {
        if (string.IsNullOrWhiteSpace(voice)) return false;
        var canonical = Canonical(voice);
        return VieneuPresetLabels.Any(label => VieneuPrefix + FoldSlug(label) == canonical);
    }

    public static string DisplayLabel(string voice)
    {
        if (!IsVieneuVoice(voice)) return voice;
        var slug = Canonical(voice)[VieneuPrefix.Length..];
        return VieneuPresetLabels.FirstOrDefault(label => FoldSlug(label) == slug) ?? voice;
    }

    // Natural pace measured on 01/10 (six standard cues per preset, GPU batch,
    // characters/second; see D:\temp\Temp\opencode\preset_pace.json). Used only
    // as an estimate when widening timing windows, not as a speed knob (the
    // model has a single pace). The +/-5% error is acceptable because the
    // worker and validator measure the real pace afterwards.
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
        var slug = Canonical(voice)[VieneuPrefix.Length..];
        var label = VieneuPresetLabels.FirstOrDefault(candidate => FoldSlug(candidate) == slug);
        if (label is not null && VieneuPaceByLabel.TryGetValue(label, out var pace) && pace > 0)
            return pace;
        return 4.0;
    }
}

/// <summary>
/// Timing limits the C# side and the VieNeu worker must agree on to the frame.
/// A single source so a planner change cannot silently drift from the worker's
/// manifest validation (mirrored by MAX_LEAD_FRAMES in internal/vieneu/worker.py).
/// </summary>
public static class TtsTimingLimits
{
    /// <summary>
    /// F1 (17/11): the most a voice may start EARLY, reading into the silence
    /// before its subtitle, after the trailing gap has been exhausted. Left gaps
    /// are only borrowed to keep a whole sentence from being cut short; the voice
    /// still appears essentially with the subtitle.
    /// </summary>
    public const double MaxLeadSeconds = 1.0;
}
