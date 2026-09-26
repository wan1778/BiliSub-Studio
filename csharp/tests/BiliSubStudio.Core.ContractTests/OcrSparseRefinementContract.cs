using System.Reflection;
using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

// Refinement cost guard (26/09): Balanced/Fast steady windows must stay
// bounded like Accurate. Before the guard, sparse modes passed the raw
// candidate list (unbounded) while Accurate capped at 4 — so on animated
// dialogue Balanced/Fast did MORE Paddle calls than Accurate, inverting the
// promised speed ranking. The guard caps sparse steady refinement at 6 spread
// frames (longer windows than Accurate's) with earliest/latest always kept,
// so edge discovery survives while the blowup is gone. Transition caps (8/5)
// stay untouched: short-caption confirmation there is deliberate.
internal static class OcrSparseRefinementContract
{
    private static readonly Type Scanner =
        typeof(OcrResult).Assembly.GetType("BiliSubStudio.Core.Ocr.OcrScanner")!;

    private static readonly MethodInfo Cap =
        Scanner.GetMethod("CapRefinementIndexes", BindingFlags.Static | BindingFlags.NonPublic)!
        ?? throw new InvalidOperationException("missing CapRefinementIndexes");

    private static readonly MethodInfo DenseNoise =
        Scanner.GetMethod("IsDenseAdaptiveVisualNoise", BindingFlags.Static | BindingFlags.NonPublic)!
        ?? throw new InvalidOperationException("missing IsDenseAdaptiveVisualNoise");

    public static Task RunAsync()
    {
        SparseSteadyBoundedAtSix();
        AccurateSteadyBoundedAtFour();
        EarliestLatestAlwaysKept();
        EvenSpreadCoversWindow();
        DenseNoiseGate();
        return Task.CompletedTask;
    }

    private static IReadOnlyList<int> InvokeCap(IReadOnlyList<int> candidates, int windowLength, int maximum) =>
        (IReadOnlyList<int>)Cap.Invoke(null, [candidates, windowLength, maximum])!;

    // Animated window: 12 probe-nominated frames in a 20-frame window must
    // come back as at most 6 for sparse modes (was: all 12, unbounded).
    private static void SparseSteadyBoundedAtSix()
    {
        var candidates = new[] { 0, 1, 2, 4, 5, 7, 9, 10, 12, 14, 16, 18 };
        var capped = InvokeCap(candidates, 20, 6);
        Check(capped.Count <= 6, $"sparse steady refinement not bounded (got {capped.Count})");
        Check(capped[0] == 0 && capped[^1] == 18, "sparse cap dropped window edges");
    }

    // Accurate path unchanged: same input caps at 4.
    private static void AccurateSteadyBoundedAtFour()
    {
        var candidates = new[] { 0, 1, 2, 4, 5, 7, 9, 10, 12, 14, 16, 18 };
        var capped = InvokeCap(candidates, 20, 4);
        Check(capped.Count <= 4, $"accurate cap drifted (got {capped.Count})");
        Check(capped[0] == 0 && capped[^1] == 18, "accurate cap dropped window edges");
    }

    private static void EarliestLatestAlwaysKept()
    {
        var capped = InvokeCap([3, 5, 9, 13, 17, 19, 21, 24], 26, 6);
        Check(capped.Contains(3) && capped.Contains(24), "cap must keep earliest/latest candidates");
        Check(capped.SequenceEqual(capped.OrderBy(x => x)), "cap must stay ordered");
        Check(capped.All(x => x >= 0 && x < 26), "cap leaked out-of-window index");
    }

    // Full-window nomination still covers the window after capping.
    private static void EvenSpreadCoversWindow()
    {
        var candidates = Enumerable.Range(0, 20).ToArray();
        var capped = InvokeCap(candidates, 20, 6);
        Check(capped.Count == 6, $"full spread did not fill the cap (got {capped.Count})");
        var maxGap = capped.Zip(capped.Skip(1), (a, b) => b - a).Max();
        Check(maxGap <= 5, $"spread left a {maxGap}-frame hole");
    }

    private static bool InvokeDense(IReadOnlyList<double> scores) =>
        (bool)DenseNoise.Invoke(null, [scores])!;

    private static void DenseNoiseGate()
    {
        Check(InvokeDense([0.5, 0.5, 0.5, 0.5, 0.5, 0.0, 0.0, 0.0, 0.0, 0.0]), "half-changed window must read dense");
        Check(!InvokeDense([0.5, 0.5, 0.5, 0.5, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0]), "below-half changes must not read dense");
        Check(!InvokeDense([0.5, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0]), "single change must not read dense");
        Check(!InvokeDense([0.5, 0.5, 0.5]), "short score list must not read dense");
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
