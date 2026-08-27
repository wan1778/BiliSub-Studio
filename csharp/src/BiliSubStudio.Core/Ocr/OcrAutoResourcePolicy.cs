using BiliSubStudio.Core.Hardware;

namespace BiliSubStudio.Core.Ocr;

internal sealed record OcrResourceDecision(bool Allowed, string Reason, string Summary);

internal static class OcrAutoResourcePolicy
{
    private const long Mib = 1024L * 1024;
    private const long Gib = 1024L * Mib;
    private const long GpuWorkerRamBytes = 384L * Mib;
    private const long CpuWorkerRamBytes = 768L * Mib;
    private const double VramGrowthSafetyFactor = 1.25;
    private const double MinimumThroughputGain = 0.10;

    // Keep the reviewed five-argument evaluator stable for existing contracts and
    // non-measured callers. Auto itself uses the public overload below so the
    // machine-measured VRAM slope remains part of Predict -> Probe -> Commit.
    internal static OcrResourceDecision Evaluate(
        HardwareSnapshot hardware,
        HardwareResourceSnapshot live,
        string activeMode,
        int currentWorkers,
        int candidate) =>
        Evaluate(hardware, live, activeMode, currentWorkers, candidate, 0, requireMeasuredVram: false);

    public static OcrResourceDecision Evaluate(
        HardwareSnapshot hardware,
        HardwareResourceSnapshot live,
        string activeMode,
        int currentWorkers,
        int candidate,
        long observedVramPerGpuWorkerBytes,
        bool requireMeasuredVram)
    {
        if (candidate is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(candidate));
        currentWorkers = Math.Clamp(currentWorkers, 0, candidate);
        observedVramPerGpuWorkerBytes = Math.Max(0, observedVramPerGpuWorkerBytes);
        var mode = NormalizeMode(activeMode, hardware.NvidiaDetected);
        var current = WorkerKinds(mode, currentWorkers);
        var target = WorkerKinds(mode, candidate);
        var addedGpu = Math.Max(0, target.Gpu - current.Gpu);
        var addedCpu = Math.Max(0, target.Cpu - current.Cpu);

        var cpuLimit = mode == "cpu"
            ? Math.Max(1, hardware.LogicalProcessors / 2)
            : Math.Max(1, hardware.LogicalProcessors);
        if (candidate > cpuLimit)
        {
            return Reject(
                $"CPU chỉ có {hardware.LogicalProcessors} logical processor; mức {candidate} không còn đủ headroom cho FFmpeg + OCR.",
                live);
        }

        var totalRam = Math.Max(live.TotalMemoryBytes, hardware.MemoryBytes);
        var ramReserve = Math.Max(2L * Gib, checked((long)(totalRam * 0.15)));
        var targetRam = checked(target.Gpu * GpuWorkerRamBytes + target.Cpu * CpuWorkerRamBytes);
        if (totalRam > 0 && totalRam < ramReserve + targetRam)
        {
            return Reject(
                $"RAM tổng không đủ ngưỡng an toàn cho {candidate} pipeline; cần giữ lại {FormatBytes(ramReserve)} cho Windows/app.",
                live);
        }
        var addedRam = checked(addedGpu * GpuWorkerRamBytes + addedCpu * CpuWorkerRamBytes);
        if (live.AvailableMemoryBytes > 0 && live.AvailableMemoryBytes < ramReserve + addedRam)
        {
            return Reject(
                $"RAM trống {FormatBytes(live.AvailableMemoryBytes)} không đủ để thêm {candidate - currentWorkers} worker và vẫn giữ reserve {FormatBytes(ramReserve)}.",
                live);
        }

        if (target.Gpu > 0)
        {
            var totalVram = live.TotalVramBytes > 0 ? live.TotalVramBytes : hardware.VramBytes;
            var vramReserve = Math.Max(512L * Mib, checked((long)(totalVram * 0.15)));
            if (totalVram <= 0)
            {
                return Reject("Không đọc được dung lượng VRAM cho topology GPU; Auto không được tăng mù.", live);
            }

            if (live.VramTelemetryAvailable)
            {
                if (live.AvailableVramBytes < vramReserve)
                {
                    return Reject(
                        $"VRAM trống {FormatBytes(live.AvailableVramBytes)} đã thấp hơn reserve an toàn {FormatBytes(vramReserve)}.",
                        live);
                }

                if (addedGpu > 0 && observedVramPerGpuWorkerBytes > 0)
                {
                    var projectedVram = checked((long)Math.Ceiling(
                        observedVramPerGpuWorkerBytes * (double)addedGpu * VramGrowthSafetyFactor));
                    if (live.AvailableVramBytes < vramReserve + projectedVram)
                    {
                        return Reject(
                            $"VRAM trống {FormatBytes(live.AvailableVramBytes)} không đủ cho dự toán thực đo thêm {addedGpu} GPU worker "
                            + $"(~{FormatBytes(projectedVram)}, đã cộng {VramGrowthSafetyFactor:0.00}x margin) và reserve {FormatBytes(vramReserve)}.",
                            live);
                    }
                }
                else if (addedGpu > 0)
                {
                    // Before the first machine-specific delta exists, do not invent a
                    // per-worker VRAM constant. Require one additional reserve block so
                    // the real topology can be probed without running directly at the cliff.
                    var unmeasuredFloor = Math.Min(totalVram, checked(vramReserve * 2));
                    if (live.AvailableVramBytes < unmeasuredFloor)
                    {
                        return Reject(
                            $"VRAM trống {FormatBytes(live.AvailableVramBytes)} quá sát reserve {FormatBytes(vramReserve)} để thử thêm GPU worker khi chưa có delta thực đo.",
                            live);
                    }
                }
            }
            else if (requireMeasuredVram && addedGpu > 0)
            {
                var conservativeLimit = HardwareService.RecommendedOcrWorkers(hardware, mode);
                if (candidate > conservativeLimit)
                {
                    return Reject(
                        $"Không đọc được VRAM trống để học mức dùng thực tế; Auto chỉ cho thử tới {conservativeLimit} worker theo hardware fallback thay vì tăng mù lên {candidate}.",
                        live);
                }
            }
        }

        var measurement = target.Gpu > 0 && live.VramTelemetryAvailable
            ? observedVramPerGpuWorkerBytes > 0
                ? $" · VRAM thực đo ~{FormatBytes(observedVramPerGpuWorkerBytes)}/GPU worker + {VramGrowthSafetyFactor:0.00}x margin"
                : " · VRAM chưa có delta thực đo; giữ thêm một reserve block trước probe topology thật"
            : string.Empty;
        return new OcrResourceDecision(
            true,
            $"RAM/VRAM/CPU đủ headroom để thử đúng {candidate} pipeline.{measurement}",
            FormatSnapshot(live) + measurement);
    }

    internal static bool HasUsefulThroughputGain(double previous, double current) =>
        previous <= 0 || current >= previous * (1 + MinimumThroughputGain);

    internal static string FormatSnapshot(HardwareResourceSnapshot snapshot)
    {
        var ram = snapshot.TotalMemoryBytes > 0
            ? $"RAM trống {FormatBytes(snapshot.AvailableMemoryBytes)}/{FormatBytes(snapshot.TotalMemoryBytes)}"
            : "RAM chưa đọc được";
        var vram = snapshot.VramTelemetryAvailable
            ? $"VRAM trống {FormatBytes(snapshot.AvailableVramBytes)}/{FormatBytes(snapshot.TotalVramBytes)}"
            : snapshot.TotalVramBytes > 0
                ? $"VRAM tổng {FormatBytes(snapshot.TotalVramBytes)} (không có số trống)"
                : "VRAM không dùng";
        return ram + " · " + vram;
    }

    private static OcrResourceDecision Reject(string reason, HardwareResourceSnapshot live) =>
        new(false, reason, FormatSnapshot(live));

    private static (int Gpu, int Cpu) WorkerKinds(string mode, int count) => mode switch
    {
        "gpu" => (count, 0),
        "cpu" => (0, count),
        "hybrid" when count <= 0 => (0, 0),
        "hybrid" when count == 1 => (1, 0),
        "hybrid" => (count - 1, 1),
        _ => throw new ArgumentException("Chế độ OCR nội bộ không hợp lệ.", nameof(mode)),
    };

    private static string NormalizeMode(string? mode, bool nvidiaDetected)
    {
        var value = (mode ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Length == 0 || value == "auto") return nvidiaDetected ? "gpu" : "cpu";
        return value is "gpu" or "cpu" or "hybrid"
            ? value
            : throw new ArgumentException("Chế độ OCR nội bộ không hợp lệ.", nameof(mode));
    }

    private static string FormatBytes(long bytes) => $"{Math.Max(0, bytes) / (double)Gib:0.00} GiB";
}
