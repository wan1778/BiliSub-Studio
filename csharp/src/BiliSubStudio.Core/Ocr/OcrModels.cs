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
// Provenance giữ exact integer SourcePts + time base. Thiếu/invalid => Unavailable, không đoán.
public sealed record RationalTimeBase([property: JsonPropertyName("num")] int Num, [property: JsonPropertyName("den")] int Den);
public sealed record PixelObservation([property: JsonPropertyName("pts")] double Pts, [property: JsonPropertyName("box")] OcrBox BoxInOcrCrop, [property: JsonPropertyName("roi")] OcrRegion SourceRoi, OcrFrameGeometry? Geometry = null, [property: JsonPropertyName("spts")] long? SourcePts = null, RationalTimeBase? SourceTimeBase = null);
public sealed record OcrCue(double Start, double End, string Text, [property: JsonPropertyName("conf")] double Confidence, [property: JsonPropertyName("support")] int RawSupportCount = 0, [property: JsonPropertyName("csupport")] int ConfidentSupportCount = 0, [property: JsonPropertyName("review")] bool NeedsReview = false, PixelObservation? FirstObservation = null, PixelObservation? LastObservation = null);

public sealed record OcrScanRequest(
    string Path,
    OcrRegion Region,
    string Mode,
    string Device,
    string Parallelism,
    double Sensitivity,
    double Duration,
    double ScanStartOffset = 0);

// Ném khi checkpoint thiếu provenance pixel (cũ/hỏng) nên không đủ điều kiện replay.
// Không bao giờ reconstruct/fabricate bbox/PTS từ Start/End.
public sealed class CheckpointNotEligibleForReplayException : InvalidOperationException
{
    public CheckpointNotEligibleForReplayException(string message) : base(message) { }
    public CheckpointNotEligibleForReplayException(string message, Exception? inner) : base(message, inner) { }
}

// Trace chẩn đoán cho một boundary kề nhau trong targeted range replay.
// IsCandidate=false => ProductionPixelComparer không được gọi (xem ReconcileRangeAsync).
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
    string? Error = null);

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
