using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace BiliSubStudio.Core.Hardware;

public sealed record HardwareSnapshot(string CpuName, int LogicalProcessors, long MemoryBytes, bool NvidiaDetected, string GpuName, string CudaDriver, long VramBytes);
public sealed record HardwareResourceSnapshot(long TotalMemoryBytes, long AvailableMemoryBytes, bool VramTelemetryAvailable, long TotalVramBytes, long AvailableVramBytes);
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

    public HardwareResourceSnapshot ResourceSnapshot()
    {
        var managedLimit = Math.Max(0, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
        var totalMemory = managedLimit;
        var availableMemory = Math.Max(0, managedLimit - GC.GetTotalMemory(forceFullCollection: false));
        if (OperatingSystem.IsWindows() && MemoryProbe.TryRead(out var physical))
        {
            totalMemory = physical.Total > (ulong)long.MaxValue ? long.MaxValue : (long)physical.Total;
            availableMemory = physical.Available > (ulong)long.MaxValue ? long.MaxValue : (long)physical.Available;
        }

        var gpu = NvidiaProbe.TryRead();
        var live = NvidiaProbe.TryReadMemory();
        return new HardwareResourceSnapshot(
            totalMemory,
            availableMemory,
            live.Available,
            live.Available ? live.TotalBytes : gpu.VramBytes,
            live.Available ? live.FreeBytes : 0);
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
            var lanes = RecommendedOcrSegmentLanes(snapshot);
            return new BenchmarkResult(
                cpuBytes / 1024d / 1024d / cpuWatch.Elapsed.TotalSeconds,
                memoryBytes / 1024d / 1024d / memoryWatch.Elapsed.TotalSeconds,
                lanes,
                overall.Elapsed);
        }, cancellationToken);
    }

    internal static int RecommendedOcrSegmentLanes(HardwareSnapshot snapshot)
    {
        var lanes = 1;
        foreach (var level in new[] { 2, 4, 8, 16 })
        {
            // A segment lane owns an FFmpeg decoder and one in-flight frame, not a
            // PaddleOCR model. Keep CPU and RAM headroom for the WinUI process and OS.
            if (snapshot.LogicalProcessors >= level * 4 && snapshot.MemoryBytes >= level * 512L * 1024 * 1024)
                lanes = level;
        }
        return lanes;
    }

    internal static int RecommendedOcrWorkers(HardwareSnapshot snapshot, string? deviceMode)
    {
        var mode = (deviceMode ?? "auto").Trim().ToLowerInvariant();
        if (mode == "auto") mode = snapshot.NvidiaDetected ? "gpu" : "cpu";
        if (mode == "cpu") return RecommendedCpuOcrWorkers(snapshot);
        if (!snapshot.NvidiaDetected) return 1;
        if (mode == "hybrid") return Math.Max(2, RecommendedGpuOcrWorkers(snapshot.VramBytes) + 1);
        return RecommendedGpuOcrWorkers(snapshot.VramBytes);
    }

    private static int RecommendedCpuOcrWorkers(HardwareSnapshot snapshot) =>
        snapshot.LogicalProcessors >= 8 && snapshot.MemoryBytes >= 4L * 1024 * 1024 * 1024 ? 2 : 1;

    internal static int RecommendedGpuOcrWorkers(long vramBytes)
    {
        const long gib = 1024L * 1024 * 1024;
        // CUDA commonly reports slightly less than the advertised VRAM tier. Keep a
        // tier tolerance so a 4 GB laptop GPU does not collapse to the 1-lane tier.
        if (vramBytes < 7 * gib / 2) return 1;
        if (vramBytes < 7 * gib) return 2;
        if (vramBytes < 14 * gib) return 4;
        if (vramBytes < 28 * gib) return 8;
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

        public static (bool Available, long TotalBytes, long FreeBytes) TryReadMemory()
        {
            if (!OperatingSystem.IsWindows()) return default;
            var initialized = false;
            try
            {
                if (nvmlInit_v2() != 0) return default;
                initialized = true;
                if (nvmlDeviceGetHandleByIndex_v2(0, out var device) != 0 || device == IntPtr.Zero) return default;
                if (nvmlDeviceGetMemoryInfo(device, out var memory) != 0) return default;
                var total = memory.Total > (ulong)long.MaxValue ? long.MaxValue : (long)memory.Total;
                var free = memory.Free > (ulong)long.MaxValue ? long.MaxValue : (long)memory.Free;
                return (true, total, free);
            }
            catch (DllNotFoundException) { return default; }
            catch (EntryPointNotFoundException) { return default; }
            finally
            {
                if (initialized)
                {
                    try { _ = nvmlShutdown(); } catch { }
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NvmlMemory
        {
            public ulong Total;
            public ulong Free;
            public ulong Used;
        }

        [DllImport("nvcuda.dll")] private static extern int cuInit(uint flags);
        [DllImport("nvcuda.dll")] private static extern int cuDriverGetVersion(out int version);
        [DllImport("nvcuda.dll")] private static extern int cuDeviceGetCount(out int count);
        [DllImport("nvcuda.dll")] private static extern int cuDeviceGet(out int device, int ordinal);
        [DllImport("nvcuda.dll")] private static extern int cuDeviceGetName(byte[] name, int length, int device);
        [DllImport("nvcuda.dll")] private static extern int cuDeviceTotalMem_v2(out nuint bytes, int device);
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int nvmlInit_v2();
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int nvmlShutdown();
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMemory memory);
    }

    private static class MemoryProbe
    {
        public static bool TryRead(out (ulong Total, ulong Available) memory)
        {
            var status = new MemoryStatusEx { Length = checked((uint)Marshal.SizeOf<MemoryStatusEx>()) };
            if (GlobalMemoryStatusEx(ref status))
            {
                memory = (status.TotalPhysical, status.AvailablePhysical);
                return true;
            }
            memory = default;
            return false;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhysical;
            public ulong AvailablePhysical;
            public ulong TotalPageFile;
            public ulong AvailablePageFile;
            public ulong TotalVirtual;
            public ulong AvailableVirtual;
            public ulong AvailableExtendedVirtual;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
    }
}
