using System.Reflection;
using System.Runtime.CompilerServices;
using BiliSubStudio.Core.Editor;

namespace BiliSubStudio.Core.ContractTests;

// Nới cue chật bằng khe im PHẢI (01/10; 07/10 user bắt sub hiện là voice
// đọc luôn nên CẤM mượn khe trái: Start không bao giờ lùi sớm hơn giờ sub,
// chỉ End nới phải lấy chỗ đọc). Mượn đúng số thiếu, chừa 0.05s với cue sau,
// không lấn/vượt cue khác, không đụng chữ, không đổi thứ tự. Chỉ VieNeu.
internal static class VoiceWindowWidenContract
{
    [ModuleInitializer]
    internal static void Install()
    {
        RuntimeHelpers.RunClassConstructor(typeof(Program).TypeHandle);
        var field = typeof(Program).GetField("Tests", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing Core contract list");
        var tests = field.GetValue(null) as List<(string Name, Func<Task> Test)>
            ?? throw new InvalidOperationException("Core contract list type drifted");
        tests.Add(("Voice window widening borrows exact shortfall from trailing gap, never moves Start early", RunAsync));
    }

    private static Task RunAsync()
    {
        TightBorrowsRightOnly();
        LooseUntouched();
        NonVieneuUntouched();
        NoGapUntouched();
        ClampToDuration();
        NoOverlapNoReorder();
        InputNotMutated();
        ShortCueOverhead();
        return Task.CompletedTask;
    }

    private static IReadOnlyList<EditorSubtitleCue> Widen(
        IReadOnlyList<EditorSubtitleCue> cues, string voice = "vieneu_hai_dang", double duration = 100) =>
        EditorVoiceCuePlanner.WidenTightWindows(cues, voice, duration);

    private static EditorSubtitleCue Cue(string id, double start, double end, string vietnamese) =>
        new(id, "1", "", start, end, "nguồn", vietnamese);

    private static string Words(int count) =>
        string.Join(" ", System.Linq.Enumerable.Repeat("chữ", count));

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("VoiceWindowWidenContract: " + message);
    }

    // Hải Đăng 4.70 chữ/s: 20 chữ cần 4.255s; khung 1s thiếu 3.255s.
    // 07/10: chỉ mượn khe phải (Start giữ nguyên giờ sub). Khe phải 1.95s
    // (câu c tới 13.0 + chừa 0.05) -> nới hết 1.95s, còn thiếu worker gánh.
    private static void TightBorrowsRightOnly()
    {
        var widened = Widen([
            Cue("a", 9.0, 9.5, "Ừ."),
            Cue("b", 10.0, 11.0, Words(20)),
            Cue("c", 13.0, 14.0, "Vâng."),
        ]);
        Check(widened.Count == 3, "không được mất cue");
        var cue = widened[1];
        Check(cue.Start == 10.0, $"Start phải giữ nguyên giờ sub, thực tế {cue.Start}");
        Check(Math.Abs(cue.End - 12.95) < 1e-9, $"phải phải mượn 1.95s, thực tế {cue.End}");
        Check(widened[0].Start == 9.0 && widened[0].End == 9.5, "cue lành không đụng");
        Check(widened[2].Start == 13.0 && widened[2].End == 14.0, "cue lành không đụng");
    }

    private static void LooseUntouched()
    {
        var widened = Widen([Cue("a", 10.0, 12.0, Words(4))]);
        Check(widened[0].Start == 10.0 && widened[0].End == 12.0, "cue vừa phải giữ nguyên");
    }

    private static void NonVieneuUntouched()
    {
        var widened = WidenTight(
            [Cue("a", 9.0, 9.5, "Ừ."), Cue("b", 10.0, 11.0, Words(20)), Cue("c", 13.0, 14.0, "Vâng.")],
            "ngoc_huyen");
        Check(widened[1].Start == 10.0 && widened[1].End == 11.0, "Piper không nới");
    }

    private static IReadOnlyList<EditorSubtitleCue> WidenTight(
        IReadOnlyList<EditorSubtitleCue> cues, string voice) =>
        EditorVoiceCuePlanner.WidenTightWindows(cues, voice, 100);

    private static void NoGapUntouched()
    {
        // Hai cue chạm khít: biên chung 11.0 bất khả xâm phạm (cue trước hết
        // chỗ nới phải, cue sau giữ nguyên Start), không chồng nhau.
        var widened = Widen([
            Cue("a", 10.0, 11.0, Words(20)),
            Cue("b", 11.0, 12.0, Words(20)),
        ]);
        Check(widened[0].End == 11.0 && widened[1].Start == 11.0, "biên chạm khít phải giữ nguyên");
        Check(widened[0].Start == 10.0 && widened[1].End > 12.0, "chỉ cue sau được nới phải, Start không lùi");
    }

    private static void ClampToDuration()
    {
        var widened = Widen([Cue("a", 98.0, 99.0, Words(20))], duration: 100);
        Check(widened[0].End <= 100, "cue cuối không vượt thời lượng video");
        Check(widened[0].Start >= 0, "không lùi quá 0");
    }

    private static void NoOverlapNoReorder()
    {
        var widened = Widen([
            Cue("a", 10.0, 10.5, Words(20)),
            Cue("b", 10.6, 11.1, Words(20)),
            Cue("c", 11.2, 11.7, Words(20)),
        ]);
        for (var i = 0; i < widened.Count; i++)
        {
            Check(widened[i].Start < widened[i].End, "khung phải dương");
            if (i > 0) Check(widened[i].Start >= widened[i - 1].End + 0.05 - 1e-9, "không chồng cue trước");
        }
    }

    private static void InputNotMutated()
    {
        var source = new[] { Cue("a", 10.0, 11.0, Words(20)), Cue("b", 13.0, 14.0, "Vâng.") };
        Widen(source);
        Check(source[0].Start == 10.0 && source[0].End == 11.0, "không sửa danh sách gốc");
    }

    // Hồi quy cue 169 (02/10) + #2837/#6470 (03/10): "Đừng hoảng." 2 chữ,
    // Ngọc Huyền 4.36 chữ/s — overhead 2 chữ +0.70s (đo thật +0.52-0.9s).
    // Thiếu (0.4587+0.70-0.54=0.6187s) vượt khe phải 0.23s -> mượn hết khe,
    // trái chạm khít giữ nguyên, chừa đúng 0.05s.
    private static void ShortCueOverhead()
    {
        var widened = Widen([
            Cue("prev", 270.0, 280.01, Words(4)),
            Cue("cue169", 280.01, 280.55, "Đừng hoảng."),
            Cue("next", 280.83, 282.43, Words(40)),
        ], voice: "vieneu_ngoc_huyen", duration: 300);
        var cue = widened[1];
        Check(cue.Start == 280.01, "trái chạm khít thì chỉ mượn khe phải");
        Check(Math.Abs(cue.End - 280.78) < 1e-9, $"mượn hết khe phải, thực tế {cue.End}");
        Check(widened[2].Start - cue.End >= 0.05 - 1e-9, "chừa 0.05s với cue sau");
        Check(widened[0].End == 280.01, "cue trái lành không đụng");
    }

    // Hồi quy #6470 (03/10): "Vâng" 1 chữ trong 0.48s, estimator cũ need
    // 0.999 (vừa khít oan) trong khi synth thật ~1.0s. Overhead 1 chữ +0.27s
    // bắt được (need 1.04) mà vẫn chừa "Ừ." 0.5s của test TightBorrows
    // (need 0.97). Khe phải 6.5s đủ nới hết số thiếu.
    private static void SingleWordOverhead()
    {
        var widened = Widen([
            Cue("prev", 12211.900, 12212.667, Words(4)),
            Cue("vâng", 12212.865, 12213.345, "Vâng"),
            Cue("next", 12219.908, 12221.0, Words(4)),
        ], voice: "vieneu_ngoc_huyen", duration: 12500);
        var cue = widened[1];
        var expectedWindow = 1 / 4.36 + .27;
        Check(Math.Abs(cue.End - cue.Start - expectedWindow) < 1e-6,
            $"nới đúng số thiếu, thực tế {cue.End - cue.Start}");
        Check(widened[2].Start - cue.End >= 0.05 - 1e-9, "chừa 0.05s với cue sau");
        Check(cue.Start >= widened[0].End + 0.05 - 1e-9, "chừa 0.05s với cue trước");
    }
}
