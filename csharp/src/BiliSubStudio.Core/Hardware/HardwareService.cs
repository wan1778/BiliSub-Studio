using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace BiliSubStudio.Core.Hardware;

public sealed record HardwareSnapshot(string CpuName, int LogicalProcessors, long MemoryBytes, bool NvidiaDetected, string GpuName, string CudaDriver, long VramBytes);
public sealed record BenchmarkResult(double CpuMegabytesPerSecond, double MemoryMegabytesPerSecond, int RecommendedOcrLanes, TimeSpan Elapsed);

public sealed class HardwareService
{
    public HardwareSnapshot Snapshot()
    {
        var cpu = RuntimeInformation.ProcessArchitecture + " CPU";
        if (OperatingSystem.IsWindows())
        {
            cpu = Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", cpu)?.ToString()?.Trim() ?? cpu;
        }
        var memory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        var gpu = NvidiaProbe.TryRead();
        return new HardwareSnapshot(cpu, Environment.ProcessorCount, memory, gpu.Detected, gpu.Name, gpu.Driver, gpu.VramBytes);
    }

    public async Task<BenchmarkResult> BenchmarkAsync(CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            var overall = Stopwatch.StartNew();
            var block = new byte[8 * 1024 * 1024];
            RandomNumberGenerator.Fill(block);
            long cpuBytes = 0;
            var cpuWatch = Stopwatch.StartNew();
            while (cpuWatch.Elapsed < TimeSpan.FromMilliseconds(650))
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = SHA256.HashData(block);
                cpuBytes += block.Length;
            }
            var target = new byte[block.Length];
            long memoryBytes = 0;
            var memoryWatch = Stopwatch.StartNew();
            while (memoryWatch.Elapsed < TimeSpan.FromMilliseconds(650))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Buffer.BlockCopy(block, 0, target, 0, block.Length);
                (block, target) = (target, block);
                memoryBytes += block.Length;
            }
            var snapshot = Snapshot();
            var lanes = 1;
            foreach (var level in new[] { 2, 4, 8, 16 })
            {
                if (snapshot.LogicalProcessors >= level * 2 && snapshot.MemoryBytes >= level * 768L * 1024 * 1024) lanes = level;
            }
            if (snapshot.NvidiaDetected)
            {
                lanes = Math.Min(lanes, RecommendedGpuOcrLanes(snapshot.VramBytes));
            }
            else
            {
                lanes = Math.Min(lanes, 4);
            }
            return new BenchmarkResult(
                cpuBytes / 1024d / 1024d / cpuWatch.Elapsed.TotalSeconds,
                memoryBytes / 1024d / 1024d / memoryWatch.Elapsed.TotalSeconds,
                lanes,
                overall.Elapsed);
        }, cancellationToken);
    }

    internal static int RecommendedGpuOcrLanes(long vramBytes)
    {
        const long gib = 1024L * 1024 * 1024;
        if (vramBytes < 4 * gib) return 1;
        if (vramBytes < 8 * gib) return 2;
        if (vramBytes < 16 * gib) return 4;
        if (vramBytes < 32 * gib) return 8;
        return 16;
    }

    private static class NvidiaProbe
    {
        public static (bool Detected, string Name, string Driver, long VramBytes) TryRead()
        {
            if (!OperatingSystem.IsWindows()) return default;
            try
            {
                if (cuInit(0) != 0 || cuDeviceGetCount(out var count) != 0 || count <= 0) return default;
                if (cuDeviceGet(out var device, 0) != 0) return default;
                var nameBytes = new byte[256];
                _ = cuDeviceGetName(nameBytes, nameBytes.Length, device);
                var end = Array.IndexOf(nameBytes, (byte)0);
                if (end < 0) end = nameBytes.Length;
                var name = System.Text.Encoding.UTF8.GetString(nameBytes, 0, end);
                _ = cuDriverGetVersion(out var driver);
                _ = cuDeviceTotalMem_v2(out var memory, device);
                return (true, name, driver > 0 ? $"CUDA {driver / 1000}.{driver % 1000 / 10}" : string.Empty, checked((long)memory));
            }
            catch (DllNotFoundException) { return default; }
            catch (EntryPointNotFoundException) { return default; }
        }

        [DllImport("nvcuda.dll")] private static extern int cuInit(uint flags);
        [DllImport("nvcuda.dll")] private static extern int cuDriverGetVersion(out int version);
        [DllImport("nvcuda.dll")] private static extern int cuDeviceGetCount(out int count);
        [DllImport("nvcuda.dll")] private static extern int cuDeviceGet(out int device, int ordinal);
        [DllImport("nvcuda.dll")] private static extern int cuDeviceGetName(byte[] name, int length, int device);
        [DllImport("nvcuda.dll")] private static extern int cuDeviceTotalMem_v2(out nuint bytes, int device);
    }
}
