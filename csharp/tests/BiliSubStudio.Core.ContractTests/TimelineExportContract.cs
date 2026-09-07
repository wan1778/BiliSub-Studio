using BiliSubStudio.Core.Configuration;
using BiliSubStudio.Core.Jobs;
using BiliSubStudio.Core.Processes;
using BiliSubStudio.Core.Timeline;

namespace BiliSubStudio.Core.ContractTests;

internal static class TimelineExportContract
{
    public static async Task RunAsync()
    {
        await RenderPlanGeometryAndMixedFormatAsync();
        await ConcatListMatchesCompilerOrderAsync();
        await SegmentServiceDrivesCleanupAwareAppJobAsync();
    }

    private static async Task RenderPlanGeometryAndMixedFormatAsync()
    {
        var audible = TimelineMediaAsset.Create(
            "C:/video/high.mp4", 1920, 1080, 60_000_000, 30, hasAudio: true);
        var silent = TimelineMediaAsset.Create(
            "C:/video/low.mp4", 1280, 720, 30_000_000, 24, hasAudio: false);
        var first = new TimelineClip(TimelineClip.NewId(), audible.Id,
            0, 0, 10_000_000, 1);
        var second = new TimelineClip(TimelineClip.NewId(), silent.Id,
            10_000_000, 2_000_000, 7_000_000, 1);
        var sequence = TimelineSequence.Create([first, second], [audible, silent], outputFrameRate: 30);
        var timeline = TimelineEditState.Create([first, second], []);
        var state = TimelineAudioEditState.Create(
            timeline, [], [],
            TimelineSourceAudioState.Defaults([first, second]),
            [], null);
        var project = new TimelineProjectV2(
            TimelineProjectV2.NewId(),
            "G10 mixed-format",
            DateTimeOffset.UtcNow,
            [audible, silent],
            sequence,
            [],
            [],
            [],
            TimelineSourceAudioState.Defaults([first, second]),
            [],
            null);
        var plan = TimelineCompiler.Compile(project);

        Check(plan.OutputWidth == 1920 && plan.OutputHeight == 1080
            && Math.Abs(plan.OutputFrameRate - 30d) <= 0.0001,
            "timeline plan output geometry must default to first clip and keep explicit FPS");
        Check(plan.Audio.OutputSampleRate == 48_000
            && Math.Abs(plan.Audio.SequenceFrameRate - 30d) <= 0.0001
            && plan.Audio.MaximumOutputDriftUs == TimelineAudioTime.MaximumOutputDriftUs(30),
            "timeline plan audio must keep 48 kHz and DEC-019 tolerance");
        var resolvedSecond = plan.Clips.Single(clip => clip.ClipId == second.Id);
        Check(resolvedSecond.PlaybackRate == 1
            && resolvedSecond.SourceInUs == 2_000_000 && resolvedSecond.SourceOutUs == 7_000_000
            && resolvedSecond.SequenceDurationUs == 5_000_000,
            "mixed-format second clip did not preserve source trim");
        var silentSource = plan.Audio.SourceAudio.Single(source => source.ClipId == second.Id);
        Check(!silentSource.HasSourceStream && silentSource.Gain == 1,
            "mixed-format silent clip did not stay silent in plan");

        var tinyFps = plan with { OutputFrameRate = double.Epsilon };
        Throws<InvalidDataException>(tinyFps.ValidateOutputGeometry,
            "timeline plan did not reject unrepresentable DEC-019 FPS");
        var tinyGeometry = plan with { OutputWidth = 0 };
        Throws<InvalidDataException>(tinyGeometry.ValidateOutputGeometry,
            "timeline plan did not reject zero output width");
        _ = state;
        await Task.CompletedTask;
    }

    private static async Task ConcatListMatchesCompilerOrderAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "bilisub-g10-list-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = AppPaths.FromRoot(root);
            var concatList = new TimelineExportConcatList(paths);
            var planProject = BuildMixedProject();
            var plan = TimelineCompiler.Compile(planProject);
            var segments = plan.Clips
                .OrderBy(clip => clip.SequenceStartUs)
                .Select((clip, index) => new TimelineExportSegmentArtifact(
                    index, clip.ClipId, Path.Combine(concatList.ExportDirectory,
                        TimelineExportConcatList.ManagedSegmentPrefix + index + ".mp4"),
                    clip.SequenceStartUs, clip.SequenceDurationUs))
                .ToArray();
            await Task.CompletedTask;
            var list = concatList.Build(plan, segments, jobId: "list-test");
            Check(File.Exists(list.ListPath), "concat list file was not written");
            var lines = await File.ReadAllLinesAsync(list.ListPath);
            Check(lines.Length == segments.Length,
                "concat list line count must equal segment count");
            Check(lines.All(line => line.StartsWith("file '", StringComparison.Ordinal)
                && line.EndsWith("'", StringComparison.Ordinal)),
                "concat list must use ffmpeg concat demuxer 'file …' entries");

            var swapped = new[]
            {
                segments[1] with { SegmentIndex = 0 },
                segments[0] with { SegmentIndex = 1 },
            };
            Throws<InvalidDataException>(() => concatList.Build(plan, swapped, "swap"),
                "concat list accepted segments out of compiler order");
            var tooShort = segments.Take(segments.Length - 1).ToArray();
            Throws<InvalidDataException>(() => concatList.Build(plan, tooShort, "short"),
                "concat list accepted fewer segments than plan clips");
            await Task.CompletedTask;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task SegmentServiceDrivesCleanupAwareAppJobAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "bilisub-g10-job-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = AppPaths.FromRoot(root);
            var concatList = new TimelineExportConcatList(paths);
            var renderer = new RecordingSegmentRenderer();
            var service = new TimelineExportService(paths, renderer, concatList);
            var project = BuildMixedProject();
            var plan = TimelineCompiler.Compile(project);

            using var jobs = new JobManager();
            var bad = jobs.Create("timeline-export");
            Throws<InvalidOperationException>(() => service.ExportAsync(
                    bad, plan, project,
                    Path.Combine(root, "out"), "missing-job-aware.mp4")
                .GetAwaiter().GetResult(),
                "export service accepted job without CleanupAwareCancel=true");
            bad.CancelComplete("skipped");

            var good = jobs.Create("timeline-export", cleanupAwareCancel: true);
            var firstStarted = Signal();
            var releaseFirst = Signal();
            renderer.OnRender = async (segment, _, _, token) =>
            {
                if (segment.SegmentIndex == 0)
                {
                    firstStarted.TrySetResult(true);
                    await releaseFirst.Task.WaitAsync(token);
                    token.ThrowIfCancellationRequested();
                    return;
                }
                token.ThrowIfCancellationRequested();
                await Task.Yield();
            };
            var exportTask = service.ExportAsync(
                good,
                plan,
                project,
                Path.Combine(root, "out"),
                "canceled.mp4",
                CancellationToken.None);
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            jobs.Cancel(good.Id);
            releaseFirst.TrySetResult(true);
            await ThrowsAsync<OperationCanceledException>(() => exportTask
                .WaitAsync(TimeSpan.FromSeconds(5)));
            // Mirror BiliSubApplication.RunJobAsync: the wrapper owns terminal
            // state, the service only unwinds renderer/process/temp cleanup.
            if (!good.Snapshot().Done) good.CancelComplete();
            await good.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Check(good.Snapshot().Status == "cancelled",
                "canceled export job did not reach terminal cancelled state");
            Check(!File.Exists(Path.Combine(root, "out", "canceled.mp4")),
                "canceled export left a final output behind");
            var prefix = TimelineExportConcatList.ManagedSegmentPrefix + good.Id;
            var leftovers = Directory.Exists(service.ExportDirectory)
                ? Directory.EnumerateFiles(service.ExportDirectory)
                    .Where(path => Path.GetFileName(path).StartsWith(prefix, StringComparison.Ordinal))
                    .ToArray()
                : [];
            Check(leftovers.Length == 0,
                "export cancellation left owned segment artifacts behind");

            var done = jobs.Create("timeline-export", cleanupAwareCancel: true);
            renderer.OnRender = (_, path, _, _) =>
                File.WriteAllBytesAsync(path, RenderingBytes(path));
            renderer.RenderedPaths.Clear();
            var doneProject = BuildMixedProject();
            var donePlan = TimelineCompiler.Compile(doneProject);
            var output = await service.ExportAsync(
                done, donePlan, doneProject,
                Path.Combine(root, "out"), "done.mp4", CancellationToken.None);
            Check(File.Exists(output.OutputPath), "successful export did not publish final file");
            Check(renderer.RenderedPaths.Count == 2
                && renderer.RenderedPaths.All(path => path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)),
                "successful export did not publish every normalized segment");
            await done.Completion.WaitAsync(TimeSpan.FromSeconds(5));

            var failingRenderer = new RecordingSegmentRenderer
            {
                OnRender = (_, _, _, _) =>
                    throw new InvalidOperationException("simulated segment failure"),
            };
            var failingService = new TimelineExportService(paths, failingRenderer, concatList);
            using var failJobs = new JobManager();
            var failedJob = failJobs.Create("timeline-export", cleanupAwareCancel: true);
            var failProject = BuildMixedProject();
            var failPlan = TimelineCompiler.Compile(failProject);
            await ThrowsAsync<InvalidOperationException>(() => failingService.ExportAsync(
                failedJob, failPlan, failProject,
                Path.Combine(root, "out"), "failed.mp4", CancellationToken.None));
            Check(!File.Exists(Path.Combine(root, "out", "failed.mp4")),
                "failed export leaked the final output file");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static TimelineProjectV2 BuildMixedProject()
    {
        var audible = TimelineMediaAsset.Create(
            "C:/video/mix-high.mp4", 1920, 1080, 60_000_000, 30, hasAudio: true);
        var silent = TimelineMediaAsset.Create(
            "C:/video/mix-low.mp4", 1280, 720, 30_000_000, 24, hasAudio: false);
        var first = new TimelineClip(TimelineClip.NewId(), audible.Id,
            0, 0, 5_000_000, 1);
        var second = new TimelineClip(TimelineClip.NewId(), silent.Id,
            5_000_000, 0, 5_000_000, 1);
        var sequence = TimelineSequence.Create([first, second], [audible, silent], outputFrameRate: 30);
        return new TimelineProjectV2(
            TimelineProjectV2.NewId(),
            "G10 mixed export",
            DateTimeOffset.UtcNow,
            [audible, silent],
            sequence,
            [],
            [],
            [],
            TimelineSourceAudioState.Defaults([first, second]),
            [],
            null);
    }

    private static byte[] RenderingBytes(string path) =>
        System.Text.Encoding.UTF8.GetBytes("segment:" + Path.GetFileName(path));

    private static TaskCompletionSource<bool> Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }

    private static void Throws<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException(message);
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

    private sealed class RecordingSegmentRenderer : ITimelineExportSegmentRenderer
    {
        public List<string> RenderedPaths { get; } = [];
        public Func<TimelineRenderSegment, string, OwnedProcessGroup, CancellationToken, Task>? OnRender { get; set; }

        public async Task RenderSegmentAsync(
            TimelineRenderSegment segment,
            string renderingPath,
            OwnedProcessGroup processes,
            CancellationToken cancellationToken)
        {
            RenderedPaths.Add(renderingPath);
            if (OnRender is null)
            {
                await File.WriteAllBytesAsync(renderingPath, RenderingBytes(renderingPath), cancellationToken);
                processes.SyntheticIdle();
                return;
            }
            await OnRender(segment, renderingPath, processes, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(renderingPath))
                await File.WriteAllBytesAsync(renderingPath, RenderingBytes(renderingPath), cancellationToken);
        }
    }
}

internal static class OwnedProcessGroupExtensions
{
    public static void SyntheticIdle(this OwnedProcessGroup owner)
    {
        // RecordingSegmentRenderer does not own an FFmpeg child; intentionally a no-op.
        _ = owner;
    }
}
