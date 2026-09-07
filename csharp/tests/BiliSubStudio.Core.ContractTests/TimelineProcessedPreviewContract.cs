using System.Reflection;
using BiliSubStudio.Core.Configuration;
using BiliSubStudio.Core.Processes;
using BiliSubStudio.Core.Timeline;

namespace BiliSubStudio.Core.ContractTests;

internal static class TimelineProcessedPreviewContract
{
    public static async Task RunAsync()
    {
        await LatestRequestWaitsForCleanupAsync();
        await CancellationResistantStaleResultIsDeletedAsync();
        await FailedReplacementKeepsCurrentArtifactAsync();
        await ServiceOwnsAtomicV2ArtifactsAsync();
        await CancellationReapsOwnedProcessTreeAsync();
    }

    private static async Task LatestRequestWaitsForCleanupAsync()
    {
        var root = TemporaryRoot();
        try
        {
            var plan = Plan();
            var firstStarted = Signal();
            var firstCleanupStarted = Signal();
            var releaseFirstCleanup = Signal();
            var active = 0;
            var secondRan = false;
            var thirdRan = false;
            var generator = new FakeGenerator(root, async (owner, _, start, duration, revision, token) =>
            {
                Check(Interlocked.Increment(ref active) == 1,
                    "Timeline preview renders overlapped");
                try
                {
                    if (revision == 1)
                    {
                        firstStarted.TrySetResult(true);
                        try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                        finally
                        {
                            firstCleanupStarted.TrySetResult(true);
                            await releaseFirstCleanup.Task;
                        }
                    }
                    else if (revision == 2)
                    {
                        secondRan = true;
                    }
                    else
                    {
                        thirdRan = true;
                    }
                    return await owner.CreateAsync(revision, start, duration);
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            });
            await using var controller = new TimelinePreviewController(generator);

            var first = controller.RequestAsync(plan, 0, 1_000_000);
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var second = controller.RequestAsync(plan, 1_000_000, 1_000_000);
            var third = controller.RequestAsync(plan, 2_000_000, 1_000_000);

            await firstCleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(controller.IsActive && generator.StartedRevisions.SequenceEqual([1L]),
                "queued Timeline preview started before prior cleanup");
            releaseFirstCleanup.TrySetResult(true);
            await ThrowsAsync<OperationCanceledException>(() => first);
            await ThrowsAsync<OperationCanceledException>(() => second);
            var latest = await third.WaitAsync(TimeSpan.FromSeconds(3));

            Check(!secondRan && thirdRan && active == 0,
                "Timeline preview latest-wins serialization drifted");
            Check(controller.CurrentRevision == 3
                && controller.CurrentArtifact == latest
                && File.Exists(latest.Path),
                "latest Timeline preview was not published as current");
            await controller.CancelAsync();
            Check(!controller.IsActive && controller.CurrentArtifact is null
                && !File.Exists(latest.Path),
                "Timeline preview cancel did not wait for cleanup and delete current proxy");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static async Task CancellationResistantStaleResultIsDeletedAsync()
    {
        var root = TemporaryRoot();
        try
        {
            var plan = Plan();
            var firstStarted = Signal();
            var releaseStale = Signal();
            string? stalePath = null;
            var generator = new FakeGenerator(root, async (owner, _, start, duration, revision, _) =>
            {
                if (revision == 1)
                {
                    firstStarted.TrySetResult(true);
                    await releaseStale.Task;
                    var stale = await owner.CreateAsync(revision, start, duration);
                    stalePath = stale.Path;
                    return stale;
                }
                return await owner.CreateAsync(revision, start, duration);
            });
            await using var controller = new TimelinePreviewController(generator);

            var first = controller.RequestAsync(plan, 0, 1_000_000);
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var latestTask = controller.RequestAsync(plan, 1_000_000, 1_000_000);
            releaseStale.TrySetResult(true);
            await ThrowsAsync<OperationCanceledException>(() => first);
            var latest = await latestTask.WaitAsync(TimeSpan.FromSeconds(3));

            Check(stalePath is not null && !File.Exists(stalePath)
                && generator.DeletedPaths.Contains(stalePath, StringComparer.OrdinalIgnoreCase),
                "cancellation-resistant stale Timeline proxy was published or leaked");
            Check(controller.CurrentArtifact == latest,
                "stale Timeline result replaced the latest artifact");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static async Task FailedReplacementKeepsCurrentArtifactAsync()
    {
        var root = TemporaryRoot();
        try
        {
            var plan = Plan();
            var generator = new FakeGenerator(root, async (owner, _, start, duration, revision, _) =>
            {
                if (revision == 2)
                    throw new InvalidOperationException("fixture render failure");
                return await owner.CreateAsync(revision, start, duration);
            });
            await using var controller = new TimelinePreviewController(generator);
            var first = await controller.RequestAsync(plan, 0, 1_000_000);
            await ThrowsAsync<InvalidOperationException>(
                () => controller.RequestAsync(plan, 1_000_000, 1_000_000));
            Check(controller.CurrentArtifact == first && File.Exists(first.Path),
                "failed replacement deleted the last accepted Timeline proxy");

            var replacement = await controller.RequestAsync(plan, 2_000_000, 1_000_000);
            Check(controller.CurrentArtifact == replacement && !File.Exists(first.Path),
                "accepted replacement did not retire the prior Timeline proxy");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static async Task ServiceOwnsAtomicV2ArtifactsAsync()
    {
        var root = TemporaryRoot();
        try
        {
            var paths = AppPaths.FromRoot(root);
            var plan = Plan();
            TimelineProcessedPreviewRequest? received = null;
            var service = new TimelineProcessedPreviewService(
                paths,
                new DelegateRenderer(async (request, renderingPath, _, token) =>
                {
                    received = request;
                    await File.WriteAllTextAsync(renderingPath, "proxy", token);
                }));
            var artifact = await service.GenerateAsync(
                plan, 1_000_000, 2_000_000, revision: 7, CancellationToken.None);
            var expectedPrefix = service.PreviewDirectory.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            Check(ReferenceEquals(received?.Plan, plan)
                && received is { Revision: 7, SequenceStartUs: 1_000_000, DurationUs: 2_000_000 },
                "processed-preview service did not consume the exact immutable plan request");
            Check(Path.GetFullPath(artifact.Path).StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase)
                && File.Exists(artifact.Path)
                && !Directory.EnumerateFiles(service.PreviewDirectory, "*.rendering.mp4").Any(),
                "Timeline proxy was not atomically published inside the V2 root");

            var outside = Path.Combine(root, "outside.mp4");
            await File.WriteAllTextAsync(outside, "outside");
            await ThrowsAsync<InvalidDataException>(() => service.DeleteAsync(
                artifact with { Path = outside }, CancellationToken.None));
            Check(File.Exists(outside), "Timeline cleanup deleted an outside-root file");

            await service.DeleteAsync(artifact, CancellationToken.None);
            var crashed = Path.Combine(service.PreviewDirectory,
                "timeline-preview-crashed.rendering.mp4");
            var old = Path.Combine(service.PreviewDirectory, "timeline-preview-old.mp4");
            var unrelated = Path.Combine(service.PreviewDirectory, "keep.txt");
            var foreignMp4 = Path.Combine(service.PreviewDirectory, "foreign.mp4");
            using var current = System.Diagnostics.Process.GetCurrentProcess();
            var live = Path.Combine(service.PreviewDirectory,
                $"timeline-preview-{Environment.ProcessId}-{current.StartTime.ToUniversalTime().Ticks}-live.mp4");
            var dead = Path.Combine(service.PreviewDirectory,
                "timeline-preview-2147483647-1-dead.mp4");
            await File.WriteAllTextAsync(crashed, "partial");
            await File.WriteAllTextAsync(old, "old");
            await File.WriteAllTextAsync(unrelated, "unrelated");
            await File.WriteAllTextAsync(foreignMp4, "foreign");
            await File.WriteAllTextAsync(live, "live");
            await File.WriteAllTextAsync(dead, "dead");
            File.SetLastWriteTimeUtc(crashed, DateTime.UtcNow.AddDays(-2));
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-2));
            await service.CleanupCacheAsync();
            Check(!File.Exists(crashed) && !File.Exists(old) && !File.Exists(dead)
                && File.Exists(unrelated) && File.Exists(foreignMp4) && File.Exists(live),
                "Timeline cache cleanup crossed its managed artifact policy");
            File.Delete(live);

            var failing = new TimelineProcessedPreviewService(
                paths,
                new DelegateRenderer(async (_, renderingPath, _, _) =>
                {
                    await File.WriteAllTextAsync(renderingPath, "partial");
                    throw new InvalidOperationException("fixture failure");
                }));
            await ThrowsAsync<InvalidOperationException>(() => failing.GenerateAsync(
                plan, 0, 1_000_000, revision: 8, CancellationToken.None));
            Check(!Directory.EnumerateFiles(service.PreviewDirectory, "timeline-preview-*").Any(),
                "failed Timeline render leaked a temporary or final proxy");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static async Task CancellationReapsOwnedProcessTreeAsync()
    {
        var root = TemporaryRoot();
        try
        {
            var executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("missing current process path");
            var arguments = new List<string>();
            if (string.Equals(
                    Path.GetFileNameWithoutExtension(executable),
                    "dotnet",
                    StringComparison.OrdinalIgnoreCase))
                arguments.Add(Assembly.GetExecutingAssembly().Location);
            arguments.Add("--fixture-spawn-child");

            var nestedStarted = Signal();
            var nestedPid = 0;
            OwnedProcessGroup? observedOwner = null;
            var service = new TimelineProcessedPreviewService(
                AppPaths.FromRoot(root),
                new DelegateRenderer(async (_, _, processes, token) =>
                {
                    observedOwner = processes;
                    await new ProcessRunner().RunAsync(
                        executable,
                        arguments,
                        token,
                        standardOutputLine: line =>
                        {
                            if (!line.StartsWith("nested:", StringComparison.Ordinal)) return;
                            nestedPid = int.Parse(
                                line["nested:".Length..],
                                System.Globalization.CultureInfo.InvariantCulture);
                            nestedStarted.TrySetResult(true);
                        },
                        owner: processes);
                }));
            using var cancellation = new CancellationTokenSource();
            var generation = service.GenerateAsync(
                Plan(), 0, 1_000_000, revision: 9, cancellation.Token);
            await nestedStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await ThrowsAsync<OperationCanceledException>(
                () => generation.WaitAsync(TimeSpan.FromSeconds(8)));
            Check(observedOwner is not null && observedOwner.ActiveCount == 0,
                "Timeline cancellation left an owned renderer process");
            Check(nestedPid > 0, "Timeline process fixture did not report nested child");
            try
            {
                using var nested = System.Diagnostics.Process.GetProcessById(nestedPid);
                if (!nested.HasExited)
                    await nested.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                Check(nested.HasExited, "Timeline cancellation abandoned a nested child process");
            }
            catch (ArgumentException)
            {
                // Reaped processes can disappear from the process table.
            }
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static TimelineRenderPlan Plan()
    {
        var fixture = TimelineAudioContractFixture.CreateLocalVoiceState();
        return TimelineCompiler.Compile(
            TimelineAudioContractFixture.Project(fixture.State, fixture.MediaAssets));
    }

    private static TaskCompletionSource<bool> Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string TemporaryRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "bilisub-timeline-g9-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { }
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }

    private static async Task ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private sealed class FakeGenerator(
        string root,
        Func<FakeGenerator, TimelineRenderPlan, long, long, long, CancellationToken,
            Task<TimelineProcessedPreviewArtifact>> generate)
        : ITimelineProcessedPreviewGenerator
    {
        public List<long> StartedRevisions { get; } = [];
        public List<string> DeletedPaths { get; } = [];

        public Task<TimelineProcessedPreviewArtifact> GenerateAsync(
            TimelineRenderPlan plan,
            long sequenceStartUs,
            long durationUs,
            long revision,
            CancellationToken cancellationToken)
        {
            StartedRevisions.Add(revision);
            return generate(this, plan, sequenceStartUs, durationUs, revision, cancellationToken);
        }

        public Task DeleteAsync(
            TimelineProcessedPreviewArtifact? artifact,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (artifact is null) return Task.CompletedTask;
            DeletedPaths.Add(artifact.Path);
            File.Delete(artifact.Path);
            return Task.CompletedTask;
        }

        public async Task<TimelineProcessedPreviewArtifact> CreateAsync(
            long revision,
            long sequenceStartUs,
            long durationUs)
        {
            var path = Path.Combine(root, $"fake-{revision}-{Guid.NewGuid():N}.mp4");
            await File.WriteAllTextAsync(path, "proxy");
            return new TimelineProcessedPreviewArtifact(
                revision, path, sequenceStartUs, durationUs);
        }
    }

    private sealed class DelegateRenderer(
        Func<TimelineProcessedPreviewRequest, string, OwnedProcessGroup, CancellationToken, Task> render)
        : ITimelineProcessedPreviewRenderer
    {
        public Task RenderAsync(
            TimelineProcessedPreviewRequest request,
            string renderingPath,
            OwnedProcessGroup processes,
            CancellationToken cancellationToken) =>
            render(request, renderingPath, processes, cancellationToken);
    }
}
