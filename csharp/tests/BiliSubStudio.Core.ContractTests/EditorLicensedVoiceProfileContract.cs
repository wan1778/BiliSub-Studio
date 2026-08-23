using System.Reflection;
using System.Runtime.CompilerServices;
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
        var index = tests.FindIndex(item => item.Name == "local NghiTTS manifest and rhythm grouping stay pinned");
        if (index < 0) throw new InvalidOperationException("legacy TTS contract entry was not found");
        tests[index] = ("local licensed VAIS profiles and rhythm grouping stay pinned", VerifyAsync);
    }

    private static Task VerifyAsync()
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
