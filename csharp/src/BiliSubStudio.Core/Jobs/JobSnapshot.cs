namespace BiliSubStudio.Core.Jobs;

public sealed record JobSnapshot(
    string Id,
    string Kind,
    string Status,
    double Progress,
    string Message,
    IReadOnlyList<string> Logs,
    int LogNext,
    bool Done,
    string? Error,
    object? Result,
    bool PauseSupported,
    bool PauseRequested,
    double BytesPerSecond = 0,
    int ActiveConnections = 0,
    bool? RangeSupported = null);
