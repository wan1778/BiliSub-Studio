using System.Reflection;
using System.Runtime.CompilerServices;
using BiliSubStudio.Core.Configuration;
using BiliSubStudio.Core.Editor;

namespace BiliSubStudio.Core.ContractTests;

internal static class EditorLicensedVoiceProfileContract
{
    [ModuleInitializer]
    internal static void Install()
    {
        RuntimeHelpers.RunClassConstructor(typeof(Program).TypeHandle);
        var field = typeof(Program).GetField("Tests", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing Core contract list");
        var tests = field.GetValue(null) as List<(string Name, Func<Task> Test)>
            ?? throw new InvalidOperationException("Core contract list type drifted");

        tests.Add(("local VieNeu hashes and whole-cue synthesis stay pinned", VerifyVoiceProfileAsync));
        Replace(tests,
            "editor project persists, isolates source drift and quarantines corrupt state",
            "editor project persists, invalidates stale TTS, isolates source drift and quarantines corrupt state",
            VerifyEditorProjectAsync);
        tests.Add(("voice dropdown uses exact canonical IDs and ValidateRequest passes for all local voices", VerifyVoiceRegistryAsync));
        tests.Add(("Whisper cue mapping stays exact and indexed for large Voice projects", VerifyIndexedCueMappingAsync));
    }

    private static void Replace(
        List<(string Name, Func<Task> Test)> tests,
        string oldName,
        string newName,
        Func<Task> test)
    {
        var index = tests.FindIndex(item => item.Name == oldName);
        if (index < 0) throw new InvalidOperationException($"contract entry was not found: {oldName}");
        tests[index] = (newName, test);
    }

    private static Task VerifyVoiceProfileAsync()
    {
        var assembly = typeof(VideoEditorService).Assembly;
        var installer = assembly.GetType("BiliSubStudio.Core.Editor.LocalTtsInstaller")!;
        static string Constant(Type type, string name) => type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.GetRawConstantValue()!.ToString()!;
        Equal("vieneu-tts", Constant(installer, "Engine"));
        Equal("vieneu-v3-turbo-1", Constant(installer, "EngineVersion"));
        Equal("61b85e3d937fbbacb387714180e8182823512523", Constant(installer, "ModelRevision"));
        Equal("whole-cue-vieneu-natural-first-v1", Constant(installer, "TimingAlgorithm"));
        Equal("vieneu_ngoc_huyen", Constant(installer, "Voice"));
        var worker = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "vieneu-worker.py"));
        True(worker.Contains(Constant(installer, "ModelRevision"), StringComparison.Ordinal), "worker model revision drifted");
        True(worker.Contains(Constant(installer, "TimingAlgorithm"), StringComparison.Ordinal), "worker voice timing revision drifted");
        True(worker.Contains("ENGINE_VERSION = \"" + Constant(installer, "EngineVersion") + "\"", StringComparison.Ordinal),
            "worker engine version drifted");
        True(!worker.Contains("Kokoro", StringComparison.Ordinal) && !worker.Contains("synth_sine", StringComparison.Ordinal)
            && !worker.Contains("nghi-tts", StringComparison.Ordinal) && !worker.Contains("nghimestudio/nghitts", StringComparison.Ordinal),
            "retired or synthetic runtime entered production");
        var service = assembly.GetType("BiliSubStudio.Core.Editor.LocalTtsService")!;
        var method = service.GetMethod("BuildWholeCue", BindingFlags.Static | BindingFlags.NonPublic)!;
        var cue = new EditorSubtitleCue("whole-cue-0001", "37", "00:00:01,000 --> 00:00:05,000", 1, 5,
            "你好", "Xin chào đạo hữu. Hôm nay trời thật đẹp!");
        var timing = new EditorCueSpeechTiming(cue.Id, 1, 5, 1.5, 3.5, .5, 1.5,
            [new EditorWordTiming("你好", 1.5, 2, .9), new EditorWordTiming("朋友", 2.5, 3.5, .9)],
            [new EditorPauseTiming(2, 2.5)], "uncertain", 0, 0);
        var whole = method.Invoke(null, [cue, "vieneu_ngoc_huyen", timing])!;
        var type = whole.GetType();
        Equal(cue.Id, type.GetProperty("Id")!.GetValue(whole));
        Equal(1d, type.GetProperty("CueStart")!.GetValue(whole));
        Equal(5d, type.GetProperty("CueEnd")!.GetValue(whole));
        Equal(1d, type.GetProperty("VoiceStart")!.GetValue(whole));
        Equal(5d, type.GetProperty("VoiceEnd")!.GetValue(whole));
        Equal("srt-fallback", type.GetProperty("TimingSource")!.GetValue(whole));
        Equal(cue.VietnameseText, type.GetProperty("Text")!.GetValue(whole));
        True(type.GetProperty("Groups") is null, "whole cue must not encode Whisper pause groups");
        var fallback = method.Invoke(null, [cue, "vieneu_ngoc_huyen", timing with { Words = [] }])!;
        Equal(1d, type.GetProperty("VoiceStart")!.GetValue(fallback));
        Equal(5d, type.GetProperty("VoiceEnd")!.GetValue(fallback));
        Equal("srt-fallback", type.GetProperty("TimingSource")!.GetValue(fallback));
        var boundaryHalo = method.Invoke(null, [cue, "vieneu_ngoc_huyen", timing with
        {
            Words = [new EditorWordTiming("neighbor", .92, .987, .9)],
            Pauses = [], SpeechStart = .92, SpeechEnd = .987,
        }])!;
        Equal(1d, type.GetProperty("VoiceStart")!.GetValue(boundaryHalo));
        Equal(5d, type.GetProperty("VoiceEnd")!.GetValue(boundaryHalo));
        Equal("srt-fallback", type.GetProperty("TimingSource")!.GetValue(boundaryHalo));
        var partialOverlap = method.Invoke(null, [cue, "vieneu_ngoc_huyen", timing with
        {
            Words = [new EditorWordTiming("crossing", .95, 1.2, .9)],
            Pauses = [], SpeechStart = .95, SpeechEnd = 1.2,
        }])!;
        Equal(1d, type.GetProperty("VoiceStart")!.GetValue(partialOverlap));
        Equal(5d, type.GetProperty("VoiceEnd")!.GetValue(partialOverlap));
        Equal("srt-fallback", type.GetProperty("TimingSource")!.GetValue(partialOverlap));
        VerifyVoiceWavFrames(service);
        VerifyNativeRateMetadata(service);
        VerifySentenceGroupMetadata(service);
        var outside = method.Invoke(null, [cue, "vieneu_ngoc_huyen", timing with
        {
            Words = [new EditorWordTiming("outside", 6, 7, .9)],
            Pauses = []
        }])!;
        Equal("srt-fallback", type.GetProperty("TimingSource")!.GetValue(outside));
        foreach (var invalid in new[] { timing with { CueId = "different-cue" } })
        {
            try
            {
                method.Invoke(null, [cue, "vieneu_ngoc_huyen", invalid]);
                throw new InvalidOperationException("Mismatched cue timing was silently accepted");
            }
            catch (TargetInvocationException error) when (error.InnerException is InvalidDataException) { }
        }
        return Task.CompletedTask;
    }

    private static Task VerifyIndexedCueMappingAsync()
    {
        var segments = Enumerable.Range(0, 12_280).Select(index =>
        {
            var start = index * 2.4;
            var wordCount = index < 3_927 ? 6 : 5;
            var words = Enumerable.Range(0, wordCount)
                .Select(word => new EditorWordTiming($"w{index}-{word}", start + word * .32, start + word * .32 + .24, .9))
                .Reverse()
                .ToArray();
            return new EditorSpeechSegment(start, start + 2, string.Empty, 0, 0, words,
                index % 2 == 0 ? "male_like" : "female_like", .8, 150);
        }).Reverse().ToArray();
        var analysis = new EditorSpeechAnalysis(
            EditorSpeechAnalysisDocument.CurrentSchema,
            new string('a', 64),
            "indexed fixture",
            new string('b', 40),
            "cpu",
            "int8",
            .5,
            segments);
        var cues = Enumerable.Range(0, 11_306)
            .Select(index =>
            {
                var start = index * 2.5;
                return new EditorSubtitleCue($"cue-{index}", (index + 1).ToString(), string.Empty,
                    start, start + 1.8, "source", "Tiếng Việt");
            }).ToArray();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var mapped = EditorSpeechAnalysisDocument.MapToCues(analysis, cues);
        watch.Stop();
        Equal(cues.Length, mapped.Count);
        Equal(6, mapped[0].Words.Count);
        Equal("w0-0", mapped[0].Words[0].Text);
        Equal("w0-5", mapped[0].Words[^1].Text);
        Equal(0d, mapped[0].SpeechStart);
        Equal(1.84d, mapped[0].SpeechEnd);
        Equal("male_like", mapped[0].VoiceClass);
        True(mapped.SelectMany(timing => timing.Words).All(word => word.End > word.Start),
            "indexed mapping returned invalid words");
        True(watch.Elapsed < TimeSpan.FromSeconds(10),
            $"indexed mapping regressed to dispatcher-blocking runtime: {watch.Elapsed}");

        var boundaryCue = new EditorSubtitleCue("boundary-cue", "3083", string.Empty,
            7943.333, 7945.4, "source", "Buông ta ra! Ca ca!");
        var boundaryAnalysis = analysis with
        {
            Segments = [new EditorSpeechSegment(7941.88, 7943.32, "neighbor", 0, 0,
                [new EditorWordTiming("呢", 7943.24, 7943.32, .24)], "uncertain", 0, 0)]
        };
        var boundaryMapped = EditorSpeechAnalysisDocument.MapToCues(boundaryAnalysis, [boundaryCue]);
        Equal(1, boundaryMapped[0].Words.Count);
        Equal(7943.24d, boundaryMapped[0].SpeechStart);
        Equal(7943.32d, boundaryMapped[0].SpeechEnd);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            EditorSpeechAnalysisDocument.MapToCues(analysis, cues, cancelled.Token);
            throw new InvalidOperationException("cancelled cue mapping continued");
        }
        catch (OperationCanceledException) { }
        return Task.CompletedTask;
    }

    private static void VerifyVoiceWavFrames(Type service)
    {
        var root = Path.Combine(Path.GetTempPath(), "bilisub-wav-frames-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // Header/frame-count fixture only, not a speech-generation test.
            var path = Path.Combine(root, "pcm-with-junk.wav");
            using (var stream = File.Create(path))
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII))
            {
                writer.Write("RIFF"u8.ToArray()); writer.Write(56u); writer.Write("WAVE"u8.ToArray());
                writer.Write("fmt "u8.ToArray()); writer.Write(16u);
                writer.Write((ushort)1); writer.Write((ushort)1); writer.Write(22050u); writer.Write(44100u);
                writer.Write((ushort)2); writer.Write((ushort)16);
                writer.Write("JUNK"u8.ToArray()); writer.Write(4u); writer.Write(0u);
                writer.Write("data"u8.ToArray()); writer.Write(8u);
                writer.Write((short)1); writer.Write((short)-1); writer.Write((short)2); writer.Write((short)-2);
            }
            var read = service.GetMethod("ReadClipFrames", BindingFlags.Static | BindingFlags.NonPublic)!;
            Equal(4L, read.Invoke(null, [path, 22050]));
            using (var truncated = new FileStream(path, FileMode.Open, FileAccess.Write)) truncated.SetLength(62);
            try
            {
                read.Invoke(null, [path, 22050]);
                throw new InvalidOperationException("Truncated PCM clip was accepted");
            }
            catch (TargetInvocationException error) when (error.InnerException is InvalidDataException) { }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void VerifyNativeRateMetadata(Type service)
    {
        // Metadata-only fixtures. No speech is synthesized or claimed here.
        var cueType = service.GetNestedType("TtsWorkerCue", BindingFlags.NonPublic)!;
        var validate = service.GetMethod("ValidateNativeSynthesis", BindingFlags.Static | BindingFlags.NonPublic)!;
        var json = new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower };
        object Cue(Dictionary<string, object> values) => System.Text.Json.JsonSerializer.Deserialize(
            System.Text.Json.JsonSerializer.Serialize(values), cueType, json)!;
        var natural = new Dictionary<string, object>
        {
            ["fit_method"] = "vieneu-natural", ["raw_duration"] = 55125d / 22050, ["frames"] = 55875L,
            ["source_frames"] = 55125L, ["generated_frames"] = 55125L,
            ["trimmed_silence_frames"] = 0L, ["padding_frames"] = 750L,
            ["base_length_scale"] = 1d, ["length_scale"] = 1d, ["synthesis_attempts"] = 3,
            ["synthesis_calls"] = 3, ["cache_hit"] = false,
            ["native_reference_frames"] = 55125L, ["group_native_frames"] = 0L,
            ["actual_speed_factor"] = 1d, ["tempo_factor"] = 0d, ["tempo_input_frames"] = 0L,
            ["tempo_attempts"] = 0, ["status"] = "fit", ["timing_source"] = "srt-fallback",
        };
        Equal(false, validate.Invoke(null, [Cue(natural), 55875L, false, 22050, 0]));
        var tempo = new Dictionary<string, object>(natural)
        {
            ["fit_method"] = "vieneu-atempo", ["raw_duration"] = 3.5d,
            ["source_frames"] = 77175L, ["generated_frames"] = 55125L,
            ["native_reference_frames"] = 77175L, ["actual_speed_factor"] = 1.4d,
            ["tempo_factor"] = 1.4d, ["tempo_input_frames"] = 77175L, ["tempo_attempts"] = 1,
            ["synthesis_attempts"] = 1, ["synthesis_calls"] = 1, ["status"] = "review",
        };
        Equal(true, validate.Invoke(null, [Cue(tempo), 55875L, false, 22050, 0]));
        var tooFast = new Dictionary<string, object>(tempo)
        {
            ["source_frames"] = 88200L, ["native_reference_frames"] = 88200L,
            ["actual_speed_factor"] = 1.6d, ["tempo_factor"] = 1.6d,
            ["tempo_input_frames"] = 88200L, ["raw_duration"] = 4d,
        };
        ThrowsInvalid(validate, [Cue(tooFast), 55875L, false, 22050, 0], "VieNeu tempo over 1.5x was accepted");
        var micro = new Dictionary<string, object>(tempo)
        {
            ["raw_duration"] = 23800d / 22050, ["frames"] = 14332L, ["target_frames"] = 14332L,
            ["source_frames"] = 23800L, ["generated_frames"] = 14000L,
            ["trimmed_silence_frames"] = 0L, ["padding_frames"] = 332L,
            ["native_reference_frames"] = 23800L, ["actual_speed_factor"] = 1.7d,
            ["tempo_factor"] = 1.7d, ["tempo_input_frames"] = 23800L, ["synthesis_attempts"] = 2,
            ["synthesis_calls"] = 2,
        };
        Equal(true, validate.Invoke(null, [Cue(micro), 14332L, false, 22050, 0]));
        var microTooFast = new Dictionary<string, object>(micro)
        {
            ["source_frames"] = 29250L, ["native_reference_frames"] = 29250L,
            ["generated_frames"] = 11250L, ["padding_frames"] = 3082L,
            ["actual_speed_factor"] = 3.1d, ["tempo_factor"] = 3.1d,
            ["tempo_input_frames"] = 29250L, ["raw_duration"] = 29250d / 22050,
        };
        ThrowsInvalid(validate, [Cue(microTooFast), 14332L, false, 22050, 0], "VieNeu micro tempo over 3.0x was accepted");
        var slow = new Dictionary<string, object>(tempo)
        {
            ["source_frames"] = 44100L, ["native_reference_frames"] = 44100L,
            ["actual_speed_factor"] = .8d, ["tempo_factor"] = .8d,
            ["tempo_input_frames"] = 44100L, ["raw_duration"] = 2d,
        };
        Equal(true, validate.Invoke(null, [Cue(slow), 55875L, false, 22050, 0]));
        var tooSlow = new Dictionary<string, object>(slow)
        {
            ["source_frames"] = 33075L, ["native_reference_frames"] = 33075L,
            ["actual_speed_factor"] = .6d, ["tempo_factor"] = .6d,
            ["tempo_input_frames"] = 33075L, ["raw_duration"] = 1.5d,
        };
        ThrowsInvalid(validate, [Cue(tooSlow), 55875L, false, 22050, 0], "VieNeu stretch below 1/1.4x was accepted");
        var degenerate = new Dictionary<string, object>(tempo)
        {
            ["generated_frames"] = 88200L, ["padding_frames"] = 0L, ["frames"] = 88200L,
            ["tempo_factor"] = 1d,
        };
        ThrowsInvalid(validate, [Cue(degenerate), 88200L, false, 22050, 0], "Degenerate VieNeu tempo was accepted");
        var naturalMismatch = new Dictionary<string, object>(natural)
        {
            ["synthesis_attempts"] = 1, ["synthesis_calls"] = 1,
            ["native_reference_frames"] = 55126L,
        };
        ThrowsInvalid(validate, [Cue(naturalMismatch), 55875L, false, 22050, 0],
            "Natural attempt-1 native mismatch was accepted");
        var overflow = new Dictionary<string, object>(natural)
        {
            ["frames"] = 66900L, ["generated_frames"] = 66150L, ["source_frames"] = 66150L,
            ["native_reference_frames"] = 66150L, ["overflow_frames"] = 11025L,
        };
        Equal(false, validate.Invoke(null, [Cue(overflow), 55875L, false, 22050, 0]));
        var silence = new Dictionary<string, object>
        {
            ["fit_method"] = "silence-fallback", ["frames"] = 55875L, ["target_frames"] = 55875L,
            ["source_frames"] = 0L, ["generated_frames"] = 0L, ["trimmed_silence_frames"] = 0L,
            ["padding_frames"] = 55875L, ["synthesis_attempts"] = 1, ["synthesis_calls"] = 1,
            ["cache_hit"] = false, ["native_reference_frames"] = 0L, ["group_native_frames"] = 0L,
            ["actual_speed_factor"] = 0d, ["tempo_factor"] = 0d, ["tempo_input_frames"] = 0L,
            ["tempo_attempts"] = 0, ["status"] = "review", ["voice_review"] = true,
            ["timing_source"] = "srt-fallback",
        };
        Equal(true, validate.Invoke(null, [Cue(silence), 55875L, false, 22050, 0]));
        foreach (var invalid in new[]
        {
            new Dictionary<string, object>(natural) { ["fit_method"] = "piper-length-scale" },
            new Dictionary<string, object>(natural) { ["fit_method"] = "piper-atempo" },
            new Dictionary<string, object>(natural) { ["generated_frames"] = 0L, ["padding_frames"] = 55875L },
            new Dictionary<string, object>(natural) { ["base_length_scale"] = 0d },
            new Dictionary<string, object>(natural) { ["length_scale"] = .299 },
            new Dictionary<string, object>(natural) { ["synthesis_attempts"] = 0 },
            new Dictionary<string, object>(natural) { ["synthesis_attempts"] = 13, ["synthesis_calls"] = 13 },
            new Dictionary<string, object>(natural) { ["cache_hit"] = true, ["synthesis_calls"] = 1 },
            new Dictionary<string, object>(tempo) { ["tempo_input_frames"] = 77174L },
            new Dictionary<string, object>(tempo) { ["tempo_factor"] = 1.5d },
            new Dictionary<string, object>(tempo) { ["tempo_attempts"] = 0 },
            new Dictionary<string, object>(tempo) { ["tempo_attempts"] = 7 },
        })
        {
            ThrowsInvalid(validate, [Cue(invalid), 55875L, false, 22050, 0],
                "Invalid VieNeu native synthesis metadata was accepted");
        }
    }

    private static void VerifySentenceGroupMetadata(Type service)
    {
        var workerType = service.GetNestedType("TtsWorkerCue", BindingFlags.NonPublic)!;
        var manifestType = service.GetNestedType("TtsCueManifest", BindingFlags.NonPublic)!;
        var validate = service.GetMethod("ValidateSentenceGroupWindows", BindingFlags.Static | BindingFlags.NonPublic)!;
        var json = new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower };
        var starts = new[] { 14.4, 15.2, 16.4, 17.2 };
        var ends = new[] { 15.2, 16.4, 17.2, 18.4 };
        var texts = new[] { "Có đệ tử bầu bạn,", "sư phụ không còn cô độc,",
            "đương nhiên ngày nào cũng vui vẻ,", "luôn được hạnh phúc vây quanh." };
        var frames = new long[] { 17640, 20812, 24866, 24882 };
        var actual = Array.CreateInstance(workerType, 4);
        var expected = Array.CreateInstance(manifestType, 4);
        var cursor = 317520L;
        for (var index = 0; index < 4; index++)
        {
            var values = new Dictionary<string, object>
            {
                ["id"] = $"cue-{index + 9}", ["timing_source"] = "sentence-group",
                ["clip_start_sample"] = cursor, ["target_frames"] = frames[index],
                ["generated_frames"] = frames[index], ["native_reference_frames"] = 119070L,
                ["group_native_frames"] = 119070L, ["actual_speed_factor"] = 1.35d,
            };
            actual.SetValue(System.Text.Json.JsonSerializer.Deserialize(
                System.Text.Json.JsonSerializer.Serialize(values), workerType, json), index);
            expected.SetValue(Activator.CreateInstance(manifestType,
                [$"cue-{index + 9}", starts[index], ends[index], "vieneu_ngoc_huyen", texts[index],
                    starts[index] + .05, ends[index] - .05, "whisper"]), index);
            cursor += frames[index];
        }
        validate.Invoke(null, [actual, expected, 22050]);
        foreach (var badSpeed in new[] { 2.0d, 0.5d })
        {
            var badNative = (long)Math.Round(badSpeed * (17640L + 20812L + 24866L + 24882L));
            var fast = Array.CreateInstance(workerType, 4);
            var position = 317520L;
            for (var index = 0; index < 4; index++)
            {
                fast.SetValue(System.Text.Json.JsonSerializer.Deserialize(
                    System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
                    {
                        ["id"] = $"cue-{index + 9}", ["timing_source"] = "sentence-group",
                        ["clip_start_sample"] = position, ["target_frames"] = frames[index],
                        ["generated_frames"] = frames[index], ["native_reference_frames"] = badNative,
                        ["group_native_frames"] = badNative, ["actual_speed_factor"] = badSpeed,
                    }), workerType, json), index);
                position += frames[index];
            }
            ThrowsInvalid(validate, [fast, expected, 22050], $"Invalid VieNeu group speed {badSpeed}x was accepted");
        }
        var broken = Array.CreateInstance(workerType, 4);
        Array.Copy(actual, broken, 4);
        broken.SetValue(System.Text.Json.JsonSerializer.Deserialize(
            System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["id"] = "cue-11", ["timing_source"] = "sentence-group",
                ["clip_start_sample"] = 355973L, ["target_frames"] = frames[2],
                ["generated_frames"] = frames[2], ["native_reference_frames"] = 119070L,
                ["group_native_frames"] = 119070L, ["actual_speed_factor"] = 1.35d,
            }), workerType, json), 2);
        ThrowsInvalid(validate, [broken, expected, 22050], "Discontinuous sentence-group timing was accepted");
    }

    private static void ThrowsInvalid(MethodInfo method, object?[] arguments, string message)
    {
        try
        {
            method.Invoke(null, arguments);
            throw new InvalidOperationException(message);
        }
        catch (TargetInvocationException error) when (error.InnerException is InvalidDataException) { }
    }
    private static async Task VerifyEditorProjectAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bilisub-csharp-vais-project-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var paths = AppPaths.FromRoot(root);
            paths.EnsureBootstrapDirectories();
            var video = Path.Combine(root, "source.mp4");
            await File.WriteAllBytesAsync(video, [1, 2, 3]);
            var store = new EditorProjectStore(paths);
            var created = await store.LoadOrCreateAsync(video, 1920, 1080, 120, CancellationToken.None);
            Equal(0, created.Regions.Count);

            var region = new EditRegion(.1, .2, .3, .25, "blur", 18, false, 1, 5, "stable-region");
            var srt = Path.Combine(root, "source.zh.srt");
            await File.WriteAllTextAsync(srt, "1\n00:00:01,000 --> 00:00:02,000\n你好\n");
            var subtitle = await EditorSubtitleDocument.LoadAsync(srt, CancellationToken.None);
            var subtitleProject = new EditorSubtitleProject(
                subtitle.Path,
                subtitle.Size,
                subtitle.LastWriteUtcTicks,
                subtitle.Sha256,
                [subtitle.Cues[0] with { VietnameseText = "Xin chào" }],
                new EditorSubtitlePlacement(.1, .72, .8, .18),
                "Dịch Trung Tu Tiên",
                TranslationSkillBundle.BuiltInSha256,
                Path.Combine(root, "source.vi.srt"));

            var speechPath = Path.Combine(root, "speech.json");
            var speechAnalysis = new EditorSpeechAnalysis(
                EditorSpeechAnalysisDocument.CurrentSchema, new string('b', 64), "fixture Whisper",
                "536b0662742c02347bc0e980a01041f333bce120", "cpu", "int8", .75, []);
            var speechSha = await EditorSpeechAnalysisDocument.SaveAsync(speechPath, speechAnalysis, CancellationToken.None);
            var voicePath = Path.Combine(root, "voice.flac");
            await File.WriteAllBytesAsync(voicePath, Enumerable.Repeat((byte)1, 128).ToArray());
            var ttsManifest = Path.Combine(root, "tts-result.json");
            var installer = typeof(VideoEditorService).Assembly.GetType("BiliSubStudio.Core.Editor.LocalTtsInstaller")!;
            var revision = installer.GetField("VoiceRevision", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!.ToString()!;
            await File.WriteAllTextAsync(ttsManifest, System.Text.Json.JsonSerializer.Serialize(new
            {
                schema = 2, sample_rate = 22050, voice_revision = revision,
                cues = new[]
                {
                    new { id = subtitle.Cues[0].Id, timing_source = "srt-fallback", status = "review",
                        clip_start_sample = 22050L, target_frames = 22050L },
                },
                master = new { path = voicePath, sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(voicePath))) },
            }));
            var ttsManifestSha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(ttsManifest)));

            var validTts = new EditorTtsProject(
                "complete", "vieneu-tts", "vieneu-v3-turbo-1",
                "vieneu_ngoc_huyen", "vieneu_ngoc_huyen",
                ttsManifest, ttsManifestSha, new EditorVoiceTrack(voicePath, 0, 120), 1, 1,
                [new EditorTtsCueWindow(subtitle.Cues[0].Id, 1, 2, "srt-fallback", "review")]);
            await store.SaveAsync(created with
            {
                FileName = "episode-edited.mp4",
                Regions = [region],
                Subtitle = subtitleProject,
                Audio = new EditorAudioSettings("duck", .35),
                Asr = new EditorAsrProject("complete", "fixture ASR", "536b0662742c02347bc0e980a01041f333bce120",
                    "cpu", "int8", srt, 1, .75),
                Speech = new EditorSpeechProject("complete", "fixture Whisper", "536b0662742c02347bc0e980a01041f333bce120",
                    "cpu", "int8", speechPath, speechSha, 1, 2, .75),
                Tts = validTts,
                VoiceOverrides = new Dictionary<string, string> { [subtitle.Cues[0].Id] = "female" },
            }, CancellationToken.None);

            var reopened = await store.LoadOrCreateAsync(video, 1920, 1080, 120, CancellationToken.None);
            Equal("episode-edited.mp4", reopened.FileName);
            Equal(1, reopened.Regions.Count);
            Equal("stable-region", reopened.Regions[0].Id);
            Equal(.1, reopened.Regions[0].X);
            Equal("Xin chào", reopened.Subtitle!.Cues[0].VietnameseText);
            Equal(.72, reopened.Subtitle.Placement.Y);
            Equal("duck", reopened.Audio!.SourceMode);
            Equal(.35, reopened.Audio.SourceGain);
            Equal("complete", reopened.Asr!.Status);
            Equal("cpu", reopened.Asr.Device);
            Equal(.75, reopened.Asr.ProbeRealtimeFactor);
            Equal("complete", reopened.Speech!.Status);
            Equal(speechSha, reopened.Speech.AnalysisSha256);
            Equal("complete", reopened.Tts!.Status);
            Equal("vieneu-tts", reopened.Tts.Engine);
            Equal("srt-fallback", reopened.Tts.CueWindows![0].TimingSource);
            Equal("female", reopened.VoiceOverrides![subtitle.Cues[0].Id]);

            var oldManifest = Path.Combine(root, "old-duration-result.json");
            await File.WriteAllTextAsync(oldManifest, System.Text.Json.JsonSerializer.Serialize(new
            {
                schema = 2, voice_revision = revision[..revision.LastIndexOf(':')] + ":whole-cue-whisper-fit-v3",
                master = new { path = voicePath, sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(voicePath))) },
            }));
            var oldSha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(oldManifest)));
            await store.SaveAsync(reopened with { Tts = validTts with { ManifestPath = oldManifest, ManifestSha256 = oldSha } }, CancellationToken.None);
            var staleTiming = await store.LoadOrCreateAsync(video, 1920, 1080, 120, CancellationToken.None);
            True(staleTiming.Tts is null, "old post-synthesis tempo policy was accepted as native synthesis");
            True(staleTiming.Speech is not null && File.Exists(voicePath) && File.Exists(oldManifest),
                "timing-policy migration removed source timing or old recoverable files");

            // A project created by the retired beta.36 voice path must keep all upstream work
            // but discard only the stale voice track so it cannot play after upgrading to VAIS.
            var legacy = reopened with
            {
                Tts = validTts with
                {
                    Engine = "piper-nghitts",
                    MaleVoice = "legacy-male",
                    FemaleVoice = "legacy-female",
                },
            };
            var projectPath = store.GetProjectPath(video);
            var json = System.Text.Json.JsonSerializer.Serialize(legacy, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower,
                WriteIndented = true,
            });
            await File.WriteAllTextAsync(projectPath, json + "\n");
            var migrated = await store.LoadOrCreateAsync(video, 1920, 1080, 120, CancellationToken.None);
            True(migrated.Tts is null, "retired TTS profile survived project reopen");
            Equal("complete", migrated.Speech!.Status);
            Equal("Xin chào", migrated.Subtitle!.Cues[0].VietnameseText);
            Equal("female", migrated.VoiceOverrides![subtitle.Cues[0].Id]);

            await store.SaveAsync(reopened, CancellationToken.None);
            File.Delete(voicePath);
            var selectivelyRecovered = await store.LoadOrCreateAsync(video, 1920, 1080, 120, CancellationToken.None);
            True(selectivelyRecovered.Tts is null, "missing TTS cache should invalidate only TTS state");
            Equal("complete", selectivelyRecovered.Speech!.Status);
            Equal("episode-edited.mp4", selectivelyRecovered.FileName);

            await File.WriteAllBytesAsync(voicePath, Enumerable.Repeat((byte)1, 128).ToArray());
            await store.SaveAsync(reopened, CancellationToken.None);
            await File.AppendAllTextAsync(video, "changed-source");
            var sourceChanged = await store.LoadOrCreateAsync(video, 1920, 1080, 120, CancellationToken.None);
            Equal("episode-edited.mp4", sourceChanged.FileName);
            Equal(0, sourceChanged.Regions.Count);
            True(sourceChanged.Subtitle is null, "changed source reused old subtitle state");
            True(sourceChanged.Asr is null, "changed source reused old ASR state");
            True(sourceChanged.Speech is null, "changed source reused old Whisper timing");
            True(sourceChanged.Tts is null, "changed source reused old TTS state");
            Equal(0, sourceChanged.VoiceOverrides?.Count ?? 0);
            Equal("keep", sourceChanged.Audio!.SourceMode);
            True(Directory.GetFiles(Path.GetDirectoryName(projectPath)!, Path.GetFileName(projectPath) + ".source-changed-*").Length == 1,
                "source-changed Editor project was not archived");

            await File.WriteAllTextAsync(projectPath, "{broken-json");
            var recovered = await store.LoadOrCreateAsync(video, 1920, 1080, 120, CancellationToken.None);
            Equal(0, recovered.Regions.Count);
            True(Directory.GetFiles(Path.GetDirectoryName(projectPath)!, Path.GetFileName(projectPath) + ".corrupt-*").Length == 1,
                "corrupt editor project was not quarantined");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task VerifyVoiceRegistryAsync()
    {
        var assembly = typeof(VideoEditorService).Assembly;
        var installer = assembly.GetType("BiliSubStudio.Core.Editor.LocalTtsInstaller")
            ?? throw new InvalidOperationException("missing LocalTtsInstaller type");
        var voices = installer.GetField("AvailableVoices", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(null) as System.Collections.IEnumerable
            ?? throw new InvalidOperationException("AvailableVoices not found");
        var list = voices.Cast<object>().Select(x => x?.ToString() ?? string.Empty).ToArray();
        Equal(25, list.Length);
        True(list.Contains("vieneu_ngoc_huyen", StringComparer.Ordinal), "canonical default voice missing from registry");
        True(list.Contains("vieneu_hai_dang", StringComparer.Ordinal), "vieneu slug missing from registry");
        True(list.Distinct(StringComparer.Ordinal).Count() == 25, "voice registry contains duplicate IDs");
        var catalog = assembly.GetType("BiliSubStudio.Core.Editor.TtsVoiceCatalog")
            ?? throw new InvalidOperationException("missing TtsVoiceCatalog type");
        string Fold(string label) => (string)catalog.GetMethod("FoldSlug",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(null, [label])!;
        Equal("hai_dang", Fold("Hải Đăng"));
        Equal("doan_trang", Fold("Đoan Trang"));
        Equal("thien_tam_duc", Fold("Thiền Tâm Đức"));
        Equal("adam_bua", Fold("Adam bựa"));
        True((bool)catalog.GetMethod("IsVieneuVoice",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(null, ["vieneu_hai_dang"])!,
            "vieneu slug not recognized");
        True(!(bool)catalog.GetMethod("IsVieneuVoice",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(null, ["ngoc_huyen"])!,
            "nghi voice misclassified as vieneu");
        Equal("Hải Đăng", (string)catalog.GetMethod("DisplayLabel",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(null, ["vieneu_hai_dang"])!);
        foreach (var v in list)
        {
            True(!string.IsNullOrWhiteSpace(v) && v.All(c => char.IsLetterOrDigit(c) || c == '_'), $"voice ID {v} must be canonical underscore form");
            True(!v.Contains('-'), $"voice ID {v} must not contain hyphen, use underscore canonical");
        }
        var service = assembly.GetType("BiliSubStudio.Core.Editor.LocalTtsService")
            ?? throw new InvalidOperationException("missing LocalTtsService type");
        var validate = service.GetMethod("ValidateRequest", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing ValidateRequest");
        var canonical = installer.GetMethod("CanonicalVoiceId", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException("missing CanonicalVoiceId");
        // UI sends hyphen form, backend must accept via canonicalization
        var hyphen = "vieneu-ngoc-huyen";
        var expectedCanonical = canonical.Invoke(null, new object[] { hyphen })?.ToString() ?? string.Empty;
        Equal("vieneu_ngoc_huyen", expectedCanonical);
        var tmpSource = Path.Combine(Path.GetTempPath(), $"bilisub-voice-reg-{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(tmpSource, new byte[] { 1, 2, 3 });
        try
        {
            // Every registry voice must pass validation (simulates dropdown selection -> request -> ValidateRequest)
            foreach (var vid in list)
            {
                var req = Activator.CreateInstance(typeof(EditorTtsRequest), new object[] { "voice-reg-test", tmpSource, 4.2, new EditorSubtitleSource("C:\\tmp\\a.srt", 10, 1, new string('0', 64), new[] { new EditorSubtitleCue("c1", "1", "00:00:00,000 --> 00:00:01,000", 0, 1, "你好", "Xin chào") }), "C:\\tmp\\analysis.json", new string('a', 64), vid, false });
                try { validate.Invoke(null, new[] { req }); }
                catch (TargetInvocationException ex) when (ex.InnerException is not null) { throw new InvalidOperationException($"ValidateRequest rejected canonical voice {vid}: {ex.InnerException.Message}", ex.InnerException); }
            }
            // Hyphen form from old UI must also pass via canonicalization
            var hyphenReq = Activator.CreateInstance(typeof(EditorTtsRequest), new object[] { "voice-reg-test", tmpSource, 4.2, new EditorSubtitleSource("C:\\tmp\\a.srt", 10, 1, new string('0', 64), new[] { new EditorSubtitleCue("c1", "1", "00:00:00,000 --> 00:00:01,000", 0, 1, "你好", "Xin chào") }), "C:\\tmp\\analysis.json", new string('a', 64), hyphen, false });
            try { validate.Invoke(null, new[] { hyphenReq }); }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException) { throw new InvalidOperationException("hyphen voice vieneu-ngoc-huyen must pass via canonicalization", ex.InnerException); }
            // Unsupported voice must be rejected (do not remove validation)
            var badReq = Activator.CreateInstance(typeof(EditorTtsRequest), new object[] { "voice-reg-test", tmpSource, 4.2, new EditorSubtitleSource("C:\\tmp\\a.srt", 10, 1, new string('0', 64), new[] { new EditorSubtitleCue("c1", "1", "00:00:00,000 --> 00:00:01,000", 0, 1, "你好", "Xin chào") }), "C:\\tmp\\analysis.json", new string('a', 64), "invalid_voice_xyz", false });
            var threw = false;
            try { validate.Invoke(null, new[] { badReq }); } catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException) { threw = true; }
            True(threw, "unsupported voice must be rejected by ValidateRequest");
        }
        finally { try { File.Delete(tmpSource); } catch { } }
        return;
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"expected {expected}; got {actual}");
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
