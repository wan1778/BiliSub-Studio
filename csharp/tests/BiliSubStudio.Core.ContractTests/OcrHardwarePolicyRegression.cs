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
            "RecommendedGpuOcrWorkers",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing OCR GPU worker policy");
        var segmentPolicy = assembly.GetMethod(
            "RecommendedOcrSegmentLanes",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing OCR segment-lane policy");
        var workerPolicy = assembly.GetMethod(
            "RecommendedOcrWorkers",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing device-aware OCR worker policy");

        static long GiB(double value) => checked((long)(value * 1024 * 1024 * 1024));
        int GpuWorkers(double gib) => (int)(gpuPolicy.Invoke(null, [GiB(gib)])
            ?? throw new InvalidOperationException("OCR GPU worker policy returned null"));
        int SegmentLanes(HardwareSnapshot hardware) => (int)(segmentPolicy.Invoke(null, [hardware])
            ?? throw new InvalidOperationException("OCR segment-lane policy returned null"));
        int DeviceWorkers(HardwareSnapshot hardware, string mode) => (int)(workerPolicy.Invoke(null, [hardware, mode])
            ?? throw new InvalidOperationException("OCR device worker policy returned null"));

        if (GpuWorkers(3) != 1 || GpuWorkers(6) != 2 || GpuWorkers(12) != 4 || GpuWorkers(24) != 8 || GpuWorkers(48) != 16)
            throw new InvalidOperationException("OCR GPU worker policy drifted from reviewed safety thresholds");
        if (GpuWorkers(3.75) != 2)
            throw new InvalidOperationException("advertised 4 GB GPU collapsed to one predicted OCR worker because CUDA reports slightly less VRAM");

        var laptop4Gb = new HardwareSnapshot("fixture", 16, GiB(32), true, "RTX laptop fixture", "CUDA 12.8", GiB(3.75));
        if (SegmentLanes(laptop4Gb) != 4)
            throw new InvalidOperationException("Ryzen-class fixture did not retain four FFmpeg segment lanes");
        if (DeviceWorkers(laptop4Gb, "auto") != 2)
            throw new InvalidOperationException("4 GB laptop GPU lost its conservative two-worker hardware estimate");

        var lowVram = new HardwareSnapshot("fixture", 32, GiB(32), true, "NVIDIA fixture", "CUDA 12.8", GiB(6));
        if (SegmentLanes(lowVram) != 8)
            throw new InvalidOperationException("segment lanes are still being capped by NVIDIA VRAM");
        if (DeviceWorkers(lowVram, "cpu") != 2)
            throw new InvalidOperationException("CPU worker policy exceeded its reviewed two-worker cap");
        if (DeviceWorkers(lowVram, "gpu") != 2 || DeviceWorkers(lowVram, "auto") != 2)
            throw new InvalidOperationException("GPU/Auto worker policy ignored NVIDIA VRAM headroom");
        if (DeviceWorkers(lowVram, "hybrid") != 3)
            throw new InvalidOperationException("Hybrid worker policy lost its independent GPU+CPU pool");

        var noGpu = new HardwareSnapshot("fixture", 32, GiB(32), false, string.Empty, string.Empty, 0);
        if (SegmentLanes(noGpu) != 8 || DeviceWorkers(noGpu, "cpu") != 2 || DeviceWorkers(noGpu, "auto") != 2)
            throw new InvalidOperationException("no-GPU segment/worker policies are no longer independent");
    }
}
