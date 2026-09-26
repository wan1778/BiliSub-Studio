namespace BiliSubStudio.Core.Ocr;

internal static class OcrTopologyBenchmark
{
    internal static int WorkerTarget(string activeMode, int lanes) =>
        string.Equals(activeMode, "cpu", StringComparison.OrdinalIgnoreCase) ? lanes : Math.Min(2, lanes);

    internal static IReadOnlyList<int> Levels { get; } = [1, 2, 4, 8, 16];
    internal static IReadOnlyList<int> HybridLevels { get; } = [2, 4, 8, 16];

    internal static async Task<int> SelectAsync(
        Func<int, CancellationToken, Task> probe,
        Func<int, CancellationToken, Task> restore,
        Action<int, int, Exception> rejected,
        CancellationToken cancellationToken) =>
        await SelectLevelsAsync(Levels, probe, restore, rejected, cancellationToken);

    internal static async Task<int> SelectForModeAsync(
        string activeMode,
        Func<int, CancellationToken, Task> probe,
        Func<int, CancellationToken, Task> restore,
        Action<int, int, Exception> rejected,
        CancellationToken cancellationToken) =>
        await SelectLevelsAsync(
            string.Equals(activeMode, "hybrid", StringComparison.OrdinalIgnoreCase) ? HybridLevels : Levels,
            probe, restore, rejected, cancellationToken);

    private static async Task<int> SelectLevelsAsync(
        IReadOnlyList<int> levels,
        Func<int, CancellationToken, Task> probe,
        Func<int, CancellationToken, Task> restore,
        Action<int, int, Exception> rejected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(levels);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(restore);
        ArgumentNullException.ThrowIfNull(rejected);
        if (levels.Count == 0 || levels.Any(level => level is < 1 or > 16))
            throw new ArgumentException("OCR topology ladder không hợp lệ.", nameof(levels));

        var best = 0;
        foreach (var level in levels)
        {
            try
            {
                await probe(level, cancellationToken);
                best = level;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                if (best == 0) throw;
                await restore(best, cancellationToken);
                rejected(level, best, error);

                // The exponential ladder finds the useful range quickly, but a
                // failed jump from (for example) four to eight workers must not
                // discard viable 5/6/7-worker topologies. Restore the known
                // stable pool before every descending fallback attempt so a
                // failed throughput probe cannot leak its larger pool into the
                // next resource calculation.
                foreach (var fallback in Enumerable.Range(best + 1, level - best - 1).Reverse())
                {
                    try
                    {
                        await probe(fallback, cancellationToken);
                        return fallback;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception fallbackError)
                    {
                        await restore(best, cancellationToken);
                        rejected(fallback, best, fallbackError);
                    }
                }

                return best;
            }
        }

        // Free-lane refinement (26/09): the exponential ladder can lock the
        // ceiling without ever measuring middle rungs — e.g. 8 → 16 passes on
        // throughput, but 12 is the true peak under contention and 14 beats
        // it. Probe at most two midpoints so auto lands on 10/12/14-class
        // topologies instead of snapping to presets. Same pass/fail protocol
        // (probe throws below useful gain) and same restore discipline, so a
        // failed midpoint always leaves the proven-best pool behind.
        if (best == levels[levels.Count - 1] && levels.Count >= 2)
        {
            var previous = levels[levels.Count - 2];
            var middle = previous + (best - previous) / 2;
            if (middle > previous)
            {
                try
                {
                    await probe(middle, cancellationToken);
                    best = middle;
                    var upper = middle + (levels[levels.Count - 1] - middle) / 2;
                    if (upper > middle)
                    {
                        try
                        {
                            await probe(upper, cancellationToken);
                            best = upper;
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception upperError)
                        {
                            await restore(best, cancellationToken);
                            rejected(upper, best, upperError);
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception middleError)
                {
                    await restore(best, cancellationToken);
                    rejected(middle, best, middleError);
                }
            }
        }

        return best;
    }
}
