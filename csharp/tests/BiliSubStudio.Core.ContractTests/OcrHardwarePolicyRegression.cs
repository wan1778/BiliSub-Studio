using System.Reflection;
using System.Runtime.CompilerServices;
using BiliSubStudio.Core.Hardware;

namespace BiliSubStudio.Core.ContractTests;

internal static class OcrHardwarePolicyRegression
{
    [ModuleInitializer]
    internal static void Verify()
    {
        var assembly = typeof(HardwareService);
        var gpuPolicy = assembly.GetMethod(
            "RecommendedGpuOcrLanes",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing OCR GPU VRAM lane policy");
        var devicePolicy = assembly.GetMethod(
            "RecommendedOcrLanes",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing device-aware OCR lane policy");

        static long GiB(long value) => value * 1024L * 1024 * 1024;
        int GpuLanes(long gib) => (int)(gpuPolicy.Invoke(null, [GiB(gib)])
            ?? throw new InvalidOperationException("OCR GPU VRAM policy returned null"));
        int DeviceLanes(HardwareSnapshot hardware, string mode) => (int)(devicePolicy.Invoke(null, [hardware, mode])
            ?? throw new InvalidOperationException("OCR device policy returned null"));

        if (GpuLanes(3) != 1 || GpuLanes(6) != 2 || GpuLanes(12) != 4 || GpuLanes(24) != 8 || GpuLanes(48) != 16)
            throw new InvalidOperationException("OCR GPU VRAM lane policy drifted from reviewed safety thresholds");

        var lowVram = new HardwareSnapshot("fixture", 32, GiB(32), true, "NVIDIA fixture", "CUDA 12.8", GiB(6));
        if (DeviceLanes(lowVram, "cpu") != 16)
            throw new InvalidOperationException("CPU OCR was incorrectly capped by NVIDIA VRAM");
        if (DeviceLanes(lowVram, "gpu") != 2 || DeviceLanes(lowVram, "auto") != 2)
            throw new InvalidOperationException("GPU/Auto OCR ignored NVIDIA VRAM headroom");
        if (DeviceLanes(lowVram, "hybrid") != 2)
            throw new InvalidOperationException("Hybrid OCR exceeded reviewed GPU+CPU topology headroom");

        var noGpu = new HardwareSnapshot("fixture", 32, GiB(32), false, string.Empty, string.Empty, 0);
        if (DeviceLanes(noGpu, "cpu") != 16 || DeviceLanes(noGpu, "auto") != 4)
            throw new InvalidOperationException("CPU/Auto no-GPU policy drifted from reviewed limits");

        Console.WriteLine("PASS  OCR topology is device-aware and respects NVIDIA VRAM headroom");
    }
}
