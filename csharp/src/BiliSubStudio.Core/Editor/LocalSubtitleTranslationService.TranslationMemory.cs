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
        var terms = Skill.MatchLockedTerms(context.Select(x => x.SourceText), int.MaxValue);
        var requiredTerms = Skill.MatchLockedTerms(target.Select(x => x.SourceText), int.MaxValue);
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

    private static (string Source, string Vietnamese, string Token)[] BuildGlossaryMask(TranslationPromptMemory memory) =>
        memory.RequiredTerms
            .OrderByDescending(x => x.Key.Length)
            .ThenBy(x => x.Key, StringComparer.Ordinal)
            .Select((x, index) => (Source: x.Key, Vietnamese: x.Value, Token: $"__TERM_{index}__"))
            .ToArray();

    private string BuildMemoryTranslationPrompt(
        IReadOnlyList<EditorSubtitleCue> target,
        IReadOnlyList<EditorSubtitleCue> context,
        TranslationPromptMemory memory)
    {
        var glossary = BuildGlossaryMask(memory);

        string MaskText(string value)
        {
            var masked = value;
            foreach (var entry in glossary)
                masked = masked.Replace(entry.Source, entry.Token, StringComparison.Ordinal);
            return masked;
        }

        var maskedContext = context.Select(x => new { id = x.Id, text = MaskText(x.SourceText) }).ToArray();
        var maskedTarget = target.Select(x => new { id = x.Id, text = MaskText(x.SourceText) }).ToArray();
        var names = memory.Names.Count == 0
            ? "-"
            : string.Join("; ", memory.Names.Select(x => $"{x.Key}={x.Value}"));
        var relations = memory.Relations.Count == 0
            ? "-"
            : string.Join("; ", memory.Relations.Select(x =>
                $"{x.Key}: gọi={x.Value.Address}; {x.Value.Note}".TrimEnd(' ', ';')));
        var relevantSkill = Skill.BuildReferenceInstructions(maskedContext.Select(x => x.text), 900).Trim();
        var contextJson = JsonSerializer.Serialize(maskedContext);
        var targetJson = JsonSerializer.Serialize(maskedTarget);
        return $$"""
            Bạn là dịch giả phụ đề phim tu tiên/tiên hiệp Trung Quốc. Dịch TARGET tự nhiên.
            Các token __TERM_X__ trong CONTEXT/TARGET là khóa glossary do chương trình chèn. BẮT BUỘC giữ nguyên từng token đúng ký tự và đúng số lần; không dịch, xóa hay đổi token. Chương trình sẽ thay token thành thuật ngữ Việt sau khi AI trả kết quả.
            Bắt buộc đúng NAMES, RELATION; tên người đọc Hán-Việt, không dịch nghĩa từng chữ (陈长安=Trần Trường An). Không hiện đại hóa xưng hô, không bịa/thêm bớt.
            NAMES: {{names}}
            RELATION: {{relations}}
            {{relevantSkill}}
            CONTEXT: {{contextJson}}
            TARGET: {{targetJson}}
            Nếu CONTEXT/TARGET xác nhận tên riêng, tông môn/địa danh hoặc quan hệ-xưng hô mới: names ghi source Hán + text Hán-Việt; relations ghi key là người đang được gọi, address là đại từ tiếng Việt đã chắc (vd con/ngươi), note thật ngắn. Không chắc thì trả []. Chỉ trả JSON.
            """;
    }

    private static int CountOccurrences(string text, string value, StringComparison comparison)
    {
        if (string.IsNullOrEmpty(value)) return 0;
        var count = 0;
        var offset = 0;
        while (offset <= text.Length - value.Length)
        {
            var index = text.IndexOf(value, offset, comparison);
            if (index < 0) break;
            count++;
            offset = index + value.Length;
        }
        return count;
    }

    private static string RepairGlossaryForCue(
        EditorSubtitleCue cue,
        string modelText,
        IReadOnlyList<(string Source, string Vietnamese, string Token)> glossary)
    {
        var translated = modelText;
        foreach (var entry in glossary)
        {
            var expectedCount = CountOccurrences(cue.SourceText, entry.Source, StringComparison.Ordinal);
            if (expectedCount == 0) continue;

            // Runtime glossary violations are repairable data, not a reason to retry Qwen.
            // Accept either the exact placeholder or leaked source Han text and restore both here.
            translated = translated.Replace(entry.Token, entry.Vietnamese, StringComparison.OrdinalIgnoreCase);
            translated = translated.Replace(entry.Source, entry.Vietnamese, StringComparison.Ordinal);

            var actualCount = CountOccurrences(translated, entry.Vietnamese, StringComparison.OrdinalIgnoreCase);
            if (actualCount < expectedCount)
            {
                var missing = expectedCount - actualCount;
                translated = (translated.TrimEnd() + " " + string.Join(" ", Enumerable.Repeat(entry.Vietnamese, missing))).Trim();
            }
        }

        // Exact and case-varied placeholders have already been restored above. Any remaining
        // TERM marker means the model structurally damaged a placeholder and C# cannot know
        // which locked term it represented safely.
        if (translated.Contains("__TERM", StringComparison.OrdinalIgnoreCase)
            || translated.Contains("TERM_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Cue {cue.Number} làm hỏng token glossary.");

        // Final idempotent sweep: never let a recognized locked Han term reach the generic
        // Han validator. This is the runtime guarantee that replaces the old hard glossary throw.
        foreach (var entry in glossary)
            translated = translated.Replace(entry.Source, entry.Vietnamese, StringComparison.Ordinal);

        return translated.Trim();
    }

    private static ValidatedTranslationBatch ValidateMemoryBatch(
        JsonElement root,
        IReadOnlyList<EditorSubtitleCue> expected,
        IReadOnlyList<EditorSubtitleCue> context,
        TranslationPromptMemory memory,
        TranslationCheckpoint checkpoint)
    {
        if (!root.TryGetProperty("translations", out var array) || array.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Model không trả mảng translations.");
        var expectedIds = expected.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var rawTranslations = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in array.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idValue) ? idValue.GetString()?.Trim() : null;
            var text = item.TryGetProperty("text", out var textValue) ? textValue.GetString()?.Trim() : null;
            if (id is null || !expectedIds.Contains(id) || !rawTranslations.TryAdd(id, text ?? string.Empty))
                throw new InvalidDataException("Model trả cue ID thừa, lặp hoặc sai.");
        }
        if (rawTranslations.Count != expected.Count) throw new InvalidDataException("Model bỏ sót cue trong batch.");

        var glossary = BuildGlossaryMask(memory);
        var translations = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var cue in expected)
        {
            var translated = RepairGlossaryForCue(cue, rawTranslations[cue.Id], glossary);
            ValidateTranslationText(cue, translated);
            translations[cue.Id] = translated;
        }

        var contextText = string.Join('\n', context.Select(x => x.SourceText));
        var targetText = string.Join('\n', expected.Select(x => x.SourceText));

        foreach (var cue in expected)
        {
            var translated = translations[cue.Id];
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
