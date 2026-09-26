using System.Reflection;
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

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
