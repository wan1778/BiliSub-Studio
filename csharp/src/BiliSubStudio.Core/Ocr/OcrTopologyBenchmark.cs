namespace BiliSubStudio.Core.Ocr;

internal static class OcrTopologyBenchmark
{
    internal static int WorkerTarget(string activeMode, int lanes) =>
        string.Equals(activeMode, "cpu", StringComparison.OrdinalIgnoreCase) ? lanes : Math.Min(2, lanes);

    // Small-tier pools run light models (~100MB/worker idle measured on a 4GB
    // card): headroom for more workers than the Medium cap. Hard cap stays 4
    // (worker protocol + BatchCoalesceCap parity). Growth is VRAM-gated per
    // scan (ShouldGrowSmallTier); never assumed from topology alone.
    internal const int SmallTierMaxWorkers = 4;
    // VRAM free còn lại sau pool small ấm mới được grow (đo thật +100MB/worker
    // idle, +~380MB lúc inference batch4 — bar 1GB dư biên ~2x).
    // 28/09 KHẨN: 16 lane NVDEC (~1.2GB) + 4 worker OOM trên card 4GB (storm
    // 16:18, kẹt 0.01×, VRAM còn 61MB) — nâng bar lên 3GB để small-tier KHÔNG
    // bao giờ grow trên card 4GB: về behaviour 2 worker ổn định đã chạy xong
    // 11222 cue. Tính toán ngân sách VRAM theo lane (NVDEC/lane) làm sau khi
    // máy ổn định, không mổ benchmark lúc này.
    internal const long SmallTierGrowReserveBytes = 3L * 1024 * 1024 * 1024;

    // 22/10 field: the grow decision is taken before the FFmpeg lanes hold
    // their NVDEC buffers, so "3 GiB free" on a 4 GiB card looked healthy and
    // small tier grew 2 -> 4. Eight NVDEC lanes plus four mobile workers then
    // exhausted the card: the visual probe died with an OpenCV allocation
    // storm (14:24:37) and the churned pool ran at 0.23x realtime while the
    // commit limit was 42.8/44.4 GiB. A 4 GiB card can host the stable
    // 2-worker pool, not 4 workers plus 8 decoders, so growing needs a card
    // with room for both roles and enough free RAM for the extra workers.
    internal const long SmallTierGrowMinTotalVramBytes = 6L * 1024 * 1024 * 1024;
    internal const long SmallTierGrowMinFreeRamBytes = 4L * 1024 * 1024 * 1024;

    internal static int SmallTierWorkerTarget(int lanes) =>
        Math.Min(SmallTierMaxWorkers, Math.Max(1, lanes));

    // Chỉ grow khi lane <= 8 (28/09: 16 lane NVDEC ~1.2GB VRAM + 4 worker là
    // tràn card 4GB dù lúc commit còn trống — storm 16:18, kẹt 0.01×. Tổng việc
    // OCR không tăng theo lane (lane chia đoạn, worker mới là nút cổ chai) nên
    // 16 lane + 2 worker vẫn xong như 8 lane, chỉ chậm hơn grow; đổi lại sống.)
    internal const int SmallTierMaxGrowLanes = 8;

    internal static bool ShouldGrowSmallTier(
        bool telemetryAvailable,
        long availableBytes,
        long totalVramBytes,
        long availableRamBytes,
        int currentWorkers,
        int lanes) =>
        telemetryAvailable
        && lanes <= SmallTierMaxGrowLanes
        && availableBytes >= SmallTierGrowReserveBytes
        && totalVramBytes >= SmallTierGrowMinTotalVramBytes
        && availableRamBytes >= SmallTierGrowMinFreeRamBytes
        && currentWorkers < SmallTierWorkerTarget(lanes);

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
