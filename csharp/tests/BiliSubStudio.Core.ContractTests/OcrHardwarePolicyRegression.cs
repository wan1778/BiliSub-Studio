using System.Reflection;
using System.Runtime.CompilerServices;
using BiliSubStudio.Core.Hardware;

namespace BiliSubStudio.Core.ContractTests;

internal static class OcrHardwarePolicyRegression
{
    [ModuleInitializer]
    internal static void Verify()
    {
        var policy = typeof(HardwareService).GetMethod(
            "RecommendedGpuOcrLanes",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing OCR GPU VRAM lane policy");

        static long GiB(long value) => value * 1024L * 1024 * 1024;
        int Lanes(long gib) => (int)(policy.Invoke(null, [GiB(gib)])
            ?? throw new InvalidOperationException("OCR GPU VRAM policy returned null"));

        if (Lanes(3) != 1 || Lanes(6) != 2 || Lanes(12) != 4 || Lanes(24) != 8 || Lanes(48) != 16)
            throw new InvalidOperationException("OCR GPU VRAM lane policy drifted from reviewed safety thresholds");

        Console.WriteLine("PASS  OCR auto topology respects NVIDIA VRAM headroom");
    }
}
