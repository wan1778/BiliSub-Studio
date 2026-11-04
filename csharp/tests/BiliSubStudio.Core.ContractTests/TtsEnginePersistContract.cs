using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using BiliSubStudio.Core.Editor;

namespace BiliSubStudio.Core.ContractTests;

// 08/10 (user: exiting and re-entering a project lost the voice and had to be regenerated): the old
// NormalizeTts only accepted the NGHI engine tuple, so every VieNeu voice was silently returned as null
// ON EVERY SAVE - disk never kept the Tts. Contract: accept both full engine tuples (engine + version +
// matching voice pair), the manifest review accepts both pinned revisions; mixed/foreign tuples are still
// rejected (no loosening).
internal static class TtsEnginePersistContract
{
    private const string VieneuEngine = "vieneu-tts";
    private const string VieneuEngineVersion = "vieneu-v3-turbo-1";
    private const string VieneuVoice = "vieneu_ngoc_huyen";
    private const string VieneuRevision = "61b85e3d937fbbacb387714180e8182823512523:whole-cue-vieneu-natural-first-v1";
    private const string NghiEngine = "nghi-tts";
    private const string NghiEngineVersion = "nghi-tts-1.0.0";
    private const string NghiVoice = "ngoc_huyen";
    private const string NghiRevision = "2140977786d76d834736c059dacfa553d4931dac2b2c7aaaea438bb2aa9da697:971f57f8d504223fee5b40d664f503cf769baf7db21f7d2ae0554a75d07de2f8:whole-cue-piper-natural-first-v17";

    [ModuleInitializer]
    internal static void Install()
    {
        RuntimeHelpers.RunClassConstructor(typeof(Program).TypeHandle);
        var field = typeof(Program).GetField("Tests", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing Core contract list");
        var tests = field.GetValue(null) as List<(string Name, Func<Task> Test)>
            ?? throw new InvalidOperationException("Core contract list type drifted");
        tests.Add(("TTS persist accepts both NGHI and VieNeu engine tuples, rejects mixed and unknown engines", RunAsync));
    }

    private static Task RunAsync()
    {
        VieneuTuplePersists();
        NghiTuplePersists();
        MixedTupleRejected();
        UnknownEngineRejected();
        WrongRevisionRejected();
        return Task.CompletedTask;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("TtsEnginePersistContract: " + message);
    }

    private static string WriteFixture(string engine, string engineVersion, string voice, string revision)
    {
        var root = Path.Combine(Path.GetTempPath(), "bilisub-tts-persist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var masterPath = Path.Combine(root, "voice-master-test.flac");
        var masterBytes = new byte[256];
        new Random(7).NextBytes(masterBytes);
        File.WriteAllBytes(masterPath, masterBytes);
        var masterSha = Convert.ToHexStringLower(SHA256.HashData(masterBytes));
        var manifestPath = Path.Combine(root, "result-test.json");
        var manifest = JsonSerializer.Serialize(new
        {
            schema = 2,
            sample_rate = 22050,
            voice_revision = revision,
            master = new { path = masterPath, sha256 = masterSha },
            cues = new[]
            {
                new { id = "a", clip_start_sample = 0L, target_frames = 22050L, timing_source = "whisper", status = "fit" },
                new { id = "b", clip_start_sample = 22050L, target_frames = 22050L, timing_source = "whisper", status = "fit" },
            },
        });
        File.WriteAllText(manifestPath, manifest);
        return manifestPath;
    }

    private static EditorTtsProject? NormalizeFixture(string engine, string engineVersion, string voice, string revision)
    {
        var manifestPath = WriteFixture(engine, engineVersion, voice, revision);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var masterPath = document.RootElement.GetProperty("master").GetProperty("path").GetString()!;
            var manifestSha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(manifestPath)));
            var candidate = new EditorTtsProject(
                "complete", engine, engineVersion, voice, voice, manifestPath, manifestSha,
                new EditorVoiceTrack(masterPath, 0, 2.0), 2, 0,
                [
                    new EditorTtsCueWindow("a", 0, 1.0, "whisper", "fit"),
                    new EditorTtsCueWindow("b", 1.0, 2.0, "whisper", "fit"),
                ], 1);
            return EditorProjectStore.NormalizeTts(candidate);
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(manifestPath)!, recursive: true); }
            catch { }
        }
    }

    private static void VieneuTuplePersists()
    {
        var normalized = NormalizeFixture(VieneuEngine, VieneuEngineVersion, VieneuVoice, VieneuRevision);
        Check(normalized is not null, "voice VieNeu hợp lệ bị loại khỏi project (mất voice mỗi lần lưu)");
        Check(string.Equals(normalized!.Engine, VieneuEngine, StringComparison.Ordinal), "engine VieNeu không được giữ nguyên");
    }

    private static void NghiTuplePersists()
    {
        var normalized = NormalizeFixture(NghiEngine, NghiEngineVersion, NghiVoice, NghiRevision);
        Check(normalized is not null, "voice NGHI hợp lệ bị loại khỏi project");
    }

    private static void MixedTupleRejected()
    {
        var normalized = NormalizeFixture(VieneuEngine, NghiEngineVersion, VieneuVoice, VieneuRevision);
        Check(normalized is null, "tuple trộn engine VieNeu + version NGHI vẫn lọt kiểm tra");
    }

    private static void UnknownEngineRejected()
    {
        var normalized = NormalizeFixture("other-tts", "v1", "other_voice", VieneuRevision);
        Check(normalized is null, "engine lạ vẫn lọt kiểm tra persist");
    }

    private static void WrongRevisionRejected()
    {
        var normalized = NormalizeFixture(VieneuEngine, VieneuEngineVersion, VieneuVoice, "bogus-revision");
        Check(normalized is null, "manifest sai voice_revision vẫn lọt kiểm tra");
    }
}
