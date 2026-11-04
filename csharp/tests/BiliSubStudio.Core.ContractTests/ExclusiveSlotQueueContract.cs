using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace BiliSubStudio.Core.ContractTests;

/// <summary>
/// EXCLUSIVE-SLOT (04/11): heavy local jobs (ASR, voice, Vietsub AI) previously REFUSED to
/// start while any other job was running, so a user with a media download had to cancel it to
/// make a voice. They now queue for the single GPU slot instead ("Đang chờ tác vụ khác xong...")
/// and start by themselves. The resource guard itself is unchanged - still never in parallel -
/// and the waiting job stays cancellable.
/// </summary>
internal static class ExclusiveSlotQueueContract
{
    [ModuleInitializer]
    internal static void Install()
    {
        RuntimeHelpers.RunClassConstructor(typeof(Program).TypeHandle);
        var field = typeof(Program).GetField("Tests", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing Core contract list");
        var tests = field.GetValue(null) as List<(string Name, Func<Task> Test)>
            ?? throw new InvalidOperationException("Core contract list type drifted");
        tests.Add(("Editor job waits for the exclusive GPU slot instead of refusing while media downloads", RunAsync));
    }

    private static Task RunAsync()
    {
        // The binary runs from bin/<cfg>/net10.0, four levels below csharp/, so the
        // source is five levels up. Walk up until the file actually exists.
        var sourcePath = FindSource();
        var source = File.ReadAllText(sourcePath);

        Check(source.Contains("WaitForExclusiveSlotAsync", StringComparison.Ordinal),
            "thiếu cơ chế xếp hàng cho slot chuyên biệt");

        // every heavy job waits instead of throwing
        var waits = Regex.Matches(source, @"await WaitForExclusiveSlotAsync\(job, ").Count;
        Check(waits >= 6, $"phải có ít nhất 6 chỗ xếp hàng, thực tế {waits}");

        // nobody is refused any more: the six heavy-job guards must be gone. (The two
        // remaining HasActiveJobs guards are deliberate - wiping cache/tools during ANY
        // job is forbidden - and the 120-minute deadline throw lives in the wait itself.)
        var refusals = Regex.Matches(source, @"HasActiveJobs\) throw").Count;
        Check(refusals == 2,
            $"chỉ còn 2 chỗ chặn (dọn cache/tools), thực tế {refusals}");

        // the wait has a deadline and stays cancellable
        Check(source.Contains("AddMinutes(120)", StringComparison.Ordinal),
            "hàng chờ phải có giới hạn 120 phút");
        Check(source.Contains("cancellationToken.ThrowIfCancellationRequested()", StringComparison.Ordinal),
            "job đang chờ vẫn phải hủy được");

        return Task.CompletedTask;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("ExclusiveSlotQueueContract: " + message);
    }

    private static string FindSource()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName, "src", "BiliSubStudio.Core", "Application", "BiliSubApplication.cs");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new FileNotFoundException("khong tim thay source BiliSubApplication.cs");
    }
}
