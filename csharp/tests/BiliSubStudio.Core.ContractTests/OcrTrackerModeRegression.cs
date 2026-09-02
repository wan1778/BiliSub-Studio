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
        for (var index = 0; index < 7; index++)
            observeExact.Invoke(exact, [10d + index / 30d, 1d / 30d, singleRune]);
        if (exactActive.GetValue(exact) is not null)
            throw new InvalidOperationException("233ms one-rune OCR noise became an exact-frame cue");
        observeExact.Invoke(exact, [10d + 7d / 30d, 1d / 30d, singleRune]);
        if (exactActive.GetValue(exact) is not null)
            throw new InvalidOperationException("sub-300ms one-rune OCR noise became an exact-frame cue");
        observeExact.Invoke(exact, [10d + 8d / 30d, 1d / 30d, singleRune]);
        var exactCue = (OcrCue)(exactActive.GetValue(exact)
            ?? throw new InvalidOperationException("exact-frame tracker did not preserve a short one-rune cue"));
        if (Math.Abs(exactCue.Start - 10d) > .000001)
            throw new InvalidOperationException("exact-frame tracker did not retain the first refined PTS as cue start");
        if (Math.Abs(exactCue.End - (10d + 9d / 30d)) > .000001)
            throw new InvalidOperationException("exact-frame tracker did not retain refined frame duration as cue end");

        var cues = trackerType.GetProperty("Cues", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing tracker cue list");

        var scriptVariant = exactConstructor.Invoke([30d, .68d, true]);
        var simplified = new OcrResult(true, true, "别睡傻了", .99, []);
        var traditional = new OcrResult(true, true, "別睡傻了", .90, []);
        observeExact.Invoke(scriptVariant, [12d, 1d / 30d, simplified]);
        observeExact.Invoke(scriptVariant, [12d + 1d / 30d, 1d / 30d, simplified]);
        observeExact.Invoke(scriptVariant, [12d + 2d / 30d, 1d / 30d, traditional]);
        observeExact.Invoke(scriptVariant, [12d + 3d / 30d, 1d / 30d, simplified]);
        var scriptVariantActive = (OcrCue)(exactActive.GetValue(scriptVariant)
            ?? throw new InvalidOperationException("simplified/traditional OCR variant unexpectedly split a continuous cue"));
        if (scriptVariantActive.Text != "别睡傻了" || Math.Abs(scriptVariantActive.Start - 12d) > .000001
            || Math.Abs(scriptVariantActive.End - (12d + 4d / 30d)) > .000001)
            throw new InvalidOperationException("simplified/traditional OCR variant changed exact cue timing or text");

        var punctuationVariant = exactConstructor.Invoke([30d, .68d, true]);
        var asciiQuestion = new OcrResult(true, true, "所以?", .99, []);
        var chineseQuestion = new OcrResult(true, true, "所以？", .98, []);
        observeExact.Invoke(punctuationVariant, [13d, 1d / 30d, asciiQuestion]);
        observeExact.Invoke(punctuationVariant, [13d + 1d / 30d, 1d / 30d, asciiQuestion]);
        observeExact.Invoke(punctuationVariant, [13d + 2d / 30d, 1d / 30d, chineseQuestion]);
        observeExact.Invoke(punctuationVariant, [13d + 3d / 30d, 1d / 30d, asciiQuestion]);
        var punctuationActive = (OcrCue)(exactActive.GetValue(punctuationVariant)
            ?? throw new InvalidOperationException("ASCII/Chinese punctuation variant unexpectedly split a continuous cue"));
        if (punctuationActive.Text != "所以?" || Math.Abs(punctuationActive.Start - 13d) > .000001
            || Math.Abs(punctuationActive.End - (13d + 4d / 30d)) > .000001)
            throw new InvalidOperationException("ASCII/Chinese punctuation variant changed exact cue timing or text");

        var distinctText = exactConstructor.Invoke([30d, .68d, true]);
        observeExact.Invoke(distinctText, [14d, 1d / 30d, simplified]);
        observeExact.Invoke(distinctText, [14d + 1d / 30d, 1d / 30d, simplified]);
        var genuinelyDifferent = new OcrResult(true, true, "别睡呆了", .99, []);
        observeExact.Invoke(distinctText, [14d + 2d / 30d, 1d / 30d, genuinelyDifferent]);
        observeExact.Invoke(distinctText, [14d + 3d / 30d, 1d / 30d, genuinelyDifferent]);
        var distinctCues = (IReadOnlyList<OcrCue>)(cues.GetValue(distinctText)
            ?? throw new InvalidOperationException("missing committed cue list for distinct subtitle text"));
        if (distinctCues.Count != 1 || distinctCues[0].Text != "别睡傻了"
            || Math.Abs(distinctCues[0].End - (14d + 2d / 30d)) > .000001)
            throw new InvalidOperationException("a genuinely different subtitle was merged as a script variant");

        var rapid = exactConstructor.Invoke([30d, .68d, true]);
        var rapidTexts = new[] { "我一定会成功", "你一定会成功", "他一定会成功", "她一定会成功" };
        for (var index = 0; index < rapidTexts.Length; index++)
        {
            var observation = new OcrResult(true, true, rapidTexts[index], .99, []);
            observeExact.Invoke(rapid, [16d + index, 1d / 30d, observation]);
            observeExact.Invoke(rapid, [16d + index + 1d / 30d, 1d / 30d, observation]);
        }
        var rapidCues = (IReadOnlyList<OcrCue>)(cues.GetValue(rapid)
            ?? throw new InvalidOperationException("missing rapid subtitle cue list"));
        var rapidActive = (OcrCue)(exactActive.GetValue(rapid)
            ?? throw new InvalidOperationException("rapid subtitle tracker lost the fourth cue"));
        var allRapidTexts = rapidCues.Select(cue => cue.Text).Append(rapidActive.Text).ToArray();
        if (rapidCues.Count != 3 || !allRapidTexts.SequenceEqual(rapidTexts))
            throw new InvalidOperationException($"four fast similar subtitles collapsed into {allRapidTexts.Length} cues: {string.Join(" | ", allRapidTexts)}");

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

        var shortCaption = exactConstructor.Invoke([30d, .68d, true]);
        observeExact.Invoke(shortCaption, [22d, 1d / 30d, full]);
        observeExact.Invoke(shortCaption, [22d + 1d / 30d, 1d / 30d, full]);
        var oneRune = new OcrResult(true, true, "走", .99, []);
        observeExact.Invoke(shortCaption, [22d + 2d / 30d, 1d / 30d, oneRune]);
        observeExact.Invoke(shortCaption, [22d + 3d / 30d, 1d / 30d, oneRune]);
        if (((IReadOnlyList<OcrCue>)(cues.GetValue(shortCaption)
                ?? throw new InvalidOperationException("missing short-caption cue list"))).Count != 0)
            throw new InvalidOperationException("one-rune subtext was promoted before minimum evidence");
        for (var index = 4; index <= 11; index++)
            observeExact.Invoke(shortCaption, [22d + index / 30d, 1d / 30d, oneRune]);
        var shortCaptionCues = (IReadOnlyList<OcrCue>)(cues.GetValue(shortCaption)
            ?? throw new InvalidOperationException("missing confirmed short-caption cue list"));
        var shortCaptionActive = (OcrCue)(exactActive.GetValue(shortCaption)
            ?? throw new InvalidOperationException("stable one-rune subtitle was discarded as a reveal fragment"));
        if (shortCaptionCues.Count != 1 || shortCaptionCues[0].Text != "你走吧"
            || shortCaptionActive.Text != "走"
            || Math.Abs(shortCaptionActive.Start - (22d + 2d / 30d)) > .000001)
            throw new InvalidOperationException("stable 300ms one-rune subtitle did not become its own timed cue");

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
        var modes = new[] { "accurate", "balanced", "fast" }.ToDictionary(
            name => name,
            name => modeFor.Invoke(null, [name, 1d])
                ?? throw new InvalidOperationException($"{name} OCR mode unavailable"));
        foreach (var (name, mode) in modes)
        {
            if (mode.GetType().GetProperty("AdaptiveTiming")?.GetValue(mode) is not true)
                throw new InvalidOperationException($"{name} OCR can quantize cue timing or drop short captions");
        }
        var accurateMode = modes["accurate"];
        if (accurateMode.GetType().GetProperty("ExhaustiveRecognition")?.GetValue(accurateMode) is not true
            || modes["balanced"].GetType().GetProperty("ExhaustiveRecognition")?.GetValue(modes["balanced"]) is true
            || modes["fast"].GetType().GetProperty("ExhaustiveRecognition")?.GetValue(modes["fast"]) is true)
            throw new InvalidOperationException("Accurate OCR no longer owns the every-frame Medium quality policy");
        var installerType = assembly.GetType("BiliSubStudio.Core.Ocr.OcrInstaller")
            ?? throw new InvalidOperationException("missing OCR installer");
        if ((string?)installerType.GetField("DetectionModel")?.GetRawConstantValue() != "PP-OCRv6_medium_det"
            || (string?)installerType.GetField("RecognitionModel")?.GetRawConstantValue() != "PP-OCRv6_medium_rec")
            throw new InvalidOperationException("OCR quality model regressed from PP-OCRv6 Medium");
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
            throw new InvalidOperationException("adaptive OCR decoder no longer preserves every source-frame PTS");

        foreach (var (name, mode) in modes)
        {
            var nativeArgs = (IReadOnlyList<string>)(buildLane.Invoke(null, ["source.mp4", new OcrRegion(.05, .65, .90, .29), mode, 1000.13d, 1002d, false])
                ?? throw new InvalidOperationException($"{name} OCR FFmpeg arguments unavailable"));
            if (!nativeArgs.Contains("-copyts") || !nativeArgs.Contains("info")
                || nativeArgs.SkipWhile(x => x != "-vf").Skip(1).FirstOrDefault() is not { } nativeFilter
                || !nativeFilter.Contains("showinfo", StringComparison.Ordinal)
                || nativeFilter.Contains("fps=", StringComparison.Ordinal))
                throw new InvalidOperationException($"{name} OCR no longer preserves native-frame PTS for adaptive short-caption recovery");
        }

        var buildSegments = checkpointType.GetMethod("BuildSegments", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing OCR lane segment builder");
        var longSegments = ((System.Collections.IEnumerable)(buildSegments.Invoke(null, [29170d, 4, 16d])
            ?? throw new InvalidOperationException("8-hour OCR lane segments unavailable"))).Cast<object>().ToArray();
        if (longSegments.Length != 4)
            throw new InvalidOperationException("8-hour OCR did not build four lane segments");
        var lane1CoreStart = (double)(longSegments[1].GetType().GetProperty("CoreStart")?.GetValue(longSegments[1])
            ?? throw new InvalidOperationException("missing lane 2 core start"));
        var lane2CoreStart = (double)(longSegments[2].GetType().GetProperty("CoreStart")?.GetValue(longSegments[2])
            ?? throw new InvalidOperationException("missing lane 3 core start"));
        var lane3CoreStart = (double)(longSegments[3].GetType().GetProperty("CoreStart")?.GetValue(longSegments[3])
            ?? throw new InvalidOperationException("missing lane 4 core start"));
        var lane3CoreEnd = (double)(longSegments[3].GetType().GetProperty("CoreEnd")?.GetValue(longSegments[3])
            ?? throw new InvalidOperationException("missing lane 4 core end"));
        if (Math.Abs(lane1CoreStart - 7292.5d) > .000001
            || Math.Abs(lane2CoreStart - 14585d) > .000001
            || Math.Abs(lane3CoreStart - 21877.5d) > .000001
            || Math.Abs(lane3CoreEnd - 29170d) > .000001)
            throw new InvalidOperationException("8-hour OCR lane ownership no longer spans the full source duration");

        var timestampReaderType = scannerType.GetNestedType("FrameTimestampReader", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing every-frame PTS reader");
        var timestampReaderConstructor = timestampReaderType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(StreamReader), typeof(double)],
            modifiers: null)
            ?? throw new InvalidOperationException("missing lane-origin-aware PTS reader constructor");
        var readTimestamp = timestampReaderType.GetMethod("ReadAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing every-frame PTS read method");
        using var stderr = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(
            "[Parsed_showinfo_0 @ 000000] n:   0 pts: 953600 pts_time:59.6 duration:    533 duration_time:0.0333125 fmt:yuv420p\n")));
        var timestampReader = timestampReaderConstructor.Invoke([stderr, 59.6d]);
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
            throw new InvalidOperationException("every-frame PTS reader changed source-global frame timing");

        using var sampledStderr = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(
            "[Parsed_showinfo_1 @ 000000] n:   0 pts: 2500 pts_time:1000 duration:      1 duration_time:0.4 fmt:yuv420p\n")));
        var sampledTimestampReader = timestampReaderConstructor.Invoke([sampledStderr, 1000.13d]);
        var sampledTimestampTask = (Task)(readTimestamp.Invoke(sampledTimestampReader, [CancellationToken.None])
            ?? throw new InvalidOperationException("sampled PTS read did not return a task"));
        sampledTimestampTask.GetAwaiter().GetResult();
        var sampledTimestamp = sampledTimestampTask.GetType().GetProperty("Result")?.GetValue(sampledTimestampTask)
            ?? throw new InvalidOperationException("sampled PTS reader did not return a frame");
        var sampledPts = (double)(sampledTimestamp.GetType().GetProperty("PresentationTime")?.GetValue(sampledTimestamp)
            ?? throw new InvalidOperationException("sampled PTS reader dropped presentation timestamp"));
        if (Math.Abs(sampledPts - 1000d) > .000001)
            throw new InvalidOperationException("sampled OCR changed an already source-global PTS after seek");

        var secondLaneScanStart = lane1CoreStart - 16d;
        using var relativeStderr = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(
            "[Parsed_showinfo_1 @ 000000] n:   0 pts: 41 pts_time:16.4 duration:      1 duration_time:0.4 fmt:yuv420p\n" +
            "[Parsed_showinfo_1 @ 000000] n:   1 pts: 42 pts_time:16.8 duration:      1 duration_time:0.4 fmt:yuv420p\n")));
        var relativeTimestampReader = timestampReaderConstructor.Invoke([relativeStderr, secondLaneScanStart]);
        var relativeTimestampTask1 = (Task)(readTimestamp.Invoke(relativeTimestampReader, [CancellationToken.None])
            ?? throw new InvalidOperationException("relative lane PTS read did not return a task"));
        relativeTimestampTask1.GetAwaiter().GetResult();
        var relativeTimestamp1 = relativeTimestampTask1.GetType().GetProperty("Result")?.GetValue(relativeTimestampTask1)
            ?? throw new InvalidOperationException("relative lane PTS reader did not return a frame");
        var relativePts1 = (double)(relativeTimestamp1.GetType().GetProperty("PresentationTime")?.GetValue(relativeTimestamp1)
            ?? throw new InvalidOperationException("relative lane PTS reader dropped presentation timestamp"));
        var relativeTimestampTask2 = (Task)(readTimestamp.Invoke(relativeTimestampReader, [CancellationToken.None])
            ?? throw new InvalidOperationException("second relative lane PTS read did not return a task"));
        relativeTimestampTask2.GetAwaiter().GetResult();
        var relativeTimestamp2 = relativeTimestampTask2.GetType().GetProperty("Result")?.GetValue(relativeTimestampTask2)
            ?? throw new InvalidOperationException("second relative lane PTS reader did not return a frame");
        var relativePts2 = (double)(relativeTimestamp2.GetType().GetProperty("PresentationTime")?.GetValue(relativeTimestamp2)
            ?? throw new InvalidOperationException("second relative lane PTS reader dropped presentation timestamp"));
        if (Math.Abs(relativePts1 - 7292.9d) > .000001 || Math.Abs(relativePts2 - 7293.3d) > .000001)
            throw new InvalidOperationException("seek-relative lane PTS was not restored to the source-global 8-hour timeline");

        var laneCheckpointType = assembly.GetType("BiliSubStudio.Core.Ocr.OcrLaneCheckpoint")
            ?? throw new InvalidOperationException("missing OCR lane checkpoint type");
        var laneArray = Array.CreateInstance(laneCheckpointType, 4);
        var laneCueStarts = new[] { 600d, 7892.5d, 15185d, 28800d };
        for (var index = 0; index < 4; index++)
        {
            var lane = Activator.CreateInstance(
                laneCheckpointType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                args: [longSegments[index], (double)(longSegments[index].GetType().GetProperty("CoreEnd")?.GetValue(longSegments[index])
                    ?? throw new InvalidOperationException("missing lane core end")),
                    new List<OcrCue> { new(laneCueStarts[index], laneCueStarts[index] + 1d, $"第{index + 1}段字幕", .99d) },
                    null, 1, 1, true],
                culture: null)
                ?? throw new InvalidOperationException("could not create synthetic OCR lane checkpoint");
            laneArray.SetValue(lane, index);
        }
        var reconcile = scannerType.GetMethod("Reconcile", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing OCR lane reconciler");
        object?[] reconcileArgs = [laneArray, 0];
        var longMerged = (IReadOnlyList<OcrCue>)(reconcile.Invoke(null, reconcileArgs)
            ?? throw new InvalidOperationException("8-hour OCR lane reconcile returned null"));
        if (longMerged.Count != 4 || Math.Abs(longMerged[^1].Start - 28800d) > .000001)
            throw new InvalidOperationException("8-hour OCR reconcile dropped cues from lanes after the first two-hour segment");

        var similarity = scannerType.GetMethod("Similarity", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing OcrScanner.Similarity");
        var reversed = (double)(similarity.Invoke(null, ["不是", "是不"])
            ?? throw new InvalidOperationException("similarity returned null"));
        if (reversed >= 0.82)
            throw new InvalidOperationException("reordered Chinese text can still be merged at a lane boundary");
    }
}
