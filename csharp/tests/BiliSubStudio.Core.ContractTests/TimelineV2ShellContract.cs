using BiliSubStudio.Core.Configuration;
using BiliSubStudio.Core.Timeline;

namespace BiliSubStudio.Core.ContractTests;

internal static class TimelineV2ShellContract
{
    public static async Task RunAsync()
    {
        Check(!TimelineFeatureFlags.Parse(null), "timeline flag must default to disabled");
        Check(!TimelineFeatureFlags.Parse(""), "timeline flag must reject empty value");
        Check(!TimelineFeatureFlags.Parse("0"), "timeline flag must reject 0");
        Check(TimelineFeatureFlags.Parse("1"), "timeline flag must accept 1");
        Check(TimelineFeatureFlags.Parse("true"), "timeline flag must accept true");

        var previous = Environment.GetEnvironmentVariable(TimelineFeatureFlags.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(TimelineFeatureFlags.EnvironmentVariable, null);
            Check(!TimelineFeatureFlags.IsEnabled, "timeline shell must stay disabled without opt-in");
            Environment.SetEnvironmentVariable(TimelineFeatureFlags.EnvironmentVariable, "1");
            Check(TimelineFeatureFlags.IsEnabled, "timeline shell must enable with BILISUB_TIMELINE_V2=1");
        }
        finally
        {
            Environment.SetEnvironmentVariable(TimelineFeatureFlags.EnvironmentVariable, previous);
        }

        var testRoot = Path.Combine(Path.GetTempPath(), "bilisub-timeline-v2-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new TimelineProjectStoreV2(Path.Combine(testRoot, "store"));
            Check(store.Root.StartsWith(Path.GetFullPath(testRoot), StringComparison.OrdinalIgnoreCase),
                "timeline store escaped its test root");

            var session = new TimelineEditorSession(store);
            var created = await session.CreateAsync("Shell kiểm tra", CancellationToken.None);
            TimelineProjectV2.ValidateId(created.Id);
            var opened = await session.OpenAsync(created.Id, CancellationToken.None);
            Check(opened.Id == created.Id && opened.Name == "Shell kiểm tra",
                "timeline session open did not return the created project");

            var listed = await store.ListAsync(CancellationToken.None);
            Check(listed.Count == 1 && listed[0].Id == created.Id,
                "timeline store list did not expose the created project");

            var files = Directory.GetFiles(store.Root, "*", SearchOption.AllDirectories);
            Check(files.Length > 0, "timeline store wrote no files");
            var rootPrefix = store.Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            foreach (var file in files)
            {
                var full = Path.GetFullPath(file);
                Check(full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase),
                    $"timeline store wrote outside its root: {file}");
            }

            var plan = TimelineCompiler.Compile(created);
            Check(plan.ProjectId == created.Id, "timeline plan lost project ownership");
            var preview = new TimelinePreviewController();
            var export = new TimelineExportService(
                AppPaths.FromRoot(testRoot),
                new TimelineAudioContractFixture.NullSegmentRenderer(),
                new TimelineExportConcatList(AppPaths.FromRoot(testRoot)),
                new TimelineAudioContractFixture.NullConcatenator());
            var previewText = await preview.DescribeAsync(plan, CancellationToken.None);
            var exportText = await export.DescribeAsync(plan, CancellationToken.None);
            Check(previewText.Contains(created.Id, StringComparison.Ordinal)
                && exportText.Contains(created.Id, StringComparison.Ordinal),
                "timeline preview/export shells must share the immutable plan");

            var productionLike = Path.Combine(testRoot, "prod", "Data", "Projects");
            try
            {
                _ = new TimelineProjectStoreV2(productionLike);
                throw new InvalidOperationException("timeline store accepted a production Data/Projects root");
            }
            catch (InvalidOperationException)
            {
            }

            try
            {
                TimelineProjectV2.ValidateId("not-an-id");
                throw new InvalidOperationException("timeline accepted an invalid project id");
            }
            catch (InvalidDataException)
            {
            }
        }
        finally
        {
            try { Directory.Delete(testRoot, recursive: true); } catch { }
        }
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
