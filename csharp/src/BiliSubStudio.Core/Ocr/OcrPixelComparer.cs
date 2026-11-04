using System.Drawing;

namespace BiliSubStudio.Core.Ocr;

public enum PixelCheckResult { Same, Different, Unavailable }

public sealed record FrameSample(double RequestedPts, double DecodedPts, double Delta);
public sealed record PixelComparison(PixelCheckResult Result, FrameSample Left, FrameSample Right, System.Drawing.Rectangle SharedCrop, double? Ssim, double? ForegroundIoU, double? EdgeIoU);

public interface IPixelComparer
{
    // Only called after the reconciler's generic visual gate (adjacent + temporal continuity
    // in native frames + spatial-compatible + real provenance). No text pattern required:
    // pixel is the only final gate (only Same merges). Never use it to scan/merge
    // arbitrary cue pairs: pixel cannot distinguish same-text different-instance across a long blank,
    // so far/non-continuation repeats never reach here thanks to the temporal gate.
    Task<PixelComparison> CheckAsync(string videoPath, PixelObservation left, PixelObservation right, CancellationToken cancellationToken);
}

public sealed record OcrFrameGeometry(int SourceCropX, int SourceCropY, int SourceCropW, int SourceCropH, int ContentW, int ContentH, int ContentLeft, int ContentTop, int CanvasW, int CanvasH);

public sealed class ProductionPixelComparer : IPixelComparer
{
    public const int OcrInputContractWidth = 1280;
    public const int OcrInputContractHeight = 320;

    private readonly Tools.ToolManager? _tools;
    private readonly Processes.ProcessRunner? _processes;
    private readonly Processes.OwnedProcessGroup? _owner;

    // Cache keyed by (video + SourcePtsA + SourcePtsB + sharedCrop): the same one boundary
    // (trace log + production merge, live + final replay) never decodes again.
    // Instance-scope (one comparer per scan) so it does not leak across scans.
    private readonly Dictionary<(string Video, long First, long Second, System.Drawing.Rectangle Crop), PixelComparison> _comparisonCache = new();
    private readonly object _cacheGate = new();

    public ProductionPixelComparer() { }
    public ProductionPixelComparer(Tools.ToolManager tools, Processes.ProcessRunner processes, Processes.OwnedProcessGroup? owner = null)
    {
        _tools = tools;
        _processes = processes;
        _owner = owner;
    }

    private static PixelComparison UnavailableComparison(FrameSample l, FrameSample r, System.Drawing.Rectangle shared)
        => new(PixelCheckResult.Unavailable, l, r, shared, null, null, null);

    public async Task<PixelComparison> CheckAsync(string videoPath, PixelObservation left, PixelObservation right, CancellationToken cancellationToken)
    {
        var leftSample = new FrameSample(left.Pts, double.NaN, double.NaN);
        var rightSample = new FrameSample(right.Pts, double.NaN, double.NaN);
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
            return UnavailableComparison(leftSample, rightSample, System.Drawing.Rectangle.Empty);
        if (left.Geometry is null || right.Geometry is null)
            return UnavailableComparison(leftSample, rightSample, System.Drawing.Rectangle.Empty);
        if (left.SourcePts is null || right.SourcePts is null)
            return UnavailableComparison(leftSample, rightSample, System.Drawing.Rectangle.Empty);
        if (_tools is null || _processes is null)
            return UnavailableComparison(leftSample, rightSample, System.Drawing.Rectangle.Empty);
        var probe = await OcrFrameExtractor.ProbeAsync(_tools, _processes, videoPath, cancellationToken);
        if (probe is null) return UnavailableComparison(leftSample, rightSample, System.Drawing.Rectangle.Empty);
        var probeTimeBase = new RationalTimeBase(probe.TimeBaseNum, probe.TimeBaseDen);
        if (!OcrFrameExtractor.EquivalentTimeBase(left.SourceTimeBase, probeTimeBase) ||
            !OcrFrameExtractor.EquivalentTimeBase(right.SourceTimeBase, probeTimeBase))
            return UnavailableComparison(leftSample, rightSample, System.Drawing.Rectangle.Empty);
        if (!OcrFrameExtractor.PtsTimeMatches(left.SourcePts.Value, left.SourceTimeBase!, left.Pts) ||
            !OcrFrameExtractor.PtsTimeMatches(right.SourcePts.Value, right.SourceTimeBase!, right.Pts))
            return UnavailableComparison(leftSample, rightSample, System.Drawing.Rectangle.Empty);
        var rectA = ToSourceRect(left, probe.Width, probe.Height);
        var rectB = ToSourceRect(right, probe.Width, probe.Height);
        if (rectA.IsEmpty || rectB.IsEmpty)
            return UnavailableComparison(leftSample, rightSample, System.Drawing.Rectangle.Empty);
        var shared = OcrCropPlanner.PlanSharedCrop(rectA, rectB, probe.Width, probe.Height);
        if (shared.IsEmpty)
            return UnavailableComparison(leftSample, rightSample, shared);
        lock (_cacheGate)
        {
            if (_comparisonCache.TryGetValue(CacheKey(videoPath, left, right, shared), out var cached))
                return cached;
        }
        string ffmpeg;
        try { ffmpeg = await _tools.EnsureFfmpegAsync(cancellationToken); }
        catch { return UnavailableComparison(leftSample, rightSample, shared); }
        var frameA = await OcrFrameExtractor.ExtractFrameAsync(ffmpeg, videoPath, left.Pts, left.SourcePts, shared, probe, _processes, _owner, cancellationToken);
        var frameB = await OcrFrameExtractor.ExtractFrameAsync(ffmpeg, videoPath, right.Pts, right.SourcePts, shared, probe, _processes, _owner, cancellationToken);
        if (frameA is null || frameB is null)
        {
            var la = frameA is not null ? new FrameSample(frameA.RequestedPts, frameA.DecodedPts, frameA.Delta) : leftSample;
            var rb = frameB is not null ? new FrameSample(frameB.RequestedPts, frameB.DecodedPts, frameB.Delta) : rightSample;
            return UnavailableComparison(la, rb, shared);
        }
        leftSample = new FrameSample(frameA.RequestedPts, frameA.DecodedPts, frameA.Delta);
        rightSample = new FrameSample(frameB.RequestedPts, frameB.DecodedPts, frameB.Delta);
        var tolerance = OcrFrameExtractor.ToleranceFor(probe);
        if (tolerance is null || Math.Abs(frameA.Delta) > tolerance || Math.Abs(frameB.Delta) > tolerance)
            return UnavailableComparison(leftSample, rightSample, shared);
        if (!OcrFrameExtractor.PtsTimeMatches(left.SourcePts.Value, probeTimeBase, frameA.DecodedPts) ||
            !OcrFrameExtractor.PtsTimeMatches(right.SourcePts.Value, probeTimeBase, frameB.DecodedPts))
            return UnavailableComparison(leftSample, rightSample, shared);
        // Metrics + threshold v1 classification (Same/Different/abstain).
        double? ssim = null; double? fg = null; double? edge = null;
        try
        {
            if (frameA.Width == frameB.Width && frameA.Height == frameB.Height
                && frameA.Pixels.Length == frameB.Pixels.Length
                && frameA.Width == shared.Width && frameA.Height == shared.Height)
            {
                ssim = OcrPixelMetrics.Ssim(frameA.Pixels, frameB.Pixels, shared.Width, shared.Height);
                fg = OcrPixelMetrics.ForegroundIoU(frameA.Pixels, frameB.Pixels, shared.Width, shared.Height);
                edge = OcrPixelMetrics.EdgeIoU(frameA.Pixels, frameB.Pixels, shared.Width, shared.Height);
            }
        }
        catch { ssim = null; fg = null; edge = null; }
        var decided = new PixelComparison(Classify(ssim, edge), leftSample, rightSample, shared, ssim, fg, edge);
        lock (_cacheGate) _comparisonCache[CacheKey(videoPath, left, right, shared)] = decided;
        return decided;
    }

    private static (string Video, long First, long Second, System.Drawing.Rectangle Crop) CacheKey(
        string videoPath, PixelObservation left, PixelObservation right, System.Drawing.Rectangle shared)
        => (videoPath, left.SourcePts!.Value, right.SourcePts!.Value, shared);

    // Sidecar pxv: export CLEAR verdicts for the cache file (path dropped — file identity covers it),
    // and reload them at the start of the next scan. Only Same/Different (Unavailable is recomputed, see OcrSidecar).
    // Metrics (ssim/fg/edge) are NOT persisted: the verdict is enough for merge; the diagnostic trace
    // (ReconcileRangeAsync) shows empty for sidecar hits — acceptable.
    public IReadOnlyList<(long First, long Second, System.Drawing.Rectangle Crop, PixelCheckResult Result)> Snapshot()
    {
        lock (_cacheGate)
            return _comparisonCache
                .Where(pair => pair.Value.Result is PixelCheckResult.Same or PixelCheckResult.Different)
                .Select(pair => (pair.Key.First, pair.Key.Second, pair.Key.Crop, pair.Value.Result))
                .ToArray();
    }

    public int Seed(string videoPath, IEnumerable<(long First, long Second, System.Drawing.Rectangle Crop, PixelCheckResult Result)> entries)
    {
        var added = 0;
        lock (_cacheGate)
        {
            foreach (var (first, second, crop, result) in entries)
            {
                if (result is not (PixelCheckResult.Same or PixelCheckResult.Different)) continue;
                if (crop.Right <= crop.Left || crop.Bottom <= crop.Top || crop.Left < 0 || crop.Top < 0) continue;
                var key = (videoPath, first, second, crop);
                if (_comparisonCache.ContainsKey(key)) continue;
                _comparisonCache[key] = new PixelComparison(result,
                    new FrameSample(double.NaN, double.NaN, double.NaN),
                    new FrameSample(double.NaN, double.NaN, double.NaN),
                    crop, null, null, null);
                added++;
            }
        }
        return added;
    }

    // Threshold production v1 (calibration 30 + 20 hard samples, line-bbox):
    // SAME: SSIM min 0.626, Edge min 0.857. DIFFERENT touching/handoff: SSIM max 0.196, Edge max 0.298.
    // ForegroundIoU is telemetry only (SAME min 0.477 vs DIFFERENT handoff 0.525, no clean separation).
    private const double SameMinSsim = 0.55;
    private const double SameMinEdgeIou = 0.80;
    private const double DifferentMaxSsim = 0.30;
    private const double DifferentMaxEdgeIou = 0.40;

    public static PixelCheckResult Classify(double? ssim, double? edgeIou)
    {
        if (ssim is null || edgeIou is null) return PixelCheckResult.Unavailable;
        if (ssim >= SameMinSsim && edgeIou >= SameMinEdgeIou) return PixelCheckResult.Same;
        if (ssim <= DifferentMaxSsim && edgeIou <= DifferentMaxEdgeIou) return PixelCheckResult.Different;
        return PixelCheckResult.Unavailable;
    }

    // Single source of truth: the OCR filter and the comparer share TryPlanOcrFrame.
    public static OcrFrameGeometry? TryPlanOcrFrame(int sourceWidth, int sourceHeight, OcrRegion roi, int canvasW = 1280, int canvasH = 320)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || canvasW <= 0 || canvasH <= 0) return null;
        if (roi.Width <= 0 || roi.Height <= 0) return null;
        var cropX = (int)Math.Floor(sourceWidth * roi.X);
        var cropY = (int)Math.Floor(sourceHeight * roi.Y);
        var cropW = (int)Math.Ceiling(sourceWidth * roi.Width);
        var cropH = (int)Math.Ceiling(sourceHeight * roi.Height);
        cropX = Math.Clamp(cropX, 0, sourceWidth - 1);
        cropY = Math.Clamp(cropY, 0, sourceHeight - 1);
        cropW = Math.Min(cropW, sourceWidth - cropX);
        cropH = Math.Min(cropH, sourceHeight - cropY);
        if (cropW <= 0 || cropH <= 0) return null;
        var scale = Math.Min(canvasW / (double)cropW, canvasH / (double)cropH);
        if (!double.IsFinite(scale) || scale <= 0) return null;
        var contentW = Math.Max(1, (int)Math.Floor(cropW * scale));
        var contentH = Math.Max(1, (int)Math.Floor(cropH * scale));
        if (contentW <= 0 || contentH <= 0 || contentW > canvasW || contentH > canvasH) return null;
        var contentL = (canvasW - contentW) / 2;
        var contentT = (canvasH - contentH) / 2;
        return new OcrFrameGeometry(cropX, cropY, cropW, cropH, contentW, contentH, contentL, contentT, canvasW, canvasH);
    }

    public static OcrFrameGeometry PlanOcrFrame(int sourceWidth, int sourceHeight, OcrRegion roi, int canvasW = 1280, int canvasH = 320)
        => TryPlanOcrFrame(sourceWidth, sourceHeight, roi, canvasW, canvasH) ?? throw new ArgumentException("Invalid OCR geometry");

    public static string BuildOcrFilter(OcrFrameGeometry g, bool enhanced)
    {
        // Uses exact integers from the planner, not force_original_aspect_ratio (avoids guessing FFmpeg rounding).
        var baseFilter = $"crop={g.SourceCropW}:{g.SourceCropH}:{g.SourceCropX}:{g.SourceCropY},scale={g.ContentW}:{g.ContentH}:flags=fast_bilinear,pad={g.CanvasW}:{g.CanvasH}:{g.ContentLeft}:{g.ContentTop}:black";
        return enhanced ? baseFilter + ",eq=contrast=1.25:brightness=0.03,unsharp=5:5:0.8" : baseFilter;
    }

    public static System.Drawing.Rectangle ToSourceRect(PixelObservation obs, int sourceWidth, int sourceHeight)
    {
        // Uses the exact Geometry stored for that very frame. Null (fallback/old checkpoint) => Unavailable, no guessing recompute.
        var g = obs.Geometry;
        if (g is null) return System.Drawing.Rectangle.Empty;
        if (obs.SourceRoi is null) return System.Drawing.Rectangle.Empty;
        if (sourceWidth <= 0 || sourceHeight <= 0) return System.Drawing.Rectangle.Empty;
        var box = obs.BoxInOcrCrop;
        // Intersect with ContentRect first. Empty => Unavailable (bbox inside padding).
        var ix1 = Math.Max(box.Left, g.ContentLeft);
        var iy1 = Math.Max(box.Top, g.ContentTop);
        var ix2 = Math.Min(box.Right, g.ContentLeft + g.ContentW);
        var iy2 = Math.Min(box.Bottom, g.ContentTop + g.ContentH);
        if (ix2 <= ix1 || iy2 <= iy1) return System.Drawing.Rectangle.Empty;
        var cx1 = (ix1 - g.ContentLeft) / (double)g.ContentW;
        var cy1 = (iy1 - g.ContentTop) / (double)g.ContentH;
        var cx2 = (ix2 - g.ContentLeft) / (double)g.ContentW;
        var cy2 = (iy2 - g.ContentTop) / (double)g.ContentH;
        // Partial-overlap: the intersecting part still maps correctly, the padding part is cut away above.
        var srcX1 = g.SourceCropX + cx1 * g.SourceCropW;
        var srcY1 = g.SourceCropY + cy1 * g.SourceCropH;
        var srcX2 = g.SourceCropX + cx2 * g.SourceCropW;
        var srcY2 = g.SourceCropY + cy2 * g.SourceCropH;
        var left = (int)Math.Floor(srcX1);
        var top = (int)Math.Floor(srcY1);
        var right = (int)Math.Ceiling(srcX2);
        var bottom = (int)Math.Ceiling(srcY2);
        left = Math.Clamp(left, 0, sourceWidth - 1);
        top = Math.Clamp(top, 0, sourceHeight - 1);
        right = Math.Clamp(right, left + 1, sourceWidth);
        bottom = Math.Clamp(bottom, top + 1, sourceHeight);
        if (right <= left || bottom <= top) return System.Drawing.Rectangle.Empty;
        return System.Drawing.Rectangle.FromLTRB(left, top, right, bottom);
    }
}

public static class OcrCropPlanner
{
    public static System.Drawing.Rectangle PlanSharedCrop(System.Drawing.Rectangle bboxA, System.Drawing.Rectangle bboxB, int sourceWidth, int sourceHeight)
    {
        if (bboxA.IsEmpty || bboxB.IsEmpty) return System.Drawing.Rectangle.Empty;
        if (sourceWidth <= 0 || sourceHeight <= 0) return System.Drawing.Rectangle.Empty;
        var union = System.Drawing.Rectangle.Union(bboxA, bboxB);
        var left = Math.Max(0, union.Left - 2);
        var top = Math.Max(0, union.Top - 2);
        var right = Math.Min(sourceWidth, union.Right + 2);
        var bottom = Math.Min(sourceHeight, union.Bottom + 2);
        if (right <= left || bottom <= top) return System.Drawing.Rectangle.Empty;
        return System.Drawing.Rectangle.FromLTRB(left, top, right, bottom);
    }
}
