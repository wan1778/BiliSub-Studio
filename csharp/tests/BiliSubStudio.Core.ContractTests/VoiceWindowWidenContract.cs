using System.Reflection;
using System.Runtime.CompilerServices;
using BiliSubStudio.Core.Editor;

namespace BiliSubStudio.Core.ContractTests;

// Widen tight cues using the trailing (right) silent gap first (01/10; on 07/10 the user
// demanded that subtitles appear exactly when the voice speaks, so the left gap is only
// ever borrowed when the right gap is exhausted AND the sentence would otherwise be cut
// short - capped at TtsTimingLimits.MaxLeadSeconds, F1 17/11). Borrow exactly the shortfall,
// leave 0.05s before the neighbouring cues, do not invade or exceed other cues, do not
// touch the text, do not reorder. VieNeu only.
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
        tests.Add(("Voice window widening borrows the trailing gap first, then a bounded lead when the sentence would be cut", RunAsync));
    }

    private static Task RunAsync()
    {
        TightBorrowsRightThenBoundedLead();
        LooseUntouched();
        NonVieneuUntouched();
        NoGapUntouched();
        ClampToDuration();
        NoOverlapNoReorder();
        InputNotMutated();
        ShortCueOverhead();
        LeadNeverExceedsBudget();
        LeftGapRespectsClearance();
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

    // Hai Dang at 4.70 words/s: 20 words need 4.255s; a 1s window is short 3.255s.
    // F1 (17/11): the right gap is used first (1.95s, leaving 0.05s before cue c), and the
    // remaining 1.305s shortfall borrows the LEFT gap up to the 1.0s lead budget, so Start
    // moves to 9.55 while the voice keeps every word.
    private static void TightBorrowsRightThenBoundedLead()
    {
        var widened = Widen([
            Cue("a", 9.0, 9.5, "Ừ."),
            Cue("b", 10.0, 11.0, Words(20)),
            Cue("c", 13.0, 14.0, "Vâng."),
        ]);
        Check(widened.Count == 3, "không được mất cue");
        var cue = widened[1];
        Check(Math.Abs(cue.End - 12.95) < 1e-9, $"phải mượn 1.95s bên phải, thực tế {cue.End}");
        // Khe trái chỉ có 0.45s (cue a kết thúc 9.5 + chừa 0.05s) nên mượn hết 0.45s,
        // không phải trọn ngân sách 1.0s.
        Check(Math.Abs(cue.Start - 9.55) < 1e-9, $"phải mượn 0.45s bên trái, thực tế {cue.Start}");
        Check(cue.Start > 9.5 && (10.0 - cue.Start) <= TtsTimingLimits.MaxLeadSeconds + 1e-9,
            "đọc sớm không quá ngân sách 1.0s");
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
        // Two cues touching exactly: the shared boundary at 11.0 is inviolable (the
        // earlier cue has no room on either side, so nothing is borrowed), and the
        // cues must not overlap.
        var widened = Widen([
            Cue("a", 10.0, 11.0, Words(20)),
            Cue("b", 11.0, 12.0, Words(20)),
        ]);
        Check(widened[0].End == 11.0 && widened[1].Start == 11.0, "biên chạm khít phải giữ nguyên");
        // Cue đầu không còn khe phải, nhưng phía trước nó trống nên mượn tối đa 1.0s
        // (F1 17/11) thay vì cắt đuôi câu.
        Check(widened[0].Start == 9.0 && widened[0].End == 11.0, "cue đầu mượn 1.0s bên trái");
        Check(widened[1].Start == 11.0 && widened[1].End > 12.0, "cue sau vẫn nới phải");
    }

    // F1 (17/11): the lead is a bounded exception, not a licence. A 9s left gap must
    // still never move Start more than MaxLeadSeconds earlier than the subtitle.
    private static void LeadNeverExceedsBudget()
    {
        var widened = Widen([
            Cue("prev", 0.0, 0.1, Words(1)),
            Cue("cue", 9.0, 9.1, Words(20)),
            Cue("next", 9.2, 30.0, Words(1)),
        ]);
        var cue = widened[1];
        Check(cue.End <= 9.15 + 1e-9, "phải chừa 0.05s với cue sau");
        Check((9.0 - cue.Start) <= TtsTimingLimits.MaxLeadSeconds + 1e-9,
            $"đọc sớm không quá 1.0s, thực tế {9.0 - cue.Start}s");
        Check(cue.Start >= 8.0, $"phải mượn trọn ngân sách 1.0s, thực tế {cue.Start}");
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

    // F1 (17/11): a left borrow must never eat into the previous cue's window.
    private static void LeftGapRespectsClearance()
    {
        // Chạm khít bên trái: không có gì để mượn, giữ nguyên giờ bắt đầu.
        var touching = Widen([
            Cue("prev", 10.0, 11.0, Words(4)),
            Cue("cue", 11.0, 11.2, Words(20)),
            Cue("next", 11.25, 14.0, Words(4)),
        ]);
        Check(touching[1].Start == 11.0, "chạm khít thì không mượn được gì, giữ nguyên Start");
        Check(touching[1].Start >= touching[0].End - 1e-9, "không chồng cue trước");
        Check(touching[1].End <= touching[2].Start - 0.05 + 1e-9, "vẫn chừa 0.05s với cue sau");

        // Có khe trái thật (câu trước đủ rộng nên không bị nới): mượn nhưng chừa
        // đúng 0.05s cho câu trước. Khe trái = 11.60 - 11.40 - 0.05 = 0.15s.
        var gapped = Widen([
            Cue("prev", 10.0, 11.4, Words(4)),
            Cue("cue", 11.6, 11.8, Words(20)),
            Cue("next", 11.9, 14.0, Words(4)),
        ]);
        Check(gapped[0].Start == 10.0 && gapped[0].End == 11.4, "câu trước đủ rộng, không nới");
        Check(gapped[1].Start >= gapped[0].End + 0.05 - 1e-9,
            $"phải chừa 0.05s với cue trước, thực tế {gapped[1].Start}");
        Check(Math.Abs(gapped[1].Start - 11.45) < 1e-9, $"phải mượn 0.15s bên trái, thực tế {gapped[1].Start}");
        Check(gapped[1].End <= gapped[2].Start - 0.05 + 1e-9, "vẫn chừa 0.05s với cue sau");
    }

    // Regression for cue 169 (02/10) + #2837/#6470 (03/10): "Đừng hoảng." is 2
    // words, Ngoc Huyen reads at 4.36 words/s - overhead for 2 words is +0.70s
    // (measured +0.52-0.9s). The shortfall (0.4587+0.70-0.54=0.6187s) exceeds the
    // 0.23s right gap, so borrow the whole gap, keep the exactly touching left edge
    // unchanged, and leave exactly 0.05s.
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

    // Regression #6470 (03/10): "Vâng" is 1 word in 0.48s; the old estimator needed
    // 0.999 (unluckily just barely) while real synthesis takes ~1.0s. The overhead of
    // 1 word (+0.27s) is now caught (need 1.04) while still leaving the 0.5s "Ừ."
    // case of the TightBorrows test alone (need 0.97). The 6.5s right gap is enough
    // to widen by the full shortfall.
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
