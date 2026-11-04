using System.Text.Json.Serialization;

namespace BiliSubStudio.Core.Ocr;

public sealed record OcrRegion(
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y,
    [property: JsonPropertyName("w")] double Width,
    [property: JsonPropertyName("h")] double Height);
public sealed record OcrLine(string Text, double Confidence, int[] Box);
public sealed record OcrBox([property: JsonPropertyName("l")] int Left, [property: JsonPropertyName("t")] int Top, [property: JsonPropertyName("r")] int Right, [property: JsonPropertyName("b")] int Bottom);
public sealed record OcrResult(bool Ok, bool Detected, string Text, double Confidence, IReadOnlyList<OcrLine> Lines, string? Error = null);
// D.1 det/rec split: timing is genuinely what the worker does (no separate cls stage in
// the PP-OCRv6 pipeline). C# accumulates it to decide Phase 2 (det-once + rec-only).
public sealed record OcrPerfSample(double DetMs, double RecMs, int DetBoxes, int RecCrops, int CacheHits, int Images);
// E.3 shadow: extended probe result. BandChangeScores/EdgeDensity may be null
// when an old worker lacks the two fields (backward compatible: shadow goes missing, does not fail).
public sealed record VisualProbeResult(
    IReadOnlyList<double> ChangeScores,
    IReadOnlyList<double>? BandChangeScores,
    IReadOnlyList<double>? EdgeDensity);
// Provenance keeps the exact integer SourcePts + time base. Missing/invalid => Unavailable, no guessing.
public sealed record RationalTimeBase([property: JsonPropertyName("num")] int Num, [property: JsonPropertyName("den")] int Den);
public sealed record PixelObservation([property: JsonPropertyName("pts")] double Pts, [property: JsonPropertyName("box")] OcrBox BoxInOcrCrop, [property: JsonPropertyName("roi")] OcrRegion SourceRoi, OcrFrameGeometry? Geometry = null, [property: JsonPropertyName("spts")] long? SourcePts = null, RationalTimeBase? SourceTimeBase = null);
public sealed record OcrCue(double Start, double End, string Text, [property: JsonPropertyName("conf")] double Confidence, [property: JsonPropertyName("support")] int RawSupportCount = 0, [property: JsonPropertyName("csupport")] int ConfidentSupportCount = 0, [property: JsonPropertyName("review")] bool NeedsReview = false, PixelObservation? FirstObservation = null, PixelObservation? LastObservation = null);
// Exclusion list at SRT export (the file <checkpointkey>.exclude.json beside the checkpoint):
// any cue matching Start (±1ms) + Text exactly is dropped from the SRT file. Not part of the
// checkpoint (does not affect key/resume); deleting the file is the undo.
public sealed record OcrCueExclusion(
    [property: JsonPropertyName("start")] double Start,
    [property: JsonPropertyName("text")] string Text);
// Text-correction list (the file <checkpointkey>.fix.json beside the checkpoint): any cue
// matching Start (±1ms) + old text exactly is replaced with the corrected text at SRT export.
// Not part of the checkpoint (does not affect key/resume); deleting the file is the undo.
public sealed record OcrCueFix(
    [property: JsonPropertyName("start")] double Start,
    [property: JsonPropertyName("old")] string OldText,
    [property: JsonPropertyName("new")] string NewText,
    // Fixes from the ear correcting text (correction) get a review flag at export so
    // the reviewer can check it — old trims/manual edits keep false as before.
    [property: JsonPropertyName("review")] bool FlagReview = false);

public sealed record OcrScanRequest(
    string Path,
    OcrRegion Region,
    string Mode,
    string Device,
    string Parallelism,
    double Sensitivity,
    double Duration,
    double ScanStartOffset = 0);

// Per-video OCR checkpoint recognition profile (06/10): <key>.info.json beside the
// checkpoint, recording the video + exactly the settings that produced the key so a pick-video miss
// still restores ROI/mode and resumes canonically. Public so App can use it.
public sealed record OcrCheckpointVideoInfo(
    string Key,
    string VideoPath,
    long Size,
    long ModUnixNano,
    OcrRegion Region,
    string Mode,
    string Device,
    string Parallelism,
    double Sensitivity,
    double Duration,
    double ScanStartOffset,
    DateTimeOffset SavedUtc);

// Thrown when a checkpoint lacks pixel provenance (old/corrupt), so it is not eligible for replay.
// Never reconstructs/fabricates bbox/PTS from Start/End.
public sealed class CheckpointNotEligibleForReplayException : InvalidOperationException
{
    public CheckpointNotEligibleForReplayException(string message) : base(message) { }
    public CheckpointNotEligibleForReplayException(string message, Exception? inner) : base(message, inner) { }
}

// Diagnostic trace for one adjacent boundary in a targeted range replay.
// IsCandidate=false => ProductionPixelComparer is not called (see ReconcileRangeAsync).
public sealed record BoundaryTrace(
    double LeftStart,
    double LeftEnd,
    string LeftText,
    double RightStart,
    double RightEnd,
    string RightText,
    double GapSeconds,
    bool IsCandidate,
    string CandidateReason,
    PixelCheckResult Pixel,
    double? Ssim,
    double? ForegroundIoU,
    double? EdgeIoU,
    bool Merged);

public enum OcrScanStartMode
{
    Fresh,
    Resume,
}

public sealed record OcrBenchmarkTelemetry(
    int Candidate,
    int LastStable,
    int Maximum,
    int WorkerCount,
    string WorkerKinds,
    string Phase,
    string ResourceSummary = "");

public sealed record OcrScanTelemetry(
    int SegmentLanes,
    int ActiveLanes,
    int CompletedLanes,
    int WorkerCount,
    string WorkerKinds,
    double AggregateProgressPercent,
    double SafeFrontierSeconds,
    int Frames,
    int OcrImages);

public sealed record OcrScanResult(
    IReadOnlyList<OcrCue> Cues,
    int Frames,
    int OcrImages,
    double MediaSeconds,
    double ElapsedSeconds,
    double RealtimeSpeed,
    int ParallelismSelected,
    int CompletedLanes,
    int BoundaryMerges,
    string Decoder,
    int WorkerCount,
    string WorkerKinds,
    double SafeFrontierSeconds,
    int TotalCueCount,
    bool Paused = false);

public sealed record OcrStatus(
    bool Ready,
    string State,
    string DeviceMode,
    string ActiveMode,
    int Workers,
    string WorkerKinds,
    string Engine,
    string Model,
    string? Error = null,
    int LiveWorkers = 0);

public sealed record OcrInferenceTelemetry(
    long OcrBatchCalls,
    long OcrBatchImages,
    long ProbeCalls,
    long ProbeImages,
    long WorkerReplacements,
    long QueueExecuted,
    int QueuePeakDepth,
    double QueueAvgDepth,
    long QueueTotalWaitMs,
    long PlannedRecycles = 0);

public sealed record OcrCheckpointInfo(
    bool Exists,
    int Schema,
    double MediaSeconds,
    int CueCount,
    int ParallelismSelected,
    int CompletedLanes,
    int TotalLanes,
    double ProgressPercent,
    IReadOnlyList<OcrCue> RecentCues,
    IReadOnlyList<OcrCue>? RecoverableCues = null);
