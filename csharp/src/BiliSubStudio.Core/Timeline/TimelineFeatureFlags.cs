namespace BiliSubStudio.Core.Timeline;

// G2: Timeline V2 stayed behind an internal flag until promotion.
// G14 (DEC-024, user-approved): the Timeline entry is visible by default and
// the flag remains only as an emergency kill-switch (BILISUB_TIMELINE_V2=0
// hides the entry). IsEnabled keeps its G2 opt-in meaning for compatibility.
// The flag is read from the environment at call time so tests can isolate
// each case.
public static class TimelineFeatureFlags
{
    public const string EnvironmentVariable = "BILISUB_TIMELINE_V2";

    public static bool IsEnabled => Parse(Environment.GetEnvironmentVariable(EnvironmentVariable));

    public static bool IsSuppressed =>
        string.Equals(Environment.GetEnvironmentVariable(EnvironmentVariable)?.Trim(), "0",
            StringComparison.OrdinalIgnoreCase);

    public static bool IsEntryVisible => !IsSuppressed;

    public static bool Parse(string? value) =>
        string.Equals(value?.Trim(), "1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
}
