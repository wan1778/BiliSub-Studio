using System.Reflection;
using BiliSubStudio.Core.Hardware;
using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

// Auto free-lane refinement (26/09): after the exponential ladder locks the
// ceiling, up to two midpoints are measured so auto lands on 10/12/14-class
// topologies instead of snapping to presets. Same pass/fail protocol as the
// ladder (probe throws below useful gain) and same restore discipline — a
// failed midpoint always leaves the proven-best pool behind.
internal static class OcrTopologyRefineContract
{
    private static readonly Type Benchmark =
        typeof(OcrResult).Assembly.GetType("BiliSubStudio.Core.Ocr.OcrTopologyBenchmark")!;

    private static readonly MethodInfo SelectLevels =
        Benchmark.GetMethod("SelectLevelsAsync", BindingFlags.Static | BindingFlags.NonPublic)!
        ?? throw new InvalidOperationException("missing SelectLevelsAsync");

    private sealed class Script
    {
        public readonly List<int> Probed = new();
        public readonly List<int> Restored = new();
        public readonly List<(int Failed, int Best)> Rejected = new();
        public readonly HashSet<int> Failures = new();

        public Script(params int[] failLevels)
        {
            foreach (var level in failLevels) Failures.Add(level);
        }

        public Task Probe(int level, CancellationToken token)
        {
            Probed.Add(level);
            if (Failures.Contains(level)) throw new InvalidOperationException($"no {level}");
            return Task.CompletedTask;
        }

        public Task Restore(int best, CancellationToken token)
        {
            Restored.Add(best);
            return Task.CompletedTask;
        }

        public void Reject(int failed, int best, Exception error) => Rejected.Add((failed, best));
    }

    private static Task<int> Select(IReadOnlyList<int> levels, Script script)
    {
        var task = (Task<int>)SelectLevels.Invoke(null, [levels, (Func<int, CancellationToken, Task>)script.Probe, (Func<int, CancellationToken, Task>)script.Restore, (Action<int, int, Exception>)script.Reject, CancellationToken.None])!;
        return task;
    }

    public static async Task RunAsync()
    {
        await LocksTwelveThenFourteenAsync();
        await KeepsCeilingWhenMiddleFailsAsync();
        await SkipsRefinementBelowCeilingAsync();
        await DegenerateLadderSkipsAsync();
        SmallTierTargetAndGate();
    }

    // Ladder passes everything, 12 passes, 14 fails → 12 with restore(12).
    private static async Task LocksTwelveThenFourteenAsync()
    {
        var script = new Script(14);
        var best = await Select([1, 2, 4, 8, 16], script);
        Check(best == 12, $"refinement did not lock 12 (got {best})");
        Check(script.Probed.Contains(12) && script.Probed.Contains(14), "refinement did not measure 12 then 14");
        Check(script.Restored[^1] == 12, "failed 14 did not restore proven 12");
        Check(script.Rejected.Contains((14, 12)), "failed 14 not reported against 12");
    }

    // Ladder passes everything, 12 fails → ceiling 16 kept with restore(16).
    private static async Task KeepsCeilingWhenMiddleFailsAsync()
    {
        var script = new Script(12);
        var best = await Select([1, 2, 4, 8, 16], script);
        Check(best == 16, $"failed midpoint must keep ceiling 16 (got {best})");
        Check(script.Probed.Contains(12), "refinement did not measure 12");
        Check(!script.Probed.Contains(14), "refinement must stop after first midpoint fails");
        Check(script.Restored[^1] == 16, "failed 12 did not restore proven 16");
        Check(script.Rejected.Contains((12, 16)), "failed 12 not reported against 16");
    }

    // 16 fails, fallbacks 15..9 fail → 8 with no refinement probes.
    // Ladder probes {1,2,4,8,16} + fallback {15..9} = 12 probes, all pre-existing.
    private static async Task SkipsRefinementBelowCeilingAsync()
    {
        var script = new Script(16, 15, 14, 13, 12, 11, 10, 9);
        var best = await Select([1, 2, 4, 8, 16], script);
        Check(best == 8, $"fallback must lock 8 (got {best})");
        Check(script.Probed.Count == 12, $"unexpected probe count (got {script.Probed.Count})");
    }

    // Degenerate ladder [1,2]: middle collapses to previous → no extra probes.
    private static async Task DegenerateLadderSkipsAsync()
    {
        var script = new Script();
        var best = await Select([1, 2], script);
        Check(best == 2, $"degenerate ladder must lock 2 (got {best})");
        Check(script.Probed.Count == 2, $"degenerate ladder must not refine (got {script.Probed.Count} probes)");
    }

    // Small-tier worker target + VRAM/RAM grow gate (pure, no GPU needed):
    // cap of 4 for every lane>=4, floor of 1; grow only when telemetry is present + free>=3GB +
    // a big enough card + spare RAM + not already enough workers. (28/09: bar raised 1GB->3GB because 16
    // NVDEC lanes + 4 workers OOM'd on a 4GB card. 22/10 field: 4GB card + 8
    // NVDEC lanes + 4 workers OOM'd again (OpenCV alloc storm 14:24) and pool
    // churn ran at 0.23x — so growing needs card >= 6GB + free RAM >= 4GB.)
    private static void SmallTierTargetAndGate()
    {
        var target = Benchmark.GetMethod("SmallTierWorkerTarget", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing SmallTierWorkerTarget");
        int Target(int lanes) => (int)target.Invoke(null, [lanes])!;
        Check(Target(1) == 1, "1 lane phải 1 worker");
        Check(Target(2) == 2, "2 lane phải 2 worker");
        Check(Target(8) == 4, "8 lane small-tier phải 4 worker");
        Check(Target(16) == 4, "16 lane small-tier vẫn cap 4");
        Check(Target(0) == 1, "lane 0 phải floor 1");
        Check(Target(99) == 4, "lane lớn vẫn cap 4");
        var gate = Benchmark.GetMethod("ShouldGrowSmallTier", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing ShouldGrowSmallTier");
        bool Grow(bool telemetry, long freeBytes, long totalVram, long freeRam, int current, int lanes) =>
            (bool)gate.Invoke(null, [telemetry, freeBytes, totalVram, freeRam, current, lanes])!;
        const long gb = 1024L * 1024 * 1024;
        Check(!Grow(false, 8 * gb, 8 * gb, 8 * gb, 2, 8), "không telemetry không được grow mù");
        Check(!Grow(true, 2 * gb, 8 * gb, 8 * gb, 2, 8), "dưới 3GB không được grow");
        Check(Grow(true, 3 * gb, 8 * gb, 8 * gb, 2, 8), "đủ 3GB + card 8GB + RAM dư thì phải grow");
        Check(!Grow(true, 8 * gb, 8 * gb, 8 * gb, 2, 16), "16 lane không grow (NVDEC VRAM)");
        Check(!Grow(true, 2 * gb, 8 * gb, 8 * gb, 4, 8), "đủ worker rồi không grow thừa");
        Check(!Grow(true, 2 * gb, 8 * gb, 8 * gb, 2, 2), "2 lane đủ 2 worker rồi");
        // 22/10 field regression: a 4 GiB card (RTX 3050 Laptop) + 8 NVDEC lanes
        // + 4 small-tier workers made probes die from lack of VRAM and caused pool churn.
        Check(!Grow(true, 3 * gb, 4 * gb, 8 * gb, 2, 8), "card 4GB không được grow small tier");
        Check(!Grow(true, 3 * gb, 8 * gb, 2 * gb, 2, 8), "RAM trống 2GB không được grow small tier");
        SmallTierBlockReason(4 * gb, 3 * gb, 8 * gb);
    }

    // The user must be able to read the reason 2 workers are kept (scan log) instead of thinking the app froze.
    private static void SmallTierBlockReason(long totalVram, long freeVram, long freeRam)
    {
        var describe = typeof(OcrManager).GetMethod("DescribeSmallTierGrowBlock", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing DescribeSmallTierGrowBlock");
        var reason = (string)describe.Invoke(null, [new HardwareResourceSnapshot(32L * 1024 * 1024 * 1024, freeRam, true, totalVram, freeVram), 8])!;
        Check(reason.Length > 0, "phải có lý do khi chặn grow");
        Check(reason.Contains("GiB"), $"lý do chặn grow phải nói rõ mức VRAM: {reason}");
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
