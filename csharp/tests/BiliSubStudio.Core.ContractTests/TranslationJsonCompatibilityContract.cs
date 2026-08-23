using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using BiliSubStudio.Core.Editor;

namespace BiliSubStudio.Core.ContractTests;

internal static class TranslationJsonCompatibilityContract
{
    [ModuleInitializer]
    internal static void Verify()
    {
        var service = typeof(LocalSubtitleTranslationService);
        static string Constant(Type type, string name) =>
            type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)?.GetRawConstantValue()?.ToString()
            ?? throw new InvalidOperationException($"missing translation constant {name}");

        if (!string.Equals(Constant(service, "ThinkingTemplateKwargs"), "{\"enable_thinking\":false}", StringComparison.Ordinal))
            throw new InvalidOperationException("Qwen3 thinking template kwargs are not pinned off");
        if (!string.Equals(Constant(service, "ReasoningMode"), "off", StringComparison.Ordinal))
            throw new InvalidOperationException("Qwen3 reasoning mode is not pinned off");

        var extract = service.GetMethod("ExtractJson", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing local translation JSON parser");
        var fenced = (JsonElement)extract.Invoke(null, ["<think>ignored</think>\n```json\n{\"bible\":\"Vân Tiêu Tông\"}\n```\n"])!;
        if (!string.Equals(fenced.GetProperty("bible").GetString(), "Vân Tiêu Tông", StringComparison.Ordinal))
            throw new InvalidOperationException("translation JSON parser no longer tolerates wrapped model output");

        var sourcePath = Path.Combine(Directory.GetCurrentDirectory(), "csharp", "src", "BiliSubStudio.Core", "Editor", "LocalSubtitleTranslationService.cs");
        if (!File.Exists(sourcePath)) return;
        var source = File.ReadAllText(sourcePath);
        foreach (var marker in new[]
        {
            "\"--chat-template-kwargs\", ThinkingTemplateKwargs",
            "\"--reasoning\", ReasoningMode",
            "\"--output\", responseFile",
            "enforceSchema: false",
        })
        {
            if (!source.Contains(marker, StringComparison.Ordinal))
                throw new InvalidOperationException("Qwen3 JSON compatibility path missing: " + marker);
        }
        if (source.Contains("/no_think", StringComparison.Ordinal))
            throw new InvalidOperationException("legacy /no_think token must not be combined with Qwen3 JSON generation");
    }
}
