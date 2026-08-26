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

        var cues = trackerType.GetProperty("Cues", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing tracker cue list");
        var reveal = exactConstructor.Invoke([30d, .68d, true]);
        var full = new OcrResult(true, true, "你走吧", .99, []);
        observeExact.Invoke(reveal, [20d, 1d / 30d, full]);
        observeExact.Invoke(reveal, [20d + 1d / 30d, 1d / 30d, full]);
        observeExact.Invoke(reveal, [20d + 2d / 30d, 1d / 30d, new OcrResult(true, true, "走", .99, [])]);
        var revealActive = (OcrCue)(exactActive.GetValue(reveal)
            ?? throw new InvalidOperationException("subtitle reveal unexpectedly split the active cue"));
        if (revealActive.Text != "你走吧")
            throw new InvalidOperationException("subtitle reveal did not retain full text");
        observeExact.Invoke(reveal, [20d + 3d / 30d, 1d / 30d, new OcrResult(true, true, "再见", .99, [])]);
        observeExact.Invoke(reveal, [20d + 4d / 30d, 1d / 30d, new OcrResult(true, true, "再见", .99, [])]);
        var revealCues = (IReadOnlyList<OcrCue>)(cues.GetValue(reveal)
            ?? throw new InvalidOperationException("subtitle reveal did not commit the previous cue"));
        if (revealCues.Count != 1 || revealCues[0].Text != "你走吧"
            || Math.Abs(revealCues[0].End - (20d + 2d / 30d)) > .000001)
            throw new InvalidOperationException("an actual new subtitle was not separated after a reveal fragment");

        var repeated = exactConstructor.Invoke([30d, .68d, true]);
        var longText = new OcrResult(true, true, "天天被幸福包围", .99, []);
        observeExact.Invoke(repeated, [30d, 1d / 30d, longText]);
        observeExact.Invoke(repeated, [30d + 1d / 30d, 1d / 30d, longText]);
        for (var index = 0; index < 24; index++)
        {
            observeExact.Invoke(repeated, [30d + (index + 2d) / 30d, 1d / 30d, new OcrResult(true, true, "幸福", .99, [])]);
        }
        var repeatedCues = (IReadOnlyList<OcrCue>)(cues.GetValue(repeated)
            ?? throw new InvalidOperationException("repeated substring did not commit the prior cue"));
        var repeatedActive = (OcrCue)(exactActive.GetValue(repeated)
            ?? throw new InvalidOperationException("repeated substring did not become a new cue"));
        if (repeatedCues.Count != 1 || repeatedCues[0].Text != "天天被幸福包围"
            || Math.Abs(repeatedCues[0].End - (30d + 2d / 30d)) > .000001
            || repeatedActive.Text != "幸福")
            throw new InvalidOperationException("persistent repeated substring was merged into the preceding subtitle");

        var checkpointType = assembly.GetType("BiliSubStudio.Core.Ocr.OcrCheckpointStore")
            ?? throw new InvalidOperationException("missing OCR checkpoint store");
        var modeFor = checkpointType.GetMethod("ModeFor", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing OCR scan mode selector");
        var accurateMode = modeFor.Invoke(null, ["accurate", 1d])
            ?? throw new InvalidOperationException("accurate OCR mode unavailable");
        var scannerType = typeof(OcrResult).Assembly.GetType("BiliSubStudio.Core.Ocr.OcrScanner")
            ?? throw new InvalidOperationException("missing OcrScanner");
        var filterOverlayLines = scannerType.GetMethod("FilterOffBaselineOverlayLines", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing off-baseline OCR line filter");
        var lowerRegion = new OcrRegion(.05, .65, .90, .29);
        var filtered = (OcrResult)(filterOverlayLines.Invoke(null, [new OcrResult(true, true, "州\n整整一万年", .955d,
        [
            new OcrLine("州", .911d, [675, 62, 710, 101]),
            new OcrLine("整整一万年", .999d, [514, 221, 765, 277]),
        ]), lowerRegion]) ?? throw new InvalidOperationException("isolated OCR line filter returned null"));
        if (filtered.Text != "整整一万年" || filtered.Lines.Count != 1 || filtered.Confidence < .99d)
            throw new InvalidOperationException("isolated weak one-glyph OCR line contaminated the subtitle text");
        var filteredOverlay = (OcrResult)(filterOverlayLines.Invoke(null, [new OcrResult(true, true, "在原地\n去吧", .998d,
        [
            new OcrLine("在原地", .999d, [0, 33, 62, 221]),
            new OcrLine("去吧", .999d, [591, 224, 694, 277]),
        ]), lowerRegion]) ?? throw new InvalidOperationException("off-baseline OCR overlay filter returned null"));
        if (filteredOverlay.Text != "去吧" || filteredOverlay.Lines.Count != 1)
            throw new InvalidOperationException("vertical left-side OCR overlay contaminated the subtitle text");
        var filteredGlyph = (OcrResult)(filterOverlayLines.Invoke(null, [new OcrResult(true, true, "口", .99d,
        [new OcrLine("口", .99d, [1225, 30, 1280, 100])]), lowerRegion])
            ?? throw new InvalidOperationException("upper one-glyph OCR filter returned null"));
        if (filteredGlyph.Detected || filteredGlyph.Lines.Count != 0)
            throw new InvalidOperationException("upper single-glyph overlay became an OCR cue");
        var upperRegionGlyph = (OcrResult)(filterOverlayLines.Invoke(null, [new OcrResult(true, true, "口", .99d,
        [new OcrLine("口", .99d, [1225, 30, 1280, 100])]), new OcrRegion(.05, .10, .90, .29)])
            ?? throw new InvalidOperationException("upper ROI one-glyph filter returned null"));
        if (!upperRegionGlyph.Detected || upperRegionGlyph.Text != "口")
            throw new InvalidOperationException("user-selected upper ROI lost a legitimate one-glyph caption");
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
