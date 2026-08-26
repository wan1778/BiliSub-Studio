using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

internal static class OcrTrackerModeRegression
{
    [ModuleInitializer]
    internal static void Verify()
    {
        var assembly = typeof(OcrResult).Assembly;
        var trackerType = assembly.GetType("BiliSubStudio.Core.Ocr.SubtitleTracker")
            ?? throw new InvalidOperationException("missing SubtitleTracker");
        var constructor = trackerType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(double), typeof(double)],
            modifiers: null)
            ?? throw new InvalidOperationException("missing mode-aware SubtitleTracker constructor");
        var observe = trackerType.GetMethod(
            "Observe",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(double), typeof(OcrResult)],
            modifiers: null)
            ?? throw new InvalidOperationException("missing SubtitleTracker.Observe");
        var active = trackerType.GetProperty("Active", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing SubtitleTracker.Active");

        var sample = new OcrResult(true, true, "你好", 0.60, []);
        object Tracker(double lowConfidence) => constructor.Invoke([1.5d, lowConfidence]);

        var fast = Tracker(0.58);
        observe.Invoke(fast, [0d, sample]);
        observe.Invoke(fast, [2d / 3d, sample]);
        if (active.GetValue(fast) is null)
            throw new InvalidOperationException("Fast OCR did not promote a 0.60-confidence two-hit subtitle");

        var accurate = Tracker(0.68);
        observe.Invoke(accurate, [0d, sample]);
        observe.Invoke(accurate, [0.25d, sample]);
        if (active.GetValue(accurate) is not null)
            throw new InvalidOperationException("Accurate OCR promoted a low-confidence subtitle before the third hit");
        observe.Invoke(accurate, [0.50d, sample]);
        if (active.GetValue(accurate) is null)
            throw new InvalidOperationException("Accurate OCR did not promote a stable third low-confidence hit");

        var interrupted = Tracker(0.58);
        observe.Invoke(interrupted, [0d, sample]);
        observe.Invoke(interrupted, [0.25d, new OcrResult(true, true, "A N", 0.95, [])]);
        observe.Invoke(interrupted, [0.50d, sample]);
        if (active.GetValue(interrupted) is not null)
            throw new InvalidOperationException("foreign-script OCR garbage did not break an unconfirmed subtitle candidate");
        observe.Invoke(interrupted, [1.00d, sample]);
        if (active.GetValue(interrupted) is null)
            throw new InvalidOperationException("subtitle candidate did not recover after fresh consecutive valid hits");

        var recovered = Tracker(.58);
        observe.Invoke(recovered, [1.00d, new OcrResult(true, true, "吃我的喝我", .80, [])]);
        observe.Invoke(recovered, [1.40d, new OcrResult(true, true, "吃我的喝我的", .74, [])]);
        var recoveredCue = (OcrCue)(active.GetValue(recovered)
            ?? throw new InvalidOperationException("OCR did not promote stable recovered text"));
        if (recoveredCue.Text != "吃我的喝我的")
            throw new InvalidOperationException("OCR tracker discarded a stable one-character recovery");
        if (Math.Abs(recoveredCue.Start - 2d / 3d) > .001)
            throw new InvalidOperationException("OCR cue start still uses the late first-detected frame instead of midpoint timing");

        var exactConstructor = trackerType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(double), typeof(double), typeof(bool)],
            modifiers: null)
            ?? throw new InvalidOperationException("missing exact-frame SubtitleTracker constructor");
        var observeExact = trackerType.GetMethod(
            "Observe",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(double), typeof(double), typeof(OcrResult)],
            modifiers: null)
            ?? throw new InvalidOperationException("missing PTS-aware SubtitleTracker.Observe");
        var exactActive = trackerType.GetProperty("Active", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing exact-frame tracker active cue");
        var exact = exactConstructor.Invoke([30d, .68d, true]);
        var singleRune = new OcrResult(true, true, "啊", .99, []);
        observeExact.Invoke(exact, [10d, 1d / 30d, singleRune]);
        observeExact.Invoke(exact, [10d + 1d / 30d, 1d / 30d, singleRune]);
        observeExact.Invoke(exact, [10d + 2d / 30d, 1d / 30d, singleRune]);
        var exactCue = (OcrCue)(exactActive.GetValue(exact)
            ?? throw new InvalidOperationException("every-frame OCR did not preserve a short one-rune cue"));
        if (Math.Abs(exactCue.Start - 10d) > .000001)
            throw new InvalidOperationException("every-frame OCR did not retain the first source-frame PTS as cue start");
        if (Math.Abs(exactCue.End - (10d + 3d / 30d)) > .000001)
            throw new InvalidOperationException("every-frame OCR did not retain source-frame duration as cue end");

        var checkpointType = assembly.GetType("BiliSubStudio.Core.Ocr.OcrCheckpointStore")
            ?? throw new InvalidOperationException("missing OCR checkpoint store");
        var modeFor = checkpointType.GetMethod("ModeFor", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing OCR scan mode selector");
        var accurateMode = modeFor.Invoke(null, ["accurate", 1d])
            ?? throw new InvalidOperationException("accurate OCR mode unavailable");
        var scannerType = typeof(OcrResult).Assembly.GetType("BiliSubStudio.Core.Ocr.OcrScanner")
            ?? throw new InvalidOperationException("missing OcrScanner");
        var buildLane = scannerType.GetMethod("BuildLaneArguments", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing OCR FFmpeg argument builder");
        var args = (IReadOnlyList<string>)(buildLane.Invoke(null, ["source.mp4", new OcrRegion(.05, .65, .90, .29), accurateMode, 1d, 2d, false])
            ?? throw new InvalidOperationException("accurate OCR FFmpeg arguments unavailable"));
        if (!args.Contains("-copyts") || !args.Contains("info") || args.SkipWhile(x => x != "-vf").Skip(1).FirstOrDefault() is not { } filter
            || !filter.Contains("showinfo", StringComparison.Ordinal) || filter.Contains("fps=", StringComparison.Ordinal))
            throw new InvalidOperationException("accurate OCR is still sampled instead of preserving every frame PTS");

        var timestampReaderType = scannerType.GetNestedType("FrameTimestampReader", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing every-frame PTS reader");
        var timestampReaderConstructor = timestampReaderType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(StreamReader)],
            modifiers: null)
            ?? throw new InvalidOperationException("missing every-frame PTS reader constructor");
        var readTimestamp = timestampReaderType.GetMethod("ReadAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing every-frame PTS read method");
        using var stderr = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(
            "[Parsed_showinfo_0 @ 000000] n:   0 pts: 953600 pts_time:59.6 duration:    533 duration_time:0.0333125 fmt:yuv420p\n")));
        var timestampReader = timestampReaderConstructor.Invoke([stderr]);
        var timestampTask = (Task)(readTimestamp.Invoke(timestampReader, [CancellationToken.None])
            ?? throw new InvalidOperationException("every-frame PTS read did not return a task"));
        timestampTask.GetAwaiter().GetResult();
        var timestamp = timestampTask.GetType().GetProperty("Result")?.GetValue(timestampTask)
            ?? throw new InvalidOperationException("every-frame PTS read did not return a frame");
        var pts = (double)(timestamp.GetType().GetProperty("PresentationTime")?.GetValue(timestamp)
            ?? throw new InvalidOperationException("PTS reader dropped presentation timestamp"));
        var duration = (double)(timestamp.GetType().GetProperty("Duration")?.GetValue(timestamp)
            ?? throw new InvalidOperationException("PTS reader dropped frame duration"));
        if (Math.Abs(pts - 59.6) > .000001 || Math.Abs(duration - .0333125) > .000001)
            throw new InvalidOperationException("every-frame PTS reader changed source frame timing");

        var similarity = scannerType.GetMethod("Similarity", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing OcrScanner.Similarity");
        var reversed = (double)(similarity.Invoke(null, ["不是", "是不"])
            ?? throw new InvalidOperationException("similarity returned null"));
        if (reversed >= 0.82)
            throw new InvalidOperationException("reordered Chinese text can still be merged at a lane boundary");
    }
}
