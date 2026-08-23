using System.Reflection;
using System.Runtime.CompilerServices;
using BiliSubStudio.Core.Editor;

namespace BiliSubStudio.Core.ContractTests;

internal static class EditorDiskSpacePolicyContract
{
    [ModuleInitializer]
    internal static void Verify()
    {
        var type = typeof(VideoEditorService);
        static object Constant(Type owner, string name) => owner
            .GetField(name, BindingFlags.Static | BindingFlags.NonPublic)?
            .GetRawConstantValue()
            ?? throw new InvalidOperationException($"Missing Editor disk-space policy constant: {name}");

        var reserve = (long)Constant(type, "RenderSafetyReserveBytes");
        var multiplier = (int)Constant(type, "RenderPreflightSourceMultiplier");
        var interval = (int)Constant(type, "RenderDiskCheckIntervalMilliseconds");

        if (reserve != 512L * 1024 * 1024)
            throw new InvalidOperationException($"Editor render safety reserve drifted: {reserve}");
        if (multiplier != 2)
            throw new InvalidOperationException($"Editor render preflight multiplier drifted: {multiplier}");
        if (interval != 3000)
            throw new InvalidOperationException($"Editor render disk check interval drifted: {interval}");

        Console.WriteLine("PASS  editor render disk-space guard policy stays pinned");
    }
}
