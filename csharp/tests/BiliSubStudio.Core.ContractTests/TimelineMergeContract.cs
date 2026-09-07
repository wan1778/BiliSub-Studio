using BiliSubStudio.Core.IO;
using BiliSubStudio.Core.Jobs;
using BiliSubStudio.Core.Timeline;

namespace BiliSubStudio.Core.ContractTests;

internal static class TimelineMergeContract
{
    public static Task RunAsync()
    {
        KillSwitchMatrix();
        ExportWiringPins();
        return Task.CompletedTask;
    }

    private static void KillSwitchMatrix()
    {
        var previous = Environment.GetEnvironmentVariable(TimelineFeatureFlags.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(TimelineFeatureFlags.EnvironmentVariable, null);
            Check(!TimelineFeatureFlags.IsSuppressed, "unset flag must not suppress the entry");
            Check(TimelineFeatureFlags.IsEntryVisible, "entry must be visible by default");
            foreach (var value in new[] { "1", "true", " True ", "anything-else", "" })
            {
                Environment.SetEnvironmentVariable(TimelineFeatureFlags.EnvironmentVariable, value);
                Check(!TimelineFeatureFlags.IsSuppressed,
                    $"only explicit 0 suppresses the entry, got '{value}'");
                Check(TimelineFeatureFlags.IsEntryVisible, "entry must stay visible without opt-out");
            }
            Environment.SetEnvironmentVariable(TimelineFeatureFlags.EnvironmentVariable, "0");
            Check(TimelineFeatureFlags.IsSuppressed, "explicit 0 must suppress the entry");
            Check(!TimelineFeatureFlags.IsEntryVisible, "entry must hide on kill-switch");
            Environment.SetEnvironmentVariable(TimelineFeatureFlags.EnvironmentVariable, " 0 ");
            Check(TimelineFeatureFlags.IsSuppressed, "kill-switch must trim whitespace");
        }
        finally
        {
            Environment.SetEnvironmentVariable(TimelineFeatureFlags.EnvironmentVariable, previous);
        }
    }

    private static void ExportWiringPins()
    {
        // The exact export filename policy the promoted page applies.
        Check(FileNamePolicy.Sanitize("Dự án hè_timeline.mp4", "Timeline.mp4")
                == "Dự án hè_timeline.mp4",
            "merge export filename policy drifted");
        Check(FileNamePolicy.Sanitize("  ", "Timeline.mp4") == "Timeline.mp4",
            "merge export filename fallback drifted");

        // The exact job shape the promoted page creates for every export.
        using var jobs = new JobManager();
        var job = jobs.Create("timeline-export", cleanupAwareCancel: true);
        try
        {
            Check(job.Kind == "timeline-export", "merge export job kind drifted");
            Check(job.CleanupAwareCancel, "merge export job must stay cleanup-aware");
            Check(!job.Snapshot().Done, "fresh merge export job must not be terminal");
        }
        finally
        {
            if (!job.Snapshot().Done) job.CancelComplete("merge wiring pin done");
            job.Dispose();
        }
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
