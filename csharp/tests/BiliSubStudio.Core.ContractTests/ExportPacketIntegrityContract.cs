using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using BiliSubStudio.Core.Editor;

namespace BiliSubStudio.Core.ContractTests;

// 08/10 vụ 123.mp4: file xuất rải 654 điểm packet audio suy biến
// (duration ~23 micro-giây, bình thường 23 mili-giây) + rớt voice cục bộ
// vài giây mà quét onset trung vị không thấy. Hợp đồng: bộ phân tích packet
// phải bắt được duration suy biến, DTS đi ngược, và bỏ qua priming đầu file.
internal static class ExportPacketIntegrityContract
{
    [ModuleInitializer]
    internal static void Install()
    {
        RuntimeHelpers.RunClassConstructor(typeof(Program).TypeHandle);
        var field = typeof(Program).GetField("Tests", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing Core contract list");
        var tests = field.GetValue(null) as List<(string Name, Func<Task> Test)>
            ?? throw new InvalidOperationException("Core contract list type drifted");
        tests.Add(("Export packet scan catches degenerate durations and backward DTS, ignores leading priming", RunAsync));
        tests.Add(("Export ffprobe arguments never carry ffmpeg-only flags (regression: -nostdin failed a 27-minute render)", FfprobeArgsAsync));
    }

    private static Task RunAsync()
    {
        CleanStreamPasses();
        DegeneratePairFlagged();
        BackwardDtsFlagged();
        LeadingPrimingIgnored();
        return Task.CompletedTask;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("ExportPacketIntegrityContract: " + message);
    }

    // ffprobe luôn in dấu chấm thập phân; Row phải format Invariant (máy
    // user culture vi-VN dấu phẩy sẽ làm parse sai).
    private static string Row(double pts, double duration) =>
        "packet," + pts.ToString("0.000000", CultureInfo.InvariantCulture)
        + "," + pts.ToString("0.000000", CultureInfo.InvariantCulture)
        + "," + duration.ToString("0.000000", CultureInfo.InvariantCulture) + ",512";

    private static void CleanStreamPasses()
    {
        var csv = string.Join('\n', Enumerable.Range(0, 100).Select(i => Row(i * 0.02322, 0.02322)));
        var (degen, backward) = VideoEditorService.AnalyzeAudioPacketDump(csv);
        Check(degen.Count == 0, "packet AAC bình thường bị báo suy biến");
        Check(backward.Count == 0, "timeline đơn điệu bị báo đi ngược");
    }

    private static void DegeneratePairFlagged()
    {
        var csv = Row(15681.369569, 0.02322) + "\n"
            + "packet,15681.392789,15681.392789,0.000023,812\n"
            + "packet,15681.392812,15681.392812,0.000023,21\n"
            + Row(15681.415, 0.02322);
        var (degen, _) = VideoEditorService.AnalyzeAudioPacketDump(csv);
        Check(degen.Contains(15681), "cặp packet suy biến thực tế (vụ 123.mp4) không bị bắt");
    }

    private static void BackwardDtsFlagged()
    {
        var csv = Row(100.0, 0.02322) + "\n" + Row(100.02322, 0.02322) + "\n" + Row(99.5, 0.02322);
        var (_, backward) = VideoEditorService.AnalyzeAudioPacketDump(csv);
        Check(backward.Count == 1, "DTS đi ngược không bị bắt");
    }

    private static void LeadingPrimingIgnored()
    {
        var csv = "packet,-0.021333,-0.021333,0.02322,23\n" + Row(0.0, 0.02322) + "\n" + Row(0.02322, 0.02322);
        var (degen, backward) = VideoEditorService.AnalyzeAudioPacketDump(csv);
        Check(degen.Count == 0 && backward.Count == 0, "priming đầu file bị báo hỏng oan");
    }

    private static Task FfprobeArgsAsync()
    {
        var args = VideoEditorService.BuildAudioPacketDumpArguments("output.mp4");
        foreach (var ffmpegOnly in new[] { "-nostdin", "-y", "-i", "-map", "-filter_complex", "-progress", "-c:a", "-c:v", "-b:a", "-t", "-ss", "-f", "-frames:v" })
            Check(!args.Contains(ffmpegOnly), $"lệnh ffprobe lọt cờ chỉ-ffmpeg {ffmpegOnly} (sẽ fail job oan sau render dài)");
        foreach (var required in new[] { "-select_streams", "a:0", "-show_entries", "-of", "csv" })
            Check(args.Contains(required), $"lệnh ffprobe thiếu cờ bắt buộc {required}");
        Check(string.Equals(args[^1], "output.mp4", StringComparison.Ordinal), "đường dẫn file phải đứng cuối lệnh ffprobe (preflight chèn read_intervals ngay trước nó)");
        return Task.CompletedTask;
    }
}
