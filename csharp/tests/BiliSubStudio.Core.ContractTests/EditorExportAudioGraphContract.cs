using System.Reflection;
using System.Runtime.CompilerServices;
using BiliSubStudio.Core.Editor;

namespace BiliSubStudio.Core.ContractTests;

// 08/10 vụ 123.mp4: file nguồn rip sẵn có 654 điểm timestamp audio suy biến,
// graph xuất cũ kế thừa nguyên si sang file xuất. Hợp đồng: nhánh nhạc nguồn
// trong graph ghép voice luôn có aresample=async (làm sạch timestamp), nhánh
// voice giữ nguyên từng mẫu (không resample, khớp giờ tuyệt đối).
// 09/10 vụ voice chỗ to chỗ nhỏ: nhánh voice thêm loudnorm (bám target EBU
// theo thời gian, không đụng timing) + alimiter chốt trần; gain user đứng
// sau norm nên núm Âm lượng voice vẫn có tác dụng mà không gây rè.
internal static class EditorExportAudioGraphContract
{
    [ModuleInitializer]
    internal static void Install()
    {
        RuntimeHelpers.RunClassConstructor(typeof(Program).TypeHandle);
        var field = typeof(Program).GetField("Tests", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing Core contract list");
        var tests = field.GetValue(null) as List<(string Name, Func<Task> Test)>
            ?? throw new InvalidOperationException("Core contract list type drifted");
        tests.Add(("Export voice mix sanitizes source timestamps but keeps voice samples exact", RunAsync));
    }

    private static Task RunAsync()
    {
        foreach (var mode in new[] { "keep", "duck" })
        {
            var graph = VideoEditorService.BuildVoiceAudioFilter(
                new EditorAudioSettings(mode, 0.19),
                new EditorVoiceTrack("voice.flac", 0, 100), 1, 0);
            var voiceChain = graph.Split("[voicea]")[0].Split(";")[^1];
            Check(voiceChain.StartsWith("[1:a]", StringComparison.Ordinal), $"không tìm thấy nhánh voice ({mode})");
            Check(!voiceChain.Contains("aresample", StringComparison.OrdinalIgnoreCase), $"nhánh voice bị resample, mất khớp giờ tuyệt đối ({mode})");
            Check(graph.Contains("aresample=async=1", StringComparison.Ordinal), $"nhánh nhạc nguồn thiếu làm sạch timestamp ({mode})");
            Check(voiceChain.Contains("loudnorm=I=-18:TP=-1.5:LRA=7", StringComparison.Ordinal), $"nhánh voice thiếu cân loudness ({mode})");
            Check(voiceChain.Contains("alimiter=limit=0.95:level=disabled", StringComparison.Ordinal), $"nhánh voice thiếu chốt trần ({mode})");
        }
        var muted = VideoEditorService.BuildVoiceAudioFilter(
            new EditorAudioSettings("mute", 1),
            new EditorVoiceTrack("voice.flac", 0, 100), 1, 0);
        Check(!muted.Contains("aresample", StringComparison.OrdinalIgnoreCase), "mute nhạc nguồn rồi vẫn resample thừa");
        return Task.CompletedTask;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("EditorExportAudioGraphContract: " + message);
    }
}
