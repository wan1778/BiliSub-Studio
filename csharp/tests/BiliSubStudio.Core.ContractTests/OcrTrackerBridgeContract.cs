using System.Reflection;
using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

// Cầu flicker tracker (video Cả Lớp 19/09: 1 câu chẻ 3-5 cue khi flicker —
// VD 2546.7s Ăn Mày 1 câu chẻ 3, 731s Cả Lớp notebook flicker): active đã
// chốt vì 2 blank mà chữ tương đương quay lại trong 0.5s thì NỐI cue cũ
// (giữ Start, nối End, cộng support) thay vì mở cue mới.
// SubtitleTracker là internal nên đi reflection như OcrTrackerModeRegression.
internal static class OcrTrackerBridgeContract
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Verify()
    {
        BridgeJoinsIdenticalReturn();
        BridgeJoinsGrowingReturn();
        BridgeRefusesShorterCaption();
        BridgeRefusesWideGap();
    }

    private static readonly ConstructorInfo TrackerCtor =
        typeof(OcrResult).Assembly.GetType("BiliSubStudio.Core.Ocr.SubtitleTracker")!
            .GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null, types: [typeof(double), typeof(double)], modifiers: null)
            ?? throw new InvalidOperationException("missing SubtitleTracker ctor");

    private static readonly MethodInfo ObserveMethod =
        typeof(OcrResult).Assembly.GetType("BiliSubStudio.Core.Ocr.SubtitleTracker")!
            .GetMethod("Observe", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null, types: [typeof(double), typeof(OcrResult)], modifiers: null)
            ?? throw new InvalidOperationException("missing Observe");

    private static readonly MethodInfo FinishMethod =
        typeof(OcrResult).Assembly.GetType("BiliSubStudio.Core.Ocr.SubtitleTracker")!
            .GetMethod("Finish", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null, types: [typeof(double)], modifiers: null)
            ?? throw new InvalidOperationException("missing Finish");

    private static readonly PropertyInfo CuesProperty =
        typeof(OcrResult).Assembly.GetType("BiliSubStudio.Core.Ocr.SubtitleTracker")!
            .GetProperty("Cues", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing Cues");

    private sealed class Driver
    {
        private readonly object _tracker;
        public Driver()
        {
            _tracker = TrackerCtor.Invoke([5.0d, 0.68d]);
        }

        public void At(double seconds, string? text, double conf = 0.9)
        {
            var result = text is null
                ? new OcrResult(true, false, string.Empty, 0, [])
                : new OcrResult(true, true, text, conf, []);
            ObserveMethod.Invoke(_tracker, [seconds, result]);
        }

        public IReadOnlyList<OcrCue> End(double seconds)
        {
            FinishMethod.Invoke(_tracker, [seconds]);
            return (IReadOnlyList<OcrCue>)CuesProperty.GetValue(_tracker)!;
        }
    }

    private static void BridgeJoinsIdenticalReturn()
    {
        var driver = new Driver();
        driver.At(0.0, "测试字幕");
        driver.At(0.2, "测试字幕");
        driver.At(0.4, null);
        driver.At(0.6, null);
        driver.At(0.7, "测试字幕");
        driver.At(0.9, "测试字幕");
        var cues = driver.End(2.0);
        if (cues.Count != 1)
            throw new InvalidOperationException($"flicker-return phải nối 1 cue (được {cues.Count})");
        if (cues[0].Start > 0.01 || cues[0].End < 0.99)
            throw new InvalidOperationException($"cue nối phải phủ 0→1s (được {cues[0].Start:0.00}-{cues[0].End:0.00})");
    }

    private static void BridgeJoinsGrowingReturn()
    {
        // Fade-in: bản đọc ngắn trước, đủ câu sau — cùng caption, nối.
        var driver = new Driver();
        driver.At(0.0, "老龙卫");
        driver.At(0.2, "老龙卫");
        driver.At(0.4, null);
        driver.At(0.6, null);
        driver.At(0.7, "老龙卫谈生意");
        driver.At(0.9, "老龙卫谈生意");
        var cues = driver.End(2.0);
        if (cues.Count != 1)
            throw new InvalidOperationException($"fade-in phải nối 1 cue (được {cues.Count})");
    }

    private static void BridgeRefusesShorterCaption()
    {
        // Thoại ngắn thật sau câu dài ("你走吧" rồi "走") — KHÔNG nối.
        var driver = new Driver();
        driver.At(0.0, "你走吧");
        driver.At(0.2, "你走吧");
        driver.At(0.4, null);
        driver.At(0.6, null);
        driver.At(0.7, "走");
        driver.At(0.9, "走");
        var cues = driver.End(2.0);
        if (cues.Count != 2)
            throw new InvalidOperationException($"câu ngắn thật phải tách (được {cues.Count})");
    }

    private static void BridgeRefusesWideGap()
    {
        // Quay lại sau 0.6s (>0.5s) — blank thật, giữ tách.
        var driver = new Driver();
        driver.At(0.0, "测试字幕");
        driver.At(0.2, "测试字幕");
        driver.At(0.4, null);
        driver.At(0.6, null);
        driver.At(1.0, "测试字幕");
        driver.At(1.2, "测试字幕");
        var cues = driver.End(3.0);
        if (cues.Count != 2)
            throw new InvalidOperationException($"gap 0.6s phải tách (được {cues.Count})");
    }
}
