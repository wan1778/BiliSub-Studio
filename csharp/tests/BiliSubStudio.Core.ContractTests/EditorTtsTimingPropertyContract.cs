using System.Reflection;
using System.Runtime.CompilerServices;
using BiliSubStudio.Core.Editor;

namespace BiliSubStudio.Core.ContractTests;

internal static class EditorTtsTimingPropertyContract
{
    private const int SampleRate = 22050;
    private const int RandomSeed = 0x3083;

    [ModuleInitializer]
    internal static void Install()
    {
        RuntimeHelpers.RunClassConstructor(typeof(Program).TypeHandle);
        var field = typeof(Program).GetField("Tests", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing Core contract list");
        var tests = field.GetValue(null) as List<(string Name, Func<Task> Test)>
            ?? throw new InvalidOperationException("Core contract list type drifted");
        tests.Add(("TTS timing boundary matrix keeps SRT authoritative", VerifyBoundaryMatrixAsync));
        tests.Add(("TTS timing randomized ASR support never changes SRT windows", VerifyRandomizedMappingsAsync));
        tests.Add(("TTS timing rejects malformed SRT and stale cue ownership", VerifyMalformedTimingAsync));
        tests.Add(("Voice planner removes unspeakable artifacts and only proven OCR flicker duplicates", VerifyOcrFlickerPlannerAsync));
    }

    private static Task VerifyBoundaryMatrixAsync()
    {
        var build = BuildWholeCueMethod();
        var cue = Cue("boundary", 1, 5);
        var cases = new (string Name, EditorWordTiming[] Words, string Source, double Start, double End)[]
        {
            ("no words", [], "srt-fallback", 1, 5),
            ("neighbor before within tolerance", [Word("before", .92, .987)], "srt-fallback", 1, 5),
            ("neighbor after within tolerance", [Word("after", 5.013, 5.08)], "srt-fallback", 1, 5),
            ("touches start only", [Word("touch-start", .9, 1)], "srt-fallback", 1, 5),
            ("touches end only", [Word("touch-end", 5, 5.1)], "srt-fallback", 1, 5),
            ("crosses start", [Word("cross-start", .95, 1.2)], "whisper", 1, 1.2),
            ("crosses end", [Word("cross-end", 4.8, 5.05)], "whisper", 4.8, 5),
            ("inside", [Word("inside", 1.4, 4.6)], "whisper", 1.4, 4.6),
            ("unsorted inside", [Word("late", 3.2, 4.1), Word("early", 1.3, 2.2)], "whisper", 1.3, 4.1),
            ("mixed neighbor and inside", [Word("before", .95, .99), Word("inside", 2, 3), Word("after", 5.01, 5.05)], "whisper", 1, 5),
            ("sub-sample overlap", [Word("sub-sample", 5 - .2 / SampleRate, 5.02)], "srt-fallback", 1, 5),
            ("one-sample overlap", [Word("one-sample", 5 - 1.1 / SampleRate, 5.02)], "whisper", 5 - 1.1 / SampleRate, 5),
        };

        foreach (var item in cases)
        {
            var timing = Timing(cue, item.Words);
            var manifest = InvokeBuild(build, cue, timing);
            Equal("srt-fallback", StringProperty(manifest, "TimingSource"), item.Name);
            Near(cue.Start, DoubleProperty(manifest, "VoiceStart"), .1 / SampleRate, item.Name + " start");
            Near(cue.End, DoubleProperty(manifest, "VoiceEnd"), .1 / SampleRate, item.Name + " end");
            AssertManifestInvariants(cue, manifest, item.Name);
        }

        var reportedCue = Cue("reported-window", .03, 1.83);
        var reportedManifest = InvokeBuild(build, reportedCue,
            Timing(reportedCue, [Word("reported-speech", 0, 1.66)]));
        Equal("srt-fallback", StringProperty(reportedManifest, "TimingSource"), "SRT timing owner");
        Equal(.03, DoubleProperty(reportedManifest, "VoiceStart"), "reported source window was not clamped to SRT start");
        Equal(1.83, DoubleProperty(reportedManifest, "VoiceEnd"), "ASR shortened the SRT end");
        AssertManifestInvariants(reportedCue, reportedManifest, "reported source window");

        var analysis = Analysis([
            Segment(Word("previous", 10.24, 10.32)),
            Segment(Word("crossing", 12.95, 13.2)),
            Segment(Word("next", 17.01, 17.07)),
        ]);
        var remapped = EditorSpeechAnalysisDocument.MapToCues(analysis,
        [
            Cue("changed-srt-a", 10.333, 12.4),
            Cue("changed-srt-b", 13, 17),
        ]);
        Equal("previous", remapped[0].Words.Single().Text, "ASR boundary word must own the first OCR cue");
        Equal(2, remapped[1].Words.Count, "same Whisper analysis must include the bounded start/end halo");
        return Task.CompletedTask;
    }

    private static Task VerifyRandomizedMappingsAsync()
    {
        const int cueCount = 8_000;
        var random = new Random(RandomSeed);
        var cues = new List<EditorSubtitleCue>(cueCount);
        var segments = new List<EditorSpeechSegment>(cueCount * 2);
        var cursor = 1d;
        for (var index = 0; index < cueCount; index++)
        {
            var gap = index % 19 == 0 ? 0 : random.NextDouble() * .18;
            var start = cursor + gap;
            var duration = index % 97 == 0 ? .001 : .08 + random.NextDouble() * 3.92;
            var end = start + duration;
            var cue = Cue("random-" + index, start, end, index + 1);
            cues.Add(cue);

            var scenario = index % 10;
            var words = scenario switch
            {
                0 => [],
                1 => new[] { Word($"before-{index}", start - .079, start - .001) },
                2 => new[] { Word($"after-{index}", end + .001, end + .079) },
                3 => new[] { Word($"inside-{index}", start + duration * .2, start + duration * .8) },
                4 => new[] { Word($"cross-start-{index}", start - .04, start + Math.Max(.001, duration * .25)) },
                5 => new[] { Word($"cross-end-{index}", end - Math.Max(.001, duration * .25), end + .04) },
                6 => new[]
                {
                    Word($"late-{index}", start + duration * .55, start + duration * .85),
                    Word($"early-{index}", start + duration * .1, start + duration * .35),
                },
                7 => new[] { Word($"touch-start-{index}", start - .04, start) },
                8 => new[] { Word($"touch-end-{index}", end, end + .04) },
                _ => new[] { Word($"sub-sample-{index}", end - .2 / SampleRate, end + .02) },
            };
            foreach (var word in words) segments.Add(Segment(word));
            cursor = end;
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var mapped = EditorSpeechAnalysisDocument.MapToCues(Analysis(segments), cues);
        var build = BuildWholeCueMethod();
        for (var index = 0; index < cues.Count; index++)
        {
            var cue = cues[index];
            var timing = mapped[index];
            Equal(cue.Id, timing.CueId, "random mapping changed cue order or identity");
            True(timing.Words.All(word => word.End > cue.Start - .500001 && word.Start < cue.End + .500001),
                "random mapping escaped the bounded ASR ownership halo");
            var manifest = InvokeBuild(build, cue, timing);
            AssertManifestInvariants(cue, manifest, "random cue " + index);

            var source = StringProperty(manifest, "TimingSource");
            Equal("srt-fallback", source, "random cue did not keep SRT as timing owner");
            Equal(cue.Start, DoubleProperty(manifest, "VoiceStart"), "ASR changed SRT start");
            Equal(cue.End, DoubleProperty(manifest, "VoiceEnd"), "ASR changed SRT end");
        }
        watch.Stop();
        True(watch.Elapsed < TimeSpan.FromSeconds(10),
            $"randomized {cueCount}-cue mapping/build exceeded 10 seconds: {watch.Elapsed}");
        return Task.CompletedTask;
    }

    private static Task VerifyMalformedTimingAsync()
    {
        var build = BuildWholeCueMethod();
        var valid = Cue("valid", 2, 3);
        ExpectInvalid(build, Cue("zero", 2, 2), Timing(Cue("zero", 2, 2), []), "zero-length SRT");
        var subSample = Cue("sub-sample", 2, 2 + .4 / SampleRate);
        ExpectInvalid(build, subSample, Timing(subSample, []), "sub-sample SRT");
        ExpectInvalid(build, valid, Timing(valid, []) with { CueId = "stale-owner" }, "stale cue owner");
        ExpectInvalid(build, valid, Timing(valid, []) with { CueStart = 1.9 }, "stale cue start");
        ExpectInvalid(build, valid, Timing(valid, []) with { CueEnd = 3.1 }, "stale cue end");
        var punctuation = valid with { Id = "punctuation", VietnameseText = "..." };
        ExpectInvalid(build, punctuation, Timing(punctuation, []), "punctuation-only voice cue");
        var emoji = valid with { Id = "emoji", VietnameseText = "🔥" };
        ExpectInvalid(build, emoji, Timing(emoji, []), "emoji-only voice cue");
        return Task.CompletedTask;
    }

    private static Task VerifyOcrFlickerPlannerAsync()
    {
        var planner = typeof(EditorTtsRequest).Assembly
            .GetType("BiliSubStudio.Core.Editor.EditorVoiceCuePlanner")!
            .GetMethod("Build", BindingFlags.Static | BindingFlags.Public)!;
        var removeUnspeakable = typeof(EditorTtsRequest).Assembly
            .GetType("BiliSubStudio.Core.Editor.EditorVoiceCuePlanner")!
            .GetMethod("RemoveUnspeakable", BindingFlags.Static | BindingFlags.Public)!;
        var contentMatrix = new[]
        {
            Cue("spoken-a", 1, 2) with { VietnameseText = "Câu thật." },
            Cue("dot", 2, 3) with { VietnameseText = "." },
            Cue("ellipsis", 3, 4) with { VietnameseText = "..." },
            Cue("hash", 4, 5) with { VietnameseText = "##" },
            Cue("question", 5, 6) with { VietnameseText = "?" },
            Cue("bang", 6, 7) with { VietnameseText = "!" },
            Cue("emoji", 7, 8) with { VietnameseText = "🔥" },
            Cue("invisible", 8, 9) with { VietnameseText = "\u200B" },
            Cue("number", 9, 10) with { VietnameseText = "5.000" },
            Cue("unicode", 10, 11) with { VietnameseText = "你好" },
            Cue("spoken-b", 11, 12) with { VietnameseText = "🔥 vẫn có lời" },
        };
        var contentPlanned = (IReadOnlyList<EditorSubtitleCue>)removeUnspeakable.Invoke(null, [contentMatrix])!;
        True(contentPlanned.Select(cue => cue.Id).SequenceEqual(["spoken-a", "number", "unicode", "spoken-b"]),
            "voice content filter removed speech or retained punctuation/symbol artifacts");
        Equal(11, contentMatrix.Length, "voice filter mutated the source SRT cue list");
        var noSpeech = (IReadOnlyList<EditorSubtitleCue>)removeUnspeakable.Invoke(null,
            [contentMatrix.Skip(1).Take(7).ToArray()])!;
        Equal(0, noSpeech.Count, "all-symbol voice plan was not empty");
        var artifact = new[]
        {
            Cue("a-long", 10, 11.067) with { VietnameseText = "Không sai, ta phải đi rồi." },
            Cue("b", 11.033, 12.167) with { VietnameseText = "Đúng, ta phải đi." },
            Cue("a-flash", 11.067, 11.1) with { VietnameseText = "Không sai ta phải đi rồi" },
            Cue("next", 17, 18) with { VietnameseText = "Câu tiếp theo" },
        };
        var planned = (IReadOnlyList<EditorSubtitleCue>)planner.Invoke(null, [artifact])!;
        Equal(2, planned.Count, "OCR A/B/A flicker was not collapsed");
        Equal("b", planned[0].Id, "planner did not keep the longest stable OCR frame");
        Equal(10d, planned[0].Start, "collapsed OCR start drifted");
        Equal(12.167d, planned[0].End, "collapsed OCR end drifted");
        Equal("Đúng, ta phải đi.", planned[0].VietnameseText, "collapsed OCR chose unstable text");

        var legitimateRepeat = new[]
        {
            Cue("repeat-1", 20, 21) with { VietnameseText = "Không." },
            Cue("repeat-2", 21, 22) with { VietnameseText = "Không." },
        };
        var preserved = (IReadOnlyList<EditorSubtitleCue>)planner.Invoke(null, [legitimateRepeat])!;
        Equal(2, preserved.Count, "normal repeated dialogue was incorrectly removed");

        var fuzzyArtifact = new[]
        {
            Cue("stable", 30, 31.133) with { VietnameseText = "ta không có hứng thú." },
            Cue("flash", 31.133, 31.233) with { VietnameseText = "Ta không hứng thú." },
            Cue("next", 32, 33) with { VietnameseText = "Ngông cuồng!" },
        };
        var fuzzyPlanned = (IReadOnlyList<EditorSubtitleCue>)planner.Invoke(null, [fuzzyArtifact])!;
        Equal(2, fuzzyPlanned.Count, "near-duplicate OCR flash was still sent to voice");
        Equal("stable", fuzzyPlanned[0].Id, "fuzzy flicker did not retain the stable caption");
        Equal(31.233d, fuzzyPlanned[0].End, "fuzzy flicker coverage was not preserved");

        var continuation = new[]
        {
            Cue("lead", 40, 40.134) with { VietnameseText = "Ở Bắc Vực," },
            Cue("tail", 40.134, 43) with { VietnameseText = "là mấy đại tộc thế gia kia." },
        };
        var continuationPlanned = (IReadOnlyList<EditorSubtitleCue>)planner.Invoke(null, [continuation])!;
        Equal(2, continuationPlanned.Count, "distinct contiguous sentence fragments were collapsed");

        return Task.CompletedTask;
    }

    private static MethodInfo BuildWholeCueMethod() => typeof(EditorTtsRequest).Assembly
        .GetType("BiliSubStudio.Core.Editor.LocalTtsService")!
        .GetMethod("BuildWholeCue", BindingFlags.Static | BindingFlags.NonPublic)!;

    private static object InvokeBuild(MethodInfo build, EditorSubtitleCue cue, EditorCueSpeechTiming timing) =>
        build.Invoke(null, [cue, "ngoc_huyen", timing])!;

    private static void ExpectInvalid(MethodInfo build, EditorSubtitleCue cue, EditorCueSpeechTiming timing, string label)
    {
        try
        {
            InvokeBuild(build, cue, timing);
            throw new InvalidOperationException(label + " was accepted");
        }
        catch (TargetInvocationException error) when (error.InnerException is InvalidDataException) { }
    }

    private static void AssertManifestInvariants(EditorSubtitleCue cue, object manifest, string label)
    {
        Equal(cue.Id, StringProperty(manifest, "Id"), label + " identity");
        Equal(cue.Start, DoubleProperty(manifest, "CueStart"), label + " cue start");
        Equal(cue.End, DoubleProperty(manifest, "CueEnd"), label + " cue end");
        var start = DoubleProperty(manifest, "VoiceStart");
        var end = DoubleProperty(manifest, "VoiceEnd");
        True(double.IsFinite(start) && double.IsFinite(end), label + " produced non-finite voice time");
        True(start >= cue.Start && end <= cue.End, label + " escaped the SRT cue window");
        True(HasSampleRange(start, end), label + " produced an empty PCM window");
        Equal("srt-fallback", StringProperty(manifest, "TimingSource"),
            label + " did not keep SRT as timing owner");
    }

    private static bool HasSampleRange(double start, double end) =>
        double.IsFinite(start) && double.IsFinite(end) && end > start
        && Math.Round(end * SampleRate) > Math.Round(start * SampleRate);

    private static EditorSubtitleCue Cue(string id, double start, double end, int number = 1) =>
        new(id, number.ToString(), string.Empty, start, end, "source", "Lời Việt để kiểm tra voice");

    private static EditorWordTiming Word(string text, double start, double end) => new(text, start, end, .9);

    private static EditorSpeechSegment Segment(EditorWordTiming word) =>
        new(word.Start, word.End, word.Text, 0, 0, [word], "uncertain", 0, 0);

    private static EditorSpeechAnalysis Analysis(IEnumerable<EditorSpeechSegment> segments) => new(
        EditorSpeechAnalysisDocument.CurrentSchema,
        new string('a', 64),
        "timing-property-fixture",
        new string('b', 40),
        "cpu",
        "int8",
        .5,
        segments.ToArray());

    private static EditorCueSpeechTiming Timing(EditorSubtitleCue cue, IReadOnlyList<EditorWordTiming> words)
    {
        var speechStart = words.Count == 0 ? cue.Start : words.Min(word => word.Start);
        var speechEnd = words.Count == 0 ? cue.End : words.Max(word => word.End);
        return new(cue.Id, cue.Start, cue.End, speechStart, speechEnd,
            Math.Max(0, speechStart - cue.Start), Math.Max(0, cue.End - speechEnd),
            words, [], "uncertain", 0, 0);
    }

    private static string StringProperty(object value, string name) =>
        value.GetType().GetProperty(name)!.GetValue(value)!.ToString()!;

    private static double DoubleProperty(object value, string name) =>
        (double)value.GetType().GetProperty(name)!.GetValue(value)!;

    private static void Near(double expected, double actual, double tolerance, string message)
    {
        if (Math.Abs(expected - actual) > tolerance)
            throw new InvalidOperationException($"{message}: expected {expected:R}, actual {actual:R}");
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}: expected {expected}, actual {actual}");
    }
}
