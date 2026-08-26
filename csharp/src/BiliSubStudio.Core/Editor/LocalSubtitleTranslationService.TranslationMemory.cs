using System.Text.Json;

namespace BiliSubStudio.Core.Editor;

public sealed partial class LocalSubtitleTranslationService
{
    private const string MemoryTranslationSchema = "{\"type\":\"object\",\"properties\":{\"translations\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\"},\"text\":{\"type\":\"string\"}},\"required\":[\"id\",\"text\"],\"additionalProperties\":false}},\"names\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{\"source\":{\"type\":\"string\"},\"text\":{\"type\":\"string\"}},\"required\":[\"source\",\"text\"],\"additionalProperties\":false}},\"relations\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{\"key\":{\"type\":\"string\"},\"address\":{\"type\":\"string\"},\"note\":{\"type\":\"string\"}},\"required\":[\"key\",\"address\",\"note\"],\"additionalProperties\":false}}},\"required\":[\"translations\",\"names\",\"relations\"],\"additionalProperties\":false}";

    private sealed record TranslationRelationMemory(string Address, string Note);

    private sealed record TranslationPromptMemory(
        IReadOnlyList<KeyValuePair<string, string>> Terms,
        IReadOnlyList<KeyValuePair<string, string>> RequiredTerms,
        IReadOnlyDictionary<string, string> Names,
        IReadOnlyDictionary<string, TranslationRelationMemory> Relations);

    private sealed record ValidatedTranslationBatch(
        IReadOnlyDictionary<string, string> Translations,
        IReadOnlyDictionary<string, string> Names,
        IReadOnlyDictionary<string, TranslationRelationMemory> Relations);

    private TranslationPromptMemory BuildTranslationPromptMemory(
        IReadOnlyList<EditorSubtitleCue> target,
        IReadOnlyList<EditorSubtitleCue> context,
        TranslationCheckpoint checkpoint)
    {
        var contextText = string.Join('\n', context.Select(x => x.SourceText));
        var terms = Skill.MatchLockedTerms(context.Select(x => x.SourceText), 24);
        var requiredTerms = Skill.MatchLockedTerms(target.Select(x => x.SourceText), 16);
        var names = checkpoint.Names
            .Where(x => contextText.Contains(x.Key, StringComparison.Ordinal))
            .OrderByDescending(x => x.Key.Length)
            .Take(12)
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        var relations = checkpoint.Relations
            .Where(x => contextText.Contains(x.Key, StringComparison.Ordinal) || names.ContainsKey(x.Key))
            .Take(6)
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        if (relations.Count == 0 && checkpoint.Relations.Count > 0)
        {
            foreach (var pair in checkpoint.Relations.Reverse().Take(2).Reverse())
                relations[pair.Key] = pair.Value;
        }
        return new TranslationPromptMemory(terms, requiredTerms, names, relations);
    }

    private string BuildMemoryTranslationPrompt(
        IReadOnlyList<EditorSubtitleCue> target,
        IReadOnlyList<EditorSubtitleCue> context,
        TranslationPromptMemory memory)
    {
        var terms = memory.Terms.Count == 0
            ? "-"
            : string.Join("; ", memory.Terms.Select(x => $"{x.Key}={x.Value}"));
        var names = memory.Names.Count == 0
            ? "-"
            : string.Join("; ", memory.Names.Select(x => $"{x.Key}={x.Value}"));
        var relations = memory.Relations.Count == 0
            ? "-"
            : string.Join("; ", memory.Relations.Select(x =>
                $"{x.Key}: gọi={x.Value.Address}; {x.Value.Note}".TrimEnd(' ', ';')));
        var relevantSkill = Skill.BuildReferenceInstructions(context.Select(x => x.SourceText), 900).Trim();
        var contextJson = JsonSerializer.Serialize(context.Select(x => new { id = x.Id, text = x.SourceText }));
        var targetJson = JsonSerializer.Serialize(target.Select(x => new { id = x.Id, text = x.SourceText }));
        return $$"""
            Bạn là dịch giả phụ đề phim tu tiên/tiên hiệp Trung Quốc. Dịch TARGET tự nhiên.
            Bắt buộc đúng TERMS, NAMES, RELATION; tên người đọc Hán-Việt, không dịch nghĩa từng chữ (陈长安=Trần Trường An). Không hiện đại hóa xưng hô, không bịa/thêm bớt.
            TERMS: {{terms}}
            NAMES: {{names}}
            RELATION: {{relations}}
            {{relevantSkill}}
            CONTEXT: {{contextJson}}
            TARGET: {{targetJson}}
            Nếu CONTEXT/TARGET xác nhận tên riêng, tông môn/địa danh hoặc quan hệ-xưng hô mới: names ghi source Hán + text Hán-Việt; relations ghi key là người đang được gọi, address là đại từ tiếng Việt đã chắc (vd con/ngươi), note thật ngắn. Không chắc thì trả []. Chỉ trả JSON.
            """;
    }

    private static ValidatedTranslationBatch ValidateMemoryBatch(
        JsonElement root,
        IReadOnlyList<EditorSubtitleCue> expected,
        IReadOnlyList<EditorSubtitleCue> context,
        TranslationPromptMemory memory,
        TranslationCheckpoint checkpoint)
    {
        var translations = ValidateBatch(root, expected);
        var contextText = string.Join('\n', context.Select(x => x.SourceText));
        var targetText = string.Join('\n', expected.Select(x => x.SourceText));

        foreach (var cue in expected)
        {
            var translated = translations[cue.Id];
            foreach (var term in memory.RequiredTerms)
            {
                if (cue.SourceText.Contains(term.Key, StringComparison.Ordinal)
                    && !translated.Contains(term.Value, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Cue {cue.Number} làm sai thuật ngữ khóa {term.Key}={term.Value}.");
            }
            foreach (var name in checkpoint.Names)
            {
                if (cue.SourceText.Contains(name.Key, StringComparison.Ordinal)
                    && !translated.Contains(name.Value, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Cue {cue.Number} làm sai tên đã khóa {name.Key}={name.Value}.");
            }
        }

        var learnedNames = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("names", out var nameArray))
        {
            if (nameArray.ValueKind != JsonValueKind.Array || nameArray.GetArrayLength() > 8)
                throw new InvalidDataException("Model trả bộ nhớ names sai kiểu hoặc quá nhiều.");
            foreach (var item in nameArray.EnumerateArray())
            {
                var source = item.TryGetProperty("source", out var sourceValue) ? sourceValue.GetString()?.Trim() : null;
                var text = item.TryGetProperty("text", out var textValue) ? textValue.GetString()?.Trim() : null;
                if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(text)) continue;
                if (source.Length is < 2 or > 12 || !source.All(IsHan) || !contextText.Contains(source, StringComparison.Ordinal))
                    throw new InvalidDataException("Model trả tên nguồn không nằm trong CONTEXT/TARGET.");
                if (text.Length > 80 || text.Any(IsHan) || text.Any(char.IsControl))
                    throw new InvalidDataException("Model trả tên Hán-Việt không hợp lệ.");
                if (memory.Terms.Any(x => string.Equals(x.Key, source, StringComparison.Ordinal))) continue;
                if (checkpoint.Names.TryGetValue(source, out var locked)
                    && !string.Equals(locked, text, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Model đổi tên đã khóa {source}={locked} thành {text}.");
                if (!learnedNames.TryAdd(source, text)
                    && !string.Equals(learnedNames[source], text, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Model trả hai cách đọc khác nhau cho tên {source}.");
            }
        }

        foreach (var cue in expected)
        {
            var translated = translations[cue.Id];
            foreach (var name in learnedNames)
            {
                if (cue.SourceText.Contains(name.Key, StringComparison.Ordinal)
                    && !translated.Contains(name.Value, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Cue {cue.Number} không dùng tên Hán-Việt vừa xác nhận {name.Key}={name.Value}.");
            }
        }

        var learnedRelations = new Dictionary<string, TranslationRelationMemory>(StringComparer.Ordinal);
        if (root.TryGetProperty("relations", out var relationArray))
        {
            if (relationArray.ValueKind != JsonValueKind.Array || relationArray.GetArrayLength() > 8)
                throw new InvalidDataException("Model trả bộ nhớ relations sai kiểu hoặc quá nhiều.");
            foreach (var item in relationArray.EnumerateArray())
            {
                var key = item.TryGetProperty("key", out var keyValue) ? keyValue.GetString()?.Trim() : null;
                var address = item.TryGetProperty("address", out var addressValue) ? addressValue.GetString()?.Trim() ?? string.Empty : string.Empty;
                var note = item.TryGetProperty("note", out var noteValue) ? noteValue.GetString()?.Trim() ?? string.Empty : string.Empty;
                if (string.IsNullOrWhiteSpace(key) || (string.IsNullOrWhiteSpace(address) && string.IsNullOrWhiteSpace(note))) continue;
                if (key.Length is < 1 or > 12 || !key.All(IsHan) || !contextText.Contains(key, StringComparison.Ordinal))
                    throw new InvalidDataException("Model trả khóa quan hệ không nằm trong CONTEXT/TARGET.");
                if (address.Length > 24 || note.Length > 140 || address.Any(IsHan) || note.Any(IsHan)
                    || address.Any(char.IsControl) || note.Any(char.IsControl))
                    throw new InvalidDataException("Model trả quan hệ/xưng hô vượt giới hạn hoặc còn chữ Hán.");
                var candidate = new TranslationRelationMemory(address, note);
                if (checkpoint.Relations.TryGetValue(key, out var lockedRelation)
                    && !string.IsNullOrWhiteSpace(lockedRelation.Address)
                    && !string.IsNullOrWhiteSpace(address)
                    && !string.Equals(lockedRelation.Address, address, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Model đổi xưng hô đã khóa của {key} từ {lockedRelation.Address} thành {address}.");
                learnedRelations[key] = candidate;
            }
        }

        var addressLocks = memory.Relations.Values
            .Concat(learnedRelations.Values)
            .Select(x => x.Address.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (addressLocks.Length == 1 && (targetText.Contains('你') || targetText.Contains('您')))
        {
            var address = addressLocks[0];
            foreach (var cue in expected.Where(x => x.SourceText.Contains('你') || x.SourceText.Contains('您')))
            {
                if (!translations[cue.Id].Contains(address, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Cue {cue.Number} không giữ xưng hô đã xác nhận '{address}'.");
            }
        }

        return new ValidatedTranslationBatch(translations, learnedNames, learnedRelations);
    }

    private static void MergeTranslationMemory(TranslationCheckpoint checkpoint, ValidatedTranslationBatch validated)
    {
        foreach (var pair in validated.Names)
        {
            if (checkpoint.Names.Count >= 256 && !checkpoint.Names.ContainsKey(pair.Key)) break;
            if (!checkpoint.Names.ContainsKey(pair.Key)) checkpoint.Names[pair.Key] = pair.Value;
        }
        foreach (var pair in validated.Relations)
        {
            if (checkpoint.Relations.Count >= 256 && !checkpoint.Relations.ContainsKey(pair.Key)) break;
            if (!checkpoint.Relations.TryGetValue(pair.Key, out var current))
            {
                checkpoint.Relations[pair.Key] = pair.Value;
                continue;
            }
            var address = string.IsNullOrWhiteSpace(current.Address) ? pair.Value.Address : current.Address;
            var note = string.IsNullOrWhiteSpace(current.Note) ? pair.Value.Note : current.Note;
            checkpoint.Relations[pair.Key] = new TranslationRelationMemory(address, note);
        }
    }

    private static bool IsHan(char value) => value is >= '\u3400' and <= '\u9FFF';
}
