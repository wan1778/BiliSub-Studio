using System.Reflection;
using System.Runtime.CompilerServices;

namespace BiliSubStudio.Core.ContractTests;

// Cắt câu theo khe im (23/09): khe im >= 0.6s cắt thành 2 câu, chữ chia theo
// giờ chữ; khe ngắn không cắt; mảnh <0.3s dính vào câu bên; câu >10s không có
// khe đủ rộng cắt ở khe rộng nhất; không đảo thứ tự, không sửa danh sách gốc.
internal static class AsrPauseSplitContract
{
    [ModuleInitializer]
    internal static void Install()
    {
        RuntimeHelpers.RunClassConstructor(typeof(Program).TypeHandle);
        var field = typeof(Program).GetField("Tests", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing Core contract list");
        var tests = field.GetValue(null) as List<(string Name, Func<Task> Test)>
            ?? throw new InvalidOperationException("Core contract list type drifted");
        tests.Add(("ASR pause split cuts cues at word-timing silences without erasing short pieces", RunAsync));
    }

    private static readonly Type CueType = typeof(object).Assembly == null
        ? null!
        : FindCoreType("BiliSubStudio.Core.Editor.LocalAsrService")!
            .GetNestedType("AsrCue", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing AsrCue");
    private static readonly Type WordType = FindCoreType("BiliSubStudio.Core.Editor.LocalAsrService")!
            .GetNestedType("AsrWord", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing AsrWord");
    private static readonly MethodInfo SplitMethod = FindCoreType("BiliSubStudio.Core.Editor.LocalAsrService")!
            .GetMethod("SplitOnPauses", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing SplitOnPauses");

    private static Type? FindCoreType(string name) =>
        typeof(BiliSubStudio.Core.Ocr.OcrCue).Assembly.GetType(name);

    private static object Word(double start, double end, string text) =>
        WordType.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Single(c => c.GetParameters().Length == 4)
            .Invoke([start, end, text, 0.9]);

    private static object Cue(double start, double end, string text, object[] words)
    {
        var listType = typeof(List<>).MakeGenericType(WordType);
        var list = Activator.CreateInstance(listType)
            ?? throw new InvalidOperationException("cannot create word list");
        var add = listType.GetMethod("Add") ?? throw new InvalidOperationException("missing List.Add");
        foreach (var word in words) add.Invoke(list, [word]);
        return CueType.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Single(c => c.GetParameters().Length == 9)
            .Invoke([start, end, text, -0.1, 0.1, list, "uncertain", 0.5, 0.0]);
    }

    private static IReadOnlyList<object> Split(object cue) =>
        (IReadOnlyList<object>)SplitMethod.Invoke(null, [cue])!;

    private static double StartOf(object cue) => (double)cue.GetType().GetProperty("Start")!.GetValue(cue)!;
    private static double EndOf(object cue) => (double)cue.GetType().GetProperty("End")!.GetValue(cue)!;
    private static string TextOf(object cue) => (string)cue.GetType().GetProperty("Text")!.GetValue(cue)!;
    private static System.Collections.IList WordsOf(object cue) =>
        (System.Collections.IList)cue.GetType().GetProperty("Words")!.GetValue(cue)!;

    private static Task RunAsync()
    {
        GapSplitsIntoTwo();
        ShortGapKeepsWhole();
        LongUnsplitFallsBackToWidest();
        TailSliverMergedBack();
        HeadSliverMergedForward();
        WordOrderPreserved();
        InputNotMutated();
        return Task.CompletedTask;
    }

    // 0.14-1.0 nói | im 0.8s | 1.8-2.5 nói: khe 0.8 >= 0.6 -> cắt 2 câu.
    private static void GapSplitsIntoTwo()
    {
        var cue = Cue(0.14, 2.5, "前半后半",
        [
            Word(0.14, 0.5, "前"),
            Word(0.5, 1.0, "半"),
            Word(1.8, 2.2, "后"),
            Word(2.2, 2.5, "半"),
        ]);
        var pieces = Split(cue);
        Check(pieces.Count == 2, $"khe 0.8s phải cắt 2 câu (got {pieces.Count})");
        Check(StartOf(pieces[0]) == 0.14 && EndOf(pieces[0]) == 1.0, "câu 1 phải đúng khung nói 1");
        Check(StartOf(pieces[1]) == 1.8 && EndOf(pieces[1]) == 2.5, "câu 2 phải đúng khung nói 2");
        Check(TextOf(pieces[0]) == "前半" && TextOf(pieces[1]) == "后半", "chữ phải chia theo khung");
    }

    // Khe 0.5s (< 0.6) và câu ngắn: không cắt.
    private static void ShortGapKeepsWhole()
    {
        var cue = Cue(0.0, 2.0, "短句",
        [
            Word(0.0, 0.8, "短"),
            Word(1.3, 2.0, "句"),
        ]);
        var pieces = Split(cue);
        Check(pieces.Count == 1, $"khe 0.5s không được cắt (got {pieces.Count})");
        Check(StartOf(pieces[0]) == 0.0 && EndOf(pieces[0]) == 2.0, "câu nguyên phải giữ nguyên");
    }

    // Câu 12s không có khe >= 0.6: cắt ở khe rộng nhất (0.4s).
    private static void LongUnsplitFallsBackToWidest()
    {
        var cue = Cue(0.0, 12.0, "长句不歇",
        [
            Word(0.0, 5.0, "长"),
            Word(5.4, 7.0, "句"),
            Word(7.2, 12.0, "不歇"),
        ]);
        var pieces = Split(cue);
        Check(pieces.Count == 2, $"câu 12s phải cắt ở khe rộng nhất (got {pieces.Count})");
        Check(EndOf(pieces[0]) == 5.0 && StartOf(pieces[1]) == 5.4, "phải cắt ở khe 0.4s giữa 长/句");
    }

    // Mảnh đuôi 0.2s (<0.3s): dính vào câu trước.
    private static void TailSliverMergedBack()
    {
        var cue = Cue(0.0, 3.6, "主要嗯",
        [
            Word(0.0, 1.0, "主"),
            Word(1.7, 2.7, "要"),
            Word(3.4, 3.6, "嗯"),
        ]);
        var pieces = Split(cue);
        Check(pieces.Count == 2, $"mảnh đuôi phải gộp lên còn 2 câu (got {pieces.Count})");
        Check(EndOf(pieces[1]) == 3.6 && StartOf(pieces[1]) == 1.7, "câu sau phải chứa mảnh đuôi");
        Check(TextOf(pieces[1]).Contains("嗯", StringComparison.Ordinal), "chữ mảnh đuôi không được mất");
    }

    // Mảnh đầu 0.15s (<0.3s): gộp xuống câu sau.
    private static void HeadSliverMergedForward()
    {
        var cue = Cue(0.0, 3.0, "嗯主要",
        [
            Word(0.0, 0.15, "嗯"),
            Word(1.5, 2.0, "主"),
            Word(2.6, 3.0, "要"),
        ]);
        var pieces = Split(cue);
        Check(pieces.Count == 2, $"mảnh đầu phải gộp xuống còn 2 câu (got {pieces.Count})");
        Check(StartOf(pieces[0]) == 0.0 && EndOf(pieces[0]) == 2.0, "câu đầu phải chứa mảnh đầu");
        Check(TextOf(pieces[0]).Contains("嗯", StringComparison.Ordinal), "chữ mảnh đầu không được mất");
    }

    private static void WordOrderPreserved()
    {
        var cue = Cue(0.0, 4.0, "四句",
        [
            Word(2.0, 2.5, "句"),
            Word(0.0, 1.0, "四"),
            Word(3.0, 4.0, "尾"),
        ]);
        var pieces = Split(cue);
        var starts = pieces.SelectMany(p => WordsOf(p).Cast<object>())
            .Select(w => (double)w.GetType().GetProperty("Start")!.GetValue(w)!)
            .ToArray();
        for (var index = 1; index < starts.Length; index++)
            Check(starts[index] >= starts[index - 1], "thứ tự chữ bị đảo sau cắt");
        Check(pieces.Count == 2, $"khe 1.0-2.0 và 2.5-3.0 phải cắt (got {pieces.Count})");
    }

    private static void InputNotMutated()
    {
        var words = new[]
        {
            Word(0.0, 0.5, "前"),
            Word(0.5, 1.0, "半"),
            Word(1.8, 2.5, "后"),
        };
        var cue = Cue(0.14, 2.5, "前半后", words);
        Split(cue);
        Check(words.Length == 3 && (double)words[0].GetType().GetProperty("Start")!.GetValue(words[0])! == 0.0,
            "cắt câu làm đổi danh sách chữ gốc");
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
