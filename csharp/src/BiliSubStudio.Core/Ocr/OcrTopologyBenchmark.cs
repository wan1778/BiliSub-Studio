namespace BiliSubStudio.Core.Ocr;

internal static class OcrTopologyBenchmark
{
    internal static IReadOnlyList<int> Levels { get; } = [1, 2, 4, 8, 16];

    internal static async Task<int> SelectAsync(
        Func<int, CancellationToken, Task> probe,
        Func<int, CancellationToken, Task> restore,
        Action<int, int, Exception> rejected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(restore);
        ArgumentNullException.ThrowIfNull(rejected);

        var best = 0;
        foreach (var level in Levels)
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
                return best;
            }
        }

        return best;
    }
}
