using System.Text.Json.Serialization;

namespace BiliSubStudio.Core.Ocr;

public sealed record OcrRegion(
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y,
    [property: JsonPropertyName("w")] double Width,
    [property: JsonPropertyName("h")] double Height);
public sealed record OcrLine(string Text, double Confidence, int[] Box);
public sealed record OcrResult(bool Ok, bool Detected, string Text, double Confidence, IReadOnlyList<OcrLine> Lines, string? Error = null);
public sealed record OcrCue(double Start, double End, string Text, [property: JsonPropertyName("conf")] double Confidence);

public sealed record OcrScanRequest(
    string Path,
    OcrRegion Region,
    string Mode,
    string Device,
    string Parallelism,
    double Sensitivity,
    double Duration);

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
    bool Paused = false);

public sealed record OcrStatus(
    bool Ready,
    string State,
    string DeviceMode,
    string ActiveMode,
    int Workers,
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
    IReadOnlyList<OcrCue> RecentCues);
