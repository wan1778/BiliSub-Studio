using System.Runtime.CompilerServices;
using BiliSubStudio.Core.Diagnostics;
using BiliSubStudio.Core.Jobs;
using BiliSubStudio.Core.Maintenance;

namespace BiliSubStudio.Core.ContractTests;

internal static class ApplicationLoggingContract
{
    [ModuleInitializer]
    internal static void Verify()
    {
        const string sensitive = @"Cookie: SESSDATA=secret token=abc C:\Users\Alice\Videos\x.mp4 https://x.test/?auth=hidden&ok=1";
        const string expected = @"Cookie=[ĐÃ ẨN] token=[ĐÃ ẨN] C:\Users\[ĐÃ ẨN]\Videos\x.mp4 https://x.test/?auth=[ĐÃ ẨN]&ok=1";
        var sanitized = BugReportService.Sanitize(sensitive);
        if (!string.Equals(sanitized, expected, StringComparison.Ordinal))
            throw new InvalidOperationException("shared LogRedactor changed the locked bug-report sanitization contract: " + sanitized);

        var root = Path.Combine(Path.GetTempPath(), "BiliSubStudio-ApplicationLogContract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using (var log = new ApplicationLog(root))
            {
                AppLogEntry? lastEntry = null;
                log.EntryAdded += entry => lastEntry = entry;
                log.Info("contract", sensitive);

                Exception failure;
                try
                {
                    ThrowNestedFailure();
                    throw new InvalidOperationException("logging contract fixture did not throw");
                }
                catch (Exception error)
                {
                    failure = error;
                }

                using var job = new AppJob("logging-contract-job", "translation", applicationLog: log);
                job.Finish(failure, "job failed token=abc");
                log.Flush(TimeSpan.FromSeconds(5));

                var memory = string.Join('\n', log.Snapshot().Select(entry => entry.Message));
                AssertRedacted(memory, "in-memory ApplicationLog");
                if (!memory.Contains(nameof(InvalidOperationException), StringComparison.Ordinal)
                    || !memory.Contains(nameof(ThrowNestedFailure), StringComparison.Ordinal))
                    throw new InvalidOperationException("AppJob.Finish no longer records the full exception/stack trace");
                if (lastEntry is null || lastEntry.Level != AppLogLevel.Error)
                    throw new InvalidOperationException("ApplicationLog EntryAdded did not receive the job error");
            }

            var disk = File.ReadAllText(Path.Combine(root, "Logs", "application.log"));
            AssertRedacted(disk, "on-disk ApplicationLog");
            if (!disk.Contains(nameof(InvalidOperationException), StringComparison.Ordinal)
                || !disk.Contains(nameof(ThrowNestedFailure), StringComparison.Ordinal))
                throw new InvalidOperationException("application.log is missing the full exception/stack trace");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void ThrowNestedFailure()
    {
        try
        {
            throw new IOException("SESSDATA=secret");
        }
        catch (Exception inner)
        {
            throw new InvalidOperationException("token=abc", inner);
        }
    }

    private static void AssertRedacted(string value, string surface)
    {
        foreach (var secret in new[] { "secret", "abc", "Alice", "hidden" })
        {
            if (value.Contains(secret, StringComparison.Ordinal))
                throw new InvalidOperationException($"{surface} leaked sensitive value: {secret}");
        }
    }
}
