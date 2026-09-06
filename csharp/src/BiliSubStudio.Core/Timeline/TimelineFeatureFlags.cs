namespace BiliSubStudio.Core.Timeline;

// G2: Timeline V2 stays behind an internal flag until promotion gate G11.
// Default is disabled so the classic path is unchanged. Dev builds opt in
// with BILISUB_TIMELINE_V2=1 (or "true"). The flag is read from the
// environment at call time so tests can isolate each case.
public static class TimelineFeatureFlags
{
    public const string EnvironmentVariable = "BILISUB_TIMELINE_V2";

    public static bool IsEnabled => Parse(Environment.GetEnvironmentVariable(EnvironmentVariable));

    public static bool Parse(string? value) =>
        string.Equals(value?.Trim(), "1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
}
