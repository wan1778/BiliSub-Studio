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
            "private string LlamaServer => Path.Combine(RuntimeDirectory, \"llama-server.exe\")",
            "PostTranslationJsonAsync(session, \"apply-template\"",
            "PostTranslationJsonAsync(session, \"completion\"",
            "payload[\"json_schema\"] = schemaDocument.RootElement.Clone()",
            "[\"cache_prompt\"] = true",
            "TryGetProperty(\"content\", out var contentValue)",
            "enforceSchema: false",
        })
        {
            if (!source.Contains(marker, StringComparison.Ordinal))
                throw new InvalidOperationException("Qwen3 JSON compatibility path missing: " + marker);
        }
        const string bibleFallbackMarker = "var warning = $\"GPU/runtime lỗi → chuyển CPU:";
        const string bibleRecoveryCatch = "catch (Exception retryRuntimeError) when (retryRuntimeError is not OperationCanceledException)";
        const string bibleCheckpointAdvance = "checkpoint = checkpoint with { Bible = bible, AnalysisPagesCompleted = page + 1 };";
        var bibleFallbackStart = source.IndexOf(bibleFallbackMarker, StringComparison.Ordinal);
        var bibleRecoveryStart = source.IndexOf(bibleRecoveryCatch, StringComparison.Ordinal);
        var bibleCheckpointAdvanceIndex = bibleRecoveryStart < 0
            ? -1
            : source.IndexOf(bibleCheckpointAdvance, bibleRecoveryStart, StringComparison.Ordinal);
        if (bibleFallbackStart < 0 || bibleRecoveryStart <= bibleFallbackStart || bibleCheckpointAdvanceIndex <= bibleRecoveryStart)
            throw new InvalidOperationException("Bible runtime fallback/recovery ordering is not locked");

        var cpuFallbackBlock = source[bibleFallbackStart..bibleRecoveryStart];
        if (!cpuFallbackBlock.Contains("await RestartTranslationServerAsync(runtime!, model, 0, job.CancellationToken);", StringComparison.Ordinal))
            throw new InvalidOperationException("Bible runtime fallback must explicitly restart on CPU");
        if (!cpuFallbackBlock.Contains("catch (OperationCanceledException)", StringComparison.Ordinal))
            throw new InvalidOperationException("Bible CPU fallback must preserve user cancellation");

        var bibleRecoveryBlock = source[bibleRecoveryStart..bibleCheckpointAdvanceIndex];
        foreach (var marker in new[]
        {
            "CPU fallback vẫn lỗi runtime → khởi động lại sạch",
            "await RestartTranslationServerAsync(runtime!, model, 0, job.CancellationToken);",
            "catch (OperationCanceledException)",
            "AI local không thể khôi phục sau lỗi runtime khi tạo bible",
            "Runtime CPU đã khôi phục → giữ bible cũ",
            "nextBible = bible;",
        })
        {
            if (!bibleRecoveryBlock.Contains(marker, StringComparison.Ordinal))
                throw new InvalidOperationException("Bible runtime recovery path missing: " + marker);
        }
        if (bibleRecoveryBlock.Contains("RunJsonAsync(", StringComparison.Ordinal))
            throw new InvalidOperationException("Bible runtime recovery must health-restart only; do not add a third model inference");

        if (source.Contains("/no_think", StringComparison.Ordinal))
            throw new InvalidOperationException("legacy /no_think token must not be combined with Qwen3 JSON generation");
        if (source.Contains("\"--output\", responseFile", StringComparison.Ordinal) || source.Contains("llama-cli.exe", StringComparison.Ordinal))
            throw new InvalidOperationException("SPEED-02 must not regress to per-request llama-cli JSON output files");
    }
}
