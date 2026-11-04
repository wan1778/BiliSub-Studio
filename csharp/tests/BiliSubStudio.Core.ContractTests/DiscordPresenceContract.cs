using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace BiliSubStudio.Core.ContractTests;

// Discord presence following real jobs: kind -> Vietnamese details text, the payload carries logo
// assets + timestamps. Pure, no Discord pipe is touched in the test.
internal static class DiscordPresenceContract
{
    [ModuleInitializer]
    internal static void Install()
    {
        RuntimeHelpers.RunClassConstructor(typeof(Program).TypeHandle);
        var field = typeof(Program).GetField("Tests", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing Core contract list");
        var tests = field.GetValue(null) as List<(string Name, Func<Task> Test)>
            ?? throw new InvalidOperationException("Core contract list type drifted");
        tests.Add(("Discord presence maps job kinds to Vietnamese activity", VerifyActivityMappingAsync));
    }

    private static Task VerifyActivityMappingAsync()
    {
        var service = Type.GetType("BiliSubStudio.Core.Presence.DiscordPresenceContent, BiliSubStudio.Core")
            ?? throw new InvalidOperationException("missing DiscordPresenceContent type");
        var name = service.GetMethod("ActivityName", BindingFlags.Static | BindingFlags.Public)
            ?? throw new InvalidOperationException("missing presence activity mapping");
        Equal("Quét OCR", (string)name.Invoke(null, ["ocrscan"])!);
        Equal("Tạo voice", (string)name.Invoke(null, ["editor-tts"])!);
        Equal("Xuất Edit", (string)name.Invoke(null, ["editor"])!);
        Equal("weird-kind", (string)name.Invoke(null, ["weird-kind"])!);
        var build = service.GetMethod("BuildActivityJson", BindingFlags.Static | BindingFlags.Public)
            ?? throw new InvalidOperationException("missing presence payload builder");
        using var idle = JsonDocument.Parse((string)build.Invoke(null, ["Đang rảnh", "Mở BiliSub Studio", null, "logo"])!);
        Equal("Đang rảnh", idle.RootElement.GetProperty("details").GetString());
        Equal("logo", idle.RootElement.GetProperty("assets").GetProperty("large_image").GetString());
        True(!idle.RootElement.TryGetProperty("timestamps", out _), "idle không gắn elapsed");
        using var busy = JsonDocument.Parse((string)build.Invoke(null, ["Quét OCR", "45%", 1700000000L, "logo"])!);
        Equal(1700000000L, busy.RootElement.GetProperty("timestamps").GetProperty("start").GetInt64());
        var shortName = service.GetMethod("ShortName", BindingFlags.Static | BindingFlags.Public)
            ?? throw new InvalidOperationException("missing presence name shortener");
        Equal("abc.mp4", (string)shortName.Invoke(null, ["abc.mp4", 40])!);
        Equal("...ijk", (string)shortName.Invoke(null, ["abcdefghijk", 6])!);
        var jobState = service.GetMethod("ComposeJobState", BindingFlags.Static | BindingFlags.Public)
            ?? throw new InvalidOperationException("missing presence job state");
        Equal("45% · phim.mp4", (string)jobState.Invoke(null, [45d, "scanning", false, "phim.mp4"])!);
        Equal("45% · đang dừng", (string)jobState.Invoke(null, [45d, "cancelling", false, null])!);
        var idleState = service.GetMethod("ComposeIdleState", BindingFlags.Static | BindingFlags.Public)
            ?? throw new InvalidOperationException("missing presence idle state");
        Equal("duan.mp4", (string)idleState.Invoke(null, [null, "duan.mp4"])!);
        Equal("Đang xem", (string)idleState.Invoke(null, [null, null])!);
        return Task.CompletedTask;
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"discord presence: expected {expected}, actual {actual}");
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("discord presence: " + message);
    }
}
