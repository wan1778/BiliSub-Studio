using System.Reflection;
using System.Runtime.CompilerServices;
using BiliSubStudio.Core.Editor;

namespace BiliSubStudio.Core.ContractTests;

// Vá cửa sổ voice (23/09): mảnh timecode hẹp (<0.15s) không giọng nào đọc vừa
// nên worker giữ im lặng oan. Chỉ gộp mảnh trùng Y HỆT vào câu lành kề và nới
// mảnh hẹp vào khoảng im PHẢI (07/10: cấm lùi Start sớm hơn giờ sub); không
// đụng chữ, không lấn câu khác, không đổi thứ tự, không sửa danh sách gốc.
internal static class VoiceWindowRepairContract
{
    [ModuleInitializer]
    internal static void Install()
    {
        RuntimeHelpers.RunClassConstructor(typeof(Program).TypeHandle);
        var field = typeof(Program).GetField("Tests", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing Core contract list");
        var tests = field.GetValue(null) as List<(string Name, Func<Task> Test)>
            ?? throw new InvalidOperationException("Core contract list type drifted");
        tests.Add(("Voice window repair absorbs duplicate slivers and widens narrow windows without touching healthy cues", RunAsync));
    }

    private static Task RunAsync()
    {
        DuplicateSliverAbsorbedForward();
        DuplicateSliverAbsorbedBackward();
        NearDuplicateNotAbsorbedButExtended();
        TouchingFragmentsUntouched();
        HealthyRepeatUntouched();
        BoxedSliverUntouched();
        BackwardExtended();
        ExtensionCapped();
        InputNotMutated();
        return Task.CompletedTask;
    }

    private static IReadOnlyList<EditorSubtitleCue> Repair(IReadOnlyList<EditorSubtitleCue> cues)
    {
        var method = typeof(EditorTtsRequest).Assembly
            .GetType("BiliSubStudio.Core.Editor.EditorVoiceCuePlanner")!
            .GetMethod("RepairVoiceWindows", BindingFlags.Static | BindingFlags.Public)!;
        return (IReadOnlyList<EditorSubtitleCue>)method.Invoke(null, [cues])!;
    }

    private static EditorSubtitleCue Cue(string id, double start, double end, string vietnamese) =>
        new(id, "1", "", start, end, "nguồn", vietnamese);

    // Mảnh trùng y hệt sau câu lành (vụ #3372/#3373: 0.133s + hở 0.833s + câu lành cùng chữ).
    private static void DuplicateSliverAbsorbedForward()
    {
        var repaired = Repair([
            Cue("sliver", 6589.767, 6589.900, " Vừa khéo tiếp xúc được điều động phòng vệ vòng ngoài Đại Lang. "),
            Cue("healthy", 6590.733, 6591.867, " Vừa khéo tiếp xúc được điều động phòng vệ vòng ngoài Đại Lang. "),
        ]);
        Check(repaired.Count == 1, "mảnh trùng phải gộp, không đọc 2 lần");
        Check(repaired[0].Id == "healthy", "phải giữ câu lành");
        Check(repaired[0].Start == 6589.767 && repaired[0].End == 6591.867, "khung phải phủ cả mảnh");
    }

    private static void DuplicateSliverAbsorbedBackward()
    {
        var repaired = Repair([
            Cue("healthy", 10.0, 11.0, "Đi thôi."),
            Cue("sliver", 11.5, 11.6, "Đi thôi."),
        ]);
        Check(repaired.Count == 1 && repaired[0].Id == "healthy", "mảnh trùng sau phải gộp vào câu lành trước");
        Check(repaired[0].Start == 10.0 && repaired[0].End == 11.6, "khung câu lành phải phủ cả mảnh");
    }

    // Khác dấu câu là khác chữ: không gộp, chỉ nới mảnh vào khoảng im.
    private static void NearDuplicateNotAbsorbedButExtended()
    {
        var repaired = Repair([
            Cue("sliver", 20.0, 20.1, "Đi thôi"),
            Cue("next", 21.0, 22.0, "Đi thôi."),
        ]);
        Check(repaired.Count == 2, "khác dấu câu không được gộp");
        Check(repaired[0].End > 20.1 && repaired[0].End <= 20.95, "mảnh phải nới vào khoảng im");
        Check(repaired[1].Start == 21.0 && repaired[1].End == 22.0, "câu lành không được đụng");
    }

    // Mảnh chạm khít câu khác chữ thì không có chỗ nới -> giữ nguyên.
    private static void TouchingFragmentsUntouched()
    {
        var repaired = Repair([
            Cue("lead", 40.0, 40.134, "Ở Bắc Vực,"),
            Cue("tail", 40.134, 43.0, "là mấy đại tộc thế gia kia."),
        ]);
        Check(repaired.Count == 2, "mảnh chạm khít không được gộp/nới");
        Check(repaired[0].Start == 40.0 && repaired[0].End == 40.134, "mảnh chạm khít phải giữ nguyên");
    }

    // Thoại lặp lành mạnh chạm nhau không bao giờ bị gộp oan.
    private static void HealthyRepeatUntouched()
    {
        var repaired = Repair([
            Cue("repeat-1", 20.0, 21.0, "Không."),
            Cue("repeat-2", 21.0, 22.0, "Không."),
        ]);
        Check(repaired.Count == 2, "thoại lặp lành mạnh bị gộp oan");
        Check(repaired[0].End == 21.0 && repaired[1].Start == 21.0, "khung câu lành phải giữ nguyên");
    }

    // Mảnh kẹt giữa 2 câu chạm khít thì không có chỗ nới.
    private static void BoxedSliverUntouched()
    {
        var repaired = Repair([
            Cue("prev", 30.0, 31.0, "Câu trước."),
            Cue("sliver", 31.0, 31.1, "Ừ."),
            Cue("next", 31.1, 32.0, "Câu sau."),
        ]);
        Check(repaired.Count == 3, "mảnh kẹt cứng không được gộp/nới");
        Check(repaired[1].Start == 31.0 && repaired[1].End == 31.1, "mảnh kẹt phải giữ nguyên cho worker xử lý");
    }

    // Khoảng im dài thì nới tối đa 2s, không nuốt cả khoảng im.
    private static void ExtensionCapped()
    {
        var repaired = Repair([
            Cue("sliver", 100.0, 100.1, "Ừ."),
            Cue("next", 200.0, 201.0, "Câu sau."),
        ]);
        Check(repaired.Count == 2, "khoảng im dài không được gộp");
        Check(repaired[0].End == 102.1, "nới phải chừa khoảng im, tối đa +2s");
    }

    // Mảnh hở với câu trước thì GIỮ NGUYÊN Start (07/10: cấm nới lùi sớm
    // hơn giờ sub) — chỉ nới phải khi có khe sau, không thì worker xử lý.
    private static void BackwardExtended()
    {
        var repaired = Repair([
            Cue("prev", 50.0, 51.0, "Câu trước."),
            Cue("sliver", 51.5, 51.6, "Ừ."),
        ]);
        Check(repaired.Count == 2, "không nới lùi không được gộp/ném câu");
        Check(repaired[1].Start == 51.5 && repaired[1].End == 51.6, "mảnh phải giữ nguyên Start giờ sub, worker xử lý");
        Check(repaired[0].Start == 50.0 && repaired[0].End == 51.0, "câu trước không được đụng");
    }

    private static void InputNotMutated()
    {
        var source = new[]
        {
            Cue("healthy", 10.0, 11.0, "Đi thôi."),
            Cue("sliver", 11.5, 11.6, "Đi thôi."),
        };
        Repair(source);
        Check(source.Length == 2 && source[0].End == 11.0 && source[1].Start == 11.5,
            "vá voice làm đổi danh sách sub gốc");
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
