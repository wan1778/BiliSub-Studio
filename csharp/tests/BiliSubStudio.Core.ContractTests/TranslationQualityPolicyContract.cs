using System.Reflection;
using System.Runtime.CompilerServices;
using BiliSubStudio.Core.Editor;

namespace BiliSubStudio.Core.ContractTests;

internal static class TranslationQualityPolicyContract
{
    [ModuleInitializer]
    internal static void Verify()
    {
        var service = typeof(LocalSubtitleTranslationService);
        var policy = service.GetField("TranslationPolicyKey", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            ?.GetRawConstantValue()?.ToString();
        if (!string.Equals(policy, "locked-memory-v1", StringComparison.Ordinal))
            throw new InvalidOperationException("locked-memory translation policy revision is not pinned");

        var sourcePath = Path.Combine(Directory.GetCurrentDirectory(), "csharp", "src", "BiliSubStudio.Core", "Editor", "LocalSubtitleTranslationService.cs");
        var memoryPath = Path.Combine(Directory.GetCurrentDirectory(), "csharp", "src", "BiliSubStudio.Core", "Editor", "LocalSubtitleTranslationService.TranslationMemory.cs");
        var skillPath = Path.Combine(Directory.GetCurrentDirectory(), "csharp", "src", "BiliSubStudio.Core", "Editor", "TranslationSkillBundle.cs");
        if (!File.Exists(sourcePath) || !File.Exists(memoryPath) || !File.Exists(skillPath)) return;
        var source = File.ReadAllText(sourcePath);
        var memory = File.ReadAllText(memoryPath);
        var skill = File.ReadAllText(skillPath);

        foreach (var marker in new[]
        {
            "[\"temperature\"] = 0.2",
            "[\"presence_penalty\"] = 0.0",
            "private const int DirectTranslationBatchSize = 1;",
            "const int analysisPages = 0;",
            "var translationBatchSize = DirectTranslationBatchSize;",
            "source.Skip(Math.Max(0, firstIndex - 2)).Take(batch.Length + 4).ToArray()",
            "BuildTranslationPromptMemory(batch, context, checkpoint)",
            "BuildMemoryTranslationPrompt(batch, context, promptMemory)",
            "MemoryTranslationSchema, 1024, job.CancellationToken, job",
            "ValidateMemoryBatch(root, batch, context, promptMemory, checkpoint)",
            "MergeTranslationMemory(checkpoint, validated)",
            "await SaveCheckpointAsync(checkpointPath, checkpoint, job.CancellationToken);",
            "loaded.Schema != 3",
            "loaded.PolicyKey, TranslationPolicyKey",
            "new(3, sourceSha, skillSha, modelKey, TranslationPolicyKey",
        })
        {
            if (!source.Contains(marker, StringComparison.Ordinal))
                throw new InvalidOperationException("locked-memory translation quality policy missing: " + marker);
        }

        foreach (var marker in new[]
        {
            "private const string MemoryTranslationSchema",
            "MatchLockedTerms(context.Select(x => x.SourceText), int.MaxValue)",
            "MatchLockedTerms(target.Select(x => x.SourceText), int.MaxValue)",
            "$\"__TERM_{index}__\"",
            "MaskText(x.SourceText)",
            "Các token __TERM_X__",
            "không dịch, xóa hay đổi token",
            "陈长安=Trần Trường An",
            "names ghi source Hán + text Hán-Việt",
            "relations ghi key là người đang được gọi",
            "translated.Replace(entry.Token, entry.Vietnamese",
            "translated.Replace(entry.Source, entry.Vietnamese",
            "CountOccurrences(cue.SourceText, entry.Source",
            "actualCount < expectedCount",
            "Cue {cue.Number} làm sai tên đã khóa",
            "Cue {cue.Number} không giữ xưng hô đã xác nhận",
            "checkpoint.Names.Count >= 256",
            "checkpoint.Relations.Count >= 256",
        })
        {
            if (!memory.Contains(marker, StringComparison.Ordinal))
                throw new InvalidOperationException("glossary-mask validator/prompt missing: " + marker);
        }

        foreach (var marker in new[]
        {
            "LockedCultivationTerms",
            "MatchLockedTerms(IEnumerable<string> sourceTexts, int maxItems = 24)",
            "(\"师尊\", \"sư tôn\")",
            "(\"筑基\", \"trúc cơ\")",
            "(\"金丹\", \"kim đan\")",
            "(\"元婴\", \"nguyên anh\")",
            "OrderByDescending(x => x.Source.Length)",
        })
        {
            if (!skill.Contains(marker, StringComparison.Ordinal))
                throw new InvalidOperationException("locked cultivation glossary missing: " + marker);
        }

        if (memory.Contains("TERMS: {{terms}}", StringComparison.Ordinal))
            throw new InvalidOperationException("legacy advisory TERMS list must not return after glossary masking");
        if (memory.Contains("Cue {cue.Number} làm sai thuật ngữ khóa", StringComparison.Ordinal))
            throw new InvalidOperationException("glossary mismatch must be repaired in C# instead of throwing");
        if (source.Contains("[\"presence_penalty\"] = 1.0", StringComparison.Ordinal))
            throw new InvalidOperationException("translation must not penalize repeated names/terms with presence_penalty=1.0");
        if (source.Contains("loaded.Schema != 2", StringComparison.Ordinal))
            throw new InvalidOperationException("old translation checkpoints must be invalidated after locked-memory policy change");
        if (source.Contains("internal const string TranslationPolicyKey = \"direct-cue-v1\"", StringComparison.Ordinal))
            throw new InvalidOperationException("old direct-cue checkpoint policy must not survive locked-memory migration");
    }
}
