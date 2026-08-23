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
            "local licensed VAIS profiles and rhythm grouping stay pinned",
            VerifyVoiceProfileAsync);
        Replace(tests,
            "editor project persists, isolates source drift and quarantines corrupt state",
            "editor project persists, invalidates stale TTS, isolates source drift and quarantines corrupt state",
            VerifyEditorProjectAsync);
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

        Equal("1.4.2", Constant(installer, "PiperVersion")?.ToString());
        Equal("rhasspy/piper-voices", Constant(installer, "VoiceRepository")?.ToString());
        Equal("3d796cc2f2c884b3517c527507e084f7bb245aea", Constant(installer, "ModelRevision")?.ToString());
        Equal("3d796cc2f2c884b3517c527507e084f7bb245aea-profile-v1", Constant(installer, "VoiceRevision")?.ToString());
        Equal("vi_VN-vais1000-medium", Constant(installer, "BaseVoice")?.ToString());
        Equal("vais1000-male-profile-v1", Constant(installer, "MaleVoice")?.ToString());
        Equal("vais1000-female-profile-v1", Constant(installer, "FemaleVoice")?.ToString());
        Equal(63_201_294L, (long)(Constant(installer, "VoiceModelBytes") ?? 0L));
        Equal(4_860L, (long)(Constant(installer, "VoiceConfigBytes") ?? 0L));
        Equal("ec7c89e2c85f4d1edc24b6120c18aaf1bda614f06b511567eb9c7c0de15e2dab", Constant(installer, "VoiceModelSha256")?.ToString());
        Equal("fafb9da1354ed4b77c31af228ed41fb41cd825c14cffa105454b25e6ae751ee0", Constant(installer, "VoiceConfigSha256")?.ToString());

        var workerPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "tts-worker.py");
        var worker = File.ReadAllText(workerPath);
        True(worker.Contains("MALE_PITCH_FACTOR = 0.84", StringComparison.Ordinal), "VAIS male acoustic factor drifted");
        True(worker.Contains("VOICE_PROFILE_REVISION = \"3d796cc2f2c884b3517c527507e084f7bb245aea-profile-v1\"", StringComparison.Ordinal), "TTS cache profile revision drifted");
        True(worker.Contains("asetrate=", StringComparison.Ordinal) && worker.Contains("tempo_compensation = 1.0 / MALE_PITCH_FACTOR", StringComparison.Ordinal), "male profile lost pitch/tempo compensation");
        True(worker.Contains("ensure_profile_cache(output_root)", StringComparison.Ordinal), "old TTS clips can bypass profile cache invalidation");
        True(worker.Contains("\"engine\": \"piper-vais1000-profiles\"", StringComparison.Ordinal), "TTS worker engine identity drifted");
        True(!worker.Contains("deepman3909", StringComparison.Ordinal) && !worker.Contains("calmwoman3688", StringComparison.Ordinal)
            && !worker.Contains("sannht/vi_voice", StringComparison.Ordinal), "retired ambiguous NghiTTS weights returned to production worker");

        var service = assembly.GetType("BiliSubStudio.Core.Editor.LocalTtsService")
            ?? throw new InvalidOperationException("missing LocalTtsService type");
        var method = service.GetMethod("BuildRhythmGroups", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing TTS rhythm grouping policy");
        var cue = new EditorSubtitleCue("rhythm-cue-0001", "1", "00:00:01,000 --> 00:00:05,000", 1, 5, "你好", "Xin chào đạo hữu");
        var timing = new EditorCueSpeechTiming(cue.Id, 1, 5, 1.2, 4.7, .2, .3,
            [new EditorWordTiming("你", 1.2, 2, .9), new EditorWordTiming("好", 3, 4.7, .9)],
            [new EditorPauseTiming(2, 3)], "female_like", .8, 210);
        var groups = ((System.Collections.IEnumerable)method.Invoke(null, [cue, timing, "Xin chào đạo hữu", "female"])!).Cast<object>().ToArray();
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
                "complete", "piper-vais1000-profiles", "1.4.2",
                "vais1000-male-profile-v1", "vais1000-female-profile-v1",
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
            Equal("piper-vais1000-profiles", reopened.Tts.Engine);
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
