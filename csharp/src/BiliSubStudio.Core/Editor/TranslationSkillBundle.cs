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
    private const string CompactCultivationProfile = "SKILL TU TIÊN: phim tu tiên/tiên hiệp/cổ trang Trung Quốc; tên người đọc Hán-Việt, không dịch nghĩa từng chữ (陈长安=Trần Trường An); giữ vai vế, xưng hô, thuật ngữ; không hiện đại hóa hay bịa.";
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

    private static readonly (string Source, string Vietnamese)[] LockedCultivationTerms =
    [
        ("太上长老", "thái thượng trưởng lão"),
        ("亲传弟子", "thân truyền đệ tử"),
        ("核心弟子", "hạch tâm đệ tử"),
        ("内门弟子", "nội môn đệ tử"),
        ("外门弟子", "ngoại môn đệ tử"),
        ("大师兄", "đại sư huynh"),
        ("大师姐", "đại sư tỷ"),
        ("半步金丹", "bán bộ kim đan"),
        ("上品灵石", "thượng phẩm linh thạch"),
        ("中品灵石", "trung phẩm linh thạch"),
        ("下品灵石", "hạ phẩm linh thạch"),
        ("炼气期", "Luyện Khí kỳ"),
        ("筑基期", "Trúc Cơ kỳ"),
        ("金丹期", "Kim Đan kỳ"),
        ("元婴期", "Nguyên Anh kỳ"),
        ("化神期", "Hóa Thần kỳ"),
        ("炼虚期", "Luyện Hư kỳ"),
        ("合体期", "Hợp Thể kỳ"),
        ("大乘期", "Đại Thừa kỳ"),
        ("渡劫期", "Độ Kiếp kỳ"),
        ("储物戒", "nhẫn trữ vật"),
        ("储物袋", "túi trữ vật"),
        ("天灵根", "thiên linh căn"),
        ("太上长老", "thái thượng trưởng lão"),
        ("师尊", "sư tôn"),
        ("师父", "sư phụ"),
        ("师祖", "sư tổ"),
        ("师伯", "sư bá"),
        ("师叔", "sư thúc"),
        ("师兄", "sư huynh"),
        ("师姐", "sư tỷ"),
        ("师弟", "sư đệ"),
        ("师妹", "sư muội"),
        ("道友", "đạo hữu"),
        ("前辈", "tiền bối"),
        ("晚辈", "vãn bối"),
        ("掌门", "chưởng môn"),
        ("宗主", "tông chủ"),
        ("长老", "trưởng lão"),
        ("老祖", "lão tổ"),
        ("本座", "bản tọa"),
        ("本尊", "bản tôn"),
        ("本帝", "bản đế"),
        ("本君", "bản quân"),
        ("本王", "bản vương"),
        ("贫道", "bần đạo"),
        ("贫僧", "bần tăng"),
        ("在下", "tại hạ"),
        ("老夫", "lão phu"),
        ("老朽", "lão hủ"),
        ("仙尊", "tiên tôn"),
        ("魔尊", "ma tôn"),
        ("帝君", "đế quân"),
        ("神尊", "thần tôn"),
        ("上神", "thượng thần"),
        ("真君", "chân quân"),
        ("真人", "chân nhân"),
        ("尊者", "tôn giả"),
        ("炼气", "luyện khí"),
        ("筑基", "trúc cơ"),
        ("结丹", "kết đan"),
        ("金丹", "kim đan"),
        ("元婴", "nguyên anh"),
        ("化神", "hóa thần"),
        ("炼虚", "luyện hư"),
        ("合体", "hợp thể"),
        ("大乘", "đại thừa"),
        ("渡劫", "độ kiếp"),
        ("飞升", "phi thăng"),
        ("破境", "phá cảnh"),
        ("突破", "đột phá"),
        ("境界", "cảnh giới"),
        ("灵气", "linh khí"),
        ("灵力", "linh lực"),
        ("真气", "chân khí"),
        ("真元", "chân nguyên"),
        ("丹田", "đan điền"),
        ("经脉", "kinh mạch"),
        ("灵根", "linh căn"),
        ("神识", "thần thức"),
        ("神魂", "thần hồn"),
        ("元神", "nguyên thần"),
        ("心魔", "tâm ma"),
        ("瓶颈", "bình cảnh"),
        ("闭关", "bế quan"),
        ("出关", "xuất quan"),
        ("修为", "tu vi"),
        ("修炼", "tu luyện"),
        ("功法", "công pháp"),
        ("心法", "tâm pháp"),
        ("剑诀", "kiếm quyết"),
        ("法诀", "pháp quyết"),
        ("神通", "thần thông"),
        ("秘术", "bí thuật"),
        ("禁术", "cấm thuật"),
        ("身法", "thân pháp"),
        ("剑意", "kiếm ý"),
        ("剑气", "kiếm khí"),
        ("威压", "uy áp"),
        ("灵压", "linh áp"),
        ("宗门", "tông môn"),
        ("山门", "sơn môn"),
        ("内门", "nội môn"),
        ("外门", "ngoại môn"),
        ("洞府", "động phủ"),
        ("洞天", "động thiên"),
        ("福地", "phúc địa"),
        ("秘境", "bí cảnh"),
        ("禁地", "cấm địa"),
        ("禁制", "cấm chế"),
        ("结界", "kết giới"),
        ("阵法", "trận pháp"),
        ("阵眼", "trận nhãn"),
        ("阵盘", "trận bàn"),
        ("符箓", "phù lục"),
        ("灵符", "linh phù"),
        ("丹药", "đan dược"),
        ("炼丹", "luyện đan"),
        ("丹炉", "đan lô"),
        ("丹火", "đan hỏa"),
        ("灵药", "linh dược"),
        ("药材", "dược liệu"),
        ("丹方", "đan phương"),
        ("法宝", "pháp bảo"),
        ("法器", "pháp khí"),
        ("灵器", "linh khí"),
        ("仙器", "tiên khí"),
        ("神器", "thần khí"),
        ("飞剑", "phi kiếm"),
        ("灵石", "linh thạch"),
        ("上界", "thượng giới"),
        ("下界", "hạ giới"),
        ("凡界", "phàm giới"),
        ("人界", "nhân giới"),
        ("仙界", "tiên giới"),
        ("魔界", "ma giới"),
        ("妖界", "yêu giới"),
        ("鬼界", "quỷ giới"),
        ("冥界", "minh giới"),
        ("神界", "thần giới"),
        ("佛界", "Phật giới"),
        ("界海", "giới hải"),
        ("仙族", "tiên tộc"),
        ("神族", "thần tộc"),
        ("魔族", "ma tộc"),
        ("妖族", "yêu tộc"),
        ("鬼族", "quỷ tộc"),
        ("灵族", "linh tộc"),
        ("妖兽", "yêu thú"),
        ("灵兽", "linh thú"),
        ("魂魄", "hồn phách"),
        ("夺舍", "đoạt xá"),
        ("轮回", "luân hồi"),
        ("转世", "chuyển thế"),
        ("寿元", "thọ nguyên"),
        ("天劫", "thiên kiếp"),
        ("雷劫", "lôi kiếp"),
        ("心劫", "tâm kiếp"),
        ("护体", "hộ thể"),
        ("自爆", "tự bạo"),
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

    public IReadOnlyList<KeyValuePair<string, string>> MatchLockedTerms(IEnumerable<string> sourceTexts, int maxItems = 24)
    {
        if (maxItems <= 0) return Array.Empty<KeyValuePair<string, string>>();
        var source = string.Join('\n', sourceTexts);
        var matched = new List<KeyValuePair<string, string>>(Math.Min(maxItems, 24));
        foreach (var term in LockedCultivationTerms.OrderByDescending(x => x.Source.Length))
        {
            if (!source.Contains(term.Source, StringComparison.Ordinal)) continue;
            if (matched.Any(x => x.Key.Contains(term.Source, StringComparison.Ordinal))) continue;
            matched.Add(new KeyValuePair<string, string>(term.Source, term.Vietnamese));
            if (matched.Count >= maxItems) break;
        }
        return matched;
    }

    public string BuildReferenceInstructions(IEnumerable<string> sourceTexts, int maxCharacters, int initialCharacters = 0)
    {
        var source = string.Join('\n', sourceTexts);
        var selected = new StringBuilder(Math.Min(maxCharacters, 64_000));
        if (initialCharacters + CompactCultivationProfile.Length + Environment.NewLine.Length <= maxCharacters)
            selected.AppendLine(CompactCultivationProfile);

        foreach (var pair in _references)
        {
            foreach (var section in pair.Value)
            {
                if (!Relevant(section, source)) continue;
                var block = Environment.NewLine + "TỪ " + pair.Key + ":" + Environment.NewLine + section.Trim() + Environment.NewLine;
                if (initialCharacters + selected.Length + block.Length > maxCharacters) continue;
                selected.Append(block);
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
        foreach (Match token in HanTokenRegex().Matches(section))
            if (token.Length >= 2 && source.Contains(token.Value, StringComparison.Ordinal)) return true;
        return false;
    }

    [GeneratedRegex(@"[\p{IsCJKUnifiedIdeographs}]{2,12}", RegexOptions.CultureInvariant)]
    private static partial Regex HanTokenRegex();
}
