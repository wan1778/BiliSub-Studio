using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace BiliSubStudio.Core.Editor;

public sealed record TranslationSkillInfo(string Name, string Sha256, int EntryCount, long ExpandedBytes);

public sealed partial class TranslationSkillBundle
{
    public const string BuiltInSha256 = "2969340edd47d3d860fc2bd7b4e0211723d5b8cad6a670d44dac707243e18213";
    private const int MaxEntries = 32;
    private const long MaxEntryBytes = 2L * 1024 * 1024;
    private const long MaxExpandedBytes = 8L * 1024 * 1024;
    private static readonly string[] Required =
    [
        "SKILL.md",
        "references/character-names.md",
        "references/dialogue-voice.md",
        "references/forms-of-address.md",
        "references/research-audit.md",
        "references/tu-tien-glossary.md",
        "references/world-systems.md",
    ];

    private readonly string _core;
    private readonly IReadOnlyDictionary<string, string[]> _references;

    private TranslationSkillBundle(TranslationSkillInfo info, string core, IReadOnlyDictionary<string, string[]> references)
    {
        Info = info;
        _core = core;
        _references = references;
    }

    public TranslationSkillInfo Info { get; }

    public static TranslationSkillBundle Load(string path, bool requireBuiltInHash = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var absolute = Path.GetFullPath(path.Trim());
        var bytes = File.ReadAllBytes(absolute);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (requireBuiltInHash && !string.Equals(sha, BuiltInSha256, StringComparison.Ordinal))
            throw new InvalidDataException("Skill dịch tích hợp không đúng SHA-256 đã khóa.");
        using var memory = new MemoryStream(bytes, writable: false);
        using var archive = new ZipArchive(memory, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count is 0 or > MaxEntries) throw new InvalidDataException("Skill ZIP có số entry không hợp lệ.");
        var root = DetectRoot(archive);
        var content = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            var normalized = entry.FullName.Replace('\\', '/');
            if (normalized.StartsWith("/", StringComparison.Ordinal) || normalized.Contains("../", StringComparison.Ordinal) || normalized.Contains(':', StringComparison.Ordinal))
                throw new InvalidDataException("Skill ZIP chứa path không an toàn.");
            if (entry.Length < 0 || entry.Length > MaxEntryBytes || expanded + entry.Length > MaxExpandedBytes)
                throw new InvalidDataException("Skill ZIP vượt giới hạn giải nén.");
            expanded += entry.Length;
            if (!normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase) || normalized.EndsWith("/", StringComparison.Ordinal)) continue;
            var relative = normalized[root.Length..];
            if (!relative.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) continue;
            using var reader = new StreamReader(entry.Open(), new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true, leaveOpen: false);
            content[relative] = reader.ReadToEnd();
        }
        foreach (var required in Required)
            if (!content.ContainsKey(required)) throw new InvalidDataException($"Skill ZIP thiếu {required}.");
        var references = Required.Skip(1).ToDictionary(x => x, x => SplitSections(content[x]), StringComparer.OrdinalIgnoreCase);
        return new TranslationSkillBundle(new TranslationSkillInfo("Dịch Trung Tu Tiên", sha, archive.Entries.Count, expanded), content["SKILL.md"], references);
    }

    public string BuildCoreInstructions()
    {
        var selected = new StringBuilder();
        selected.AppendLine("QUY TẮC SKILL BẮT BUỘC (nguyên bản):").AppendLine(_core.Trim());
        return selected.ToString();
    }

    public string BuildReferenceInstructions(IEnumerable<string> sourceTexts, int maxCharacters, int initialCharacters = 0)
    {
        var source = string.Join('\n', sourceTexts);
        var selected = new StringBuilder(Math.Min(maxCharacters, 64_000));
        foreach (var pair in _references)
        {
            foreach (var section in pair.Value)
            {
                if (!Relevant(section, source)) continue;
                if (initialCharacters + selected.Length + section.Length + 80 > maxCharacters) break;
                selected.AppendLine().Append("TỪ ").Append(pair.Key).AppendLine(":").AppendLine(section.Trim());
            }
        }
        return selected.ToString();
    }

    public string BuildInstructions(IEnumerable<string> sourceTexts, int maxCharacters = 56_000)
    {
        var core = BuildCoreInstructions();
        return core + BuildReferenceInstructions(sourceTexts, maxCharacters, core.Length);
    }

    private static string DetectRoot(ZipArchive archive)
    {
        var skill = archive.Entries.FirstOrDefault(x => x.FullName.Replace('\\', '/').EndsWith("/SKILL.md", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("Skill ZIP thiếu SKILL.md ở thư mục gốc skill.");
        var normalized = skill.FullName.Replace('\\', '/');
        return normalized[..^("SKILL.md".Length)];
    }

    private static string[] SplitSections(string source) => Regex.Split(source.Replace("\r\n", "\n", StringComparison.Ordinal), @"(?m)(?=^#{1,4}\s)")
        .Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();

    private static bool Relevant(string section, string source)
    {
        if (section.StartsWith("#", StringComparison.Ordinal) && section.Length < 800) return true;
        foreach (Match token in HanTokenRegex().Matches(section))
            if (token.Length >= 2 && source.Contains(token.Value, StringComparison.Ordinal)) return true;
        return false;
    }

    [GeneratedRegex(@"[\p{IsCJKUnifiedIdeographs}]{2,12}", RegexOptions.CultureInvariant)]
    private static partial Regex HanTokenRegex();
}
