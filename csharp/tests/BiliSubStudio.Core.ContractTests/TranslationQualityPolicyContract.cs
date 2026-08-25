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
        if (!string.Equals(policy, "source-first-v2", StringComparison.Ordinal))
            throw new InvalidOperationException("translation quality policy revision is not pinned");

        var sourcePath = Path.Combine(Directory.GetCurrentDirectory(), "csharp", "src", "BiliSubStudio.Core", "Editor", "LocalSubtitleTranslationService.cs");
        if (!File.Exists(sourcePath)) return;
        var source = File.ReadAllText(sourcePath);

        foreach (var marker in new[]
        {
            "[\"temperature\"] = 0.2",
            "[\"presence_penalty\"] = 0.0",
            "request.ModelMode == EditorTranslationModelMode.Quality",
            "translationBatchSize = Math.Min(translationBatchSize, TranslationBatchMedium)",
            "THỨ TỰ ƯU TIÊN KHI CÓ MÂU THUẪN:",
            "TARGET + NGỮ CẢNH LÂN CẬN là nguồn sự thật về nội dung câu.",
            "nếu chưa đủ bằng chứng thì giữ cách diễn đạt trung tính",
            "Nếu nguyên văn cố ý mơ hồ, giữ mơ hồ",
            "TỰ KIỂM TRA THẦM TRƯỚC KHI TRẢ JSON",
            "NGUYÊN TẮC NGUỒN SỰ THẬT:",
            "Không biến suy đoán thành dữ kiện đã khóa",
            "loaded.Schema != 2",
            "loaded.PolicyKey, TranslationPolicyKey",
            "new(2, sourceSha, skillSha, modelKey, TranslationPolicyKey",
        })
        {
            if (!source.Contains(marker, StringComparison.Ordinal))
                throw new InvalidOperationException("translation quality policy missing: " + marker);
        }

        if (source.Contains("[\"presence_penalty\"] = 1.0", StringComparison.Ordinal))
            throw new InvalidOperationException("translation must not penalize repeated names/terms with presence_penalty=1.0");
        if (source.Contains("loaded.Schema != 1", StringComparison.Ordinal))
            throw new InvalidOperationException("old translation checkpoints must be invalidated after prompt/sampler policy changes");
    }
}
