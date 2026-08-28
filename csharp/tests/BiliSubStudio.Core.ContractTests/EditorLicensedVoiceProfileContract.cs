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

        Replace(tests,
            "local NghiTTS manifest and rhythm grouping stay pinned",
            "local Ngọc Huyền voice and rhythm grouping stay pinned",
            VerifyVoiceProfileAsync);
        Replace(tests,
            "editor project persists, isolates source drift and quarantines corrupt state",
            "editor project persists, invalidates stale TTS, isolates source drift and quarantines corrupt state",
            VerifyEditorProjectAsync);
        tests.Add(("voice dropdown uses exact canonical IDs and ValidateRequest passes for all local voices", VerifyVoiceRegistryAsync));
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
        var installer = assembly.GetType("BiliSubStudio.Core.Editor.LocalTtsInstaller")
            ?? throw new InvalidOperationException("missing LocalTtsInstaller type");
        static object? Constant(Type type, string name) => type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetRawConstantValue();

        Equal("nghi-tts-1.0.0", Constant(installer, "EngineVersion")?.ToString());
        Equal("nghimestudio/nghitts", Constant(installer, "ModelRepository")?.ToString());
        Equal("nghi-2026-09-01", Constant(installer, "ModelRevision")?.ToString());
        Equal("nghi-2026-09-01-ngoc_huyen-v1", Constant(installer, "VoiceRevision")?.ToString());
        Equal("ngoc_huyen", Constant(installer, "Voice")?.ToString());

        var workerPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "tts-worker.py");
        var worker = File.ReadAllText(workerPath);
        True(worker.Contains("ENGINE = \"nghi-tts\"", StringComparison.Ordinal), "NGHI-TTS worker engine missing");
        True(worker.Contains("VOICE_REVISION = \"nghi-2026-09-01-ngoc_huyen-v1\"", StringComparison.Ordinal), "TTS cache revision drifted");
        True(worker.Contains("ensure_voice_cache(output_root)", StringComparison.Ordinal), "old TTS clips can bypass voice cache invalidation");
        True(worker.Contains("\"engine\": \"nghi-tts\"", StringComparison.Ordinal) || worker.Contains("ENGINE = \"nghi-tts\"", StringComparison.Ordinal), "TTS worker engine identity drifted");
        True(worker.Contains("vietnormalizer", StringComparison.Ordinal), "NGHI worker must use vietnormalizer");
        True(!worker.Contains("class KokoroNgocHuyen", StringComparison.Ordinal), "Kokoro worker must not remain on NGHI production path");

        var service = assembly.GetType("BiliSubStudio.Core.Editor.LocalTtsService")
            ?? throw new InvalidOperationException("missing LocalTtsService type");
        var method = service.GetMethod("BuildRhythmGroups", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing TTS rhythm grouping policy");
        var cue = new EditorSubtitleCue("rhythm-cue-0001", "1", "00:00:01,000 --> 00:00:05,000", 1, 5, "你好", "Xin chào đạo hữu");
        var timing = new EditorCueSpeechTiming(cue.Id, 1, 5, 1.2, 4.7, .2, .3,
            [new EditorWordTiming("你", 1.2, 2, .9), new EditorWordTiming("好", 3, 4.7, .9)],
            [new EditorPauseTiming(2, 3)], "female_like", .8, 210);
        var groups = ((System.Collections.IEnumerable)method.Invoke(null, [cue, timing, "Xin chào đạo hữu", "ngoc_huyen"])!).Cast<object>().ToArray();
        Equal(2, groups.Length);
        var firstType = groups[0].GetType();
        Equal(1.2d, (double)(firstType.GetProperty("Start")?.GetValue(groups[0]) ?? 0d));
        Equal(2d, (double)(firstType.GetProperty("End")?.GetValue(groups[0]) ?? 0d));
        True(!string.IsNullOrWhiteSpace(firstType.GetProperty("Text")?.GetValue(groups[0])?.ToString()), "TTS first rhythm group lost Vietnamese text");
        return Task.CompletedTask;
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
            await File.WriteAllTextAsync(ttsManifest, "{\"schema\":1}");
            var ttsManifestSha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(ttsManifest)));

            var validTts = new EditorTtsProject(
                "complete", "nghi-tts", "nghi-tts-1.0.0",
                "ngoc_huyen", "ngoc_huyen",
                ttsManifest, ttsManifestSha, new EditorVoiceTrack(voicePath, 0, 120), 1, 0);
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
            Equal("nghi-tts", reopened.Tts.Engine);
            Equal("female", reopened.VoiceOverrides![subtitle.Cues[0].Id]);

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
        Equal(14, list.Length);
        True(list.Contains("ngoc_huyen", StringComparer.Ordinal), "canonical ngoc_huyen missing from registry");
        True(list.Distinct(StringComparer.Ordinal).Count() == 14, "voice registry contains duplicate IDs");
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
        // UI sends hyphen form ngoc-huyen, backend must accept via canonicalization
        var hyphen = "ngoc-huyen";
        var expectedCanonical = canonical.Invoke(null, new object[] { hyphen })?.ToString() ?? string.Empty;
        Equal("ngoc_huyen", expectedCanonical);
        var tmpSource = Path.Combine(Path.GetTempPath(), $"bilisub-voice-reg-{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(tmpSource, new byte[] { 1, 2, 3 });
        try
        {
            // Every registry voice must pass validation (simulates dropdown selection -> request -> ValidateRequest)
            foreach (var vid in list)
            {
                var req = Activator.CreateInstance(typeof(EditorTtsRequest), new object[] { "voice-reg-test", tmpSource, 4.2, new EditorSubtitleSource("C:\\tmp\\a.srt", 10, 1, new string('0', 64), new[] { new EditorSubtitleCue("c1", "1", "00:00:00,000 --> 00:00:01,000", 0, 1, "你好", "Xin chào") }), "C:\\tmp\\analysis.json", new string('a', 64), vid });
                try { validate.Invoke(null, new[] { req }); }
                catch (TargetInvocationException ex) when (ex.InnerException is not null) { throw new InvalidOperationException($"ValidateRequest rejected canonical voice {vid}: {ex.InnerException.Message}", ex.InnerException); }
            }
            // Hyphen form from old UI must also pass via canonicalization
            var hyphenReq = Activator.CreateInstance(typeof(EditorTtsRequest), new object[] { "voice-reg-test", tmpSource, 4.2, new EditorSubtitleSource("C:\\tmp\\a.srt", 10, 1, new string('0', 64), new[] { new EditorSubtitleCue("c1", "1", "00:00:00,000 --> 00:00:01,000", 0, 1, "你好", "Xin chào") }), "C:\\tmp\\analysis.json", new string('a', 64), hyphen });
            try { validate.Invoke(null, new[] { hyphenReq }); }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException) { throw new InvalidOperationException("hyphen voice ngoc-huyen must pass via canonicalization", ex.InnerException); }
            // Unsupported voice must be rejected (do not remove validation)
            var badReq = Activator.CreateInstance(typeof(EditorTtsRequest), new object[] { "voice-reg-test", tmpSource, 4.2, new EditorSubtitleSource("C:\\tmp\\a.srt", 10, 1, new string('0', 64), new[] { new EditorSubtitleCue("c1", "1", "00:00:00,000 --> 00:00:01,000", 0, 1, "你好", "Xin chào") }), "C:\\tmp\\analysis.json", new string('a', 64), "invalid_voice_xyz" });
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
