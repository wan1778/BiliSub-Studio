namespace BiliSubStudio.Core.Ocr;

internal static class OcrTopologyBenchmark
{
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

        return best;
    }
}
