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
        if (!string.Equals(policy, "direct-cue-v1", StringComparison.Ordinal))
            throw new InvalidOperationException("direct-cue translation policy revision is not pinned");

        var sourcePath = Path.Combine(Directory.GetCurrentDirectory(), "csharp", "src", "BiliSubStudio.Core", "Editor", "LocalSubtitleTranslationService.cs");
        if (!File.Exists(sourcePath)) return;
        var source = File.ReadAllText(sourcePath);

        foreach (var marker in new[]
        {
            "[\"temperature\"] = 0.2",
            "[\"presence_penalty\"] = 0.0",
            "private const int DirectTranslationBatchSize = 1;",
            "const int analysisPages = 0;",
            "var translationBatchSize = DirectTranslationBatchSize;",
            "source.Skip(Math.Max(0, firstIndex - 2)).Take(batch.Length + 4).ToArray()",
            "Skill.BuildReferenceInstructions(context.Select(x => x.SourceText), 2_000)",
            "if (compactBible.Length > 1_600) compactBible = compactBible[..1_600];",
            "Dịch phụ đề Trung → Việt. Chỉ dịch TARGET và chỉ trả JSON.",
            "đúng nghĩa; giữ phủ định/câu hỏi/chủ thể; tên Hán-Việt và xưng hô theo ngữ cảnh; không bịa",
            "TranslationSchema, 1024, job.CancellationToken, job",
            "await SaveCheckpointAsync(checkpointPath, checkpoint, job.CancellationToken);",
            "loaded.Schema != 2",
            "loaded.PolicyKey, TranslationPolicyKey",
            "new(2, sourceSha, skillSha, modelKey, TranslationPolicyKey",
        })
        {
            if (!source.Contains(marker, StringComparison.Ordinal))
                throw new InvalidOperationException("direct-cue translation quality policy missing: " + marker);
        }

        if (source.Contains("[\"presence_penalty\"] = 1.0", StringComparison.Ordinal))
            throw new InvalidOperationException("translation must not penalize repeated names/terms with presence_penalty=1.0");
        if (source.Contains("loaded.Schema != 1", StringComparison.Ordinal))
            throw new InvalidOperationException("old translation checkpoints must stay invalidated after prompt/sampler policy changes");
    }
}
