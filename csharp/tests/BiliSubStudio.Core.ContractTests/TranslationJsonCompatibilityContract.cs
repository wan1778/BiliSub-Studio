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

        var match = service.GetMethod("MatchTranslationItems", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException("missing model translation item matcher");
        var cue = new EditorSubtitleCue("technical-cue-id", "4", "00:00:00,000 --> 00:00:01,000", 0, 1, "你走吧");
        using var directJson = JsonDocument.Parse("{\"translations\":[{\"id\":\"4\",\"text\":\"Ngươi đi đi.\"}]}");
        var direct = (IReadOnlyDictionary<string, string>)match.Invoke(null, [directJson.RootElement.Clone(), new[] { cue }])!;
        if (direct.Count != 1 || !string.Equals(direct[cue.Id], "Ngươi đi đi.", StringComparison.Ordinal))
            throw new InvalidOperationException("single-cue translation must recover a model-visible ID to its only technical target ID");

        using var contextLeakJson = JsonDocument.Parse("{\"translations\":[{\"id\":\"3\",\"text\":\"Sư phụ.\"},{\"id\":\"4\",\"text\":\"Ngươi đi đi.\"},{\"id\":\"5\",\"text\":\"Đệ tử vẫn muốn ở bên ngài.\"}]}");
        var contextLeak = (IReadOnlyDictionary<string, string>)match.Invoke(null, [contextLeakJson.RootElement.Clone(), new[] { cue }])!;
        if (contextLeak.Count != 1 || !string.Equals(contextLeak[cue.Id], "Ngươi đi đi.", StringComparison.Ordinal))
            throw new InvalidOperationException("single-cue translation must recover the uniquely identified TARGET when Qwen echoes CONTEXT");

        using var ambiguousContextJson = JsonDocument.Parse("{\"translations\":[{\"id\":\"3\",\"text\":\"Sư phụ.\"},{\"id\":\"5\",\"text\":\"Đệ tử vẫn muốn ở bên ngài.\"}]}");
        try
        {
            _ = match.Invoke(null, [ambiguousContextJson.RootElement.Clone(), new[] { cue }]);
            throw new InvalidOperationException("ambiguous context output must not be guessed as the TARGET");
        }
        catch (TargetInvocationException error) when (error.InnerException is InvalidDataException)
        {
        }

        var sourcePath = Path.Combine(Directory.GetCurrentDirectory(), "csharp", "src", "BiliSubStudio.Core", "Editor", "LocalSubtitleTranslationService.cs");
        if (!File.Exists(sourcePath)) return;
        var source = File.ReadAllText(sourcePath);
        var memoryPath = Path.Combine(Directory.GetCurrentDirectory(), "csharp", "src", "BiliSubStudio.Core", "Editor", "LocalSubtitleTranslationService.TranslationMemory.cs");
        var memory = File.Exists(memoryPath) ? File.ReadAllText(memoryPath) : string.Empty;
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
            "internal static IReadOnlyDictionary<string, string> MatchTranslationItems",
            "if (expected.Count == 1)",
        })
        {
            if (!source.Contains(marker, StringComparison.Ordinal))
                throw new InvalidOperationException("Qwen3 JSON compatibility path missing: " + marker);
        }
        if (!memory.Contains("var rawTranslations = MatchTranslationItems(root, expected);", StringComparison.Ordinal))
            throw new InvalidOperationException("memory translation validator does not use the shared single-cue ID matcher");
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

        // Translation Core compatibility remains covered above. The Editor no longer
        // exposes translation or live translation checkpoints; it consumes external SRT.
        var editorPath = Path.Combine(Directory.GetCurrentDirectory(), "csharp", "src", "BiliSubStudio.App", "Pages", "EditorPage.SubtitleCueEditing.cs");
        if (!File.Exists(editorPath)) return;
        var editor = File.ReadAllText(editorPath);
        if (editor.Contains("StartEditorTranslation(", StringComparison.Ordinal)
            || editor.Contains("TryApplyLiveTranslationCheckpointAsync", StringComparison.Ordinal))
            throw new InvalidOperationException("Vietnamese-only editor must not run AI translation");
        foreach (var marker in new[] { "EditorVietnameseSubtitleWorkflow.HasDraftChange",
                     "SubtitleManualStore.SaveAsync", "RenderSubtitleCueList();", "LoadSelectedSubtitleCue();",
                     "UpdateSubtitleSummary();", "QueuePreviewRefresh();" })
            if (!editor.Contains(marker, StringComparison.Ordinal))
                throw new InvalidOperationException("Vietnamese cue import/edit UI path missing: " + marker);
    }
}
